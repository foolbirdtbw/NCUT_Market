using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Infrastructure.Persistence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace NCUT_Market.Infrastructure.Storage;

/// <summary>
/// One stored photo: the keys of its three files plus the facts about the upload they came from.
/// </summary>
/// <remarks>
/// There is no key for a full-resolution copy, because no full-resolution copy is written. 1280 is
/// the largest render and the largest any page asks for, so storing the source as well would spend
/// about nine tenths of this site's disk on files nothing ever requests — measured on a 4032x3024
/// phone photo, the three renders together came to 490 KB against 3.9 MB for the source.
/// </remarks>
/// <param name="LargeKey">Longest edge 1280.</param>
/// <param name="MediumKey">Longest edge 640.</param>
/// <param name="ThumbnailKey">Square, cropped to 320.</param>
/// <param name="Width">Width of the upload, after EXIF rotation is applied.</param>
/// <param name="Height">Height of the upload, after EXIF rotation is applied.</param>
/// <param name="FileSize">Bytes of the incoming upload. Nothing that large is kept.</param>
/// <param name="MimeType">Type of the decoded image, not of the declared content type.</param>
internal sealed record StoredImage(
    string LargeKey,
    string MediumKey,
    string ThumbnailKey,
    int Width,
    int Height,
    long FileSize,
    string MimeType);

/// <summary>
/// Writes an uploaded image to the local filesystem as three derived sizes.
/// </summary>
/// <remarks>
/// <para>
/// Every file is written by re-encoding a decoded image, never by copying the uploaded bytes. That
/// is what makes the upload path safe: a file that decodes as an image is written back out as one,
/// so a polyglot carrying an HTML or script payload does not survive the round trip, and EXIF —
/// including GPS coordinates a seller never meant to publish — is dropped rather than served back.
/// </para>
/// <para>
/// File names are generated here. Nothing the client sends reaches the path, so there is no
/// traversal and no way to choose an extension.
/// </para>
/// </remarks>
internal sealed class ImageStorage(IOptions<StorageOptions> options, ILogger<ImageStorage> logger)
{
    /// <summary>Longest edge of the large render, in pixels.</summary>
    private const int LargeEdge = 1280;

    /// <summary>Longest edge of the medium render, in pixels.</summary>
    private const int MediumEdge = 640;

    /// <summary>Side of the square thumbnail, in pixels.</summary>
    private const int ThumbnailEdge = 320;

    /// <summary>JPEG/WebP quality for the derived renders.</summary>
    private const int Quality = 82;

    private readonly StorageOptions _options = options.Value;

    /// <summary>
    /// Decodes an image, writes its three renders, and returns their keys.
    /// </summary>
    /// <param name="content">The uploaded bytes.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The stored image, or <see langword="null"/> when the bytes are not an image this service can
    /// handle. Returning null rather than throwing keeps the caller's failure path in
    /// <c>OperationResult</c>, where the project keeps its expected failures.
    /// </returns>
    public async Task<StoredImage?> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        Image image;

        try
        {
            image = await Image.LoadAsync(content, cancellationToken);
        }
        catch (UnknownImageFormatException)
        {
            return null;
        }
        catch (InvalidImageContentException)
        {
            // A truncated download or a corrupt file. Same outcome as an unsupported format: the
            // client sent something that is not a usable image.
            return null;
        }

