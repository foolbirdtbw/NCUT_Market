namespace NCUT_Market.Core.Entities;

/// <summary>
/// Marks an entity whose <c>created_at</c> column is stamped by <c>AppDbContext.SaveChanges*</c>.
/// <para>
/// Implementing this adds no property to the EF model — <c>CreatedAt</c> already exists on the entity
/// and is already mapped. The interface is only a compile-time view over it, which is why adding it
/// produces no migration.
/// </para>
/// </summary>
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}
