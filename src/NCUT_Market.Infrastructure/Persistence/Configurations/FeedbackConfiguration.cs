using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.ToTable("feedbacks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Content).HasMaxLength(1000).IsRequired();

        // No default and no sentinel: there is no obvious "unset" kind to fall back to, so the column
        // is simply required and the service refuses anything outside the two members. A row with a
        // 0 here would mean something wrote without checking.
        builder.Property(x => x.Kind)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .IsRequired();

        // The enum starts at 1, so 0 is never a legitimate value. Declaring it as the sentinel makes
        // explicit that the database default applies only when Status was genuinely left unset.
        builder.Property(x => x.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint unsigned")
            .HasDefaultValue(FeedbackStatus.Open)
            .HasSentinel((FeedbackStatus)0);

        builder.Property(x => x.IsAnonymous).HasColumnType("tinyint(1)").HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        // Restrict, not Cascade: users are disabled rather than deleted, so a user row must never be
        // able to take the board with it. No navigation on User — nothing reads the other direction,
        // the same call ConversationConfiguration makes for its third edge onto the same table.
        builder.HasOne(x => x.Author)
            .WithMany()
            .HasForeignKey(x => x.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        // Covers the tie-break half of "most voted first, then newest". The vote count itself is a
        // correlated COUNT over feedback_votes and has no column on this table to index.
        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("idx_feedbacks_created_at");
    }
}
