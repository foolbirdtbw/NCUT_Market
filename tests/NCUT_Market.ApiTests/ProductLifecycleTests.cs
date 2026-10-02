using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The listing state machine: draft to published to sold, offline and back, and the transitions that
/// are refused.
/// </summary>
public sealed class ProductLifecycleTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Creating_a_listing_stores_it_as_a_draft_with_an_activity_timestamp()
    {
        // The activity timestamp is the point of this test, not the draft status. products.last_activity_at
        // is NOT NULL with no column default, and AppDbContext deliberately does not stamp it — it is the
        // draft-cleanup job's clock and a blanket SaveChanges rule would advance it on every incidental
        // write. So a create that forgets it does not merely omit a field, it fails to insert at all.
        // Asserting the value here is what turns that from a 500 at runtime into a named failure.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var product = await client.CreateDraftAsync(fixture, "生命周期-草稿");

        Assert.Equal(ProductStatus.Draft, product.Status);
        Assert.Null(product.PublishedAt);

        var lastActivityAt = await fixture.LastActivityAtAsync(product.Id);

        Assert.NotEqual(default, lastActivityAt);

        // Beijing wall-clock, so it is ahead of UTC rather than behind by eight hours. A regression to
        // DateTime.UtcNow here would write a time eight hours in the past, which no other assertion in
        // this suite would notice.
        Assert.True(
            lastActivityAt > DateTime.UtcNow.AddHours(7),
            $"LastActivityAt was {lastActivityAt:O}, which is not Beijing wall-clock.");
    }

    [Fact]
    public async Task Publishing_without_a_photo_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-无图发布");

        var response = await client.PostAsync($"/api/products/{draft.Id}/publish", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task A_draft_becomes_published_with_the_first_publish()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-发布");

        var upload = await client.UploadImageAsync(draft.Id, TestImages.Png(800, 600));
        upload.EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/api/products/{draft.Id}/publish", content: null);
        var published = await response.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(published);
        Assert.Equal(ProductStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task Taking_a_listing_offline_and_publishing_it_again_keeps_the_original_publish_time()
    {
        // Republishing is not a new listing. If PublishedAt were reassigned, a listing that keeps being
        // taken down and put back up would look permanently fresh — and once the feed orders by anything
        // other than CreatedAt, that becomes a way to sit at the top of the page.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-重新上架");
        var firstPublishedAt = published.PublishedAt;

        Assert.NotNull(firstPublishedAt);

        var offline = await client.PostAsync($"/api/products/{published.Id}/offline", content: null);
        var offlined = await offline.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.NotNull(offlined);
        Assert.Equal(ProductStatus.Offline, offlined.Status);

        var again = await client.PostAsync($"/api/products/{published.Id}/publish", content: null);
        var republished = await again.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotNull(republished);
        Assert.Equal(ProductStatus.Published, republished.Status);
        Assert.Equal(firstPublishedAt, republished.PublishedAt);
    }

    [Fact]
    public async Task Publishing_an_already_published_listing_is_a_no_op_not_an_error()
    {
        // Reachable by double-clicking the publish button. Answering it with a conflict would put an
        // error card in front of a user whose listing is exactly where they wanted it.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-重复发布");

        var response = await client.PostAsync($"/api/products/{published.Id}/publish", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Taking_an_unpublished_listing_offline_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-草稿下架");

        var response = await client.PostAsync($"/api/products/{draft.Id}/offline", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Marking_a_listing_sold_records_the_time_and_closes_the_lifecycle()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-卖出");

        var sold = await client.PostAsync($"/api/products/{published.Id}/sold", content: null);
        var result = await sold.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, sold.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(ProductStatus.Sold, result.Status);
        Assert.NotNull(result.SoldAt);

        // Sold is terminal. The only transitions left are none.
        var republish = await client.PostAsync($"/api/products/{published.Id}/publish", content: null);
        Assert.Equal(HttpStatusCode.Conflict, republish.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await republish.ReadCodeAsync());
    }

    [Fact]
    public async Task Marking_an_unpublished_listing_sold_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-草稿卖出");

        var response = await client.PostAsync($"/api/products/{draft.Id}/sold", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Deleting_a_published_listing_is_refused_so_a_live_page_is_never_one_click_from_gone()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-删已发布");

        var response = await client.DeleteAsync($"/api/products/{published.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Deleting_a_sold_listing_is_refused_because_it_is_the_record_that_the_sale_happened()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-删已售出");
        await client.PostAsync($"/api/products/{published.Id}/sold", content: null);

        var response = await client.DeleteAsync($"/api/products/{published.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Deleting_a_draft_succeeds_and_the_listing_is_then_gone()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-删草稿");

        var response = await client.DeleteAsync($"/api/products/{draft.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await client.GetAsync($"/api/products/{draft.Id}");

        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_offlined_listing_succeeds()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var published = await client.PublishListingAsync(fixture, "生命周期-删已下架");
        await client.PostAsync($"/api/products/{published.Id}/offline", content: null);

        var response = await client.DeleteAsync($"/api/products/{published.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_listing_removes_its_photos_too()
    {
        // The image rows go with the product through the ON DELETE CASCADE on product_images.product_id.
        // The files do not cascade — they are deleted explicitly — but the rows are what a reader would
        // trip over, so this checks the database half of it.
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var draft = await client.CreateDraftAsync(fixture, "生命周期-删带图草稿");
        await client.UploadImageAsync(draft.Id, TestImages.Png(800, 600));

        var before = await client.GetAsync($"/api/products/{draft.Id}");
        var withImage = await before.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.NotNull(withImage);
        Assert.Single(withImage.Images);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/products/{draft.Id}")).StatusCode);

        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<NCUT_Market.Infrastructure.Persistence.AppDbContext>();

        var orphaned = await dbContext.ProductImages.CountAsync(x => x.ProductId == draft.Id);

        Assert.Equal(0, orphaned);
    }

    [Fact]
    public async Task A_listing_that_does_not_exist_is_a_404_for_every_lifecycle_action()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        const long missing = 9_000_000_000;

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/products/{missing}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/products/{missing}/offline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/products/{missing}/sold", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/products/{missing}")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_listing_in_a_category_that_does_not_exist_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest("生命周期-坏分类", null, 10m, 1, 9_000_000_000, fixture.DormitoryAreaId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Creating_a_listing_with_an_out_of_range_condition_is_a_validation_error()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest("生命周期-坏成色", null, 10m, 9, fixture.ChildCategoryId, fixture.DormitoryAreaId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationError, await response.ReadCodeAsync());
    }
}
