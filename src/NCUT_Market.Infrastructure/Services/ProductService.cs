using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Storage;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class ProductService(
    AppDbContext dbContext,
    ImageStorage imageStorage,
    IOptions<StorageOptions> storageOptions) : IProductService
{
    /// <summary>Most photos one listing may hold.</summary>
    private const int MaxImagesPerProduct = 9;

    /// <summary>Largest single upload accepted, in bytes.</summary>
    private const int MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Character that makes the next character literal inside a <c>LIKE</c> pattern.
    /// </summary>
    /// <remarks>
    /// Backslash, because it is already MySQL's default <c>LIKE</c> escape and so behaves the same
    /// whether or not the <c>ESCAPE</c> clause survives translation.
    /// </remarks>
    private const string LikeEscape = "\\";

    private readonly StorageOptions _storage = storageOptions.Value;

    /// <summary>
    /// The list projection, shared by the public feed and the seller's own list.
    /// </summary>
    /// <remarks>
    /// Written once as an expression rather than twice inline, because the two lists must not drift:
    /// a card that shows a field on one page and not the other is the kind of difference nobody
    /// notices until a user does.
    ///
    /// The thumbnail arrives as a storage key and is turned into a URL afterwards, in memory. Doing
    /// the concatenation in SQL would rely on the provider translating string addition into
    /// <c>CONCAT</c>; the page is at most 100 rows, so mapping it here costs nothing and cannot
    /// fail to translate.
    /// </remarks>
    private static readonly Expression<Func<Product, ProductSummaryResponse>> SummaryProjection =
        product => new ProductSummaryResponse(
            product.Id,
            product.Title,
            product.Price,
            product.Condition,
            product.Status,
            product.Category.Name,
            product.DormitoryArea.Name,
            product.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => image.ThumbnailKey)
                .FirstOrDefault(),
            product.CreatedAt,
            product.Seller.Nickname);

    public async Task<PagedResult<ProductSummaryResponse>> ListAsync(
        ProductListQuery query,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        var products = dbContext.Products
            .AsNoTracking()
            .Where(x => x.Status == ProductStatus.Published);

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = "%" + EscapeLike(query.Q.Trim()) + "%";

            // Title or description, so a search for a colour or a brand mentioned only in the body
            // still finds the listing. Case is handled by the column collation
            // (utf8mb4_0900_ai_ci, case-insensitive), not here.
            products = products.Where(x =>
                EF.Functions.Like(x.Title, pattern, LikeEscape) ||
                (x.Description != null && EF.Functions.Like(x.Description, pattern, LikeEscape)));
        }

        if (query.CategoryId is long categoryId)
        {
            var categoryIds = await ResolveCategoryAndDescendantsAsync(categoryId, cancellationToken);

            products = products.Where(x => categoryIds.Contains(x.CategoryId));
        }

        if (query.AreaId is long areaId)
        {
            products = products.Where(x => x.DormitoryAreaId == areaId);
        }

        if (query.Condition is int condition)
        {
            // Casting rather than range-checking: an out-of-range value casts to an undefined member
            // that matches nothing, which is the right answer for a filter nobody can satisfy.
            var wanted = (ProductCondition)condition;

            products = products.Where(x => x.Condition == wanted);
        }

        if (query.MinPrice is decimal minPrice)
        {
            products = products.Where(x => x.Price >= minPrice);
        }

        if (query.MaxPrice is decimal maxPrice)
        {
            products = products.Where(x => x.Price <= maxPrice);
        }

        var totalCount = await products.CountAsync(cancellationToken);

        var items = await Order(products, query.Sort)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken);

        return pagination.ToResult(WithThumbnailUrls(items), totalCount);
    }

    public async Task<PagedResult<ProductSummaryResponse>> ListMineAsync(
        long sellerId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        // No status filter: this is the seller's own working set, so drafts, offlined and sold
        // listings all belong here. It is the only list that shows anything but Published.
        var products = dbContext.Products
            .AsNoTracking()
            .Where(x => x.SellerId == sellerId);

        var totalCount = await products.CountAsync(cancellationToken);

        var items = await products
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken);

        return pagination.ToResult(WithThumbnailUrls(items), totalCount);
    }

    public async Task<OperationResult<ProductDetailResponse>> GetByIdAsync(
        long id,
        long? viewerId,
        CancellationToken cancellationToken = default)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(DetailProjection)
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.NotFound,
                $"Product {id} was not found.");
        }

        // A listing that is not live is not merely hidden from the feed, it is invisible: reported
        // as not-found rather than forbidden, so guessing an id does not confirm that a draft exists.
        var isVisible = product.Status is ProductStatus.Published or ProductStatus.Sold;

        if (!isVisible && product.SellerId != viewerId)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.NotFound,
                $"Product {id} was not found.");
        }

        return OperationResult<ProductDetailResponse>.Success(WithImageUrls(product));
    }

    public async Task<OperationResult<ProductDetailResponse>> CreateAsync(
        long sellerId,
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveTargetAsync(request.CategoryId, request.DormitoryAreaId, cancellationToken);

        if (target.ErrorCode is not null)
        {
            return OperationResult<ProductDetailResponse>.Failure(target.ErrorCode, target.ErrorMessage!);
        }

        var product = new Product
        {
            SellerId = sellerId,
            CategoryId = request.CategoryId,
            DormitoryAreaId = request.DormitoryAreaId,
            Title = request.Title.Trim(),
            Description = Normalise(request.Description),
            Price = request.Price,
            Condition = (ProductCondition)request.Condition,
            Status = ProductStatus.Draft,

            // Set by hand. The column is NOT NULL with no default, and AppDbContext deliberately
            // does not stamp it — LastActivityAt is the future draft-cleanup job's clock, and a
            // blanket SaveChanges rule would advance it on every incidental write. Omitting it here
            // is not a subtle bug: the insert fails.
            LastActivityAt = AppDbContext.AuditNow
        };

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(product.Id, sellerId, cancellationToken);
    }

    public async Task<OperationResult<ProductDetailResponse>> UpdateAsync(
        long id,
        long sellerId,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(id, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(code!, message!);
        }

        var target = await ResolveTargetAsync(request.CategoryId, request.DormitoryAreaId, cancellationToken);

        if (target.ErrorCode is not null)
        {
            return OperationResult<ProductDetailResponse>.Failure(target.ErrorCode, target.ErrorMessage!);
        }

        product.Title = request.Title.Trim();
        product.Description = Normalise(request.Description);
        product.Price = request.Price;
        product.Condition = (ProductCondition)request.Condition;
        product.CategoryId = request.CategoryId;
        product.DormitoryAreaId = request.DormitoryAreaId;

        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(product.Id, sellerId, cancellationToken);
    }

    public async Task<OperationResult<ProductDetailResponse>> PublishAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(id, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(code!, message!);
        }

        // Published is not an error — publishing an already-live listing is a no-op a client can
        // reach by double-clicking. Sold is: a sold listing cannot go back on sale.
        if (product.Status == ProductStatus.Sold)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidState,
                "A sold listing cannot be published again.");
        }

        var hasImage = await dbContext.ProductImages
            .AnyAsync(x => x.ProductId == id, cancellationToken);

        if (!hasImage)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidArgument,
                "A listing needs at least one photo before it can be published.");
        }

        if (product.Status != ProductStatus.Published)
        {
            product.Status = ProductStatus.Published;

            // Only the first publish sets this. Taking a listing down and putting it back up does
            // not make it new again, and the feed orders by CreatedAt anyway.
            product.PublishedAt ??= AppDbContext.AuditNow;
        }

        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(product.Id, sellerId, cancellationToken);
    }

    public async Task<OperationResult<ProductDetailResponse>> OfflineAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(id, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(code!, message!);
        }

        if (product.Status != ProductStatus.Published)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidState,
                "Only a published listing can be taken offline.");
        }

        product.Status = ProductStatus.Offline;
        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(product.Id, sellerId, cancellationToken);
    }

    public async Task<OperationResult<ProductDetailResponse>> MarkSoldAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(id, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(code!, message!);
        }

        if (product.Status != ProductStatus.Published)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidState,
                "Only a published listing can be marked as sold.");
        }

        product.Status = ProductStatus.Sold;
        product.SoldAt = AppDbContext.AuditNow;
        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(product.Id, sellerId, cancellationToken);
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(id, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<bool>.Failure(code!, message!);
        }

        // A published listing has to be taken down first, so deleting is never one click away from
        // a live page. A sold one is kept: it is the record that the transaction happened.
        if (product.Status is not (ProductStatus.Draft or ProductStatus.Offline))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "Take the listing offline before deleting it.");
        }

        var keys = await dbContext.ProductImages
            .Where(x => x.ProductId == id)
            .Select(x => new StoredImage(x.OriginalKey, x.LargeKey, x.MediumKey, x.ThumbnailKey, 0, 0, 0, string.Empty))
            .ToListAsync(cancellationToken);

        // The image rows go with the product through the cascade; the files do not, so they are
        // removed after the row is gone rather than before. See DeleteFiles.
        dbContext.Products.Remove(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        DeleteFiles(keys);

        return OperationResult<bool>.Success(true);
    }

    public async Task<OperationResult<ProductImageResponse>> AddImageAsync(
        long productId,
        long sellerId,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(productId, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductImageResponse>.Failure(code!, message!);
        }

        var existing = await dbContext.ProductImages
            .CountAsync(x => x.ProductId == productId, cancellationToken);

        if (existing >= MaxImagesPerProduct)
        {
            // Conflict, not InvalidArgument: the tenth photo is a well-formed request that the
            // listing's current state cannot accept, which is what 409 is for. Nothing about the
            // request itself is wrong, so telling the caller to fix its arguments would be a lie.
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.Conflict,
                $"A listing can hold at most {MaxImagesPerProduct} photos.");
        }

        var buffered = await ReadWithLimitAsync(content, MaxImageBytes, cancellationToken);

        if (buffered is null)
        {
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.InvalidArgument,
                $"Each photo must be at most {MaxImageBytes / (1024 * 1024)} MB.");
        }

        StoredImage? stored;

        try
        {
            stored = await imageStorage.SaveAsync(buffered, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Decoding can fail in ways ImageStorage does not enumerate. Converting it here keeps an
            // unusable upload a client error rather than a 500 — nothing about a malformed image is
            // unexpected from the server's point of view.
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.UnsupportedMediaType,
                "That file could not be read as an image.");
        }

        if (stored is null)
        {
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.UnsupportedMediaType,
                "Only JPEG, PNG and WebP images are accepted.");
        }

        var image = new ProductImage
        {
            ProductId = productId,
            OriginalKey = stored.OriginalKey,
            LargeKey = stored.LargeKey,
            MediumKey = stored.MediumKey,
            ThumbnailKey = stored.ThumbnailKey,
            Width = stored.Width,
            Height = stored.Height,
            FileSize = stored.FileSize,
            MimeType = stored.MimeType,
            SortOrder = existing
        };

        dbContext.ProductImages.Add(image);

        // Attaching a photo is a real edit, so it counts as activity for the draft-cleanup clock.
        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ProductImageResponse>.Success(new ProductImageResponse(
            image.Id,
            ToUrl(image.LargeKey)!,
            ToUrl(image.MediumKey)!,
            ToUrl(image.ThumbnailKey)!,
            image.Width,
            image.Height,
            image.SortOrder));
    }

    public async Task<OperationResult<bool>> DeleteImageAsync(
        long productId,
        long sellerId,
        long imageId,
        CancellationToken cancellationToken = default)
    {
        var (product, code, message) = await RequireOwnedAsync(productId, sellerId, cancellationToken);

        if (product is null)
        {
            return OperationResult<bool>.Failure(code!, message!);
        }

        var image = await dbContext.ProductImages
            .FirstOrDefaultAsync(x => x.Id == imageId && x.ProductId == productId, cancellationToken);

        if (image is null)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.NotFound,
                $"Image {imageId} was not found on product {productId}.");
        }

        var stored = new StoredImage(
            image.OriginalKey, image.LargeKey, image.MediumKey, image.ThumbnailKey,
            image.Width, image.Height, image.FileSize, image.MimeType);

        dbContext.ProductImages.Remove(image);
        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        DeleteFiles([stored]);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// The detail projection, including the photo list.
    /// </summary>
    private static readonly Expression<Func<Product, ProductDetailResponse>> DetailProjection =
        product => new ProductDetailResponse(
            product.Id,
            product.SellerId,
            product.Seller.Nickname,
            product.Title,
            product.Description,
            product.Price,
            product.Condition,
            product.Status,
            product.CategoryId,
            product.Category.Name,
            product.DormitoryAreaId,
            product.DormitoryArea.Name,
            product.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => new ProductImageResponse(
                    image.Id,
                    image.LargeKey,
                    image.MediumKey,
                    image.ThumbnailKey,
                    image.Width,
                    image.Height,
                    image.SortOrder))
                .ToList(),
            product.CreatedAt,
            product.PublishedAt,
            product.SoldAt);

    private static IQueryable<Product> Order(IQueryable<Product> products, ProductSort sort) => sort switch
    {
        // Id breaks ties, so two listings created in the same millisecond cannot swap places between
        // page 1 and page 2 and be shown twice or skipped.
        ProductSort.PriceAsc => products.OrderBy(x => x.Price).ThenBy(x => x.Id),
        ProductSort.PriceDesc => products.OrderByDescending(x => x.Price).ThenBy(x => x.Id),
        _ => products.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
    };

    /// <summary>
    /// Loads a product and establishes that the given user owns it.
    /// </summary>
    /// <returns>
    /// The product, or a failure code and message. The caller turns those into its own
    /// <see cref="OperationResult{T}"/>; the two-step shape is what lets one helper serve methods
    /// with different result types.
    /// </returns>
    private async Task<(Product? Product, string? Code, string? Message)> RequireOwnedAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (product is null)
        {
            return (null, ErrorCodes.NotFound, $"Product {id} was not found.");
        }

        if (product.SellerId != sellerId)
        {
            // 403 rather than a uniform 404. Telling the two apart confirms that an id exists, which
            // is information a stranger guessing ids would otherwise have to work for — but this is
            // a campus marketplace where the same is already true of every published listing, and a
            // clear "that is not yours" is worth more than the ambiguity.
            return (null, ErrorCodes.Forbidden, "You can only change your own listings.");
        }

        return (product, null, null);
    }

    /// <summary>
    /// Checks that the category and dormitory area exist and are active.
    /// </summary>
    private async Task<(string? ErrorCode, string? ErrorMessage)> ResolveTargetAsync(
        long categoryId,
        long dormitoryAreaId,
        CancellationToken cancellationToken)
    {
        var categoryExists = await dbContext.Categories
            .AnyAsync(x => x.Id == categoryId && x.Status == CategoryStatus.Active, cancellationToken);

        if (!categoryExists)
        {
            return (ErrorCodes.InvalidArgument, $"Category {categoryId} does not exist.");
        }

        var areaExists = await dbContext.DormitoryAreas
            .AnyAsync(x => x.Id == dormitoryAreaId && x.Status == DormitoryAreaStatus.Active, cancellationToken);

        return areaExists
            ? (null, null)
            : (ErrorCodes.InvalidArgument, $"Dormitory area {dormitoryAreaId} does not exist.");
    }

    /// <summary>
    /// Expands a category id to itself plus every active descendant.
    /// </summary>
    /// <remarks>
    /// The whole active dictionary is read once and walked in memory. Categories are a
    /// hand-maintained list of a few dozen rows, so this costs one cheap query and, unlike a
    /// fixed-depth self-join, keeps working if a fourth level is ever added.
    /// </remarks>
    private async Task<List<long>> ResolveCategoryAndDescendantsAsync(
        long categoryId,
        CancellationToken cancellationToken)
    {
        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(x => x.Status == CategoryStatus.Active)
            .Select(x => new { x.Id, x.ParentId })
            .ToListAsync(cancellationToken);

        var resolved = new List<long> { categoryId };
        var frontier = new List<long> { categoryId };

        while (frontier.Count > 0)
        {
            // Only ids not already resolved are carried forward. That is also what terminates the
            // walk: a parentId cycle would otherwise keep producing the same ids forever.
            var next = categories
                .Where(x => x.ParentId is long parent && frontier.Contains(parent))
                .Select(x => x.Id)
                .Where(id => !resolved.Contains(id))
                .ToList();

            resolved.AddRange(next);
            frontier = next;
        }

        return resolved;
    }

    /// <summary>
    /// Makes <c>%</c>, <c>_</c> and the escape character itself literal in a <c>LIKE</c> pattern.
    /// </summary>
    /// <remarks>
    /// The backslash is replaced first. Doing it after would escape the backslashes this method had
    /// just inserted, so a search for <c>100%</c> would become <c>%100\\\%%</c> and match
    /// everything — a wrong answer that looks like a working search.
    /// </remarks>
    private static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// Reads a stream, refusing anything over the limit without buffering the whole upload first.
    /// </summary>
    /// <returns>The buffered content, or null when the limit was exceeded.</returns>
    private static async Task<MemoryStream?> ReadWithLimitAsync(
        Stream content,
        int limit,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;

        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                await buffer.DisposeAsync();
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>Deletes image files, best effort, after their rows are already gone.</summary>
    private void DeleteFiles(IEnumerable<StoredImage> images)
    {
        foreach (var image in images)
        {
            imageStorage.Delete(image);
        }
    }

    /// <summary>Turns a blank description into null so the column does not hold empty strings.</summary>
    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Prefixes a storage key with the public path, or null when there is no key.</summary>
    private string? ToUrl(string? key) =>
        string.IsNullOrEmpty(key) ? null : _storage.PublicBasePath + "/" + key;

    private List<ProductSummaryResponse> WithThumbnailUrls(List<ProductSummaryResponse> items) =>
        items.Select(item => item with { ThumbnailUrl = ToUrl(item.ThumbnailUrl) }).ToList();

    private ProductDetailResponse WithImageUrls(ProductDetailResponse product) => product with
    {
        Images = product.Images
            .Select(image => image with
            {
                Url = ToUrl(image.Url)!,
                MediumUrl = ToUrl(image.MediumUrl)!,
                ThumbnailUrl = ToUrl(image.ThumbnailUrl)!
            })
            .ToList()
    };
}
