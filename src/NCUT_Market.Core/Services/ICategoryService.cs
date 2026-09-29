using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;

namespace NCUT_Market.Core.Services;

/// <summary>Read access to the category dictionary.</summary>
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
}
