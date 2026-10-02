namespace NCUT_Market.Core.Common;

/// <summary>
/// Ordering for the public product listing.
/// </summary>
/// <remarks>
/// Only <see cref="Newest"/> is backed by an index (<c>idx_products_status_created_at</c>). The two
/// price orders are a filesort over the filtered set — fine at campus scale, and worth knowing
/// before someone sorts a table of tens of thousands of rows.
/// </remarks>
public enum ProductSort
{
    /// <summary>Newest listings first. The default.</summary>
    Newest = 1,

    /// <summary>Cheapest first.</summary>
    PriceAsc = 2,

    /// <summary>Most expensive first.</summary>
    PriceDesc = 3
}
