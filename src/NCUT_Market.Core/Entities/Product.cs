using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

public sealed class Product : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long SellerId { get; set; }

    public long CategoryId { get; set; }

    public long DormitoryAreaId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public decimal Price { get; set; }

    /// <summary>Maps to the <c>condition</c> column — a MySQL reserved word, quoted by the provider.</summary>
    public ProductCondition Condition { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Draft;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Bumped only by real edits (title, description, price, category, condition, area, images) —
    /// never by viewing. The draft cleanup job deletes on this, so "opened the edit page" must not
    /// count as activity.
    /// </summary>
    public DateTime LastActivityAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? SoldAt { get; set; }

    public User Seller { get; set; } = null!;

    public Category Category { get; set; } = null!;

    public DormitoryArea DormitoryArea { get; set; } = null!;

    public ICollection<ProductImage> Images { get; } = [];

    public ICollection<Notification> Notifications { get; } = [];

    /// <summary>
    /// Threads opened about this listing. They outlive it: the foreign key is SET NULL, so deleting
    /// the product leaves the conversations behind with their title frozen. See
    /// <see cref="Conversation"/>.
    /// </summary>
    public ICollection<Conversation> Conversations { get; } = [];
}
