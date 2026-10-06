using System.Text;
using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<DormitoryArea> DormitoryAreas => Set<DormitoryArea>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    /// <summary>
    /// "Now" for every audit timestamp this context writes, expressed in Beijing time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixed +8 offset rather than <see cref="DateTime.Now"/>. China has had no daylight saving
    /// since 1991 and runs a single time zone, so +8 always holds — whereas <c>DateTime.Now</c> would
    /// silently tie "these columns hold Beijing time" to the time zone of whichever machine happens to
    /// be running, and the whole table would shift the day the app moves to a UTC host.
    /// </para>
    /// <para>
    /// <see cref="DateTime.SpecifyKind"/> strips the <see cref="DateTimeKind.Utc"/> that
    /// <see cref="DateTime.UtcNow"/> sets, because the value is no longer UTC. Leaving the kind as Utc
    /// would be a lie that some future serialiser or comparison would act on. The connection string
    /// deliberately carries no <c>DateTimeKind</c> option either, so values read back are
    /// <see cref="DateTimeKind.Unspecified"/> and round-trip unchanged.
    /// </para>
    /// <para>
    /// <b>Bulk-update rule:</b> <c>ExecuteUpdateAsync</c> and <c>ExecuteDeleteAsync</c> bypass the
    /// change tracker entirely and get no stamping from this context. Any file that contains
    /// <c>ExecuteUpdateAsync</c> must also reference <see cref="AuditNow"/> and set the timestamp
    /// column by hand. Grep for one to find the other.
    /// </para>
    /// </remarks>
    public static DateTime AuditNow =>
        DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8), DateTimeKind.Unspecified);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Stamps <c>created_at</c> / <c>updated_at</c> across the change tracker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only these two overloads are overridden, and that is sufficient: the parameterless
    /// <c>SaveChanges()</c> and <c>SaveChangesAsync(ct)</c> both forward to them, so all four public
    /// entry points are covered.
    /// </para>
    /// <para>
    /// The asymmetry between the two branches is deliberate. On <see cref="EntityState.Added"/> a
    /// caller-supplied value wins, which is the back door seeders and backfills need to reproduce
    /// historical rows; on <see cref="EntityState.Modified"/> the stamp is unconditional, because
    /// pinning an old <c>updated_at</c> is never a legitimate intent.
    /// </para>
    /// <para>
    /// <see cref="ChangeTracker.Entries()"/> runs <c>DetectChanges</c> before returning, so by the
    /// time the loop classifies an entry, the caller's own edits are already visible as
    /// <see cref="EntityState.Modified"/>. The values written below are picked up because
    /// <c>base.SaveChanges</c> runs <c>DetectChanges</c> again on its way to the database.
    /// </para>
    /// <para>
    /// <see cref="Product.LastActivityAt"/> is never touched here. It is a business field meaning
    /// "the listing was really edited", and only the product service may advance it — the draft
    /// cleanup job deletes on that column, so stamping it on every save would keep abandoned drafts
    /// alive forever.
    /// </para>
    /// <para>
    /// <see cref="IHasVersion.Version"/> advances on <see cref="EntityState.Modified"/> alongside the
    /// audit stamp. <c>Added</c> deliberately leaves it alone: a new row starts at whatever the column
    /// default says, and a token that skipped its first value would be a worse lie than a repeat.
    /// </para>
    /// </remarks>
    private void ApplyAuditTimestamps()
    {
        var now = AuditNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is IHasCreatedAt addedCreatedAt && addedCreatedAt.CreatedAt == default)
                    {
                        addedCreatedAt.CreatedAt = now;
                    }

                    if (entry.Entity is IHasUpdatedAt addedUpdatedAt && addedUpdatedAt.UpdatedAt == default)
                    {
                        addedUpdatedAt.UpdatedAt = now;
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is IHasUpdatedAt modifiedUpdatedAt)
                    {
                        modifiedUpdatedAt.UpdatedAt = now;
                    }

                    // Bumped here, and only here, for the same reason updated_at is: it is a rule
                    // about every write to the row, not a decision any one caller is in a position to
                    // make. An entity that is modified without this advancing would carry its old
                    // token into the WHERE clause and pass a concurrency check it should have failed.
                    if (entry.Entity is IHasVersion modifiedVersion)
                    {
                        modifiedVersion.Version++;
                    }

                    break;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasCharSet("utf8mb4").UseCollation("utf8mb4_0900_ai_ci");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplySnakeCaseColumnNames(modelBuilder);
    }

    /// <summary>
    /// Rewrites every column name to snake_case in one pass, so individual configurations never
    /// carry a HasColumnName call. Runs after the configurations, which is why any raw SQL inside a
    /// check constraint has to spell its columns in snake_case by hand.
    /// </summary>
    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (!char.IsUpper(current))
            {
                builder.Append(current);
                continue;
            }

            // Insert a separator before an uppercase letter that starts a new word: either the
            // previous character was lowercase/digit (productId -> product_id), or we are at the end
            // of an acronym run (apiKey -> api_key).
            var previousIsLowerOrDigit = i > 0 && !char.IsUpper(name[i - 1]);
            var nextIsLower = i + 1 < name.Length && !char.IsUpper(name[i + 1]);

            if (i > 0 && (previousIsLowerOrDigit || nextIsLower))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
