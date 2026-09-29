namespace NCUT_Market.Core.Entities;

/// <summary>
/// Marks an entity that carries <c>updated_at</c>.
/// <para>
/// Deliberately separate from <see cref="IHasCreatedAt"/>: <see cref="ProductImage"/> and
/// <see cref="Notification"/> have only a creation timestamp, and a combined interface would force a
/// new — and therefore schema-changing — property onto them.
/// </para>
/// </summary>
public interface IHasUpdatedAt
{
    DateTime UpdatedAt { get; set; }
}
