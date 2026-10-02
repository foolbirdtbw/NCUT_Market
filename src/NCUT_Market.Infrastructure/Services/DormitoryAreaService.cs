using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class DormitoryAreaService(AppDbContext dbContext) : IDormitoryAreaService
{
    public async Task<PagedResult<DormitoryAreaResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.DormitoryAreas
            .AsNoTracking()
            .Where(x => x.Status == DormitoryAreaStatus.Active);

        var totalCount = await query.CountAsync(cancellationToken);

        // By sort_order, not created_at DESC — see the note in CategoryService. Both are dictionary
        // tables with a composite index whose leading column is status and second column is
        // sort_order.
        var items = await query
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new DormitoryAreaResponse(
                x.Id,
                x.Name,
                x.SortOrder,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

        return pagination.ToResult(items, totalCount);
    }

    public async Task<OperationResult<DormitoryAreaResponse>> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        // The status filter is part of the lookup, not a check afterwards, so a deactivated area is
        // indistinguishable from one that never existed. That is the intent: this is the buyer-facing
        // catalogue and it should not confirm that a hidden row is there.
        var area = await dbContext.DormitoryAreas
            .AsNoTracking()
            .Where(x => x.Id == id && x.Status == DormitoryAreaStatus.Active)
            .Select(x => new DormitoryAreaResponse(
                x.Id,
                x.Name,
                x.SortOrder,
                x.CreatedAt,
                x.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return area is null
            ? OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个宿舍区。")
            : OperationResult<DormitoryAreaResponse>.Success(area);
    }
}
