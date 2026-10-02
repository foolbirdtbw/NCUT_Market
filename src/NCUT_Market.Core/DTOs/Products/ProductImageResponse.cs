namespace NCUT_Market.Core.DTOs.Products;

/// <summary>
/// One photo of a listing, with the sizes a buyer-facing page actually uses.
/// </summary>
/// <param name="Id">Primary key. Used to address the image for deletion.</param>
/// <param name="Url">The large render, for the detail gallery.</param>
/// <param name="MediumUrl">
/// The medium render, offered to the browser alongside <paramref name="Url"/> as a narrower
/// candidate for a phone-sized viewport.
/// </param>
/// <param name="ThumbnailUrl">The square thumbnail, for grids and cards.</param>
/// <param name="Width">Width of the original upload, in pixels — after EXIF rotation is applied.</param>
/// <param name="Height">Height of the original upload, in pixels.</param>
/// <param name="SortOrder">Display order within the listing.</param>
/// <remarks>
/// <para>
/// Four sizes exist in storage (original/large/medium/thumbnail) and three are exposed. The original
/// stays behind on purpose: it is the only copy of the bytes the seller uploaded, every render below
/// it is lossy, and a photo that has been through a resize cannot be turned back into a better one.
/// What it must not do is reach a page — it can be several megabytes, and no page here displays an
/// image big enough to need it.
/// </para>
/// <para>
/// URLs rather than storage keys: the key is an internal layout detail, and a client that has to
/// concatenate a path prefix to render an image is a client that will get it wrong.
/// </para>
/// </remarks>
public sealed record ProductImageResponse(
    long Id,
    string Url,
    string MediumUrl,
    string ThumbnailUrl,
    int Width,
    int Height,
    int SortOrder);
