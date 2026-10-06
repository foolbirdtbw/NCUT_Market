using NCUT_Market.Core.Common;

namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// A thread and one page of its messages.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="ProductId">The listing, or null once it has been deleted.</param>
/// <param name="ProductTitle">Frozen at creation.</param>
/// <param name="ProductThumbnailUrl">Null when the listing had no photo or the file is gone.</param>
/// <param name="PeerId">The other party.</param>
/// <param name="PeerNickname">Display name of the other party.</param>
/// <param name="Messages">
/// The <em>most recent</em> page of the thread, in chronological order — oldest first within the
/// page, so the frontend can append to the bottom without reversing anything.
/// </param>
/// <param name="Trade">
/// The trade on the listing, or null once the listing has been hard-deleted. Never null otherwise,
/// even when no proposal exists — the status is what tells a client whether it may propose at all.
/// </param>
/// <remarks>
/// <para>
/// The paging is over descending ids and then reversed, not over ascending ids. Taking
/// <c>Skip</c>/<c>Take</c> of an ascending order hands back the <em>first</em> page of a chat, which
/// is the wrong end of it: opening a conversation should show what was just said, not what was said
/// when the thread started.
/// </para>
/// <para>
/// The paging metadata therefore describes a window counted from the newest message, so
/// <c>page 2</c> is the twenty messages before the newest twenty. A client paging forward walks
/// backwards through the history.
/// </para>
/// </remarks>
public sealed record ConversationDetailResponse(
    long Id,
    long? ProductId,
    string ProductTitle,
    string? ProductThumbnailUrl,
    long PeerId,
    string PeerNickname,
    PagedResult<MessageResponse> Messages,
    ConversationTradeResponse? Trade);
