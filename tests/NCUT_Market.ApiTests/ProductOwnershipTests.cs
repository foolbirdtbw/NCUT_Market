using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Who may see and change a listing.
/// </summary>
/// <remarks>
/// Every test here uses two separately registered accounts. The ownership check lives in the service
/// rather than in an authorization policy, because <c>[Authorize]</c> can establish that somebody is
/// signed in but not that they own this row — so these are the tests that prove the check is actually
/// reached on every mutating endpoint rather than only on the ones somebody remembered.
/// </remarks>
public sealed class ProductOwnershipTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Updating_someone_elses_listing_is_forbidden()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.CreateDraftAsync(fixture, "归属-改别人的");

        var response = await stranger.PutAsJsonAsync(
            $"/api/products/{listing.Id}",
            new UpdateProductRequest("归属-被抢走了", null, 1m, 1, fixture.ChildCategoryId, fixture.DormitoryAreaId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Publishing_deleting_and_photographing_someone_elses_listing_are_all_forbidden()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.CreateDraftAsync(fixture, "归属-全动作");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsync($"/api/products/{listing.Id}/publish", null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsync($"/api/products/{listing.Id}/offline", null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsync($"/api/products/{listing.Id}/sold", null)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.DeleteAsync($"/api/products/{listing.Id}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.UploadImageAsync(listing.Id, TestImages.Png(40, 30))).StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_delete_a_photo_off_someone_elses_listing()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.CreateDraftAsync(fixture, "归属-删别人的图");

        var upload = await owner.UploadImageAsync(listing.Id, TestImages.Png(800, 600));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var response = await stranger.DeleteAsync($"/api/products/{listing.Id}/images/{image.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_draft_is_visible_to_its_seller_and_not_found_to_everyone_else()
    {
        // 404 rather than 403, and that is the interesting part: a 403 would confirm that an id exists,
        // turning a guess into a way to enumerate drafts. The seller's own view is the exception, which
        // is what makes the edit page work.
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.CreateDraftAsync(fixture, "归属-草稿可见性");

        var asOwner = await owner.GetAsync($"/api/products/{listing.Id}");
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);

        var asStranger = await stranger.GetAsync($"/api/products/{listing.Id}");
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);

        var asAnonymous = await fixture.CreateAnonymousClient().GetAsync($"/api/products/{listing.Id}");
        Assert.Equal(HttpStatusCode.NotFound, asAnonymous.StatusCode);
    }

    [Fact]
    public async Task An_offlined_listing_is_visible_to_its_seller_and_not_found_to_everyone_else()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.PublishListingAsync(fixture, "归属-下架可见性");
        await owner.PostAsync($"/api/products/{listing.Id}/offline", content: null);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/products/{listing.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/products/{listing.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_sold_listing_stays_reachable_by_anyone_who_has_the_link()
    {
        // It leaves the feed but the detail page keeps working, so a link somebody shared before the
        // sale does not turn into a 404 that looks like a broken site.
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();

        var listing = await owner.PublishListingAsync(fixture, "归属-已售可见性");
        await owner.PostAsync($"/api/products/{listing.Id}/sold", content: null);

        var response = await anonymous.GetAsync($"/api/products/{listing.Id}");
        var detail = await response.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(detail);
        Assert.Equal(ProductStatus.Sold, detail.Status);
    }

    [Fact]
    public async Task A_published_listing_is_visible_to_anonymous_callers()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();

        var listing = await owner.PublishListingAsync(fixture, "归属-公开可见");

        var response = await fixture.CreateAnonymousClient().GetAsync($"/api/products/{listing.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mutating_a_listing_that_does_not_exist_is_a_404_not_a_403()
    {
        // The two must not be confused: 403 means "it exists and is not yours", 404 means "there is
        // nothing here". A service that checked ownership before existence would answer 403 for every
        // id a client invented.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.PutAsJsonAsync(
            "/api/products/9000000000",
            new UpdateProductRequest("归属-不存在", null, 1m, 1, fixture.ChildCategoryId, fixture.DormitoryAreaId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Mutating_a_listing_without_a_token_is_a_401_carrying_a_code()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();

        var listing = await owner.CreateDraftAsync(fixture, "归属-未登录");

        var response = await anonymous.PostAsync($"/api/products/{listing.Id}/publish", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.Unauthorized, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task The_owners_list_shows_their_listings_in_every_status_and_nobody_elses()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var draft = await owner.CreateDraftAsync(fixture, "归属-我的列表-草稿");
        var sold = await owner.PublishListingAsync(fixture, "归属-我的列表-已售");
        await owner.PostAsync($"/api/products/{sold.Id}/sold", content: null);

        var strangersDraft = await stranger.CreateDraftAsync(fixture, "归属-别人的草稿");

        var response = await owner.GetAsync("/api/products/mine");
        var page = await response.Content.ReadFromJsonAsync<PagedResult<ProductSummaryResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(page);

        var ids = page.Items.Select(x => x.Id).ToArray();

        // Draft and sold both belong here — this is the seller's working set, not the public feed, and
        // it is the only list that shows anything other than Published.
        Assert.Contains(draft.Id, ids);
        Assert.Contains(sold.Id, ids);

        // Every listing lives in one table, so a dropped seller filter would show up as somebody
        // else's draft appearing in this seller's working set. A draft is the sharpest case: it is
        // invisible everywhere else, so nothing but a missing filter can put it here.
        Assert.DoesNotContain(strangersDraft.Id, ids);
    }

    [Fact]
    public async Task The_owners_list_without_a_token_is_a_401()
    {
        var response = await fixture.CreateAnonymousClient().GetAsync("/api/products/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.Unauthorized, await response.ReadCodeAsync());
    }
}
