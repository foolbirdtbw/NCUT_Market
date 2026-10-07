using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;

namespace NCUT_Market.Core.Services;

/// <summary>
/// The product lifecycle: listing, creating, editing, the status transitions, and photo management.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation takes the acting user's id and enforces ownership itself, rather than leaving the
/// check to the caller. A service that trusts its caller to have authorised the request is one
/// re-used endpoint away from letting anyone edit anyone's listing.
/// </para>
/// <para>
/// Image bytes arrive as a <see cref="Stream"/> rather than an <c>IFormFile</c>, because this
/// assembly takes no dependencies and <c>IFormFile</c> lives in the web framework. The controller
/// does the conversion.
/// </para>
/// </remarks>
public interface IProductService
{
    /// <summary>
    /// One page of the public listing: published items only, filtered and ordered.
    /// </summary>
    /// <param name="query">Filters. Every field is optional.</param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<PagedResult<ProductSummaryResponse>> ListAsync(
        ProductListQuery query,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the caller's own listings, in every status including drafts and sold items.
    /// </summary>
    /// <param name="sellerId">The signed-in user.</param>
    /// <param name="pagination">Page and page size.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<PagedResult<ProductSummaryResponse>> ListMineAsync(
        long sellerId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One listing in full.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="viewerId">
    /// The signed-in user, or null when the request is anonymous. Used only to decide whether a
    /// draft or offlined listing is visible: those are 404 to everyone but their seller, so the
    /// listing's existence is not revealed to a stranger who guesses an id.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<OperationResult<ProductDetailResponse>> GetByIdAsync(
        long id,
        long? viewerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a listing as a draft.
    /// </summary>
    /// <param name="sellerId">The signed-in user, who becomes the seller.</param>
    /// <param name="request">The listing's details.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.InvalidArgument"/> when the category or dormitory
    /// area does not exist or is not active.</returns>
    Task<OperationResult<ProductDetailResponse>> CreateAsync(
        long sellerId,
        CreateProductRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a listing's editable fields. Allowed in any status.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.Forbidden"/> when someone else owns the listing.</returns>
    Task<OperationResult<ProductDetailResponse>> UpdateAsync(
        long id,
        long sellerId,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a listing to <see cref="Enums.ProductStatus.Published"/>, from either draft or offline.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidState"/> when the listing is already sold or already
    /// published, and with <see cref="ErrorCodes.InvalidArgument"/> when it has no photo — a
    /// marketplace listing without one is not something a buyer can act on.
    /// </returns>
    Task<OperationResult<ProductDetailResponse>> PublishAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a published listing down without deleting it.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.InvalidState"/> unless the listing is published.</returns>
    Task<OperationResult<ProductDetailResponse>> OfflineAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a published listing as sold, which removes it from the public listing.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.InvalidState"/> unless the listing is published.</returns>
    Task<OperationResult<ProductDetailResponse>> MarkSoldAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a listing off the caller's hands, along with its photos where nothing else needs them.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidState"/> unless the listing is a draft, offlined or
    /// sold — a published one has to be taken down first, so deleting is never one click away from a
    /// live page.
    /// </returns>
    /// <remarks>
    /// Two very different things happen here, and the difference is whether the listing sold through
    /// the platform (which is what <c>TransactionBuyerId</c> records). A sale that went through it
    /// left a trade record, and that record is not a copy of anything — it <i>is</i> the transaction
    /// columns on this row — so the row is only marked (<c>Product.DeletedAt</c>) and the trade panel
    /// stays readable in both parties' threads. Everything else is removed outright.
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long sellerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one photo and attaches it to a listing.
    /// </summary>
    /// <param name="productId">The listing to attach it to.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="content">The uploaded bytes.</param>
    /// <param name="fileName">The client's file name. Used only to derive a display-adjacent
    /// extension after the real format has been established from the bytes.</param>
    /// <param name="contentType">The declared MIME type. Treated as a hint only — the actual format
    /// is determined by decoding.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.UnsupportedMediaType"/> when the bytes are not a supported
    /// image, <see cref="ErrorCodes.InvalidArgument"/> when the file is too large or the listing
    /// already holds the maximum number of photos, and <see cref="ErrorCodes.Forbidden"/> when
    /// someone else owns the listing.
    /// </returns>
    Task<OperationResult<ProductImageResponse>> AddImageAsync(
        long productId,
        long sellerId,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one photo from a listing and deletes its files.
    /// </summary>
    /// <param name="productId">The listing it belongs to.</param>
    /// <param name="sellerId">The signed-in user, who must be the seller.</param>
    /// <param name="imageId">The photo to remove.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<OperationResult<bool>> DeleteImageAsync(
        long productId,
        long sellerId,
        long imageId,
        CancellationToken cancellationToken = default);
}
