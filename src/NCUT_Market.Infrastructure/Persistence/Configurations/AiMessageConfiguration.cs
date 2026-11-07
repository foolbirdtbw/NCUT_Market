using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class AiMessageConfiguration : IEntityTypeConfiguration<AiMessage>
{
    public void Configure(EntityTypeBuilder<AiMessage> builder)
    {
        builder.ToTable("ai_messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Question).HasMaxLength(400).IsRequired();
        builder.Property(x => x.Answer).HasMaxLength(400).IsRequired();
        builder.Property(x => x.ResultsJson).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");

        // Same call as every other edge onto users: accounts are disabled rather than deleted, and
        // closing one removes these rows explicitly.
        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The only query there is: this account's rows, newest first, a page at a time.
        builder.HasIndex(x => new { x.UserId, x.Id })
            .HasDatabaseName("ix_ai_messages_user_id_id");
    }
}
