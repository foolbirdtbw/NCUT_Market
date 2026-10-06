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
/// Three sizes exist in storage and all three are exposed. There is no full-resolution copy of the
/// upload — <c>ImageStorage</c> writes only the large/medium/thumbnail renders and keeps the bytes
/// the seller sent nowhere, so the largest thing a page can show is <paramref name="Url"/>.
/// </para>
/// <para>
/// The large render is a proportional fit, not a crop: it is the upload scaled so its longest edge is
/// at most 1280, aspect ratio untouched, and an upload already inside that box is written through
/// unchanged. That is what makes it safe to hand to a viewer: a page can show the whole photo rather
/// than the square slice a grid cell would give it.
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
