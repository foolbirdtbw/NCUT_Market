namespace NCUT_Market.Core.Enums;

/// <summary>
/// Personal notifications only. Site-wide bulletins are a separate concept — see the Announcement entity.
/// </summary>
public enum NotificationType : byte
{
    /// <summary>A draft listing was removed by the 7-day inactivity cleanup job.</summary>
    DraftDeleted = 1,

    ProductSold = 2,

    ProductOffline = 3
}
