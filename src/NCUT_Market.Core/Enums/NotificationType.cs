namespace NCUT_Market.Core.Enums;

/// <summary>
/// Personal notifications only. Site-wide bulletins are a separate concept — see the Announcement entity.
/// </summary>
public enum NotificationType : byte
{
    /// <summary>A draft listing was removed by the 7-day inactivity cleanup job.</summary>
    DraftDeleted = 1,

    /// <summary>
    /// A listing changed hands. Raised both by the seller's "标记已售出" shortcut and by the trade
    /// flow once both sides have confirmed.
    /// </summary>
    ProductSold = 2,

    ProductOffline = 3,

    /// <summary>The other party accepted a trade proposal sent from the thread.</summary>
    TransactionAccepted = 4,

    /// <summary>
    /// The other side confirmed and this side has not, so the trade is waiting on them. Sent by the
    /// trade itself and again by the sweep as the deadline approaches.
    /// </summary>
    TransactionConfirmationNeeded = 5,

    /// <summary>
    /// A trade proposal expired, or a trade nobody confirmed rolled back to for sale.
    /// </summary>
    TransactionCancelled = 6
}
