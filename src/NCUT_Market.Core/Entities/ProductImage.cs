namespace NCUT_Market.Core.Entities;

/// <summary>
/// One uploaded photo. The storage layer writes four derived sizes, so each row carries four object
/// keys rather than a single URL.
/// </summary>
public sealed class ProductImage : IHasCreatedAt
{
    public long Id { get; set; }

    public long ProductId { get; set; }

    public required string OriginalKey { get; set; }

    public required string LargeKey { get; set; }

    public required string MediumKey { get; set; }

    public required string ThumbnailKey { get; set; }

    /// <summary>Width of the original, in pixels. Column is INT UNSIGNED.</summary>
    public int Width { get; set; }

    /// <summary>Height of the original, in pixels. Column is INT UNSIGNED.</summary>
    public int Height { get; set; }

    /// <summary>Size of the original in bytes. Column is BIGINT UNSIGNED.</summary>
    public long FileSize { get; set; }

    public required string MimeType { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public Product Product { get; set; } = null!;
}
