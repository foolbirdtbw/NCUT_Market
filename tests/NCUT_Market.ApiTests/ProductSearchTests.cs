using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The public feed's filters: keyword, category, price, condition, and paging.
/// </summary>
/// <remarks>
/// <para>
/// Every test isolates itself with a random token woven into the titles it creates, because the feed
/// is the whole published table and the database is shared and never cleaned. A search for a token
/// that only this test invented returns only this test's listings, which is what makes the assertions
/// about counts meaningful rather than "at least".
/// </para>
/// <para>
/// The wildcard tests are the reason this file exists. <c>EscapeLike</c> has to escape the
/// backslash <em>before</em> the percent and underscore; doing it the other way round turns the escape
/// characters it just inserted into literals, and the search stops finding things while still
/// returning a clean 200. Nothing else in the suite would notice.
/// </para>
/// </remarks>
public sealed class ProductSearchTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_keyword_matches_the_title_and_the_description()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var inTitle = await client.PublishListingAsync(fixture, $"搜索-标题-{token} 蓝色背包");
        var inDescription = await client.PublishListingAsync(
            fixture,
            $"搜索-描述-{token}",
            description: $"九成新 {token} 蓝色");

        var results = await SearchAsync(anonymous, ("q", token));

        var ids = results.Items.Select(x => x.Id).ToArray();

        Assert.Contains(inTitle.Id, ids);
        Assert.Contains(inDescription.Id, ids);
        Assert.Equal(2, results.TotalCount);
    }

    [Fact]
    public async Task A_percent_sign_in_the_keyword_matches_the_literal_character()
    {
        // The failure this catches: escaping in the wrong order produces a pattern that matches nothing
        // (or, with the opposite mistake, everything), and either way the endpoint still answers 200
        // with a plausible-looking page.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var literal = await client.PublishListingAsync(fixture, $"搜索-百分比-{token}100% 全新充电宝");
        var decoy = await client.PublishListingAsync(fixture, $"搜索-百分比-{token}10000mAh 充电宝");

        var results = await SearchAsync(anonymous, ("q", token + "100%"));

        var ids = results.Items.Select(x => x.Id).ToArray();

        Assert.Contains(literal.Id, ids);

        // If % were left as a wildcard this listing matches too, and the search silently answers "any
        // listing whose title contains this token followed by 100 and then anything".
        Assert.DoesNotContain(decoy.Id, ids);
        Assert.Equal(1, results.TotalCount);
    }

    [Fact]
    public async Task An_underscore_in_the_keyword_matches_the_literal_character()
    {
        // Same class of bug as the percent test, different character. Underscore is the one people
        // forget, because it looks like punctuation rather than a wildcard.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var literal = await client.PublishListingAsync(fixture, $"搜索-下划线-{token}AB_CD 型号");
        var decoy = await client.PublishListingAsync(fixture, $"搜索-下划线-{token}ABXCD 型号");

        var results = await SearchAsync(anonymous, ("q", token + "AB_CD"));

        var ids = results.Items.Select(x => x.Id).ToArray();

        Assert.Contains(literal.Id, ids);
        Assert.DoesNotContain(decoy.Id, ids);
        Assert.Equal(1, results.TotalCount);
    }

    [Fact]
    public async Task A_backslash_in_the_keyword_matches_the_literal_character()
    {
        // The third character EscapeLike touches, and the one that makes the ordering matter: it is both
        // the thing being escaped and the escape character itself.
        //
        // The decoy is what makes this test mean anything. Under MySQL's LIKE, escaping a character that
        // is not a wildcard yields the character itself — so if the backslash were passed through
        // unescaped, the pattern "C:\Users" would be read as "C:Users" and would match the decoy while
        // missing the real thing. Both listings exist, so a test that only asserted "something matched"
        // would pass either way.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var literal = await client.PublishListingAsync(fixture, $@"搜索-反斜杠-{token}路径 C:\Users 备份盘");
        var decoy = await client.PublishListingAsync(fixture, $@"搜索-反斜杠-{token}路径 C:Users 备份盘");

        var results = await SearchAsync(anonymous, ("q", $@"{token}路径 C:\Users"));

        var ids = results.Items.Select(x => x.Id).ToArray();

        Assert.Contains(literal.Id, ids);
        Assert.DoesNotContain(decoy.Id, ids);
        Assert.Equal(1, results.TotalCount);
    }

    [Fact]
    public async Task A_keyword_matches_regardless_of_case()
    {
        // Relies on the column collation being utf8mb4_0900_ai_ci. There is no LOWER() in the query, so
        // if the column were ever created with a case-sensitive collation this is where it shows up.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var listing = await client.PublishListingAsync(fixture, $"搜索-大小写-{token}-TESTItem 显示器");

        var results = await SearchAsync(anonymous, ("q", token + "-testitem"));

        Assert.Equal(1, results.TotalCount);
        Assert.Equal(listing.Id, results.Items[0].Id);
    }

    [Fact]
    public async Task Filtering_by_a_parent_category_includes_listings_in_its_children()
    {
        // The listing is filed under the child. Filtering by the root has to reach it, or picking a
        // top-level category on the site returns an empty page while every listing sits one level down.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var listing = await client.PublishListingAsync(
            fixture,
            $"搜索-分类子孙-{token}",
            categoryId: fixture.ChildCategoryId);

        var results = await SearchAsync(
            anonymous,
            ("q", token),
            ("categoryId", fixture.RootCategoryId.ToString()));

        Assert.Equal(1, results.TotalCount);
        Assert.Equal(listing.Id, results.Items[0].Id);
    }

    [Fact]
    public async Task Filtering_by_an_unrelated_category_excludes_the_listing()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        await client.PublishListingAsync(fixture, $"搜索-别的分类-{token}", categoryId: fixture.ChildCategoryId);

        var results = await SearchAsync(
            anonymous,
            ("q", token),
            ("categoryId", fixture.OtherCategoryId.ToString()));

        Assert.Equal(0, results.TotalCount);
    }

    [Fact]
    public async Task Price_bounds_are_inclusive()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var cheap = await client.PublishListingAsync(fixture, $"搜索-价格低-{token}", price: 10m);
        var dear = await client.PublishListingAsync(fixture, $"搜索-价格高-{token}", price: 100m);

        var bounded = await SearchAsync(
            anonymous,
            ("q", token),
            ("minPrice", "10"),
            ("maxPrice", "100"));

        // Both, because both bounds are inclusive.
        Assert.Equal(2, bounded.TotalCount);

        var onlyDear = await SearchAsync(anonymous, ("q", token), ("minPrice", "11"));

        Assert.Equal(1, onlyDear.TotalCount);
        Assert.Equal(dear.Id, onlyDear.Items[0].Id);

        var onlyCheap = await SearchAsync(anonymous, ("q", token), ("maxPrice", "10"));

        Assert.Equal(1, onlyCheap.TotalCount);
        Assert.Equal(cheap.Id, onlyCheap.Items[0].Id);
    }

    [Fact]
    public async Task Paging_covers_every_listing_exactly_once()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var created = new List<long>();

        for (var index = 0; index < 3; index++)
        {
            var listing = await client.PublishListingAsync(fixture, $"搜索-翻页-{token}-{index}");
            created.Add(listing.Id);
        }

        var first = await SearchAsync(anonymous, ("q", token), ("page", "1"), ("pageSize", "2"));
        var second = await SearchAsync(anonymous, ("q", token), ("page", "2"), ("pageSize", "2"));
        var past = await SearchAsync(anonymous, ("q", token), ("page", "3"), ("pageSize", "2"));

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.TotalPages);

        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Empty(past.Items);

        // A listing appearing on both pages, or on neither, is the failure an OFFSET bug produces.
        // Ordering ties are broken by id, so this is stable across the two requests.
        var seen = first.Items.Concat(second.Items).Select(x => x.Id).ToArray();

        Assert.Equal(created.OrderBy(x => x).ToArray(), seen.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task A_draft_never_appears_in_the_public_feed()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var token = NewToken();

        var draft = await client.CreateDraftAsync(fixture, $"搜索-草稿-{token}");

        var feed = await SearchAsync(anonymous, ("q", token));

        Assert.Equal(0, feed.TotalCount);

        // But the seller can still reach it directly, which is what the edit page relies on.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/products/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_empty_result_is_an_empty_page_not_an_error()
    {
        var anonymous = fixture.CreateAnonymousClient();

        var results = await SearchAsync(anonymous, ("q", "no-listing-has-this-" + NewToken()));

        Assert.Equal(0, results.TotalCount);
        Assert.Equal(0, results.TotalPages);
        Assert.Empty(results.Items);
    }

    /// <summary>A keyword no other test could have produced.</summary>
    private static string NewToken() => "t" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Runs a search with every value URL-encoded, so a term containing <c>%</c> or a space reaches the
    /// server as data rather than being read as syntax by the query-string parser.
    /// </summary>
    private static async Task<PagedResult<ProductSummaryResponse>> SearchAsync(
        HttpClient client,
        params (string Key, string Value)[] parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        var response = await client.GetAsync("/api/products?" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<PagedResult<ProductSummaryResponse>>()
            ?? throw new InvalidOperationException("The listing endpoint returned no body.");
    }
}
