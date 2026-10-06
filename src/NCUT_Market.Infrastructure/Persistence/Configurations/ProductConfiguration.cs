using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_price", "`price` >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasColumnType("text");
        builder.Property(x => x.Price).HasPrecision(10, 2).HasDefaultValue(0m);

        // "condition" is a MySQL reserved word (stored-program DECLARE ... CONDITION). The provider
        // back-quotes identifiers everywhere, so DDL and LINQ are safe — but hand-written SQL in the
        // draft cleanup job must write `condition`, not condition.
        builder.Property(x => x.Condition)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .IsRequired();

        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(ProductStatus.Draft)
            .HasSentinel((ProductStatus)0);

        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.LastActivityAt).HasColumnType("datetime(3)");
        builder.Property(x => x.PublishedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.SoldAt).HasColumnType("datetime(3)");
        builder.Property(x => x.TransactionAcceptedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.BuyerConfirmedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.SellerConfirmedAt).HasColumnType("datetime(3)");

        // MySQL has no rowversion type, so this is an ordinary column the context bumps by hand on
        // every modification (see ApplyAuditTimestamps) and EF compares in the WHERE clause. Unsigned
        // because it only ever counts up; int rather than bigint because four billion writes to one
        // listing is not a thing that happens.
        builder.Property(x => x.Version)
            .HasConversion<uint>()
            .HasColumnType("int unsigned")
            .HasDefaultValue(0u)
            .IsConcurrencyToken();

        // Restricted on purpose: neither disabling a user, nor retiring a category or an area,
        // may take listings down with it.
        builder.HasOne(x => x.Seller)
            .WithMany(x => x.Products)
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Category)
            .WithMany(x => x.Products)
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DormitoryArea)
            .WithMany(x => x.Products)
            .HasForeignKey(x => x.DormitoryAreaId)
            .OnDelete(DeleteBehavior.Restrict);

        // The trade's buyer, which is a second edge onto User and so is named explicitly. Restrict for
        // the same reason as Seller: users are disabled, never deleted, and a user row must not be able
        // to take a listing with it.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.TransactionBuyerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SellerId).HasDatabaseName("idx_products_seller_id");
        builder.HasIndex(x => x.CategoryId).HasDatabaseName("idx_products_category_id");
        builder.HasIndex(x => x.DormitoryAreaId).HasDatabaseName("idx_products_dormitory_area_id");

        // Named like its three siblings above rather than left to the provider's IX_* default. This
        // one is not read by any query — it exists only because MySQL requires an index on the
        // referencing side of a foreign key — which is exactly why it should be identifiable by name
        // when someone is looking at the table wondering what it is for.
        builder.HasIndex(x => x.TransactionBuyerId)
            .HasDatabaseName("idx_products_transaction_buyer_id");

        // Drives the 7-day draft cleanup scan: WHERE status = Draft AND last_activity_at < @cutoff.
        builder.HasIndex(x => new { x.Status, x.LastActivityAt })
            .HasDatabaseName("idx_products_status_last_activity_at");

        // Drives the listing feed, newest first.
        builder.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("idx_products_status_created_at");

        // Drives all four of the sweep's second-stage scans, which all read
        // WHERE status = InTransaction AND transaction_accepted_at < @cutoff.
        builder.HasIndex(x => new { x.Status, x.TransactionAcceptedAt })
            .HasDatabaseName("idx_products_status_transaction_accepted_at");
    }
}
