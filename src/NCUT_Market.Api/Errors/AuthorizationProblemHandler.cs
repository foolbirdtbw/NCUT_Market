using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace NCUT_Market.Api.Errors;

/// <summary>
/// Rewrites authorization failures as problem+json.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a request that fails <c>[Authorize]</c> gets a bare status code and an empty body:
/// no <c>code</c>, no <c>traceId</c>, no <c>detail</c>. The frontend's error card reads all three,
/// so every 401 would render as "请求失败（HTTP 401）" with nothing to act on and nothing to quote
/// in a log search — the exact failure the error contract exists to prevent.
/// </para>
/// <para>
/// The response goes through <see cref="IProblemDetailsService"/>, so the <c>CustomizeProblemDetails</c>
/// callback registered in Program.cs still runs and inserts <c>traceId</c> and the status-derived
/// <c>code</c>. Writing the JSON by hand here would bypass that and produce a second, slightly
/// different problem shape.
/// </para>
/// </remarks>
internal sealed class AuthorizationProblemHandler : IAuthorizationMiddlewareResultHandler
{
    // The default handler is what actually invokes the endpoint when authorization succeeded, so
    // this wraps it rather than replacing it.
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Challenged && !authorizeResult.Forbidden)
        {
            await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        // Challenged means no usable identity was presented, so the client's move is to log in.
        // Forbidden means one was, and it is not allowed — a different problem with a different fix.
        var status = authorizeResult.Challenged
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;

        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();

        context.Response.StatusCode = status;

        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = authorizeResult.Challenged ? "Unauthorized" : "Forbidden",
                Detail = authorizeResult.Challenged
                    ? "Sign in to continue."
                    : "You do not have permission to do that."
            }
        });
    }
}
