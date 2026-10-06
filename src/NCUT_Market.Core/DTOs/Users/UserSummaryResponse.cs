using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Users;

/// <summary>
/// One account as the administrator's user list shows it.
/// </summary>
/// <param name="Id">Primary key. The id the reset-code endpoint is addressed by.</param>
/// <param name="Username">Login name, which is what an administrator is usually given.</param>
/// <param name="Nickname">Display name, for when the administrator was given that instead.</param>
/// <param name="Role">Carried so the list can mark the other administrators.</param>
/// <param name="Status">
/// Active or disabled. Shown because it is the difference between "this user has forgotten their
/// password" and "this user was removed" — two cases an administrator must not answer the same way.
/// </param>
/// <param name="CreatedAt">Registration time, the only history this schema keeps about an account.</param>
/// <param name="PasswordResetExpiresAt">
/// When an outstanding reset code lapses, or null if none is pending. Without it an administrator has
/// only their memory to tell them whether they already handed this user a code.
/// </param>
/// <remarks>
/// There is no contact field to include: <c>users</c> has no email and no phone column, which is the
/// whole reason password recovery runs through an administrator in the first place.
/// </remarks>
public sealed record UserSummaryResponse(
    long Id,
    string Username,
    string Nickname,
    UserRole Role,
    UserStatus Status,
    DateTime CreatedAt,
    DateTime? PasswordResetExpiresAt);

/// <summary>
/// A freshly minted password-reset code, returned to the administrator who asked for it.
/// </summary>
/// <param name="ResetCode">The code, formatted <c>XXXX-XXXX</c>. This is the only time it is readable.</param>
/// <param name="ExpiresAt">
/// When it stops working, in Beijing time with no timezone suffix — the same convention as every other
/// timestamp the API emits.
/// </param>
/// <remarks>
/// Only the digest reaches the database, so this response cannot be replayed by asking again: a second
/// call mints a different code and silently invalidates this one. Losing the code is therefore recovered
/// from, not undone.
/// </remarks>
public sealed record ResetCodeResponse(string ResetCode, DateTime ExpiresAt);
