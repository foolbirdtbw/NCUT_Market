namespace NCUT_Market.Infrastructure.Security;

/// <summary>
/// Token signing and validation settings, bound from the <c>Jwt</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SigningKey"/> has no default and is not present in <c>appsettings.json</c> — it comes
/// from <c>.env</c> as <c>Jwt__SigningKey</c>. The API refuses to start without it. That is not
/// defensiveness: signing with an empty key produces tokens that anyone can forge, and the failure
/// is invisible from the outside, so the only safe moment to notice is at startup.
/// </para>
/// <para>
/// Settable properties rather than a positional record, because the options binder needs a
/// parameterless constructor — the same reason <c>PaginationQuery</c> is shaped this way.
/// </para>
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Token issuer (<c>iss</c> claim).</summary>
    public string Issuer { get; set; } = "ncut-market";

    /// <summary>Intended audience (<c>aud</c> claim).</summary>
    public string Audience { get; set; } = "ncut-market";

    /// <summary>
    /// HMAC-SHA256 signing key, base64. HS256 requires at least 32 bytes of key material; anything
    /// shorter is rejected when the token handler is configured.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Token lifetime in minutes. Defaults to seven days: this is a campus marketplace that people
    /// check occasionally, and there is no refresh flow, so a short expiry means a login prompt
    /// every time someone comes back.
    /// </summary>
    public int ExpireMinutes { get; set; } = 10080;
}
