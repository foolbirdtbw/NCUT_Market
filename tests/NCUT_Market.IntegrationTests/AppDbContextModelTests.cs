using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.IntegrationTests;

/// <summary>
/// Builds the EF model offline — no database needed. Fails if any IEntityTypeConfiguration is broken,
/// and pins the schema decisions that are easy to regress silently: the unsigned size columns and the
/// per-relationship delete behaviours.
/// </summary>
public sealed class AppDbContextModelTests
{
    private const string UnusedConnectionString =
        "Server=localhost;Port=3306;Database=ncut_market;User=root;Password=;";

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(UnusedConnectionString, new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public void Model_contains_exactly_the_nine_designed_tables()
    {
        using var context = CreateContext();

        var tables = context.Model.GetEntityTypes()
            .Select(x => x.GetTableName())
            .OfType<string>()
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "announcements",
                "categories",
                "conversations",
                "dormitory_areas",
                "messages",
                "notifications",
                "product_images",
                "products",
                "users"
            },
            tables);
    }

    [Fact]
    public void Product_image_size_columns_are_unsigned()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(ProductImage))!;

        Assert.Equal("bigint unsigned", entity.FindProperty(nameof(ProductImage.FileSize))!.GetColumnType());
        Assert.Equal("int unsigned", entity.FindProperty(nameof(ProductImage.Width))!.GetColumnType());
        Assert.Equal("int unsigned", entity.FindProperty(nameof(ProductImage.Height))!.GetColumnType());
    }

    [Fact]
    public void Notification_to_user_is_restrict_so_a_user_is_never_cascaded_away()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Notification))!;

        var foreignKey = entity.GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(User));

        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Notification_to_product_is_set_null_so_notifications_outlive_deleted_products()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Notification))!;

        var foreignKey = entity.GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(Product));

        Assert.Equal(DeleteBehavior.SetNull, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Every_product_foreign_key_is_restrict()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        var foreignKeys = entity.GetForeignKeys();

        var restrictedPrincipals = foreignKeys
            .Where(x => x.DeleteBehavior == DeleteBehavior.Restrict)
            .Select(x => x.PrincipalEntityType.ClrType)
            .ToHashSet();

        // The count, not just the set. Product now has two edges onto User — the seller and the
        // trade's buyer — and a set of principal types cannot tell them apart: a Cascade on the new
        // one would leave this comparison passing.
        Assert.Equal(4, foreignKeys.Count());

        Assert.Equal(
            new HashSet<Type> { typeof(User), typeof(Category), typeof(DormitoryArea) },
            restrictedPrincipals);
    }

    [Fact]
    public void Products_trade_buyer_is_a_restricted_second_edge_onto_user()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        var buyerForeignKey = entity.GetForeignKeys()
            .Single(x => x.Properties.Any(p => p.Name == nameof(Product.TransactionBuyerId)));

        Assert.Equal(typeof(User), buyerForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, buyerForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Products_trade_columns_are_typed_and_tokened()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        Assert.Equal(
            "datetime(3)",
            entity.FindProperty(nameof(Product.TransactionAcceptedAt))!.GetColumnType());
        Assert.Equal(
            "datetime(3)",
            entity.FindProperty(nameof(Product.BuyerConfirmedAt))!.GetColumnType());
        Assert.Equal(
            "datetime(3)",
            entity.FindProperty(nameof(Product.SellerConfirmedAt))!.GetColumnType());

        // The concurrency token. MySQL has no rowversion type, so the context bumps this column by
        // hand on every modification; drop IsConcurrencyToken and every read-modify-write in
        // TransactionService silently loses updates instead of throwing.
        var version = entity.FindProperty(nameof(Product.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal("int unsigned", version.GetColumnType());
    }

    [Fact]
    public void The_trade_sweep_has_a_covering_index()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        var indexNames = entity.GetIndexes()
            .Select(x => x.GetDatabaseName())
            .OfType<string>()
            .ToArray();

        // Both second-stage scans read WHERE status = InTransaction AND transaction_accepted_at < cutoff.
        Assert.Contains("idx_products_status_transaction_accepted_at", indexNames);
    }

    [Fact]
    public void Columns_are_snake_cased_including_the_reserved_word_condition()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        Assert.Equal("condition", entity.FindProperty(nameof(Product.Condition))!.GetColumnName());
        Assert.Equal("last_activity_at", entity.FindProperty(nameof(Product.LastActivityAt))!.GetColumnName());
        Assert.Equal("dormitory_area_id", entity.FindProperty(nameof(Product.DormitoryAreaId))!.GetColumnName());
        Assert.Equal("seller_id", entity.FindProperty(nameof(Product.SellerId))!.GetColumnName());
    }

    [Fact]
    public void Draft_cleanup_scan_has_a_covering_index()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Product))!;

        var indexNames = entity.GetIndexes()
            .Select(x => x.GetDatabaseName())
            .OfType<string>()
            .ToArray();

        Assert.Contains("idx_products_status_last_activity_at", indexNames);
    }

    /// <summary>
    /// The decision this stage was most likely to get wrong, and the one a later edit would silently
    /// flip: deleting a listing must not delete the buyer's side of the conversation.
    /// </summary>
    [Fact]
    public void Conversation_to_product_is_set_null_so_threads_outlive_a_deleted_listing()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Conversation))!;

        var foreignKey = entity.GetForeignKeys()
            .Single(x => x.PrincipalEntityType.ClrType == typeof(Product));

        Assert.Equal(DeleteBehavior.SetNull, foreignKey.DeleteBehavior);

        // Nullable, or SET NULL would be rejected by the schema itself.
        Assert.True(foreignKey.Properties.Single().IsNullable);
    }

    [Fact]
    public void Conversation_to_user_foreign_keys_are_restrict()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Conversation))!;

        var behaviours = entity.GetForeignKeys()
            .Where(x => x.PrincipalEntityType.ClrType == typeof(User))
            .Select(x => x.DeleteBehavior)
            .ToArray();

        // Buyer, seller and whoever proposed a trade — three edges onto users, all Restrict. An
        // accidental Cascade onto users would be caught here.
        Assert.Equal(3, behaviours.Length);
        Assert.All(behaviours, x => Assert.Equal(DeleteBehavior.Restrict, x));
    }

    [Fact]
    public void Message_to_conversation_is_cascade_and_to_user_is_restrict()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Message))!;

        Assert.Equal(
            DeleteBehavior.Cascade,
            entity.GetForeignKeys()
                .Single(x => x.PrincipalEntityType.ClrType == typeof(Conversation))
                .DeleteBehavior);

        Assert.Equal(
            DeleteBehavior.Restrict,
            entity.GetForeignKeys()
                .Single(x => x.PrincipalEntityType.ClrType == typeof(User))
                .DeleteBehavior);
    }

    [Fact]
    public void Conversation_columns_are_snake_cased()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Conversation))!;

        Assert.Equal("product_id", entity.FindProperty(nameof(Conversation.ProductId))!.GetColumnName());
        Assert.Equal("product_title", entity.FindProperty(nameof(Conversation.ProductTitle))!.GetColumnName());
        Assert.Equal("buyer_id", entity.FindProperty(nameof(Conversation.BuyerId))!.GetColumnName());
        Assert.Equal("seller_id", entity.FindProperty(nameof(Conversation.SellerId))!.GetColumnName());
        Assert.Equal("last_message_at", entity.FindProperty(nameof(Conversation.LastMessageAt))!.GetColumnName());
        Assert.Equal(
            "buyer_last_read_at",
            entity.FindProperty(nameof(Conversation.BuyerLastReadAt))!.GetColumnName());
        Assert.Equal(
            "seller_last_read_at",
            entity.FindProperty(nameof(Conversation.SellerLastReadAt))!.GetColumnName());
    }

    [Fact]
    public void Conversation_has_a_unique_index_per_listing_and_buyer()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Conversation))!;

        var indexNames = entity.GetIndexes()
            .Select(x => x.GetDatabaseName())
            .OfType<string>()
            .ToArray();

        Assert.Contains("uk_conversations_product_id_buyer_id", indexNames);
        Assert.Contains("idx_conversations_buyer_id_last_message_at", indexNames);
        Assert.Contains("idx_conversations_seller_id_last_message_at", indexNames);

        // The sweep's first phase reads WHERE transaction_proposed_at < cutoff. "IS NOT NULL" says
        // nothing extra: the two proposal columns are written and cleared as a pair, and a null never
        // satisfies a comparison anyway.
        Assert.Contains("idx_conversations_transaction_proposed_at", indexNames);
    }

    [Fact]
    public void Messages_has_a_conversation_id_index()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Message))!;

        var indexNames = entity.GetIndexes()
            .Select(x => x.GetDatabaseName())
            .OfType<string>()
            .ToArray();

        Assert.Contains("idx_messages_conversation_id_id", indexNames);
    }

    [Fact]
    public void Message_content_is_capped_at_500()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(Message))!;

        Assert.Equal(500, entity.FindProperty(nameof(Message.Content))!.GetMaxLength());
    }

    [Fact]
    public void Users_role_is_tinyint_unsigned_defaulting_to_the_ordinary_role()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(User))!;

        var role = entity.FindProperty(nameof(User.Role))!;

        Assert.Equal("tinyint unsigned", role.GetColumnType());
        Assert.Equal(UserRole.User, role.GetDefaultValue());
    }

    [Fact]
    public void Users_reset_columns_are_nullable_and_typed()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(User))!;

        var code = entity.FindProperty(nameof(User.PasswordResetCode))!;

        // Nullable with no default, and that is the point: every other column on this entity is either
        // non-null or has a sentinel, because they hold state every account has. These two hold state most
        // accounts do not have at all, and "no reset in flight" has to be distinguishable from "a reset
        // code that happens to be empty" — the redemption path hashes what the user typed and compares, so
        // an empty string that defaulted its way in would be a matchable credential.
        Assert.True(code.IsNullable);
        Assert.Null(code.GetDefaultValue());
        Assert.Equal(64, code.GetMaxLength());

        var expiry = entity.FindProperty(nameof(User.PasswordResetExpiresAt))!;

        Assert.True(expiry.IsNullable);
        Assert.Null(expiry.GetDefaultValue());

        // Same wall-clock column type as every other timestamp in the schema, so a comparison against
        // AppDbContext.AuditNow is comparing like with like.
        Assert.Equal("datetime(3)", expiry.GetColumnType());
    }

    [Fact]
    public void Users_last_seen_column_is_nullable_and_typed()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(User))!;

        var lastSeen = entity.FindProperty(nameof(User.LastSeenAt))!;

        // Nullable with no default. The online count compares this column against a cutoff, so a row that
        // has never been seen has to evaluate false to that comparison rather than satisfy it — a default
        // of, say, the epoch or the migration date would put every account in the count until it aged out,
        // and a non-nullable column could not express "never" at all.
        Assert.True(lastSeen.IsNullable);
        Assert.Null(lastSeen.GetDefaultValue());

        // Same wall-clock column type as every other timestamp in the schema, so the cutoff computed from
        // AppDbContext.AuditNow is comparing like with like.
        Assert.Equal("datetime(3)", lastSeen.GetColumnType());
    }
}
