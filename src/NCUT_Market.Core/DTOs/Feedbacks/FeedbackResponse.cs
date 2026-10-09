using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Feedbacks;

/// <summary>
/// One post on the feedback board, as everyone sees it.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Content">Body text. Plain text; the frontend escapes it and keeps its newlines.</param>
/// <param name="Kind">Bug report or feature request.</param>
/// <param name="Status">Where an administrator has moved it. Stays <c>Open</c> until one does.</param>
/// <param name="AuthorNickname">
/// The poster's nickname, or null when they posted anonymously. That null <em>is</em> the anonymity
/// guarantee: <c>author_id</c> is written to the row on every post, but it is not a member of this
/// DTO, so no response can leak it — including to an administrator.
/// </param>
/// <param name="CreatedAt">Beijing time, no timezone suffix.</param>
/// <param name="VoteCount">How many accounts have voted for it. One vote each.</param>
/// <param name="HasVoted">
/// Whether the caller is one of them. Always false for an anonymous caller, which is what the SQL
/// gives for free: the comparison is against a null parameter, and null equals nothing.
/// </param>
public sealed record FeedbackResponse(
    long Id,
    string Content,
    FeedbackKind Kind,
    FeedbackStatus Status,
    string? AuthorNickname,
    DateTime CreatedAt,
    int VoteCount,
    bool HasVoted);
