namespace NCUT_Market.Core.DTOs.Feedbacks;

/// <summary>
/// The board's answer to one tap on a vote button.
/// </summary>
/// <param name="VoteCount">The count after the toggle, recounted rather than nudged by one.</param>
/// <param name="HasVoted">Whether the caller's vote is now on.</param>
/// <remarks>
/// Only the button changes client-side. The list is ordered by vote count, so re-fetching here would
/// make the row the reader just tapped jump to a new position under the cursor; the new order arrives
/// on the next visit to the page.
/// </remarks>
public sealed record FeedbackVoteResponse(int VoteCount, bool HasVoted);
