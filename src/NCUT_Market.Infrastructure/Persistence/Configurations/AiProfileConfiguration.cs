using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class AiProfileConfiguration : IEntityTypeConfiguration<AiProfile>
{
    public void Configure(EntityTypeBuilder<AiProfile> builder)
    {
        builder.ToTable("ai_profiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Summary).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Keywords).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CategoryName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        // Users are disabled rather than deleted, so Restrict is the same call every other edge onto
        // users in this schema makes. Closing an account removes this row explicitly.
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // One profile per account. The service reads it by user id on every hints request, so the
        // unique index is also the access path.
        builder.HasIndex(x => x.UserId)
            .IsUnique()
            .HasDatabaseName("uk_ai_profiles_user_id");
    }
}
