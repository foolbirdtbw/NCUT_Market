namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// One row of "my messages".
/// </summary>
/// <param name="Id">Primary key, used to open the thread.</param>
/// <param name="ProductId">
/// The listing the thread is about, or null once it has been hard-deleted. The frontend uses null to
/// decide whether the title is still a link.
/// </param>
/// <param name="ProductTitle">
/// Frozen at the moment the thread was created, so a deleted listing still reads correctly.
/// </param>
/// <param name="ProductThumbnailUrl">Square thumbnail of the listing as it was, or null if it had no
/// photo or the file is gone.</param>
/// <param name="PeerId">The other party — the seller if the caller is the buyer, and vice versa.</param>
/// <param name="PeerNickname">Display name of <paramref name="PeerId"/>.</param>
/// <param name="LastMessagePreview">The most recent message, truncated for a list row. Null when
/// nobody has written yet, which is the state right after "联系卖家" is clicked.</param>
/// <param name="LastMessageAt">Beijing time. Drives the row's timestamp and the list's ordering.</param>
/// <param name="HasUnread">Whether the other side has written since the caller last read this thread.</param>
public sealed record ConversationSummaryResponse(
    long Id,
    long? ProductId,
    string ProductTitle,
    string? ProductThumbnailUrl,
    long PeerId,
    string PeerNickname,
    string? LastMessagePreview,
    DateTime LastMessageAt,
    bool HasUnread);
