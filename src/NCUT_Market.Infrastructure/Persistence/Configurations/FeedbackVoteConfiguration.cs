using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class FeedbackVoteConfiguration : IEntityTypeConfiguration<FeedbackVote>
{
    public void Configure(EntityTypeBuilder<FeedbackVote> builder)
    {
        builder.ToTable("feedback_votes");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");

        // Voters are users and users are disabled rather than deleted, so Restrict — the same call
        // every other edge onto users in this schema makes. No navigation on User, as nothing reads it.
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Cascade, the opposite call and deliberately so: a vote has no meaning apart from the post it
        // is on, so an administrator removing a post must not leave its votes behind pointing at
        // nothing. Same relationship Message has to Conversation.
        builder.HasOne(x => x.Feedback)
            .WithMany(x => x.Votes)
            .HasForeignKey(x => x.FeedbackId)
            .OnDelete(DeleteBehavior.Cascade);

        // One vote per person per post. This is the constraint the whole feature rests on, and it is
        // here rather than in the service because a find-then-insert pair is not atomic.
        builder.HasIndex(x => new { x.FeedbackId, x.UserId })
            .IsUnique()
            .HasDatabaseName("uk_feedback_votes_feedback_id_user_id");

        // The other direction: "which of these have I already voted for", which the list projection
        // asks once per row. The unique index above cannot serve it — user_id is its trailing column.
        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("idx_feedback_votes_user_id");
    }
}
