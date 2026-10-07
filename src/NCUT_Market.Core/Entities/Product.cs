using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

public sealed class Product : IHasCreatedAt, IHasUpdatedAt, IHasVersion
{
    public long Id { get; set; }

    public long SellerId { get; set; }

    public long CategoryId { get; set; }

    public long DormitoryAreaId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public decimal Price { get; set; }

    /// <summary>Maps to the <c>condition</c> column — a MySQL reserved word, quoted by the provider.</summary>
    public ProductCondition Condition { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Draft;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Bumped only by real edits (title, description, price, category, condition, area, images) —
    /// never by viewing. The draft cleanup job deletes on this, so "opened the edit page" must not
    /// count as activity.
    /// </summary>
    public DateTime LastActivityAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? SoldAt { get; set; }

    /// <summary>
    /// When the seller cleared this listing out of their own list, or null while it is still theirs
    /// to manage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever set on a listing sold through the platform (<see cref="TransactionBuyerId"/> is not
    /// null). The trade record <i>is</i> the transaction columns on this row — there is no separate
    /// table — so hard-deleting it would blank <c>ConversationTradeResponse</c> and take the trade
    /// panel out of both parties' threads. Marking it instead keeps the panel and the whole
    /// accept / confirm-receipt / confirm-payment history readable on both sides.
    /// </para>
    /// <para>
    /// Every query that lists or opens a listing filters on this being null, so a removed listing is
    /// gone from the feed, from 我的商品 and from its own detail page (404 for everyone, the seller
    /// included). A listing the seller took down or marked sold by hand has no thread holding a
    /// record of it and is still hard-deleted.
    /// </para>
    /// </remarks>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The buyer of the trade accepted through the message thread, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what "sold to somebody" means, not <see cref="Status"/>.</b> A seller can still
    /// mark a listing sold by hand, with no counterparty at all, from the detail page — so
    /// <c>Status == Sold</c> covers both "a two-sided trade completed" and "the seller got rid of it".
    /// Any query that needs the first has to test this column.
    /// </para>
    /// <para>
    /// Which thread the trade came from is not stored: it is <c>(this listing, this buyer)</c>, and
    /// the unique index on the conversation table means there is exactly one candidate. Trade actions
    /// arrive addressed by thread id, so every one of them re-derives the pair and checks it — the
    /// seller is a participant in all of their threads and would otherwise be able to confirm a trade
    /// from an unrelated one.
    /// </para>
    /// </remarks>
    public long? TransactionBuyerId { get; set; }

    /// <summary>
    /// When the proposal was accepted. The second of the flow's two clocks: <c>AcceptedAt + 1 day</c>
    /// is when the sweep acts on a trade nobody has finished confirming.
    /// </summary>
    public DateTime? TransactionAcceptedAt { get; set; }

    /// <summary>
    /// When the buyer confirmed receipt. Doubles as the flag — null means "not yet".
    /// </summary>
    public DateTime? BuyerConfirmedAt { get; set; }

    /// <summary>When the seller confirmed payment. See <see cref="BuyerConfirmedAt"/>.</summary>
    public DateTime? SellerConfirmedAt { get; set; }

    /// <summary>
    /// Optimistic-concurrency token, bumped by <c>AppDbContext</c> on every update.
    /// </summary>
    /// <remarks>
    /// The trade flow is the first thing in this project where two people write the same row at the
    /// same time on purpose, and every one of its transitions is a read-modify-write. Without this,
    /// a buyer accepting a proposal at the instant the sweep is rolling it back leaves a listing
    /// marked "交易中" with no buyer — a live trade with no counterparty. See <see cref="IHasVersion"/>.
    /// </remarks>
    public uint Version { get; set; }

    public User Seller { get; set; } = null!;

    public Category Category { get; set; } = null!;

    public DormitoryArea DormitoryArea { get; set; } = null!;

    public ICollection<ProductImage> Images { get; } = [];

    public ICollection<Notification> Notifications { get; } = [];

    /// <summary>
    /// Threads opened about this listing. They outlive it: the foreign key is SET NULL, so deleting
    /// the product leaves the conversations behind with their title frozen. See
    /// <see cref="Conversation"/>.
    /// </summary>
    public ICollection<Conversation> Conversations { get; } = [];
}
