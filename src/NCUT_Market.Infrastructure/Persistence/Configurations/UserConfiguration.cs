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
        // Same shape as Status, and for the same reason: the enum starts at 1, so 0 is never a
        // legitimate value and the column default applies only when Role was genuinely left unset.
        builder.Property(x => x.Role)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(UserRole.User)
            .HasSentinel((UserRole)0);
        // Both nullable with no default: a user with no reset in flight has nothing in either column,
        // and the sentinel machinery above exists only for the non-nullable enums.
        builder.Property(x => x.PasswordResetCode).HasMaxLength(64);
        builder.Property(x => x.PasswordResetExpiresAt).HasColumnType("datetime(3)");
        // Nullable and unindexed. Null because "never seen" has to be distinguishable from any real
        // instant, and the online count compares against a cutoff that a default would satisfy.
        // Unindexed because the users table is small and this column is rewritten on every
        // authenticated request, so an index on it would cost more to maintain than it saves.
        builder.Property(x => x.LastSeenAt).HasColumnType("datetime(3)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        builder.HasIndex(x => x.Username).IsUnique().HasDatabaseName("uk_users_username");
    }
}
