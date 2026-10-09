namespace NCUT_Market.Core.Enums;

/// <summary>
/// Where a piece of feedback stands. Only an administrator moves it; nothing here changes on its own.
/// </summary>
/// <remarks>
/// Four values rather than a boolean "done", because the answer that matters to the administrator
/// later is not "was this dealt with" but "did we decide against it" — <see cref="Rejected"/> is the
/// row worth re-reading when the same request comes back a third time.
/// </remarks>
public enum FeedbackStatus : byte
{
    /// <summary>Nobody has looked at it yet. What a new post gets.</summary>
    Open = 1,

    /// <summary>Agreed to, not built yet.</summary>
    Accepted = 2,

    /// <summary>Shipped.</summary>
    Done = 3,

    /// <summary>Considered and turned down.</summary>
    Rejected = 4
}
