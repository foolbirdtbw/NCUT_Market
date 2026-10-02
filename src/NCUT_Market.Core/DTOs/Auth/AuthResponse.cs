namespace NCUT_Market.Core.DTOs.Auth;

/// <summary>
/// The account behind the current request.
/// </summary>
/// <param name="Id">Primary key. Becomes <c>sellerId</c> on the products this user lists.</param>
/// <param name="Username">Login name.</param>
/// <param name="Nickname">Display name shown on listings.</param>
/// <remarks>
/// There is no <c>status</c> member. Every path that produces this DTO has already established the
/// account is active — a disabled account cannot hold a valid token, and login refuses one — so the
/// field would be a constant. Same reasoning as the dictionary DTOs.
/// </remarks>
public sealed record CurrentUserResponse(long Id, string Username, string Nickname);

/// <summary>
/// A successful register or login.
/// </summary>
/// <param name="Token">Signed JWT. Sent as <c>Authorization: Bearer &lt;token&gt;</c>.</param>
/// <param name="ExpiresAt">
/// When the token stops being accepted, in Beijing time with no timezone suffix — the same
/// convention as every other timestamp the API emits.
/// </param>
/// <param name="User">Who the token is for, so the client need not decode it.</param>
/// <remarks>
/// Registration returns this rather than a bare 201 with a Location header, so a new user does not
/// have to make a second round trip to log in with the credentials they just typed.
/// </remarks>
public sealed record AuthResponse(string Token, DateTime ExpiresAt, CurrentUserResponse User);
