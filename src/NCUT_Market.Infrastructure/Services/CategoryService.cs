using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class CategoryService(AppDbContext dbContext) : ICategoryService
{
    public async Task<PagedResult<CategoryResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Categories
            .AsNoTracking()
            .Where(x => x.Status == CategoryStatus.Active);

        var totalCount = await query.CountAsync(cancellationToken);

        // Ordered by sort_order rather than the usual created_at DESC. Categories are a
        // hand-maintained dictionary, not a feed — "newest first" means nothing to a category picker,
        // and ordering by created_at would stop idx_categories_status_sort_order from covering the
        // sort. This is the one deliberate departure from the list-ordering convention elsewhere.
        var items = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new CategoryResponse(
                x.Id,
                x.Name,
                x.ParentId,
                x.SortOrder,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

        return pagination.ToResult(items, totalCount);
    }
}
