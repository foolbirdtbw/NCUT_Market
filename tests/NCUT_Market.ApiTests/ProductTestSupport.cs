using System.Net.Http.Headers;
using System.Net.Http.Json;
using NCUT_Market.Core.DTOs.Products;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Real image bytes for upload tests.
/// </summary>
/// <remarks>
/// Generated rather than committed as a fixture file. ImageSharp is already a project dependency
/// through the API, and a binary in the repository is one more thing to keep in step with the
/// decoders. A solid colour is enough: these tests are about dimensions, format handling and the
/// storage layout, none of which care what the picture depicts.
/// </remarks>
internal static class TestImages
{
    /// <summary>A PNG of the given size.</summary>
    public static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);

        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);

        return buffer.ToArray();
    }

    /// <summary>A JPEG of the given size, for the tests that must not be served back as a PNG.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);

        return buffer.ToArray();
    }

    /// <summary>
    /// A JPEG whose pixels are stored rotated, with the EXIF tag that says how to put them upright.
    /// </summary>
    /// <param name="width">Width of the stored pixels, before the rotation is applied.</param>
    /// <param name="height">Height of the stored pixels, before the rotation is applied.</param>
    /// <param name="orientation">The EXIF orientation value. 6 is what a phone writes held upright.</param>
    /// <remarks>
    /// The other two builders produce images whose pixels already match how they should be displayed,
    /// so nothing about them exercises the orientation path. This one does, and decoding it without
    /// applying the tag gives the wrong aspect ratio by construction.
    /// </remarks>
    public static byte[] JpegWithOrientation(int width, int height, ushort orientation)
    {
        using var image = new Image<Rgba32>(width, height);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, orientation);
        image.Metadata.ExifProfile = exif;

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);

        return buffer.ToArray();
    }

    /// <summary>The pixel size of an encoded image, read back by decoding it.</summary>
    /// <remarks>
    /// Deliberately the raw stored size: <see cref="Image.Load(byte[])"/> does not apply EXIF
    /// orientation, so a caller comparing this against what the API reports is comparing stored pixels
    /// against displayed ones.
    /// </remarks>
    public static (int Width, int Height) SizeOf(byte[] bytes)
    {
        using var image = Image.Load(bytes);

        return (image.Width, image.Height);
    }

    /// <summary>
    /// The EXIF orientation tag of an encoded image, or <see langword="null"/> when it carries none.
    /// </summary>
    public static ushort? OrientationOf(byte[] bytes)
    {
        using var image = Image.Load(bytes);

        if (image.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.Orientation, out var orientation))
        {
            return orientation.Value;
        }

        return null;
    }
}

/// <summary>
/// The handful of request sequences the product tests repeat: create a draft, attach a photo,
/// publish.
/// </summary>
/// <remarks>
/// Publishing is three calls rather than one because a photo cannot exist before the listing it
/// belongs to — <c>product_images.product_id</c> is non-null with a cascading foreign key. Every test
/// that wants a listing in the public feed has to walk the same three steps, so they are written once
/// here rather than forty times across the test classes.
/// </remarks>
internal static class ProductTestSupport
{
    /// <summary>Creates a draft and returns it. Fails the test if the API refuses.</summary>
    public static async Task<ProductDetailResponse> CreateDraftAsync(
        this HttpClient client,
        ApiFixture fixture,
        string title,
        decimal price = 10m,
        int condition = 1,
        long? categoryId = null,
        long? areaId = null,
        string? description = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(
                title,
                description,
                price,
                condition,
                categoryId ?? fixture.ChildCategoryId,
                areaId ?? fixture.DormitoryAreaId));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ProductDetailResponse>())
            ?? throw new InvalidOperationException("Creating a listing returned no body.");
    }

    /// <summary>Posts one file to a listing's image endpoint.</summary>
    public static async Task<HttpResponseMessage> UploadImageAsync(
        this HttpClient client,
        long productId,
        byte[] bytes,
        string fileName = "photo.png",
        string contentType = "image/png")
    {
        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        // The name has to be "file": the controller binds a single IFormFile by that parameter name.
        form.Add(file, "file", fileName);

        return await client.PostAsync($"/api/products/{productId}/images", form);
    }

    /// <summary>
    /// Creates a draft, gives it one photo, and publishes it — a listing in the public feed.
    /// </summary>
    public static async Task<ProductDetailResponse> PublishListingAsync(
        this HttpClient client,
        ApiFixture fixture,
        string title,
        decimal price = 10m,
        int condition = 1,
        long? categoryId = null,
        long? areaId = null,
        string? description = null)
    {
        var draft = await client.CreateDraftAsync(fixture, title, price, condition, categoryId, areaId, description);

        var upload = await client.UploadImageAsync(draft.Id, TestImages.Png(800, 600));
        upload.EnsureSuccessStatusCode();

        var published = await client.PostAsync($"/api/products/{draft.Id}/publish", content: null);
        published.EnsureSuccessStatusCode();

        return (await published.Content.ReadFromJsonAsync<ProductDetailResponse>())
            ?? throw new InvalidOperationException("Publishing returned no body.");
    }

    /// <summary>Reads a problem response's <c>code</c> member.</summary>
    public static async Task<string?> ReadCodeAsync(this HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        using var document = System.Text.Json.JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
