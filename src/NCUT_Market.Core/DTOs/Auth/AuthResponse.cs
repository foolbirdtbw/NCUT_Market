using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Auth;

/// <summary>
/// The account behind the current request.
/// </summary>
/// <param name="Id">Primary key. Becomes <c>sellerId</c> on the products this user lists.</param>
/// <param name="Username">Login name.</param>
/// <param name="Nickname">Display name shown on listings.</param>
/// <param name="Role">
/// Whether this account may publish announcements, carried so the frontend can decide whether to
/// render the admin form at all.
/// </param>
/// <remarks>
/// <para>
/// There is no <c>status</c> member. Every path that produces this DTO has already established the
/// account is active — a disabled account cannot hold a valid token, and login refuses one — so the
/// field would be a constant. Same reasoning as the dictionary DTOs.
/// </para>
/// <para>
/// <paramref name="Role"/> is the reverse case, and is here precisely because it is <em>not</em>
/// settled by authentication: it is read fresh from the row on each request, so this value is only a
/// hint for what to draw. The API re-checks it against the database on every admin call, and a client
/// that ignores this field entirely — or lies about it — changes nothing about what it may do.
/// </para>
/// </remarks>
public sealed record CurrentUserResponse(long Id, string Username, string Nickname, UserRole Role);

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
