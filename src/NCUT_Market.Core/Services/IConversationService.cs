using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Private threads between a buyer and a seller, one per listing per buyer.
/// </summary>
/// <remarks>
/// Membership is enforced here rather than by the caller: a thread is visible to its two participants
/// and to nobody else, and every method that takes a thread id re-checks that against the database.
/// A non-participant gets <see cref="ErrorCodes.NotFound"/>, not <see cref="ErrorCodes.Forbidden"/> —
/// unlike a listing, a thread's existence is never public, so a 403 would confirm that it is there.
/// </remarks>
public interface IConversationService
{
    /// <summary>
    /// One page of the caller's threads, most recently active first.
    /// </summary>
    /// <param name="userId">The signed-in user, on either side of the thread.</param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<PagedResult<ConversationSummaryResponse>> ListAsync(
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many of the caller's threads hold an unread message.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// A <em>thread</em> count, not a message count — see <see cref="UnreadCountResponse"/>.
    /// </remarks>
    Task<OperationResult<UnreadCountResponse>> GetUnreadCountAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a thread about a listing, or returns the existing one.
    /// </summary>
    /// <param name="buyerId">The signed-in user, who becomes the buyer.</param>
    /// <param name="request">The listing to talk about.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidArgument"/> when the caller owns the listing — there is
    /// nobody to talk to — and with <see cref="ErrorCodes.NotFound"/> when the listing is not visible
    /// to them, which includes another user's draft.
    /// </returns>
    /// <remarks>
    /// Idempotent. Two rapid clicks can still race past the existence check and collide on the unique
    /// index; like <c>AuthService.RegisterAsync</c> the race is acknowledged rather than guarded, and
    /// surfaces as a conflict rather than a duplicate row.
    /// </remarks>
    Task<OperationResult<ConversationSummaryResponse>> StartAsync(
        long buyerId,
        StartConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A thread and the most recent page of its messages, oldest first.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be a participant.</param>
    /// <param name="pagination">Page and page size, counted back from the newest message.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<OperationResult<ConversationDetailResponse>> GetAsync(
        long id,
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a message into a thread.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="senderId">The signed-in user, who must be a participant.</param>
    /// <param name="request">The text.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.ValidationError"/> when the text is blank.</returns>
    Task<OperationResult<MessageResponse>> SendAsync(
        long id,
        long senderId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks everything in the thread read for the calling side.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be a participant.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// A separate call rather than a side effect of <see cref="GetAsync"/>, because a read endpoint
    /// that writes is one prefetch away from marking things read that nobody looked at.
    /// </remarks>
    Task<OperationResult<bool>> MarkReadAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a thread out of the caller's own list. The other party keeps theirs.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be a participant.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidState"/> while a trade on this thread is still running.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Not a delete. The row, its messages and its trade panel all stay; what changes is one column
    /// saying that this side no longer wants it in their list. A thread is one row shared by two
    /// people, so removing it for one of them cannot be allowed to remove it for the other.
    /// </para>
    /// <para>
    /// Reversible in one direction only, on purpose: the next message either party writes clears both
    /// sides' markers, so the thread reappears for both. See <c>Conversation.BuyerDeletedAt</c>.
    /// </para>
    /// <para>
    /// A running trade is refused because the confirm-receipt and confirm-payment buttons exist nowhere
    /// but the thread, so hiding it would leave whoever still owes a confirmation with no way to give
    /// it. Nothing blocks a <em>proposal</em>: the sweep drops one within a day, and any message
    /// revives the thread anyway.
    /// </para>
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);
}
