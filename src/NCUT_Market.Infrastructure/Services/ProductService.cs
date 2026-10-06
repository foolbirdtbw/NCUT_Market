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
        // InTransaction belongs here as much as Published does. A listing with a trade agreed in
        // progress stays in the feed on purpose: the other people already talking about it must not
        // watch it vanish, and a badge says what it is. Deliberately only these two — Sold stays out,
        // as it always has.
        var products = dbContext.Products
            .AsNoTracking()
            .Where(x => x.Status == ProductStatus.Published || x.Status == ProductStatus.InTransaction);

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = LikePattern.Contains(query.Q.Trim());

            // Title or description, so a search for a colour or a brand mentioned only in the body
            // still finds the listing. Case is handled by the column collation
            // (utf8mb4_0900_ai_ci, case-insensitive), not here.
            products = products.Where(x =>
                EF.Functions.Like(x.Title, pattern, LikePattern.Escape) ||
                (x.Description != null && EF.Functions.Like(x.Description, pattern, LikePattern.Escape)));
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
            .Select(DetailProjection(AppDbContext.AuditNow.AddDays(-InterestedWindowDays)))
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个商品。");
        }

        // A listing that is not live is not merely hidden from the feed, it is invisible: reported
        // as not-found rather than forbidden, so guessing an id does not confirm that a draft exists.
        //
        // InTransaction is in the visible set for the same reason it is in the feed filter. The whole
        // point of the state is that the listing does not disappear from under the people discussing
        // it, and that only works if the page they are looking at still opens.
        var isVisible = product.Status is ProductStatus.Published
            or ProductStatus.Sold
            or ProductStatus.InTransaction;

        if (!isVisible && product.SellerId != viewerId)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个商品。");
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
                "已卖出的商品不能再上架。");
        }

        var hasImage = await dbContext.ProductImages
            .AnyAsync(x => x.ProductId == id, cancellationToken);

        if (!hasImage)
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidArgument,
                "上架前至少要传一张照片。");
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
                "只有在售中的商品才能下架。");
        }

        if (await HasPendingProposalAsync(id, cancellationToken))
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidState,
                "有人正在跟你谈这个商品的交易，先处理它再下架。");
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
                "只有在售中的商品才能标记为已售出。");
        }

        // The hand-sold shortcut stays, for a deal closed in person with no thread behind it. It must
        // not, however, run over an offer somebody is waiting on an answer to: that would sell the
        // listing out from under a negotiation the platform is in the middle of brokering, and leave
        // a proposal pointing at an item that is gone.
        if (await HasPendingProposalAsync(id, cancellationToken))
        {
            return OperationResult<ProductDetailResponse>.Failure(
                ErrorCodes.InvalidState,
                "有人正在跟你谈这个商品的交易，先去私信里处理它。");
        }

        // Deliberately leaves every Transaction* column alone. Status == Sold with a null
        // TransactionBuyerId is exactly what "sold to nobody in particular" means, and it is what
        // tells the thread view not to draw a trade panel over this listing.
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
                "请先下架，再删除。");
        }

        // Reachable even though the status is Draft or Offline: a listing can be taken down while an
        // offer is still sitting unanswered in somebody's thread, and deleting it would leave that
        // proposal addressing a product that no longer exists.
        if (await HasPendingProposalAsync(id, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "有人正在跟你谈这个商品的交易，先去私信里处理它。");
        }

        var keys = await dbContext.ProductImages
            .Where(x => x.ProductId == id)
            .Select(x => new StoredImage(x.LargeKey, x.MediumKey, x.ThumbnailKey, 0, 0, 0, string.Empty))
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
                $"一个商品最多放 {MaxImagesPerProduct} 张照片。");
        }

        var buffered = await ReadWithLimitAsync(content, MaxImageBytes, cancellationToken);

        if (buffered is null)
        {
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.InvalidArgument,
                $"每张照片不能超过 {MaxImageBytes / (1024 * 1024)} MB。");
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
                "这个文件不是有效的图片。");
        }

        if (stored is null)
        {
            return OperationResult<ProductImageResponse>.Failure(
                ErrorCodes.UnsupportedMediaType,
                "只支持 JPEG、PNG 和 WebP 格式的图片。");
        }

        var image = new ProductImage
        {
            ProductId = productId,
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
                "找不到这张照片。");
        }

        var stored = new StoredImage(
            image.LargeKey, image.MediumKey, image.ThumbnailKey,
            image.Width, image.Height, image.FileSize, image.MimeType);

        dbContext.ProductImages.Remove(image);
        product.LastActivityAt = AppDbContext.AuditNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        DeleteFiles([stored]);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>How far back the "recently asked about" count on the detail page looks.</summary>
    private const int InterestedWindowDays = 7;

    /// <summary>
    /// The detail projection, including the photo list.
    /// </summary>
    /// <param name="interestedSince">
    /// Cutoff for the "recently asked about" count, supplied by the caller because the projection is
    /// an expression tree and cannot read a clock — the same reason
    /// <c>ConversationService.Summaries</c> closes over the caller's id.
    /// </param>
    /// <remarks>
    /// The two counts are a correlated <c>COUNT</c> each, and they are the first in this file — the
    /// projections so far have only ever selected scalars or first-of-a-collection. That is fine
    /// against the unique index on <c>(product_id, buyer_id)</c> and only ever runs for a single row.
    /// </remarks>
    private static Expression<Func<Product, ProductDetailResponse>> DetailProjection(
        DateTime interestedSince) =>
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
            product.SoldAt,

            // One thread per prospective buyer — StartAsync is find-or-create against a unique index
            // on (product_id, buyer_id) — so this is a count of distinct people, which is what the
            // page says it is.
            product.Conversations.Count(),

            // LastMessageAt rather than a scan of messages: the column is written when the thread is
            // created and on every message, so it already answers "has anyone said anything here
            // lately" without touching the messages table.
            product.Conversations.Count(conversation => conversation.LastMessageAt >= interestedSince));

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
            return (null, ErrorCodes.NotFound, "找不到这个商品。");
        }

        if (product.SellerId != sellerId)
        {
            // 403 rather than a uniform 404. Telling the two apart confirms that an id exists, which
            // is information a stranger guessing ids would otherwise have to work for — but this is
            // a campus marketplace where the same is already true of every published listing, and a
            // clear "that is not yours" is worth more than the ambiguity.
            return (null, ErrorCodes.Forbidden, "只能修改自己发布的商品。");
        }

        return (product, null, null);
    }

    /// <summary>
    /// Whether anybody is waiting on the seller to answer a trade proposal on this listing.
    /// </summary>
    /// <remarks>
    /// A plain existence check over the unique index on <c>(product_id, buyer_id)</c>. It is not part
    /// of the same transaction as the write it guards, so a proposal landing in the gap would slip
    /// past — the window is milliseconds wide, a proposal cannot be made on a listing that is already
    /// down, and the worst outcome is a stale proposal that expires on its own within the day.
    /// </remarks>
    private async Task<bool> HasPendingProposalAsync(long productId, CancellationToken cancellationToken) =>
        await dbContext.Conversations
            .AnyAsync(x => x.ProductId == productId && x.TransactionProposedAt != null, cancellationToken);

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
            return (ErrorCodes.InvalidArgument, "所选分类不存在。");
        }

        var areaExists = await dbContext.DormitoryAreas
            .AnyAsync(x => x.Id == dormitoryAreaId && x.Status == DormitoryAreaStatus.Active, cancellationToken);

        return areaExists
            ? (null, null)
            : (ErrorCodes.InvalidArgument, "所选宿舍区不存在。");
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
