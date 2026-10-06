using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.IntegrationTests;

/// <summary>
/// Pins the behaviour of the <c>created_at</c> / <c>updated_at</c> stamping in
/// <see cref="AppDbContext"/>.
/// </summary>
/// <remarks>
/// The tests never call <c>Task.Delay</c>. Every timestamp they compare against is either an explicit
/// backdated literal or read from <see cref="AppDbContext.AuditNow"/> before and after the call, so
/// there is no clock-precision flake to go intermittent in CI.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class AuditTimestampTests(DatabaseFixture fixture)
{
    /// <summary>Milliseconds, matching the <c>datetime(3)</c> columns.</summary>
    private const long TicksPerStoredUnit = TimeSpan.TicksPerMillisecond;

    [Fact]
    public async Task Inserting_a_category_fills_CreatedAt_and_UpdatedAt()
    {
        await fixture.WithDbContextAsync(async dbContext =>
        {
            var before = AppDbContext.AuditNow;

            var category = new Category { Name = UniqueName("insert") };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            var after = AppDbContext.AuditNow;

            Assert.NotEqual(default, category.CreatedAt);
            Assert.Equal(category.CreatedAt, category.UpdatedAt);
            Assert.InRange(category.CreatedAt, before, after);
        });
    }

    [Fact]
    public async Task Inserting_a_category_stamps_Beijing_time_not_UTC()
    {
        await fixture.WithDbContextAsync(async dbContext =>
        {
            var category = new Category { Name = UniqueName("beijing") };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            // The two candidate clocks are eight hours apart, so this window cannot admit both: a
            // regression to DateTime.UtcNow would land well outside it.
            var expected = DateTime.UtcNow.AddHours(8);
            var drift = (expected - category.CreatedAt).Duration();

            Assert.True(
                drift < TimeSpan.FromMinutes(1),
                $"Expected a Beijing-time stamp near {expected:O} but found {category.CreatedAt:O} " +
                $"(off by {drift}).");
        });
    }

    [Fact]
    public async Task Explicitly_backdated_timestamps_survive_insert()
    {
        // The seeder back door: a caller-supplied value wins on insert, which is what makes it
        // possible to reproduce historical rows without a raw SQL script.
        var backdated = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Unspecified);

        await fixture.WithDbContextAsync(async dbContext =>
        {
            var category = new Category
            {
                Name = UniqueName("backdated"),
                CreatedAt = backdated,
                UpdatedAt = backdated
            };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            Assert.Equal(backdated, category.CreatedAt);
            Assert.Equal(backdated, category.UpdatedAt);

            // Read it back through a different context so the assertion is about the stored row, not
            // about the object still sitting in this one. The literal has no sub-millisecond ticks, so
            // datetime(3) round-trips it exactly.
            dbContext.ChangeTracker.Clear();

            var stored = await dbContext.Categories
                .AsNoTracking()
                .SingleAsync(x => x.Id == category.Id);

            Assert.Equal(backdated, stored.CreatedAt);
            Assert.Equal(backdated, stored.UpdatedAt);
        });
    }

    [Fact]
    public async Task Modifying_a_category_advances_only_UpdatedAt()
    {
        var baseline = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);

        await fixture.WithDbContextAsync(async dbContext =>
        {
            var category = new Category
            {
                Name = UniqueName("modify"),
                CreatedAt = baseline,
                UpdatedAt = baseline
            };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            category.Name = UniqueName("modify-renamed");
            await dbContext.SaveChangesAsync();

            // CreatedAt is stamped only on insert, so a later edit must leave it exactly as it was.
            Assert.Equal(baseline, category.CreatedAt);

            // UpdatedAt is stamped unconditionally on modify — a caller pinning an old value is never
            // a legitimate intent. The backdated baseline is what makes this assertable without a
            // delay.
            Assert.NotEqual(baseline, category.UpdatedAt);
            Assert.True(category.UpdatedAt > baseline);
        });
    }

    [Fact]
    public async Task CreatedAt_only_entities_are_stamped_and_LastActivityAt_is_left_alone()
    {
        var lastActivity = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Unspecified);

        await fixture.WithDbContextAsync(async dbContext =>
        {
            var user = new User
            {
                Username = UniqueName("u"),
                PasswordHash = "not-a-real-hash",
                Nickname = "审计测试"
            };

            var category = new Category { Name = UniqueName("c") };

            var area = new DormitoryArea { Name = UniqueName("a") };

            dbContext.Users.Add(user);
            dbContext.Categories.Add(category);
            dbContext.DormitoryAreas.Add(area);
            await dbContext.SaveChangesAsync();

            var product = new Product
            {
                SellerId = user.Id,
                CategoryId = category.Id,
                DormitoryAreaId = area.Id,
                Title = UniqueName("p"),
                Price = 12.50m,
                Condition = ProductCondition.Good,
                // Cannot be left at default: DateTime.MinValue is 0001-01-01, below MySQL's datetime
                // floor of 1000-01-01, and strict mode rejects it with error 1292.
                LastActivityAt = lastActivity
            };

            dbContext.Products.Add(product);
            await dbContext.SaveChangesAsync();

            var image = new ProductImage
            {
                ProductId = product.Id,
                LargeKey = "large.jpg",
                MediumKey = "medium.jpg",
                ThumbnailKey = "thumb.jpg",
                MimeType = "image/jpeg",
                Width = 1600,
                Height = 1200,
                FileSize = 812_345
            };

            dbContext.ProductImages.Add(image);
            await dbContext.SaveChangesAsync();

            // ProductImage has no UpdatedAt at all, which is the reason the two marker interfaces are
            // separate rather than one IAuditable.
            Assert.NotEqual(default, image.CreatedAt);
            Assert.Equal(product.CreatedAt, product.UpdatedAt);

            // Now edit the product itself. UpdatedAt must move; LastActivityAt must not, because it is
            // a business field the draft cleanup job deletes on, and only the product service may
            // advance it.
            product.Title = UniqueName("p-edited");
            await dbContext.SaveChangesAsync();

            Assert.True(product.UpdatedAt > product.CreatedAt);
            Assert.Equal(lastActivity, product.LastActivityAt);
        });
    }

    [Fact]
    public async Task ExecuteUpdate_bypasses_audit_stamping_so_bulk_updates_must_set_it_by_hand()
    {
        // This test exists to make a known blind spot explicit rather than to celebrate it.
        // ExecuteUpdateAsync compiles straight to UPDATE and never touches the change tracker, so
        // ApplyAuditTimestamps does not run. The convention that guards it is "any file containing
        // ExecuteUpdateAsync must also reference AppDbContext.AuditNow" — both halves are asserted
        // here so that "fixing" the audit stamping cannot quietly leave bulk updates behind.
        var baseline = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Unspecified);

        await fixture.WithDbContextAsync(async dbContext =>
        {
            var category = new Category
            {
                Name = UniqueName("bulk"),
                CreatedAt = baseline,
                UpdatedAt = baseline
            };

            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync();

            // The broken way: the row changes and updated_at does not.
            var affected = await dbContext.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Name, UniqueName("bulk-renamed")));

            Assert.Equal(1, affected);

            await dbContext.Entry(category).ReloadAsync();
            Assert.Equal(baseline, category.UpdatedAt);

            // The required way: the same bulk update, with the timestamp supplied explicitly.
            // Truncated to milliseconds because datetime(3) cannot hold the sub-millisecond ticks that
            // AuditNow returns, and the value below is compared against one read back from MySQL.
            var now = AppDbContext.AuditNow;
            var stampedAt = now.AddTicks(-(now.Ticks % TicksPerStoredUnit));

            await dbContext.Categories
                .Where(x => x.Id == category.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedAt, stampedAt));

            await dbContext.Entry(category).ReloadAsync();

            Assert.Equal(stampedAt, category.UpdatedAt);
            Assert.Equal(baseline, category.CreatedAt);
        });
    }

    /// <summary>
    /// A per-run unique name. Rows are never cleaned up, and <c>dormitory_areas.name</c> is uniquely
    /// indexed, so uniqueness has to come from the data rather than from teardown.
    /// </summary>
    private static string UniqueName(string prefix) =>
        $"{prefix}-{Guid.NewGuid().ToString("N")[..12]}";
}
