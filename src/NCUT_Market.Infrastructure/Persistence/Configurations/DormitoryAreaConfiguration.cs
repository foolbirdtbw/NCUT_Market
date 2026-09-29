using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class DormitoryAreaConfiguration : IEntityTypeConfiguration<DormitoryArea>
{
    public void Configure(EntityTypeBuilder<DormitoryArea> builder)
    {
        builder.ToTable("dormitory_areas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SortOrder).HasDefaultValue(0);
        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(DormitoryAreaStatus.Active)
            .HasSentinel((DormitoryAreaStatus)0);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("uk_dormitory_areas_name");
        builder.HasIndex(x => new { x.Status, x.SortOrder })
            .HasDatabaseName("idx_dormitory_areas_status_sort_order");
    }
}
