using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(x => x.Id);

        // 500 to match the body column on notifications, which is the project's existing answer to
        // "how long is a short user-written block of text".
        builder.Property(x => x.Content).HasMaxLength(500).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");

        // A message has no meaning without its thread, so it goes down with it.
        builder.HasOne(x => x.Conversation)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, like every other foreign key pointing at a user.
        builder.HasOne(x => x.Sender)
            .WithMany(x => x.Messages)
            .HasForeignKey(x => x.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Drives the thread view, which pages by descending id.
        builder.HasIndex(x => new { x.ConversationId, x.Id })
            .HasDatabaseName("idx_messages_conversation_id_id");
    }
}
