namespace NCUT_Market.Core.Entities;

/// <summary>
/// One account's upvote on one <see cref="Feedback"/>.
/// </summary>
/// <remarks>
/// <para>
/// "One vote each" is enforced by a unique index rather than by the service. The service looks the row
/// up and then inserts or removes it, and two taps arriving together can both pass that lookup; the
/// index is what actually makes the rule true.
/// </para>
/// <para>
/// Only <see cref="IHasCreatedAt"/> — a vote is never edited. Taking a vote back deletes the row and
/// changing your mind again inserts a new one, so there is no <c>updated_at</c> for this table to hold.
/// </para>
/// </remarks>
public sealed class FeedbackVote : IHasCreatedAt
{
    public long Id { get; set; }

    public long FeedbackId { get; set; }

    public long UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Feedback Feedback { get; set; } = null!;

    public User User { get; set; } = null!;
}
