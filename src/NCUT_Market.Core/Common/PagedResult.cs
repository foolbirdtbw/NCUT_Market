namespace NCUT_Market.Core.Common;

/// <summary>
/// One page of a list endpoint. Serialised as
/// <c>{ items, page, pageSize, totalCount, totalPages }</c>.
/// </summary>
/// <remarks>
/// <see cref="TotalPages"/> divides by <see cref="PageSize"/>, so a <see cref="PageSize"/> of zero
/// would produce garbage. That is safe only because <see cref="PaginationQuery"/> guarantees
/// <c>PageSize &gt;= 1</c> — do not add a second construction path that bypasses it.
/// </remarks>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
