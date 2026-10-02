using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Core.Common;

namespace NCUT_Market.Api.Errors;

/// <summary>
/// Builds RFC 7807 failure responses from a domain error code.
/// </summary>
/// <remarks>
/// <para>
/// The single place an <see cref="ErrorCodes"/> value becomes an HTTP status. Controllers used to
/// each build this by hand, which was fine for two endpoints and would not be for twenty: the
/// <c>ProblemDetailsFactory</c> plus indexer plus <c>ObjectResult</c> incantation is easy to write
/// slightly differently, and a client branching on <c>code</c> only needs one endpoint to disagree.
/// </para>
/// <para>
/// The mapping here is the inverse of <c>CodeForStatus</c> in Program.cs, which goes status → code.
/// They must agree: a code produced by one and interpreted by the other is how a 409 turns into a
/// 400 on the way out.
/// </para>
/// </remarks>
internal static class ProblemResults
{
    /// <summary>
    /// A problem+json response for a failed operation.
    /// </summary>
    /// <param name="controller">The controller the response is returned from.</param>
    /// <param name="errorCode">A value from <see cref="ErrorCodes"/>.</param>
    /// <param name="detail">The human-readable explanation.</param>
    /// <remarks>
    /// Returns <see cref="ActionResult"/> rather than <see cref="IActionResult"/> because
    /// <c>ActionResult&lt;T&gt;</c> has an implicit conversion from the former and not the latter.
    /// Actions that declare <c>ActionResult&lt;ProductDetailResponse&gt;</c> can therefore return
    /// this directly, while actions returning plain <c>IActionResult</c> still accept it.
    /// </remarks>
    public static ActionResult Failure(ControllerBase controller, string errorCode, string detail)
    {
        var status = StatusFor(errorCode);

        // Built through the factory and then poked with the indexer, rather than passing an
        // `extensions:` dictionary. The factory invokes CustomizeProblemDetails on its way out,
        // which has already inserted a status-derived `code` and a `traceId` — and Problem() adds
        // the caller's extensions with Add(), so supplying "code" there throws "An item with the
        // same key has already been added" and turns the intended status into a 400. The indexer
        // overwrites, which is what lets the more specific domain code win over the default.
        var problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: status,
            title: TitleFor(status),
            detail: detail);

        problemDetails.Extensions["code"] = errorCode;

        return new ObjectResult(problemDetails)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>The HTTP status a domain error code travels on.</summary>
    private static int StatusFor(string errorCode) => errorCode switch
    {
        ErrorCodes.ValidationError => StatusCodes.Status400BadRequest,
        ErrorCodes.BadRequest => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidArgument => StatusCodes.Status400BadRequest,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.MethodNotAllowed => StatusCodes.Status405MethodNotAllowed,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.InvalidState => StatusCodes.Status409Conflict,
        ErrorCodes.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ErrorCodes.NotImplemented => StatusCodes.Status501NotImplemented,
        ErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>
    /// The short title for a status. Matches what <c>ApiExceptionHandler</c> emits for the same
    /// status, so the two never disagree about what a 409 is called.
    /// </summary>
    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "请求格式不对",
        StatusCodes.Status401Unauthorized => "需要登录",
        StatusCodes.Status403Forbidden => "没有权限",
        StatusCodes.Status404NotFound => "找不到",
        StatusCodes.Status405MethodNotAllowed => "不支持这个请求方法",
        StatusCodes.Status409Conflict => "操作冲突",
        StatusCodes.Status415UnsupportedMediaType => "不支持这种文件类型",
        StatusCodes.Status501NotImplemented => "功能还没做",
        StatusCodes.Status504GatewayTimeout => "请求超时",
        _ => "服务器出错了"
    };
}
