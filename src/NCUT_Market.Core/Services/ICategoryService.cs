using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Category dictionary: public reads, administrator-only writes.
/// </summary>
public interface ICategoryService
{
    /// <summary>
    /// One page of active categories, ordered by <c>sort_order</c> then <c>id</c>.
    /// </summary>
    /// <remarks>
    /// Flat, not a tree. Paginating a tree would mean paging root nodes, which is not what a client
    /// filling a category picker wants; callers rebuild the three levels from <c>parentId</c>.
    /// </remarks>
    Task<PagedResult<CategoryResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a category.
    /// </summary>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="request">The category to add.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The new category, <see cref="ErrorCodes.Forbidden"/> for a non-admin,
    /// <see cref="ErrorCodes.InvalidArgument"/> for a parent that does not exist, or
    /// <see cref="ErrorCodes.Conflict"/> when a sibling already has that name.
    /// </returns>
    Task<OperationResult<CategoryResponse>> CreateAsync(
        long userId,
        CreateCategoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites a category in place.
    /// </summary>
    /// <param name="id">The category to rewrite.</param>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The category as saved, <see cref="ErrorCodes.NotFound"/> for an unknown id, or the same
    /// failures as <see cref="CreateAsync"/>. A parent that is this category or one of its own
    /// descendants is refused with <see cref="ErrorCodes.InvalidArgument"/>.
    /// </returns>
    Task<OperationResult<CategoryResponse>> UpdateAsync(
        long id,
        long userId,
        UpdateCategoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a category outright.
    /// </summary>
    /// <param name="id">The category to remove.</param>
    /// <param name="userId">The caller. Refused unless this is an active admin.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// <see cref="ErrorCodes.NotFound"/> for an unknown id, or <see cref="ErrorCodes.InvalidState"/>
    /// when the category still has children or listings pointing at it.
    /// </returns>
    Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default);
}
