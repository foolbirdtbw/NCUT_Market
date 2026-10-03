namespace NCUT_Market.Core.Enums;

/// <summary>
/// What an account may do beyond the ordinary.
/// </summary>
/// <remarks>
/// Read from the row on every administrative request rather than carried as a claim in the token, so
/// promoting an account with a hand-run UPDATE takes effect on that user's very next request instead
/// of requiring them to sign in again. See the note on <c>AnnouncementService</c>'s admin check.
/// </remarks>
public enum UserRole : byte
{
    /// <summary>An ordinary student account — everything except publishing announcements.</summary>
    User = 1,

    /// <summary>May publish and delete site announcements.</summary>
    Admin = 2
}
