namespace NCUT_Market.Core.Common;

/// <summary>
/// Page and page size as they arrive from the query string, already clamped to sane bounds.
/// </summary>
/// <remarks>
/// <para>
/// The public parameterless constructor plus settable properties is the shape MVC's query-string
/// binder needs; doing the clamping inside the accessors is what stops every list action from
/// repeating <c>Math.Max(1, page)</c> / <c>Math.Clamp(pageSize, 1, 100)</c>. Bind it as
/// <c>[FromQuery] PaginationQuery pagination</c>.
/// </para>
/// <para>
/// Not a positional record on purpose: a positional record's generated properties cannot be
/// intercepted on the construction path, so the clamp would have nowhere to live.
/// </para>
/// <para>
/// Out-of-range values are clamped, not rejected — a client asking for page 0 gets page 1, not a 400.
/// Only a non-numeric value is a model-state error.
/// </para>
/// </remarks>
public sealed record PaginationQuery
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    private int _page = 1;

    private int _pageSize = DefaultPageSize;

    /// <summary>Used by model binding; yields the defaults when the query string is empty.</summary>
    public PaginationQuery()
    {
    }

    /// <summary>Used by tests and by any caller that already holds the values.</summary>
    public PaginationQuery(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>1-based. Anything below 1 is raised to 1.</summary>
    public int Page
    {
        get => _page;
        init => _page = Math.Max(1, value);
    }

    /// <summary>
    /// Anything below 1 falls back to the default rather than to 1, so a client sending
    /// <c>pageSize=0</c> gets a usable page instead of a single row.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value <= 0 ? DefaultPageSize : Math.Clamp(value, 1, MaxPageSize);
    }

    /// <summary>
    /// The SQL OFFSET. Computed in 64-bit and capped, because <c>(page - 1) * pageSize</c> overflows
    /// to a negative OFFSET for an absurd page number — producing a 500 that the page and page-size
    /// clamps appear to have prevented.
    /// </summary>
    public int Skip => (int)Math.Min((long)(Page - 1) * PageSize, int.MaxValue);

    /// <summary>
    /// Builds the wire shape, echoing back the <em>clamped</em> page and page size rather than the raw
    /// query-string values.
    /// </summary>
    public PagedResult<T> ToResult<T>(IReadOnlyList<T> items, int totalCount) =>
        new(items, Page, PageSize, totalCount);
}
