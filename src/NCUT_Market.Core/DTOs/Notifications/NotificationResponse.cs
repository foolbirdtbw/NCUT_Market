using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Notifications;

/// <summary>
/// One notification, as shown in the list.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Type">What happened. The client uses it to pick a style, not to read the text.</param>
/// <param name="Title">Already-rendered headline, e.g. "交易已完成".</param>
/// <param name="Content">
/// Already-rendered body, with the listing's name baked in at creation time. It still reads correctly
/// after the listing is hard-deleted, which is the whole reason the text is stored rather than
/// composed on read.
/// </param>
/// <param name="RelatedProductId">
/// The listing, or null when there was none or it has since been deleted. Advisory only — the text
/// does not depend on it.
/// </param>
/// <param name="ProductTitle">
/// The listing's name, read live off the row so the client can gather one listing's notices under it.
/// Null exactly when <paramref name="RelatedProductId"/> is — that is, once the listing is gone — and
/// a null here is what tells the client this notice is not part of any group. Unlike
/// <paramref name="Title"/> and <paramref name="Content"/> there is nothing to freeze: it is the
/// grouping label, not part of what the notification says.
/// </param>
/// <param name="IsRead">Whether this has been opened.</param>
/// <param name="CreatedAt">Beijing time, no timezone suffix.</param>
/// <param name="ReadAt">When it was opened, or null while it has not been.</param>
public sealed record NotificationResponse(
    long Id,
    NotificationType Type,
    string Title,
    string Content,
    long? RelatedProductId,
    string? ProductTitle,
    bool IsRead,
    DateTime CreatedAt,
    DateTime? ReadAt);
