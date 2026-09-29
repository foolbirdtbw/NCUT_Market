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

        builder.HasIndex(x => x.SellerId).HasDatabaseName("idx_products_seller_id");
        builder.HasIndex(x => x.CategoryId).HasDatabaseName("idx_products_category_id");
        builder.HasIndex(x => x.DormitoryAreaId).HasDatabaseName("idx_products_dormitory_area_id");

        // Drives the 7-day draft cleanup scan: WHERE status = Draft AND last_activity_at < @cutoff.
        builder.HasIndex(x => new { x.Status, x.LastActivityAt })
            .HasDatabaseName("idx_products_status_last_activity_at");

        // Drives the listing feed, newest first.
        builder.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("idx_products_status_created_at");
    }
}
