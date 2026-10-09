using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Feedbacks;

namespace NCUT_Market.Core.Services;

/// <summary>
/// The public feedback board. Reading is anonymous, posting and voting need an account, and moving a
/// post's status or removing one is admin-only.
/// </summary>
/// <remarks>
/// The admin check lives in the implementation and reads <c>users.role</c> from the database on every
/// call rather than trusting a claim in the caller's token, so a promotion applied with a hand-run
/// UPDATE takes effect on the account's next request — the same call
/// <see cref="IAnnouncementService"/> makes, and for the same reason.
/// </remarks>
public interface IFeedbackService
{
    /// <summary>
    /// One page of the board, most voted first.
    /// </summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="viewerId">
    /// The caller, or null when nobody is signed in. Used for one field only — which rows they have
    /// already voted for — so an anonymous reader sees every count and no lit buttons.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// Ties are broken by <c>created_at</c> and then <c>id</c>, so the order is total: two posts made
    /// in the same millisecond still have one of them first, every time.
    /// </remarks>
    Task<PagedResult<FeedbackResponse>> ListAsync(
        PaginationQuery pagination,
        long? viewerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a piece of feedback, immediately visible.
    /// </summary>
    /// <param name="userId">The signed-in user. Any account may post; no role is required.</param>
    /// <param name="request">The post.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.ValidationError"/> for a <c>Kind</c> outside the enum.</returns>
    Task<OperationResult<FeedbackResponse>> CreateAsync(
        long userId,
        CreateFeedbackRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the caller's vote on this post on, or off if it was already on.
    /// </summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="userId">The signed-in user. Voting for your own post is allowed.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The count as it stands after the change. Fails with <see cref="ErrorCodes.NotFound"/> when the
    /// post does not exist; there is no state in which a vote is refused.
    /// </returns>
    /// <remarks>
    /// One endpoint for both directions rather than a POST/DELETE pair: the button has exactly one
    /// action, and the client already knows which way it is going.
    /// </remarks>
    Task<OperationResult<FeedbackVoteResponse>> ToggleVoteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a post to a different status.
    /// </summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="userId">The signed-in user, who must be an active admin.</param>
    /// <param name="request">The new status.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.Forbidden"/> when the caller is not an admin,
    /// <see cref="ErrorCodes.ValidationError"/> for a status outside the enum, and
    /// <see cref="ErrorCodes.NotFound"/> when the post does not exist.
    /// </returns>
    Task<OperationResult<FeedbackResponse>> SetStatusAsync(
        long id,
        long userId,
        UpdateFeedbackStatusRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a post and every vote on it.
    /// </summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="userId">The signed-in user, who must be an active admin.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.Forbidden"/> when the caller is not an admin.</returns>
    /// <remarks>
    /// Admin-only, and that includes the post's own author: a board anybody can quietly withdraw from
    /// is one where the vote counts stop meaning anything. A hard delete rather than a hidden flag,
    /// because the votes have to go somewhere and cascading them is the whole answer.
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);
}
