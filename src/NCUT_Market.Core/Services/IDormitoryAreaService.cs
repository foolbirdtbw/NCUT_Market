using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Dormitory-area dictionary: public reads, administrator-only writes.
/// </summary>
public interface IDormitoryAreaService
{
    /// <summary>
    /// One page of active dormitory areas, ordered by <c>sort_order</c> then <c>id</c>.
    /// </summary>
    Task<PagedResult<DormitoryAreaResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A single active dormitory area, or a failed result carrying
    /// <see cref="ErrorCodes.NotFound"/>.
    /// </summary>
    /// <remarks>
    /// "Not found" covers both a missing row and a deactivated one: the buyer-facing catalogue must
    /// not reveal that a disabled area exists. Returns a result rather than throwing because a bad id
    /// is an expected outcome of a public URL, not an exceptional one.
    /// </remarks>
    Task<OperationResult<DormitoryAreaResponse>> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a dormitory area.
    /// </summary>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="request">The area to add.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The new area, <see cref="ErrorCodes.Forbidden"/> for a non-admin, or
    /// <see cref="ErrorCodes.Conflict"/> when the name is taken.
    /// </returns>
    Task<OperationResult<DormitoryAreaResponse>> CreateAsync(
        long userId,
        CreateDormitoryAreaRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites a dormitory area in place.
    /// </summary>
    /// <param name="id">The area to rewrite.</param>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The area as saved, <see cref="ErrorCodes.NotFound"/> for an unknown id, or
    /// <see cref="ErrorCodes.Conflict"/> when the new name belongs to a different area.
    /// </returns>
    Task<OperationResult<DormitoryAreaResponse>> UpdateAsync(
        long id,
        long userId,
        UpdateDormitoryAreaRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a dormitory area outright.
    /// </summary>
    /// <param name="id">The area to remove.</param>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// <see cref="ErrorCodes.NotFound"/> for an unknown id, or <see cref="ErrorCodes.InvalidState"/>
    /// when listings still point at it.
    /// </returns>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);
}
