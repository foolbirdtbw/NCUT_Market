using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Products;

/// <summary>
/// A listing in full, including everything the edit form needs to prefill.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="SellerId">Who listed it. A client compares this against its own id to decide
/// whether to offer publish/offline/sold/delete.</param>
/// <param name="SellerNickname">Display name of the seller.</param>
/// <param name="Title">Listing title.</param>
/// <param name="Description">Free text, or null.</param>
/// <param name="Price">In yuan.</param>
/// <param name="Condition">1..4 on the wire, per <see cref="ProductCondition"/>.</param>
/// <param name="Status">1..4 on the wire, per <see cref="ProductStatus"/>.</param>
/// <param name="CategoryId">Current category. Present so the edit form can select it.</param>
/// <param name="CategoryName">Denormalised for display.</param>
/// <param name="DormitoryAreaId">Current area. Present so the edit form can select it.</param>
/// <param name="DormitoryAreaName">Denormalised for display.</param>
/// <param name="Images">Ordered by sort order. Empty for a listing with no photos.</param>
/// <param name="CreatedAt">Beijing time, no timezone suffix.</param>
/// <param name="PublishedAt">When it first went live. Null while it has never been published.</param>
/// <param name="SoldAt">When it was marked sold. Null otherwise.</param>
public sealed record ProductDetailResponse(
    long Id,
    long SellerId,
    string SellerNickname,
    string Title,
    string? Description,
    decimal Price,
    ProductCondition Condition,
    ProductStatus Status,
    long CategoryId,
    string CategoryName,
    long DormitoryAreaId,
    string DormitoryAreaName,
    IReadOnlyList<ProductImageResponse> Images,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? SoldAt);
