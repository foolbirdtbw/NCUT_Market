namespace NCUT_Market.Core.Enums;

/// <summary>
/// Listing lifecycle. Draft listings that see no activity for 7 days are hard-deleted by the
/// draft cleanup job, so there is deliberately no "Deleted" member here.
/// </summary>
public enum ProductStatus : byte
{
    Draft = 1,
    Published = 2,
    Sold = 3,
    Offline = 4
}
