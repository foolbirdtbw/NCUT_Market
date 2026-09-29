using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Username).HasMaxLength(50).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Nickname).HasMaxLength(50).IsRequired();
        builder.Property(x => x.AvatarKey).HasMaxLength(255);
        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(UserStatus.Active)
            .HasSentinel((UserStatus)0);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        builder.HasIndex(x => x.Username).IsUnique().HasDatabaseName("uk_users_username");
    }
}