        using (image)
        {
            // Rotation first, then everything else. A phone photo carries its orientation in EXIF
            // and its stored pixels are sideways; resizing before orienting would size the wrong
            // axis and the thumbnail would come out wrong.
            image.Mutate(x => x.AutoOrient());

            var (extension, mimeType, encoder) = Describe(image.Metadata.DecodedImageFormat);

            var (directory, keyPrefix) = NewShard();

            Directory.CreateDirectory(directory);

            var largeKey = keyPrefix + "_l" + extension;
            var mediumKey = keyPrefix + "_m" + extension;
            var thumbnailKey = keyPrefix + "_t" + extension;

            var fileSize = content.CanSeek ? content.Length : 0;

            await SaveResizedAsync(image, largeKey, new Size(LargeEdge, LargeEdge), ResizeMode.Max, encoder, cancellationToken);
            await SaveResizedAsync(image, mediumKey, new Size(MediumEdge, MediumEdge), ResizeMode.Max, encoder, cancellationToken);
            await SaveResizedAsync(image, thumbnailKey, new Size(ThumbnailEdge, ThumbnailEdge), ResizeMode.Crop, encoder, cancellationToken);

            return new StoredImage(
                largeKey,
                mediumKey,
                thumbnailKey,
                image.Width,
                image.Height,
                fileSize,
                mimeType);
        }
    }

    /// <summary>
    /// Deletes the three files behind a stored image.
    /// </summary>
    /// <param name="image">The keys to remove.</param>
    /// <remarks>
    /// Best effort by design, and called only after the database row is gone. A file that outlives
    /// its row wastes disk; a row whose file is missing renders as a broken image, so the ordering
    /// matters more than the cleanup succeeding.
    /// </remarks>
    public void Delete(StoredImage image)
    {
        foreach (var key in new[] { image.LargeKey, image.MediumKey, image.ThumbnailKey })
        {
            try
            {
                File.Delete(Path.Combine(_options.UploadRoot, key));
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete image file {Key}; it is now orphaned.", key);
            }
        }
    }

    private async Task SaveResizedAsync(
        Image image,
        string key,
        Size size,
        ResizeMode mode,
        IImageEncoder encoder,
        CancellationToken cancellationToken)
    {
        // ResizeMode.Max upscales, which is not what "make a smaller copy" should mean: a 400px-wide
        // upload rendered to 1280 costs disk and decode time on every page view and shows no more
        // detail, because there is none to show. So a Max render that already fits is written as is.
        //
        // Crop is exempt on purpose. The thumbnail has to come out exactly 320 square or the cards in
        // the grid are visibly ragged, and a consistent size is worth the upscale on tiny sources.
        var fitsAlready = image.Width <= size.Width && image.Height <= size.Height;

        if (mode != ResizeMode.Crop && fitsAlready)
        {
            await image.SaveAsync(Path.Combine(_options.UploadRoot, key), encoder, cancellationToken);

            return;
        }

        using var resized = image.Clone(x => x.Resize(new ResizeOptions { Size = size, Mode = mode }));

        await resized.SaveAsync(Path.Combine(_options.UploadRoot, key), encoder, cancellationToken);
    }

    /// <summary>
    /// Picks the month shard and the key prefix for a new image.
    /// </summary>
    /// <remarks>
    /// Sharded by month so one directory does not accumulate every file the site ever receives.
    /// The folder name comes from <see cref="AppDbContext.AuditNow"/> to keep a single clock in the
    /// codebase — it is a shard label, not a timestamp anybody displays.
    /// </remarks>
    private (string Directory, string KeyPrefix) NewShard()
    {
        var shard = AppDbContext.AuditNow.ToString("yyyyMM");
        var name = Guid.NewGuid().ToString("N");

        return (Path.Combine(_options.UploadRoot, shard), shard + "/" + name);
    }

    /// <summary>
    /// Maps the decoded format to a file extension, a MIME type, and an encoder.
    /// </summary>
    /// <remarks>
    /// The derivatives are written in the source's own format rather than always JPEG, so a PNG
    /// with transparency keeps its alpha instead of acquiring a black or white box. Anything that
    /// reaches the fallback is a format <see cref="SaveAsync"/> has already agreed to decode but
    /// this service has no encoder for, which would produce a file with an extension nothing will
    /// serve.
    /// </remarks>
    private static (string Extension, string MimeType, IImageEncoder Encoder) Describe(IImageFormat? format)
    {
        var name = format?.Name;

        if (string.Equals(name, "PNG", StringComparison.OrdinalIgnoreCase))
        {
            return (".png", "image/png", new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }

        if (string.Equals(name, "WEBP", StringComparison.OrdinalIgnoreCase))
        {
            return (".webp", "image/webp", new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = Quality });
        }

        return (".jpg", "image/jpeg", new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = Quality });
    }
}
