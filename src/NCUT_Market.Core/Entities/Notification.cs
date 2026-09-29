using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

/// <summary>
/// A personal notification. <see cref="Title"/> and <see cref="Content"/> hold the message already
/// rendered at creation time, with the product name baked in — that is what keeps the notification
/// readable after the product row is gone and <see cref="RelatedProductId"/> has been nulled out.
/// </summary>
public sealed class Notification : IHasCreatedAt
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public NotificationType Type { get; set; }

    /// <summary>Rendered message title, e.g. "商品已被删除".</summary>
    public required string Title { get; set; }

    /// <summary>Rendered message body, e.g. "你的商品「二手台灯」已被系统删除".</summary>
    public required string Content { get; set; }

    /// <summary>Nulled out when the referenced product is hard-deleted.</summary>
    public long? RelatedProductId { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }

    public User User { get; set; } = null!;

    public Product? RelatedProduct { get; set; }
}
