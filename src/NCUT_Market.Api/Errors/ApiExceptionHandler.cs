using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;

namespace NCUT_Market.Api.Errors;

/// <summary>
/// Turns an unhandled exception into an RFC 7807 response carrying a <c>code</c> extension member.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin: business failures are supposed to travel as <c>OperationResult&lt;T&gt;</c> and
/// be unpacked by the controller, so anything arriving here is a bug or an infrastructure fault. That
/// is why <see cref="DbUpdateException"/> is the only EF type referenced — a duplicate username or a
/// delete blocked by <c>ON DELETE RESTRICT</c> is a routine conflict, and it is the one case where
/// the ORM's failure genuinely is the answer rather than a symptom.
/// </para>
/// <para>
/// Titles are Chinese, because the frontend falls back to <c>title</c> whenever a response carries
/// no <c>detail</c> — which is every case this handler produces. The <c>code</c> member is still the
/// stable thing clients should branch on; the title is display text only.
/// </para>
/// </remarks>
public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A client hanging up is not a server fault. Mapping it would turn every abandoned request
        // into a 500 and an Error-level log line, which is exactly how real failures get buried.
        // Returning false hands the exception back to the pipeline to unwind normally.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (status, title, code, logLevel) = exception switch
        {
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "请求格式不对",
                ErrorCodes.BadRequest,
                LogLevel.Warning),
            // Covers ArgumentNullException and ArgumentOutOfRangeException too.
            ArgumentException => (
                StatusCodes.Status400BadRequest,
                "参数不对",
                ErrorCodes.InvalidArgument,
                LogLevel.Warning),
            // 403 rather than 401: a 401 is a challenge and requires WWW-Authenticate, which needs the
            // authentication middleware this stage does not have yet.
            UnauthorizedAccessException => (
                StatusCodes.Status403Forbidden,
                "没有权限",
                ErrorCodes.Forbidden,
                LogLevel.Warning),
            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "找不到",
                ErrorCodes.NotFound,
                LogLevel.Information),
            DbUpdateException => (
                StatusCodes.Status409Conflict,
                "操作冲突",
                ErrorCodes.Conflict,
                LogLevel.Warning),
            NotImplementedException => (
                StatusCodes.Status501NotImplemented,
                "功能还没做",
                ErrorCodes.NotImplemented,
                LogLevel.Warning),
            TimeoutException or OperationCanceledException => (
                StatusCodes.Status504GatewayTimeout,
                "请求超时",
                ErrorCodes.Timeout,
                LogLevel.Error),
            _ => (
                StatusCodes.Status500InternalServerError,
                "服务器出错了",
                ErrorCodes.InternalError,
                LogLevel.Error)
        };

        // One log line per failure, carrying the same trace id that goes into the response body, so a
        // reported trace id finds the stack trace without the stack trace being sent to the client.
        logger.Log(
            logLevel,
            exception,
            "API request failed with code {ErrorCode}. TraceId: {TraceId}",
            code,
            httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                // Both members are set with the indexer rather than by initialising the dictionary,
                // and both duplicate what the pipeline would otherwise supply. That redundancy is the
                // point: the traceId here is the same value logged above, and writing it explicitly
                // means the response cannot end up carrying a different identifier than the log.
                Extensions =
                {
                    ["code"] = code,
                    ["traceId"] = httpContext.TraceIdentifier
                }
            },
            Exception = exception
        });
    }
}
