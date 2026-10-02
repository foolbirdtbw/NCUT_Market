using NCUT_Market.Core.Common;

namespace NCUT_Market.Core.DTOs.Products;

/// <summary>
/// Filters for the public listing, as they arrive from the query string.
/// </summary>
/// <remarks>
/// <para>
/// A separate record from <see cref="PaginationQuery"/> rather than a subclass, because
/// <see cref="PaginationQuery"/> is sealed. The controller binds both as
/// <c>[FromQuery]</c> parameters, which keeps page and page size shared with every other list
/// endpoint instead of forking them here.
/// </para>
/// <para>
/// Every filter is optional; an absent one does not constrain the query. Settable init properties
/// with no positional constructor is the shape MVC's query-string binder needs — the same reason
/// <see cref="PaginationQuery"/> is not a positional record.
/// </para>
/// </remarks>
public sealed record ProductListQuery
{
    /// <summary>Keyword matched against title and description. Null or blank means no keyword.</summary>
    public string? Q { get; init; }

    /// <summary>
    /// Category to filter by. Matches the category itself and all of its descendants, so picking a
    /// top-level category does not return an empty page while its children hold every listing.
    /// </summary>
    public long? CategoryId { get; init; }

    /// <summary>Dormitory area to filter by.</summary>
    public long? AreaId { get; init; }

    /// <summary>Condition to filter by, 1..4. An out-of-range value matches nothing.</summary>
    public int? Condition { get; init; }

    /// <summary>Inclusive lower price bound, in yuan.</summary>
    public decimal? MinPrice { get; init; }

    /// <summary>Inclusive upper price bound, in yuan.</summary>
    public decimal? MaxPrice { get; init; }

    /// <summary>
    /// Result ordering. Bound from the query string by name (<c>?sort=PriceAsc</c>) or by number;
    /// MVC accepts both.
    /// </summary>
    public ProductSort Sort { get; init; } = ProductSort.Newest;
}
