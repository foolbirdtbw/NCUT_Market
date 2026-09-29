using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SortOrder).HasDefaultValue(0);
        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(CategoryStatus.Active)
            .HasSentinel((CategoryStatus)0);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        // A category that still has children must not be removable out from under them.
        builder.HasOne(x => x.Parent)
            .WithMany(x => x.Children)
            .HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ParentId).HasDatabaseName("idx_categories_parent_id");
        builder.HasIndex(x => new { x.Status, x.SortOrder })
            .HasDatabaseName("idx_categories_status_sort_order");

        // Name is deliberately not unique: MySQL treats every NULL parent_id as distinct, so a
        // UNIQUE (parent_id, name) would not stop duplicate top-level categories anyway. Duplicate
        // detection belongs in the application layer.
    }
}
