using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("product_images", table =>
        {
            table.HasCheckConstraint("ck_product_images_width", "`width` > 0");
            table.HasCheckConstraint("ck_product_images_height", "`height` > 0");
            table.HasCheckConstraint("ck_product_images_file_size", "`file_size` > 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.LargeKey).HasMaxLength(255).IsRequired();
        builder.Property(x => x.MediumKey).HasMaxLength(255).IsRequired();
        builder.Property(x => x.ThumbnailKey).HasMaxLength(255).IsRequired();

        // Unsigned to match the agreed schema. A signed int would still hold any realistic pixel
        // count, but the column type is part of the contract and the provider honours it verbatim.
        builder.Property(x => x.Width).HasColumnType("int unsigned").IsRequired();
        builder.Property(x => x.Height).HasColumnType("int unsigned").IsRequired();
        builder.Property(x => x.FileSize).HasColumnType("bigint unsigned").IsRequired();

        builder.Property(x => x.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SortOrder).HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");

        // Images have no meaning without their product, so they go down with it.
        builder.HasOne(x => x.Product)
            .WithMany(x => x.Images)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProductId, x.SortOrder })
            .HasDatabaseName("idx_product_images_product_id_sort_order");
    }
}
