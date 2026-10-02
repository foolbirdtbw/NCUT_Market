using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NCUT_Market.Api.Configuration;

/// <summary>
/// Declares the bearer scheme in the generated OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// Without this the document describes every request but never says how to authenticate one, so the
/// Scalar page has no place to paste a token and the only way to try an authenticated endpoint is to
/// leave the browser and hand-write curl. The endpoints themselves work either way; this is about the
/// documentation being usable.
/// </para>
/// <para>
/// The scheme is taken from the registered authentication schemes rather than written in, so if the
/// bearer handler is ever replaced the document follows it instead of describing an API that is no
/// longer there.
/// </para>
/// </remarks>
internal sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider)
    : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemes = await schemeProvider.GetAllSchemesAsync();

        if (!schemes.Any(scheme => scheme.Name == JwtBearerDefaults.AuthenticationScheme))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();

        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[JwtBearerDefaults.AuthenticationScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Sign in at POST /api/auth/login and paste the token field here."
        };
    }
}
