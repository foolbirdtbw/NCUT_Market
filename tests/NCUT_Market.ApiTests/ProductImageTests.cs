using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Photo upload, the derived sizes, and serving the files back.
/// </summary>
public sealed class ProductImageTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>Side of the square thumbnail, from ImageStorage.</summary>
    private const int ThumbnailEdge = 320;

    /// <summary>Longest edge of the large render, from ImageStorage.</summary>
    private const int LargeEdge = 1280;

    /// <summary>Longest edge of the medium render, from ImageStorage.</summary>
    private const int MediumEdge = 640;

    [Fact]
    public async Task Uploading_stores_three_sizes_and_reports_the_sources_dimensions()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-四档");

        var response = await client.UploadImageAsync(listing.Id, TestImages.Png(800, 600));
        var image = await response.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(image);

        // Dimensions are of the original upload, which is what the detail page sizes its placeholder
        // from — so a wrong value here shows up as layout that jumps when the photo loads.
        Assert.Equal(800, image.Width);
        Assert.Equal(600, image.Height);
        Assert.Equal(0, image.SortOrder);

        var keys = await fixture.ImageKeysAsync(image.Id);

        Assert.Equal(3, keys.Length);
        Assert.Equal(3, keys.Distinct().Count());

        // All three live under the same month shard, which is what keeps one directory from
        // collecting every file the site ever receives.
        var shard = keys[0].Split('/')[0];

        Assert.All(keys, key => Assert.StartsWith(shard + "/", key));
    }

    [Fact]
    public async Task The_thumbnail_is_a_square_crop()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-方形缩略图");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(800, 600));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var bytes = await client.GetByteArrayAsync(image.ThumbnailUrl);
        var (width, height) = TestImages.SizeOf(bytes);

        // Square, even though the source is 4:3. A crop that silently fell back to Max would give
        // 320x240 and put bars in every card in the grid.
        Assert.Equal(ThumbnailEdge, width);
        Assert.Equal(ThumbnailEdge, height);
    }

    [Fact]
    public async Task The_large_render_is_capped_on_its_longest_edge()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-大图上限");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(1600, 1200));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var (width, height) = TestImages.SizeOf(await client.GetByteArrayAsync(image.Url));

        Assert.Equal(LargeEdge, width);
        Assert.Equal(960, height);

        // The original is untouched at full resolution — only the render is capped.
        Assert.Equal(1600, image.Width);
    }

    [Fact]
    public async Task A_small_image_is_not_upscaled_for_the_large_render()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-小图不放大");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(400, 300));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var (width, height) = TestImages.SizeOf(await client.GetByteArrayAsync(image.Url));

        Assert.Equal(400, width);
        Assert.Equal(300, height);
    }

    [Fact]
    public async Task The_medium_render_is_a_third_size_the_gallery_can_offer_the_browser()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-中图");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(1600, 1200));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var (width, height) = TestImages.SizeOf(await client.GetByteArrayAsync(image.MediumUrl));

        Assert.Equal(MediumEdge, width);
        Assert.Equal(480, height);

        // Three URLs a page can choose between, and they must be three different files — a projection
        // that wired medium to the large key would serve a 1280 render to a phone and cost exactly the
        // bandwidth the medium size exists to save.
        Assert.Equal(3, new[] { image.Url, image.MediumUrl, image.ThumbnailUrl }.Distinct().Count());
    }

    [Fact]
    public async Task A_photo_whose_pixels_are_rotated_is_stored_upright()
    {
        // Orientation 6 is what a phone writes when it is held upright: the sensor's pixels land in
        // the file rotated 90° clockwise, and the tag is the only thing that says so. Every other test
        // in this class uploads an image whose pixels already match how it should be displayed, so
        // none of them would notice if AutoOrient were dropped — this one fails if it is, and the
        // symptom it guards against is every portrait photo on the site lying on its side.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-手机竖拍");

        var upload = await client.UploadImageAsync(
            listing.Id,
            TestImages.JpegWithOrientation(1600, 1200, 6),
            "photo.jpg",
            "image/jpeg");

        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        // Stored as 1600x1200, displayed as 1200x1600. Reporting the stored numbers would make the
        // detail page reserve a landscape box for a portrait photo and then jump when it loads.
        Assert.Equal(1200, image.Width);
        Assert.Equal(1600, image.Height);

        var served = await client.GetByteArrayAsync(image.Url);
        var (width, height) = TestImages.SizeOf(served);

        // Upright on disk, and capped on the long edge it actually has once rotated (1600, not 1200).
        Assert.Equal(960, width);
        Assert.Equal(1280, height);

        // The rotation is in the pixels now, so the tag has to be gone. Leaving it behind makes a
        // viewer rotate an already-upright photo a second time — the classic result of orienting on
        // read and forgetting to clear the tag on write.
        var orientation = TestImages.OrientationOf(served);

        Assert.True(
            orientation is null or 1,
            $"EXIF orientation {orientation} survived the re-encode, so the photo would be rotated twice.");
    }

    [Fact]
    public async Task The_stored_format_comes_from_the_bytes_not_the_declared_content_type()
    {
        // A PNG sent as image/jpeg. Trusting the header would write a .jpg file holding PNG bytes, which
        // the static file provider would then serve as image/jpeg — a mismatch the browser resolves by
        // sniffing, which is exactly the behaviour nosniff exists to forbid.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-格式以字节为准");

        var upload = await client.UploadImageAsync(
            listing.Id,
            TestImages.Png(800, 600),
            "photo.jpg",
            "image/jpeg");

        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.NotNull(image);
        Assert.EndsWith(".png", image.Url, StringComparison.Ordinal);

        var fetched = await client.GetAsync(image.Url);

        Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_jpeg_stays_a_jpeg()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-jpeg");

        var upload = await client.UploadImageAsync(
            listing.Id,
            TestImages.Jpeg(800, 600),
            "photo.jpg",
            "image/jpeg");

        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);
        Assert.EndsWith(".jpg", image.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploading_something_that_is_not_an_image_is_refused()
    {
        // A text file with an image name and an image content type. The extension and the header both
        // claim it is a JPEG; only decoding the bytes settles it.
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-假图");

        var response = await client.UploadImageAsync(
            listing.Id,
            "this is not an image, it is a text file"u8.ToArray(),
            "photo.jpg",
            "image/jpeg");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedMediaType, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Uploading_more_than_five_megabytes_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-超限");

        // Deliberately not a real image: the size check runs before decoding, so this exercises the
        // limit without generating a five-megabyte photograph on a memory-constrained machine.
        var oversized = new byte[(5 * 1024 * 1024) + 256];

        var response = await client.UploadImageAsync(listing.Id, oversized);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task A_listing_accepts_nine_photos_and_refuses_the_tenth()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-上限");

        // Small images: this test is about the count, and nine uploads each write four files.
        var tiny = TestImages.Png(40, 30);

        for (var index = 0; index < 9; index++)
        {
            var upload = await client.UploadImageAsync(listing.Id, tiny);

            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        }

        var tenth = await client.UploadImageAsync(listing.Id, tiny);

        // 409 rather than 400: the tenth upload is a perfectly well-formed request that the listing's
        // current state cannot accept, so the caller has nothing to fix in the request itself.
        Assert.Equal(HttpStatusCode.Conflict, tenth.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await tenth.ReadCodeAsync());
    }

    [Fact]
    public async Task Photos_are_ordered_by_the_order_they_were_uploaded()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-顺序");

        for (var index = 0; index < 3; index++)
        {
            await client.UploadImageAsync(listing.Id, TestImages.Png(40, 30));
        }

        var response = await client.GetAsync($"/api/products/{listing.Id}");
        var detail = await response.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.NotNull(detail);
        Assert.Equal([0, 1, 2], detail.Images.Select(x => x.SortOrder).ToArray());
    }

    [Fact]
    public async Task A_stored_photo_is_served_with_an_image_type_and_nosniff()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-服务");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(800, 600));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var response = await client.GetAsync(image.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("image/", response.Content.Headers.ContentType?.MediaType);

        // Without this a browser is free to second-guess the content type, which is how an image
        // endpoint becomes an HTML endpoint.
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Deleting_a_photo_removes_the_row_and_the_files()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();
        var listing = await client.CreateDraftAsync(fixture, "图片-删除");

        var upload = await client.UploadImageAsync(listing.Id, TestImages.Png(800, 600));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        var response = await client.DeleteAsync($"/api/products/{listing.Id}/images/{image.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The row is gone first and the files after, on purpose: a row whose file is missing renders as
        // a broken image, while a file whose row is gone is only wasted disk.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(image.Url)).StatusCode);

        var detail = await client.GetAsync($"/api/products/{listing.Id}");
        var product = await detail.Content.ReadFromJsonAsync<ProductDetailResponse>();

        Assert.NotNull(product);
        Assert.Empty(product.Images);
    }

    [Fact]
    public async Task Deleting_a_photo_that_is_not_on_the_listing_is_a_404()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var first = await client.CreateDraftAsync(fixture, "图片-交叉删除A");
        var second = await client.CreateDraftAsync(fixture, "图片-交叉删除B");

        var upload = await client.UploadImageAsync(first.Id, TestImages.Png(40, 30));
        var image = await upload.Content.ReadFromJsonAsync<ProductImageResponse>();

        Assert.NotNull(image);

        // The id exists, but not on this listing. Scoping the lookup by product id is what stops a
        // seller from deleting a photo off somebody else's listing by pairing the wrong ids.
        var response = await client.DeleteAsync($"/api/products/{second.Id}/images/{image.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Uploading_to_a_listing_that_does_not_exist_is_a_404()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.UploadImageAsync(9_000_000_000, TestImages.Png(40, 30));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Uploading_without_a_token_is_a_401()
    {
        var (owner, _) = await fixture.CreateSignedInClientAsync();
        var anonymous = fixture.CreateAnonymousClient();

        var listing = await owner.CreateDraftAsync(fixture, "图片-未登录");

        var response = await anonymous.UploadImageAsync(listing.Id, TestImages.Png(40, 30));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
