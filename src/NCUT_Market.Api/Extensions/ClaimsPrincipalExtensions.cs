using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NCUT_Market.Api.Extensions;

/// <summary>
/// Reads the caller's identity out of a validated token.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The user id carried in the token's subject claim.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <exception cref="InvalidOperationException">
    /// The subject claim is missing or unparseable.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The claim is read by its JWT name, <c>sub</c>, because <c>MapInboundClaims</c> is turned off
    /// when the bearer handler is configured. Left on, the handler silently renames inbound claims
    /// to their <c>ClaimTypes</c> equivalents and <c>sub</c> arrives as
    /// <c>ClaimTypes.NameIdentifier</c> — a mapping that works until someone constructs a token a
    /// different way and the claim quietly stops being found.
    /// </para>
    /// <para>
    /// Throwing rather than returning null is deliberate. This is only reachable from an
    /// <c>[Authorize]</c> endpoint, so a principal without a subject means the token handler
    /// validated something it should not have — a server-side defect. Reporting it as an unauthenticated
    /// request would hide that; a 500 is the honest answer.
    /// </para>
    /// </remarks>
    public static long GetUserId(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (string.IsNullOrEmpty(subject) || !long.TryParse(subject, CultureInfo.InvariantCulture, out var userId))
        {
            throw new InvalidOperationException(
                "The authenticated principal has no usable 'sub' claim.");
        }

        return userId;
    }
}
