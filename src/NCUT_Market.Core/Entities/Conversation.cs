namespace NCUT_Market.Core.Entities;

/// <summary>
/// One buyer's private thread about one listing.
/// </summary>
/// <remarks>
/// <para>
/// A conversation exists only because somebody who does not own a listing opened one from its page —
/// the seller can reply but never start a thread. <see cref="SellerId"/> is denormalised from the
/// product for that reason: the list query is "threads where I am on either side", and carrying it
/// here saves a join on every page of it. Nothing in the project transfers a listing between users,
/// so it cannot drift.
/// </para>
/// <para>
/// <see cref="ProductTitle"/> and <see cref="ProductThumbnailKey"/> are frozen at creation for the
/// same reason <see cref="Notification"/> bakes the product name into its rendered text: the listing
/// can be hard-deleted afterwards and the thread still has to read correctly. That is also why
/// <see cref="ProductId"/> is nullable and its foreign key is SET NULL — deleting a listing must not
/// delete the buyer's half of the conversation.
/// </para>
/// </remarks>
public sealed class Conversation : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    /// <summary>Nulled out when the listing is hard-deleted. See the remarks on this type.</summary>
    public long? ProductId { get; set; }

    /// <summary>The listing's title as it read when the thread started.</summary>
    public required string ProductTitle { get; set; }

    /// <summary>
    /// Storage key of the listing's first thumbnail at creation, or the empty string when it had
    /// none — a key, not a URL. Turned into a URL in memory by the service, like every other image
    /// the API returns.
    /// </summary>
    public required string ProductThumbnailKey { get; set; }

    /// <summary>The side that opened the thread, by clicking through from the listing.</summary>
    public long BuyerId { get; set; }

    /// <summary>The listing's owner. Denormalised — see the remarks on this type.</summary>
    public long SellerId { get; set; }

    /// <summary>
    /// When the thread last saw a message, or when it was created if neither side has written yet.
    /// </summary>
    /// <remarks>
    /// Deliberately never stamped by <c>AppDbContext</c>: it is business data like
    /// <see cref="Product.LastActivityAt"/>, not an audit column. Every write that adds a message has
    /// to set it, and creation has to set it even with no messages — the column is not nullable.
    /// </remarks>
    public DateTime LastMessageAt { get; set; }

    /// <summary>
    /// How far the buyer has read. Never null: a new thread is stamped with its own creation instant,
    /// which is what lets "is there anything unread" be a plain column comparison in SQL instead of a
    /// COALESCE over a fallback value.
    /// </summary>
    public DateTime BuyerLastReadAt { get; set; }

    /// <summary>How far the seller has read. See <see cref="BuyerLastReadAt"/>.</summary>
    public DateTime SellerLastReadAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Product? Product { get; set; }

    public User Buyer { get; set; } = null!;

    public User Seller { get; set; } = null!;

    public ICollection<Message> Messages { get; } = [];
}
