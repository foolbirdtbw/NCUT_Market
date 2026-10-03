namespace NCUT_Market.Core.Entities;

/// <summary>
/// One message in a <see cref="Conversation"/>. Immutable once written — there is no edit and no
/// delete, so the thread is a faithful record of what was said.
/// </summary>
public sealed class Message : IHasCreatedAt
{
    public long Id { get; set; }

    public long ConversationId { get; set; }

    public long SenderId { get; set; }

    /// <summary>The text as typed, trimmed. Capped at 500 characters, matching the column.</summary>
    public required string Content { get; set; }

    public DateTime CreatedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public User Sender { get; set; } = null!;
}
