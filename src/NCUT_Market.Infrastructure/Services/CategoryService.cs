using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;
using NCUT_Market.Core.Entities;
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

    public async Task<OperationResult<CategoryResponse>> CreateAsync(
        long userId,
        CreateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<CategoryResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var name = request.Name.Trim();

        // Read once, in full, and walked in memory. Categories are a hand-maintained list of a few
        // dozen rows, and both checks below need a different slice of the same table — the parent
        // lookup wants one row, the cycle walk wants the ancestry. One query serves both.
        var categories = await LoadTreeAsync(cancellationToken);

        var parentProblem = CheckParent(categories, request.ParentId, editingId: null);

        if (parentProblem is not null)
        {
            return OperationResult<CategoryResponse>.Failure(parentProblem.Value.Code, parentProblem.Value.Message);
        }

        if (await NameTakenAsync(name, request.ParentId, editingId: null, cancellationToken))
        {
            return OperationResult<CategoryResponse>.Failure(
                ErrorCodes.Conflict,
                DuplicateMessage(request.ParentId, name));
        }

        var category = new Category
        {
            Name = name,
            ParentId = request.ParentId,
            SortOrder = request.SortOrder
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<CategoryResponse>.Success(ToResponse(category));
    }

    public async Task<OperationResult<CategoryResponse>> UpdateAsync(
        long id,
        long userId,
        UpdateCategoryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<CategoryResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var category = await dbContext.Categories
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (category is null)
        {
            return OperationResult<CategoryResponse>.Failure(ErrorCodes.NotFound, "找不到这个分类。");
        }

        var name = request.Name.Trim();
        var categories = await LoadTreeAsync(cancellationToken);

        var parentProblem = CheckParent(categories, request.ParentId, editingId: id);

        if (parentProblem is not null)
        {
            return OperationResult<CategoryResponse>.Failure(parentProblem.Value.Code, parentProblem.Value.Message);
        }

        if (await NameTakenAsync(name, request.ParentId, editingId: id, cancellationToken))
        {
            return OperationResult<CategoryResponse>.Failure(
                ErrorCodes.Conflict,
                DuplicateMessage(request.ParentId, name));
        }

        category.Name = name;
        category.ParentId = request.ParentId;
        category.SortOrder = request.SortOrder;

        // UpdatedAt is stamped by AppDbContext.ApplyAuditTimestamps on the way out.
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<CategoryResponse>.Success(ToResponse(category));
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

        var category = await dbContext.Categories
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (category is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个分类。");
        }

        // Both references are pre-checked rather than left to the foreign keys. `categories.parent_id`
        // and `products.category_id` are both ON DELETE RESTRICT, so an unchecked delete surfaces as a
        // DbUpdateException and then as ApiExceptionHandler's generic "操作冲突" — true but useless to
        // whoever is looking at the screen.
        //
        // The child check deliberately ignores Status: a deactivated child still holds the foreign key
        // and would block the delete at the database level anyway.
        if (await dbContext.Categories.AnyAsync(x => x.ParentId == id, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "这个分类下面还有子分类，不能删除。");
        }

        if (await dbContext.Products.AnyAsync(x => x.CategoryId == id, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "还有商品使用这个分类，不能删除。");
        }

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Every category, active or not, as the bare shape the tree walks need.
    /// </summary>
    /// <remarks>
    /// Not filtered to <see cref="CategoryStatus.Active"/>. The cycle walk has to see disabled rows
    /// too, or a chain running through one would look like it ends there and a cycle behind it would
    /// go undetected.
    /// </remarks>
    private async Task<List<CategoryNode>> LoadTreeAsync(CancellationToken cancellationToken) =>
        await dbContext.Categories
            .AsNoTracking()
            .Select(x => new CategoryNode(x.Id, x.ParentId, x.Status))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Validates a proposed <c>parentId</c> against the loaded tree.
    /// </summary>
    /// <returns>The failure to report, or <see langword="null"/> when the parent is acceptable.</returns>
    private static (string Code, string Message)? CheckParent(
        List<CategoryNode> categories,
        long? parentId,
        long? editingId)
    {
        if (parentId is not long wanted)
        {
            return null;
        }

        var parent = categories.FirstOrDefault(x => x.Id == wanted);

        if (parent is null || parent.Status != CategoryStatus.Active)
        {
            return (ErrorCodes.InvalidArgument, "找不到这个父分类。");
        }

        if (editingId is not long self)
        {
            return null;
        }

        if (wanted == self)
        {
            return (ErrorCodes.InvalidArgument, "不能把分类挂到它自己下面。");
        }

        // Walk from the proposed parent up to the root. Meeting the category being edited means the
        // edit would close a loop, which the self-referencing foreign key accepts happily and which
        // nothing downstream is written to survive — the client-side tree builder recurses.
        //
        // The visited set is what terminates this, not the tree's shape: one row whose parent_id
        // points back into its own ancestry would otherwise spin forever, and the database has no
        // constraint that forbids it.
        var visited = new HashSet<long>();

        for (var cursor = parent.ParentId; cursor is long ancestor; )
        {
            if (ancestor == self)
            {
                return (ErrorCodes.InvalidArgument, "不能把分类挂到它自己的子分类下面。");
            }

            if (!visited.Add(ancestor))
            {
                break;
            }

            cursor = categories.FirstOrDefault(x => x.Id == ancestor)?.ParentId;
        }

        return null;
    }

    /// <summary>
    /// Whether a sibling under the same parent already carries this name.
    /// </summary>
    /// <remarks>
    /// Scoped to siblings, so 教材 may hang off more than one parent. This is the check
    /// <c>CategoryConfiguration</c> asks for by name: <c>name</c> deliberately carries no unique index,
    /// because MySQL treats every NULL <c>parent_id</c> as distinct and the constraint would not stop
    /// duplicate top-level rows anyway.
    ///
    /// The comparison runs in SQL so it uses the column's <c>utf8mb4_0900_ai_ci</c> collation, which is
    /// case- and accent-insensitive — the same rule a buyer sees when filtering.
    /// </remarks>
    private async Task<bool> NameTakenAsync(
        string name,
        long? parentId,
        long? editingId,
        CancellationToken cancellationToken) =>
        await dbContext.Categories.AnyAsync(
            x => x.ParentId == parentId
                && x.Name == name
                && (editingId == null || x.Id != editingId),
            cancellationToken);

    private static string DuplicateMessage(long? parentId, string name) =>
        parentId is null
            ? $"顶级分类里已经有叫「{name}」的了。"
            : $"这个父分类下已经有叫「{name}」的分类了。";

    private static CategoryResponse ToResponse(Category category) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.SortOrder,
        category.CreatedAt,
        category.UpdatedAt);

    /// <summary>One row of the tree walk: everything the ancestor checks need and nothing else.</summary>
    private sealed record CategoryNode(long Id, long? ParentId, CategoryStatus Status);
}
