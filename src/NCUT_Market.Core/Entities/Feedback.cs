using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

/// <summary>
/// A bug report or feature request from a signed-in user, readable by everyone and voted on by anyone
/// with an account.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shapeless beyond the body: no title, no attachment, no reply thread. The board is read
/// to decide what to build next, and one paragraph plus a vote count is what that decision needs.
/// </para>
/// <para>
/// <see cref="AuthorId"/> is written on every post, <see cref="IsAnonymous"/> or not. Nothing in the
/// API returns it — see <c>FeedbackResponse</c> — but keeping it is the difference between moderation
/// and a hole, and it costs one column.
/// </para>
/// </remarks>
public sealed class Feedback : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long AuthorId { get; set; }

    public required string Content { get; set; }

    public FeedbackKind Kind { get; set; }

    public FeedbackStatus Status { get; set; } = FeedbackStatus.Open;

    /// <summary>
    /// Keep the poster's nickname off the board. It hides the name, not the row: the post still shows
    /// up for everyone, and <see cref="AuthorId"/> is still recorded.
    /// </summary>
    public bool IsAnonymous { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public User Author { get; set; } = null!;

    public ICollection<FeedbackVote> Votes { get; } = [];
}
