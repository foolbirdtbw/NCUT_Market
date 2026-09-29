using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

/// <summary>
/// Self-referencing tree, e.g. 宿舍用品 → 收纳 / 床上用品 / 桌椅 / 小家电.
/// </summary>
public sealed class Category : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Null for a top-level category.</summary>
    public long? ParentId { get; set; }

    public int SortOrder { get; set; }

    public CategoryStatus Status { get; set; } = CategoryStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Category? Parent { get; set; }

    public ICollection<Category> Children { get; } = [];

    public ICollection<Product> Products { get; } = [];
}
