using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcements");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Content).HasColumnType("text").IsRequired();
        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(AnnouncementStatus.Draft)
            .HasSentinel((AnnouncementStatus)0);
        builder.Property(x => x.PublishedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.ExpiredAt).HasColumnType("datetime(3)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        // Effective announcements are resolved as:
        //   status = Published AND published_at <= @now AND (expired_at IS NULL OR expired_at > @now)
        // so this index covers the status filter and the ordering.
        //
        // @now MUST be AppDbContext.AuditNow (or the same value passed in as a parameter) — never a
        // bare MySQL NOW(). The two columns hold Beijing time, because that is what AuditNow writes;
        // this MySQL instance is pinned to UTC by default-time-zone=+00:00 in my.ini, and that pin is
        // deliberate and shared with the EasyERP schema on the same server. Comparing the two clocks
        // is an eight-hour error: with published_at stamped from Beijing-now and NOW() returning
        // UTC-now, published_at > NOW(), so an announcement published this second would stay
        // invisible for eight hours. The same applies to CURRENT_TIMESTAMP defaults and to any
        // hand-written SQL in this project.
        builder.HasIndex(x => new { x.Status, x.PublishedAt })
            .HasDatabaseName("idx_announcements_status_published_at");
    }
}
