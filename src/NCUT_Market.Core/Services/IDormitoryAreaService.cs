using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;

namespace NCUT_Market.Core.Services;

/// <summary>Read access to the dormitory-area dictionary.</summary>
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
}
