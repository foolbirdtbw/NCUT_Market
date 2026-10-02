using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Products;

/// <summary>
/// A listing as it appears in a list or a card.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Title">Listing title.</param>
/// <param name="Price">In yuan.</param>
/// <param name="Condition">
/// Carried as the enum type. It reaches the wire as a number either way, because no
/// <c>JsonStringEnumConverter</c> is registered and adding one would change every enum in the API
/// at once — but the type keeps the OpenAPI document readable and stops a client author from having
/// to guess what 3 means. A test asserts the wire stays numeric.
/// </param>
/// <param name="Status">
/// Lifecycle state, 1..4 per <see cref="ProductStatus"/>. Always
/// <see cref="ProductStatus.Published"/> in the public feed, which filters to it — it earns its place
/// on the seller's own list, where drafts, offlined and sold listings sit side by side and a card
/// that did not say which was which would be unusable.
/// </param>
/// <param name="CategoryName">Denormalised for display, so a card needs no second lookup.</param>
/// <param name="DormitoryAreaName">Denormalised for display.</param>
/// <param name="ThumbnailUrl">Square thumbnail. Null when the listing has no photo yet.</param>
/// <param name="CreatedAt">Beijing time, no timezone suffix.</param>
/// <param name="SellerNickname">Display name of the seller, not their login name.</param>
public sealed record ProductSummaryResponse(
    long Id,
    string Title,
    decimal Price,
    ProductCondition Condition,
    ProductStatus Status,
    string CategoryName,
    string DormitoryAreaName,
    string? ThumbnailUrl,
    DateTime CreatedAt,
    string SellerNickname);
