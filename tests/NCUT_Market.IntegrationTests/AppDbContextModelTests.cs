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

        var restrictedPrincipals = entity.GetForeignKeys()
            .Where(x => x.DeleteBehavior == DeleteBehavior.Restrict)
            .Select(x => x.PrincipalEntityType.ClrType)
            .ToHashSet();

        Assert.Equal(
            new HashSet<Type> { typeof(User), typeof(Category), typeof(DormitoryArea) },
            restrictedPrincipals);
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

        // Buyer and seller, both Restrict — an accidental Cascade onto users would be caught here.
        Assert.Equal(2, behaviours.Length);
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
}
