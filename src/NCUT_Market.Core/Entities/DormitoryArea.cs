using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

/// <summary>
/// Where an item physically sits, so buyers can filter listings near them.
/// </summary>
public sealed class DormitoryArea : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public DormitoryAreaStatus Status { get; set; } = DormitoryAreaStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<Product> Products { get; } = [];
}
