namespace NCUT_Market.Core.Entities;

/// <summary>
/// Marks an entity that carries an optimistic-concurrency token.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="IHasCreatedAt"/>, this adds no property to the EF model — <c>Version</c> already
/// exists on the entity and is mapped as a concurrency token by its configuration. The interface is a
/// compile-time view over it.
/// </para>
/// <para>
/// The value is bumped by <c>AppDbContext.ApplyAuditTimestamps</c> on every
/// <c>EntityState.Modified</c> entry, in the same pass that
/// stamps <c>updated_at</c>. Callers therefore never write it, and — more to the point — never have
/// to remember to. A configuration that marks the column as a token without the entity implementing
/// this would produce an UPDATE that carries the same value it started with, which is a concurrency
/// check that never fires.
/// </para>
/// <para>
/// Only <see cref="Product"/> implements it today. The write it protects is the one that decides who
/// buys a listing, and getting that wrong sells the same item twice; two clicks on "发起交易" in one
/// thread are harmless by comparison, so <see cref="Conversation"/> deliberately goes without.
/// </para>
/// </remarks>
public interface IHasVersion
{
    uint Version { get; set; }
}
