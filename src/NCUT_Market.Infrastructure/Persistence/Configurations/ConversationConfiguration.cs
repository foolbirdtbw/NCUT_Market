using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCUT_Market.Core.Entities;

namespace NCUT_Market.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProductTitle).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ProductThumbnailKey).HasMaxLength(255).IsRequired();

        builder.Property(x => x.LastMessageAt).HasColumnType("datetime(3)");
        builder.Property(x => x.BuyerLastReadAt).HasColumnType("datetime(3)");
        builder.Property(x => x.SellerLastReadAt).HasColumnType("datetime(3)");
        builder.Property(x => x.TransactionProposedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetime(3)");
        builder.Property(x => x.UpdatedAt).HasColumnType("datetime(3)");

        // SET NULL, not CASCADE, for the reason NotificationConfiguration already spells out for the
        // same situation: ProductService hard-deletes draft and offline listings, and a thread has to
        // outlive that. Cascading would mean a seller deleting their own listing destroys the buyer's
        // message history — the other party's data, gone as a side effect of someone else's click.
        // ProductTitle and ProductThumbnailKey are frozen at creation to keep the row readable once
        // ProductId is null.
        builder.HasOne(x => x.Product)
            .WithMany(x => x.Conversations)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // Both sides Restrict: users are disabled rather than deleted, so a user row must never be
        // able to take a thread with it. Two navigations point at User, so both are named explicitly.
        builder.HasOne(x => x.Buyer)
            .WithMany(x => x.BuyerConversations)
            .HasForeignKey(x => x.BuyerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Seller)
            .WithMany(x => x.SellerConversations)
            .HasForeignKey(x => x.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        // A third edge onto User — whoever proposed a trade here, which is either side. No navigation,
        // because nothing reads it: the only thing done with this column is addressing a notification
        // to its value.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.TransactionProposedById)
            .OnDelete(DeleteBehavior.Restrict);

        // One thread per buyer per listing. MySQL permits repeated NULLs in a unique index, so this
        // also stops a second thread reappearing for the same pair after the listing is deleted.
        builder.HasIndex(x => new { x.ProductId, x.BuyerId })
            .IsUnique()
            .HasDatabaseName("uk_conversations_product_id_buyer_id");

        // "My threads, newest first" is an OR over the two sides, so each arm gets its own index.
        builder.HasIndex(x => new { x.BuyerId, x.LastMessageAt })
            .HasDatabaseName("idx_conversations_buyer_id_last_message_at");
        builder.HasIndex(x => new { x.SellerId, x.LastMessageAt })
            .HasDatabaseName("idx_conversations_seller_id_last_message_at");

        // The sweep's first phase. Indexed on its own rather than with TransactionProposedById,
        // because "IS NOT NULL AND < cutoff" says exactly what "< cutoff" says once you know the two
        // columns are written and cleared as a pair — and a null never satisfies a comparison anyway,
        // so the single-column index covers the whole predicate.
        builder.HasIndex(x => x.TransactionProposedAt)
            .HasDatabaseName("idx_conversations_transaction_proposed_at");
    }
}
