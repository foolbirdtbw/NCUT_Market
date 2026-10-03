using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Announcements;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Site-wide announcements. Reading is public; writing is admin-only.
/// </summary>
/// <remarks>
/// The admin check lives in the implementation and reads <c>users.role</c> from the database on every
/// call, rather than trusting a claim in the caller's token. That means a promotion applied with a
/// hand-run UPDATE takes effect on the account's next request, with no need to sign in again — see
/// <see cref="Enums.UserRole"/>.
/// </remarks>
public interface IAnnouncementService
{
    /// <summary>
    /// One page of the announcements that are live right now, newest first.
    /// </summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// "Live" is evaluated against the application clock, not the database's. Every timestamp column
    /// holds Beijing wall-clock time while the MySQL instance runs on UTC, so a comparison against
    /// <c>NOW()</c> would be eight hours out. See <c>AnnouncementConfiguration</c>.
    /// </remarks>
    Task<PagedResult<AnnouncementResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an announcement immediately.
    /// </summary>
    /// <param name="userId">The signed-in user, who must be an active admin.</param>
    /// <param name="request">The announcement.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.Forbidden"/> when the caller is not an admin, and with
    /// <see cref="ErrorCodes.ValidationError"/> when <c>ExpiredAt</c> is not in the future.
    /// </returns>
    Task<OperationResult<AnnouncementResponse>> PublishAsync(
        long userId,
        CreateAnnouncementRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an announcement outright.
    /// </summary>
    /// <param name="id">Primary key.</param>
    /// <param name="userId">The signed-in user, who must be an active admin.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>Fails with <see cref="ErrorCodes.Forbidden"/> when the caller is not an admin.</returns>
    /// <remarks>
    /// A hard delete, not a move to <see cref="Enums.AnnouncementStatus.Expired"/>: the only way to
    /// retract a typo is to remove it, and there is no audit requirement here that a status column
    /// would serve.
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);
}
