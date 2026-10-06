using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Auth;

/// <summary>
/// A new account. Registration signs the user in directly, so there is no separate first login.
/// </summary>
/// <param name="Username">Login name. Must be unique across the site.</param>
/// <param name="Password">Plain text, hashed before storage and never logged.</param>
/// <param name="Nickname">Display name shown on listings.</param>
/// <remarks>
/// <para>
/// The lengths mirror the column limits in <c>UserConfiguration</c> rather than being chosen
/// independently: a request that passes validation here must not then fail on insert. Password's
/// cap is the exception — the column stores a hash, so the limit exists only to bound the work an
/// unauthenticated caller can ask the hasher to do.
/// </para>
/// <para>
/// Every attribute is targeted at <c>param:</c>, not <c>property:</c>. MVC refuses to validate a
/// positional record whose metadata sits on the generated property — it throws at validation time
/// with "validation metadata defined on property ... will be ignored", which surfaces as a 500 on
/// every write endpoint rather than as a validation error.
/// </para>
/// <para>
/// The messages are written out rather than left to the default DataAnnotations text, which is
/// English and phrased for a developer ("The field Password must be a string with a minimum
/// length of..."). These strings are shown verbatim under the form that produced them.
/// </para>
/// </remarks>
public sealed record RegisterRequest(
    [param: Required(ErrorMessage = "请填用户名。")]
    [param: StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度要在 3 到 50 个字符之间。")]
    string Username,
    [param: Required(ErrorMessage = "请填密码。")]
    [param: StringLength(128, MinimumLength = 6, ErrorMessage = "密码长度要在 6 到 128 个字符之间。")]
    string Password,
    [param: Required(ErrorMessage = "请填昵称。")]
    [param: StringLength(50, MinimumLength = 1, ErrorMessage = "昵称最多 50 个字符。")]
    string Nickname);

/// <summary>Credentials for an existing account.</summary>
/// <param name="Username">Login name.</param>
/// <param name="Password">Plain text.</param>
public sealed record LoginRequest(
    [param: Required(ErrorMessage = "请填用户名。")] string Username,
    [param: Required(ErrorMessage = "请填密码。")] string Password);

/// <summary>
/// Exchanges a one-time reset code for a password of the user's own choosing.
/// </summary>
/// <param name="Username">Identifies the account. Landing here with a real code but the wrong username
/// fails the same way as a wrong code, so this pair cannot be probed one half at a time.</param>
/// <param name="ResetCode">
/// The code an administrator handed over, as typed. Hyphens, spaces and letter case are all tolerated;
/// see <c>ResetCode.Normalize</c>.
/// </param>
/// <param name="NewPassword">
/// Plain text, hashed before storage. Never the code, and never anything an administrator has seen —
/// which is the point of routing recovery through a code rather than through a temporary password.
/// </param>
/// <remarks>
/// The password bounds match <see cref="RegisterRequest"/>, so a password that would have been accepted
/// at sign-up is accepted here too. The username and code bounds are only there to bound the work a
/// caller can ask for; neither is a length the user chose.
/// </remarks>
public sealed record CompleteResetRequest(
    [param: Required(ErrorMessage = "请填用户名。")]
    [param: StringLength(50, ErrorMessage = "用户名最多 50 个字符。")]
    string Username,
    [param: Required(ErrorMessage = "请填重置码。")]
    [param: StringLength(32, ErrorMessage = "重置码最多 32 个字符。")]
    string ResetCode,
    [param: Required(ErrorMessage = "请填新密码。")]
    [param: StringLength(128, MinimumLength = 6, ErrorMessage = "密码长度要在 6 到 128 个字符之间。")]
    string NewPassword);
