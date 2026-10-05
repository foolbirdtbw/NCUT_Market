using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;
using NCUT_Market.Core.Entities;
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

    public async Task<OperationResult<DormitoryAreaResponse>> CreateAsync(
        long userId,
        CreateDormitoryAreaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var name = request.Name.Trim();

        if (await NameTakenAsync(name, editingId: null, cancellationToken))
        {
            return OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.Conflict,
                $"宿舍区「{name}」已经存在了。");
        }

        var area = new DormitoryArea
        {
            Name = name,
            SortOrder = request.SortOrder
        };

        dbContext.DormitoryAreas.Add(area);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<DormitoryAreaResponse>.Success(ToResponse(area));
    }

    public async Task<OperationResult<DormitoryAreaResponse>> UpdateAsync(
        long id,
        long userId,
        UpdateDormitoryAreaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var area = await dbContext.DormitoryAreas
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (area is null)
        {
            return OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个宿舍区。");
        }

        var name = request.Name.Trim();

        if (await NameTakenAsync(name, editingId: id, cancellationToken))
        {
            return OperationResult<DormitoryAreaResponse>.Failure(
                ErrorCodes.Conflict,
                $"宿舍区「{name}」已经存在了。");
        }

        area.Name = name;
        area.SortOrder = request.SortOrder;

        // UpdatedAt is stamped by AppDbContext.ApplyAuditTimestamps on the way out.
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<DormitoryAreaResponse>.Success(ToResponse(area));
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<bool>.Failure(ErrorCodes.Forbidden, AdminUserExtensions.ForbiddenMessage);
        }

        var area = await dbContext.DormitoryAreas
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (area is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个宿舍区。");
        }

        // Pre-checked rather than left to the foreign key. `products.dormitory_area_id` is
        // ON DELETE RESTRICT, so an unchecked delete reaches the database, throws, and comes back
        // through ApiExceptionHandler as a generic "操作冲突" that says nothing about which listings
        // are in the way.
        if (await dbContext.Products.AnyAsync(x => x.DormitoryAreaId == id, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "还有商品挂在这个宿舍区，不能删除。");
        }

        dbContext.DormitoryAreas.Remove(area);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Whether another area already carries this name.
    /// </summary>
    /// <remarks>
    /// <c>uk_dormitory_areas_name</c> would refuse the write on its own, but as a raw duplicate-key
    /// error the caller never sees. Checking here is what turns it into a sentence naming the area.
    /// </remarks>
    private async Task<bool> NameTakenAsync(
        string name,
        long? editingId,
        CancellationToken cancellationToken) =>
        await dbContext.DormitoryAreas.AnyAsync(
            x => x.Name == name && (editingId == null || x.Id != editingId),
            cancellationToken);

    private static DormitoryAreaResponse ToResponse(DormitoryArea area) => new(
        area.Id,
        area.Name,
        area.SortOrder,
        area.CreatedAt,
        area.UpdatedAt);
}
