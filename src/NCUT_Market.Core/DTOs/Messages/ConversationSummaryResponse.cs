namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// One row of "my messages".
/// </summary>
/// <param name="Id">Primary key, used to open the thread.</param>
/// <param name="ProductId">
/// The listing the thread is about, as the raw foreign key — <b>not</b> nulled when the listing has
/// gone from view, unlike <see cref="ConversationDetailResponse.ProductId"/>. Nothing in the list
/// view reads it (the row links to the thread, not to the listing, and the title is plain text), so
/// it is kept as-is rather than paying for a join on every page. Do not build a link out of it: it
/// can name a listing whose page is a 404.
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
/// <param name="UnreadCount">
/// How many messages the other side has sent since the caller last read this thread. A count rather
/// than a flag because the row has room for one and "3 waiting" reads better than a dot. The header
/// badge counts <em>threads</em> — see <see cref="UnreadCountResponse"/> — and the two are deliberately
/// different numbers.
/// </param>
public sealed record ConversationSummaryResponse(
    long Id,
    long? ProductId,
    string ProductTitle,
    string? ProductThumbnailUrl,
    long PeerId,
    string PeerNickname,
    string? LastMessagePreview,
    DateTime LastMessageAt,
    int UnreadCount);
