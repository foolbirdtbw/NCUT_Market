using NCUT_Market.Core.Common;

namespace NCUT_Market.IntegrationTests;

/// <summary>
/// Clamping and offset arithmetic in <see cref="PaginationQuery"/>. No database involved.
/// </summary>
public sealed class PaginationQueryTests
{
    [Fact]
    public void Parameterless_construction_yields_the_defaults()
    {
        // This is the shape the query-string binder builds when the request carries no paging params.
        var pagination = new PaginationQuery();

        Assert.Equal(1, pagination.Page);
        Assert.Equal(PaginationQuery.DefaultPageSize, pagination.PageSize);
        Assert.Equal(0, pagination.Skip);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(-9999, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Page_is_raised_to_at_least_one(int page, int expected)
    {
        Assert.Equal(expected, new PaginationQuery { Page = page }.Page);
    }

    [Theory]
    [InlineData(0, PaginationQuery.DefaultPageSize)]
    [InlineData(-1, PaginationQuery.DefaultPageSize)]
    [InlineData(-9999, PaginationQuery.DefaultPageSize)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(PaginationQuery.MaxPageSize, PaginationQuery.MaxPageSize)]
    [InlineData(PaginationQuery.MaxPageSize + 1, PaginationQuery.MaxPageSize)]
    [InlineData(9999, PaginationQuery.MaxPageSize)]
    [InlineData(int.MaxValue, PaginationQuery.MaxPageSize)]
    public void PageSize_is_clamped(int pageSize, int expected)
    {
        Assert.Equal(expected, new PaginationQuery { PageSize = pageSize }.PageSize);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_rather_than_rejected()
    {
        // Clamping, not validation: a client asking for page 0 gets page 1 and a 200, not a 400.
        var pagination = new PaginationQuery(0, -1);

        Assert.Equal(1, pagination.Page);
        Assert.Equal(PaginationQuery.DefaultPageSize, pagination.PageSize);
    }

    [Fact]
    public void Init_accessors_can_be_assigned_through_reflection()
    {
        // MVC's binder assigns through PropertyInfo.SetValue rather than a constructor, so an
        // init-only accessor is only usable if this succeeds — and only useful if the clamp still
        // fires on that path. Both are checked here.
        //
        // This is a proxy for the real thing: the live check is
        // GET /api/dormitory-areas?pageSize=9999 returning pageSize 100. If that ever comes back
        // 9999, change `init` to `set` in PaginationQuery; the clamping logic itself still holds,
        // because a `set` accessor also runs on every assignment.
        var type = typeof(PaginationQuery);
        var pagination = new PaginationQuery();

        type.GetProperty(nameof(PaginationQuery.Page))!.SetValue(pagination, 5);
        type.GetProperty(nameof(PaginationQuery.PageSize))!.SetValue(pagination, 9999);

        Assert.Equal(5, pagination.Page);
        Assert.Equal(PaginationQuery.MaxPageSize, pagination.PageSize);
    }

    [Fact]
    public void Skip_uses_the_clamped_values()
    {
        // page 3 of 20-row pages starts at row 41, i.e. offset 40.
        Assert.Equal(40, new PaginationQuery(3, 20).Skip);

        // The raw page would have produced a negative offset; the clamp prevents it.
        Assert.Equal(0, new PaginationQuery(-5, 20).Skip);
    }

    [Fact]
    public void Skip_saturates_instead_of_overflowing_for_an_absurd_page_number()
    {
        // (page - 1) * pageSize overflows to a negative int well before page reaches int.MaxValue.
        // A negative OFFSET is a SQL error, which would surface as a 500 that the page and page-size
        // clamps appear to have ruled out.
        var pagination = new PaginationQuery(int.MaxValue, PaginationQuery.MaxPageSize);

        Assert.Equal(int.MaxValue, pagination.Skip);
        Assert.True(pagination.Skip > 0);
    }

    [Fact]
    public void ToResult_echoes_the_clamped_values_not_the_raw_ones()
    {
        var pagination = new PaginationQuery(0, 9999);

        var result = pagination.ToResult(new[] { 1, 2, 3 }, totalCount: 3);

        Assert.Equal(1, result.Page);
        Assert.Equal(PaginationQuery.MaxPageSize, result.PageSize);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.TotalPages);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void TotalPages_is_zero_for_an_empty_result_rather_than_one()
    {
        var pagination = new PaginationQuery(1, 20);

        Assert.Equal(0, pagination.ToResult(Array.Empty<int>(), totalCount: 0).TotalPages);
    }

    [Fact]
    public void TotalPages_rounds_up_a_partial_page()
    {
        var pagination = new PaginationQuery(1, 2);

        Assert.Equal(2, pagination.ToResult(new[] { 1, 2 }, totalCount: 3).TotalPages);
    }
}
