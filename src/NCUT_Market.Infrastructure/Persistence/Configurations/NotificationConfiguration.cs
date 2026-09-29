using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Type)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .IsRequired();
        builder.Property(x => x.Title).HasMaxLength(100).IsRequired();

        // Holds the message already rendered with the product name in it. That is what makes the
        // notification survive the product being hard-deleted.
        builder.Property(x => x.Content).HasMaxLength(500).IsRequired();

        builder.Property(x => x.IsRead).HasColumnType("tinyint(1)").HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.ReadAt).HasColumnType("datetime(3)");

        // Restrict, not Cascade: users are disabled rather than deleted, so a user row must never be
        // able to silently take a notification history with it.
        builder.HasOne(x => x.User)
            .WithMany(x => x.Notifications)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The opposite call, and deliberately so: the draft cleanup job hard-deletes products, and
        // the notification must outlive that. Content is already self-contained.
        builder.HasOne(x => x.RelatedProduct)
            .WithMany(x => x.Notifications)
            .HasForeignKey(x => x.RelatedProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // Drives the unread badge and the notification list.
        builder.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt })
            .HasDatabaseName("idx_notifications_user_id_is_read_created_at");
        builder.HasIndex(x => x.RelatedProductId)
            .HasDatabaseName("idx_notifications_related_product_id");
    }
}
