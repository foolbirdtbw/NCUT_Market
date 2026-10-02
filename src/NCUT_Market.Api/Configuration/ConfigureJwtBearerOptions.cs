using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NCUT_Market.Infrastructure.Security;

namespace NCUT_Market.Api.Configuration;

/// <summary>
/// Builds the bearer handler's validation parameters from <see cref="JwtOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The parameters are constructed here rather than inside a lambda passed to <c>AddJwtBearer</c> so
/// that they are read from <see cref="IOptions{TOptions}"/> at the moment the handler is first
/// resolved. That indirection is what lets the HTTP test factory substitute its own signing key
/// with a <c>PostConfigure</c>: a lambda capturing the configuration value directly would have
/// baked it in before the test could reach it, and the tests would silently run against whatever
/// key the developer's .env happens to hold.
/// </para>
/// <para>
/// The missing-key check throws here, and Program.cs forces this to resolve during startup, so the
/// API refuses to boot rather than signing tokens with an empty key. That failure mode is worth
/// being loud about: tokens signed with a blank key are trivially forgeable, and nothing about the
/// running application looks wrong.
/// </para>
/// </remarks>
internal sealed class ConfigureJwtBearerOptions(IOptions<JwtOptions> jwtOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        // IConfigureNamedOptions is called once per scheme; only the default one is ours.
        if (name == JwtBearerDefaults.AuthenticationScheme)
        {
            Configure(options);
        }
    }

    public void Configure(JwtBearerOptions options)
    {
        var settings = jwtOptions.Value;

        if (string.IsNullOrWhiteSpace(settings.SigningKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set Jwt__SigningKey in .env — see .env.example.");
        }

        byte[] key;

        try
        {
            key = Convert.FromBase64String(settings.SigningKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not valid base64. Regenerate it with `openssl rand -base64 48`.",
                exception);
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey is {key.Length} bytes; HMAC-SHA256 needs at least 32.");
        }

        // Inbound claims are left as they were signed. With mapping on, the handler rewrites `sub`
        // to ClaimTypes.NameIdentifier and `unique_name` to ClaimTypes.Name, so the claim a token
        // actually carries is not the one the code has to ask for — and a token minted by anything
        // other than this handler stops being readable.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateLifetime = true
        };
    }
}
