namespace NCUT_Market.Core.Enums;

/// <summary>
/// Which of the two piles a piece of feedback belongs in.
/// </summary>
/// <remarks>
/// The split is the whole point of the board: the administrator reads this column to decide whether
/// the work ahead is fixing code or writing it, so the two members are two kinds of work rather than
/// two degrees of the same one.
/// </remarks>
public enum FeedbackKind : byte
{
    /// <summary>Something on the site is broken.</summary>
    Bug = 1,

    /// <summary>Something on the site is missing.</summary>
    Feature = 2
}
