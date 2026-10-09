using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.DTOs.Notifications;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Reading and acknowledging personal notifications, for the page behind the header badge.
/// </summary>
/// <remarks>
/// <para>
/// Reading, acknowledging and clearing out personal notifications. Nothing here creates one: they are
/// written by whoever changes the state that warrants one, in the same <c>SaveChangesAsync</c> as the
/// change itself, so a notification cannot describe something that did not happen. Adding a
/// <c>CreateAsync</c> to this interface would invite callers to split the two apart.
/// </para>
/// <para>
/// Like <see cref="IConversationService"/>, ownership is enforced here rather than by the caller: a
/// notification addressed to somebody else is <see cref="ErrorCodes.NotFound"/>, not
/// <see cref="ErrorCodes.Forbidden"/>.
/// </para>
/// </remarks>
public interface INotificationService
{
    /// <summary>One page of the caller's notifications, newest first.</summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<PagedResult<NotificationResponse>> ListAsync(
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many notifications the caller has not opened — the number behind the header badge.
    /// </summary>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// A per-row count here, unlike <see cref="IConversationService.GetUnreadCountAsync"/>, which
    /// counts threads. Each notification is one independent thing to deal with, so the two numbers
    /// mean the same kind of thing to a reader even though they are counted differently.
    /// </remarks>
    Task<OperationResult<UnreadCountResponse>> GetUnreadCountAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks one notification read.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be the recipient.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// Idempotent: marking an already-read notification read again is a no-op that succeeds, because
    /// it is what a client reopening a page will do.
    /// </remarks>
    Task<OperationResult<bool>> MarkReadAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Drops one of the caller's own notifications.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be the recipient.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// A genuine delete, unlike <see cref="IConversationService.DeleteAsync"/>: a notification belongs
    /// to the one person it is addressed to and nothing else references it.
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every notification the caller holds about one listing — the whole group the frontend
    /// renders together.
    /// </summary>
    /// <param name="productId">The listing whose notices go.</param>
    /// <param name="userId">The signed-in user, whose rows these are.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// Idempotent, unlike <see cref="DeleteAsync"/>: the scope is the caller's own rows to begin with,
    /// so matching none of them is an empty group rather than a wrong id. A client clicking delete
    /// twice, or on a page that has gone stale, gets a success either way.
    /// </remarks>
    Task<OperationResult<bool>> DeleteByProductAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken = default);
}
