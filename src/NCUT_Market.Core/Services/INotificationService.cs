using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.DTOs.Notifications;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Reading and acknowledging personal notifications, for the page behind the header badge.
/// </summary>
/// <remarks>
/// <para>
/// Read-only plus "mark this one read". Nothing here creates a notification: they are written by
/// whoever changes the state that warrants one, in the same <c>SaveChangesAsync</c> as the change
/// itself, so a notification cannot describe something that did not happen. Adding a
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
}
