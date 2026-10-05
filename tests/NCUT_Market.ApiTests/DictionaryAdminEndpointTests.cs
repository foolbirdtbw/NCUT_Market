using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;
using NCUT_Market.Core.DTOs.DormitoryAreas;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The category and dormitory-area dictionaries: public to read, admin-only to change.
/// </summary>
/// <remarks>
/// <para>
/// The reads are asserted to stay anonymous in <see cref="Both_dictionaries_stay_readable_without_a_token"/>.
/// That is the load-bearing half of the design: the product search filter and the publish form both
/// call these endpoints with no token, so anything that quietly added auth here would break the shop
/// while every write test below still passed.
/// </para>
/// <para>
/// As with announcements, the admin check reads <c>users.role</c> from the database rather than the
/// token, so these tests promote an account and then reuse the <em>same</em> client.
/// </para>
/// </remarks>
public sealed class DictionaryAdminEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>A unique suffix, so rows this class creates never collide with another run's.</summary>
    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>A signed-in client that has been promoted through the database.</summary>
    private async Task<HttpClient> CreateAdminAsync()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        await fixture.PromoteToAdminAsync(auth.User.Id);

        return client;
    }

    private static async Task<List<CategoryResponse>> ListCategoriesAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/categories?pageSize={PaginationQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PagedResult<CategoryResponse>>())!.Items.ToList();
    }

    private static async Task<List<DormitoryAreaResponse>> ListAreasAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/dormitory-areas?pageSize={PaginationQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PagedResult<DormitoryAreaResponse>>())!.Items.ToList();
    }

    private static async Task<CategoryResponse> CreateCategoryAsync(
        HttpClient client,
        string name,
        long? parentId = null,
        int sortOrder = 0)
    {
        var response = await client.PostAsJsonAsync(
            "/api/categories", new CreateCategoryRequest(name, parentId, sortOrder));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())
            ?? throw new InvalidOperationException("Creating a category returned no body.");
    }

    // ---------- who may write ----------

    [Fact]
    public async Task Writes_are_refused_to_anonymous_callers()
    {
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(
                "/api/categories", new CreateCategoryRequest("分类-匿名", null, 0))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync(
                "/api/categories/1", new UpdateCategoryRequest("分类-匿名", null, 0))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.DeleteAsync("/api/categories/1")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(
                "/api/dormitory-areas", new CreateDormitoryAreaRequest("宿舍区-匿名", 0))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.DeleteAsync("/api/dormitory-areas/1")).StatusCode);
    }

    [Fact]
    public async Task Writes_are_refused_to_ordinary_users()
    {
        var (user, _) = await fixture.CreateSignedInClientAsync();

        var created = await user.PostAsJsonAsync(
            "/api/categories", new CreateCategoryRequest("分类-普通用户", null, 0));

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await created.ReadCodeAsync());

        var updated = await user.PutAsJsonAsync(
            "/api/categories/1", new UpdateCategoryRequest("分类-普通用户", null, 0));

        Assert.Equal(HttpStatusCode.Forbidden, updated.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await updated.ReadCodeAsync());

        var deleted = await user.DeleteAsync("/api/categories/1");

        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await deleted.ReadCodeAsync());

        var area = await user.PostAsJsonAsync(
            "/api/dormitory-areas", new CreateDormitoryAreaRequest("宿舍区-普通用户", 0));

        Assert.Equal(HttpStatusCode.Forbidden, area.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await area.ReadCodeAsync());
    }

    // ---------- the round trips ----------

    [Fact]
    public async Task An_admin_can_add_move_and_delete_a_category_branch()
    {
        var admin = await CreateAdminAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var suffix = NewSuffix();

        var firstParent = await CreateCategoryAsync(admin, $"分类-甲-{suffix}", sortOrder: 10);
        var secondParent = await CreateCategoryAsync(admin, $"分类-乙-{suffix}", sortOrder: 20);
        var child = await CreateCategoryAsync(admin, $"分类-子-{suffix}", firstParent.Id, sortOrder: 30);

        // The tree the buyer-facing catalogue rebuilds has to actually contain the new rows.
        var afterCreate = await ListCategoriesAsync(anonymous);

        Assert.Contains(afterCreate, x => x.Id == child.Id && x.ParentId == firstParent.Id);

        // Move it under the other parent and rename it in the same call — an update is a replacement,
        // not a patch.
        var moved = await admin.PutAsJsonAsync(
            $"/api/categories/{child.Id}",
            new UpdateCategoryRequest($"分类-子改-{suffix}", secondParent.Id, 5));

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var afterMove = await ListCategoriesAsync(anonymous);
        var reread = afterMove.Single(x => x.Id == child.Id);

        Assert.Equal($"分类-子改-{suffix}", reread.Name);
        Assert.Equal(secondParent.Id, reread.ParentId);
        Assert.Equal(5, reread.SortOrder);

        // UpdatedAt is stamped by the audit rule, not written by hand; a no-op update would leave it
        // where it started and this is where that shows up.
        Assert.True(reread.UpdatedAt >= reread.CreatedAt);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/categories/{child.Id}")).StatusCode);

        Assert.DoesNotContain(await ListCategoriesAsync(anonymous), x => x.Id == child.Id);
    }

    [Fact]
    public async Task An_admin_can_add_rename_and_delete_a_dormitory_area()
    {
        var admin = await CreateAdminAsync();
        var anonymous = fixture.CreateAnonymousClient();
        var suffix = NewSuffix();

        var created = await admin.PostAsJsonAsync(
            "/api/dormitory-areas", new CreateDormitoryAreaRequest($"宿舍区-{suffix}", 7));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var area = (await created.Content.ReadFromJsonAsync<DormitoryAreaResponse>())!;

        Assert.Contains(await ListAreasAsync(anonymous), x => x.Id == area.Id && x.SortOrder == 7);

        var renamed = await admin.PutAsJsonAsync(
            $"/api/dormitory-areas/{area.Id}",
            new UpdateDormitoryAreaRequest($"宿舍区改-{suffix}", 3));

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var reread = (await ListAreasAsync(anonymous)).Single(x => x.Id == area.Id);

        Assert.Equal($"宿舍区改-{suffix}", reread.Name);
        Assert.Equal(3, reread.SortOrder);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/dormitory-areas/{area.Id}")).StatusCode);

        Assert.DoesNotContain(await ListAreasAsync(anonymous), x => x.Id == area.Id);
    }

    // ---------- the refusals ----------

    [Fact]
    public async Task A_duplicate_dormitory_area_name_is_a_conflict_that_says_so()
    {
        var admin = await CreateAdminAsync();

        string name = $"宿舍区-重名-{NewSuffix()}";

        var first = await admin.PostAsJsonAsync(
            "/api/dormitory-areas", new CreateDormitoryAreaRequest(name, 0));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await admin.PostAsJsonAsync(
            "/api/dormitory-areas", new CreateDormitoryAreaRequest(name, 0));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await second.ReadCodeAsync());

        // The point of pre-checking: uk_dormitory_areas_name would refuse this too, but as a raw
        // duplicate-key error the caller never sees.
        Assert.Contains("宿舍区", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_with_children_cannot_be_deleted()
    {
        var admin = await CreateAdminAsync();
        var suffix = NewSuffix();

        var parent = await CreateCategoryAsync(admin, $"分类-有子-{suffix}");

        await CreateCategoryAsync(admin, $"分类-有子的子-{suffix}", parent.Id);

        var refused = await admin.DeleteAsync($"/api/categories/{parent.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await refused.ReadCodeAsync());

        Assert.Contains("子分类", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_in_use_cannot_be_deleted()
    {
        var admin = await CreateAdminAsync();
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var suffix = NewSuffix();

        var category = await CreateCategoryAsync(admin, $"分类-在用-{suffix}");

        await seller.PublishListingAsync(fixture, $"字典测试-分类在用-{suffix}", categoryId: category.Id);

        var refused = await admin.DeleteAsync($"/api/categories/{category.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await refused.ReadCodeAsync());
        Assert.Contains("商品", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dormitory_area_in_use_cannot_be_deleted()
    {
        var admin = await CreateAdminAsync();
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var suffix = NewSuffix();

        var created = await admin.PostAsJsonAsync(
            "/api/dormitory-areas", new CreateDormitoryAreaRequest($"宿舍区-在用-{suffix}", 0));

        var area = (await created.Content.ReadFromJsonAsync<DormitoryAreaResponse>())!;

        await seller.PublishListingAsync(fixture, $"字典测试-宿舍区在用-{suffix}", areaId: area.Id);

        var refused = await admin.DeleteAsync($"/api/dormitory-areas/{area.Id}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await refused.ReadCodeAsync());
        Assert.Contains("商品", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_category_cannot_be_its_own_parent_or_its_own_descendants_child()
    {
        var admin = await CreateAdminAsync();
        var suffix = NewSuffix();

        var parent = await CreateCategoryAsync(admin, $"分类-环-父-{suffix}");
        var child = await CreateCategoryAsync(admin, $"分类-环-子-{suffix}", parent.Id);
        var grandchild = await CreateCategoryAsync(admin, $"分类-环-孙-{suffix}", child.Id);

        var self = await admin.PutAsJsonAsync(
            $"/api/categories/{child.Id}", new UpdateCategoryRequest(child.Name, child.Id, 0));

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // The one that a naive "is the parent me?" check would let through: making a node a child of
        // its own grandchild closes a loop three levels deep, and the client-side tree builder
        // recurses over it forever.
        var descendant = await admin.PutAsJsonAsync(
            $"/api/categories/{parent.Id}",
            new UpdateCategoryRequest(parent.Name, grandchild.Id, 0));

        Assert.Equal(HttpStatusCode.BadRequest, descendant.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await descendant.ReadCodeAsync());
    }

    [Fact]
    public async Task A_parent_that_does_not_exist_is_rejected_in_chinese()
    {
        var admin = await CreateAdminAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/categories",
            new CreateCategoryRequest($"分类-野父-{NewSuffix()}", 999_999_999, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
        Assert.Contains("父分类", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_id_cannot_be_updated_or_deleted()
    {
        var admin = await CreateAdminAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync(
                "/api/categories/999999999", new UpdateCategoryRequest("分类-没有", null, 0))).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.DeleteAsync("/api/categories/999999999")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await admin.DeleteAsync("/api/dormitory-areas/999999999")).StatusCode);
    }

    // ---------- the regression guard ----------

    [Fact]
    public async Task Both_dictionaries_stay_readable_without_a_token()
    {
        // Both controllers carry a class-level [Authorize]. The product search filter and the publish
        // form call these two endpoints with no token at all, so a missing [AllowAnonymous] would take
        // the whole shop down while every write test above still passed.
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/categories?pageSize={PaginationQuery.MaxPageSize}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/dormitory-areas?pageSize={PaginationQuery.MaxPageSize}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/dormitory-areas/{fixture.DormitoryAreaId}")).StatusCode);
    }
}
