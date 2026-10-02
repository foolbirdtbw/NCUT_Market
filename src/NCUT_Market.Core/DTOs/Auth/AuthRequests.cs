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
/// </remarks>
public sealed record RegisterRequest(
    [param: Required, StringLength(50, MinimumLength = 3)] string Username,
    [param: Required, StringLength(128, MinimumLength = 6)] string Password,
    [param: Required, StringLength(50, MinimumLength = 1)] string Nickname);

/// <summary>Credentials for an existing account.</summary>
/// <param name="Username">Login name.</param>
/// <param name="Password">Plain text.</param>
public sealed record LoginRequest(
    [param: Required] string Username,
    [param: Required] string Password);
