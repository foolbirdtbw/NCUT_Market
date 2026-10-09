using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.DTOs.Notifications;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Personal notifications — the page behind the header badge.
/// </summary>
/// <remarks>
/// <para>
/// Every action requires a token, and every one is scoped to the caller's own rows in
/// <see cref="INotificationService"/>. There is no way to read somebody else's, and a notification
/// belonging to another account reports 404 rather than 403, for the same reason a thread does: its
/// existence is not public.
/// </para>
/// <para>
/// Read-only apart from marking one read. Notifications are written by whatever changed the state
/// that warranted one, in the same save — so there is no endpoint that creates one, and no way for a
/// client to fabricate history.
/// </para>
/// </remarks>
[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(INotificationService notificationService) : ControllerBase
{
    /// <summary>The caller's notifications, newest first.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty.</response>
    /// <response code="401">No token.</response>
    [HttpGet]
    [ProducesResponseType<PagedResult<NotificationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<NotificationResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await notificationService.ListAsync(User.GetUserId(), pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>How many notifications are unopened — the number behind the header badge.</summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The count. Zero when signed in with nothing waiting.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// Declared before the constrained <c>{id:long}</c> route below for the same reason
    /// <c>ConversationsController</c> orders its own: the two templates do not actually collide, but
    /// reading them in this order is one less thing to check.
    /// </remarks>
    [HttpGet("unread-count")]
    [ProducesResponseType<UnreadCountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UnreadCountResponse>> UnreadCount(CancellationToken cancellationToken)
    {
        var result = await notificationService.GetUnreadCountAsync(User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Marks one notification read.</summary>
    /// <param name="id">Notification id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Marked read, or already was.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such notification, or it belongs to somebody else.</response>
    [HttpPost("{id:long}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(long id, CancellationToken cancellationToken)
    {
        var result = await notificationService.MarkReadAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Drops one notification.</summary>
    /// <param name="id">Notification id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Gone.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such notification, or it belongs to somebody else.</response>
    /// <remarks>
    /// The whole row goes, and there is no undo — which is why the confirm dialog is on the client.
    /// Unlike deleting a thread, nothing is shared here: a notification has exactly one reader.
    /// </remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await notificationService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Drops every notification you hold about one listing.</summary>
    /// <param name="productId">Listing id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Gone, or there was nothing to drop.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// The one delete here that can match nothing and still succeed. The rows are already scoped to the
    /// caller, so an empty match means an empty group rather than a wrong id, and a client clicking
    /// twice should not be told off for it. The listing is never checked for existence either: its
    /// notices are the caller's rows regardless of what has since happened to it.
    /// <para>
    /// The plain <c>{id:long}</c> route above does not shadow this one — <c>product</c> is not a number,
    /// so its constraint rules it out, and a literal segment wins outright.
    /// </para>
    /// </remarks>
    [HttpDelete("product/{productId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteByProduct(long productId, CancellationToken cancellationToken)
    {
        var result = await notificationService.DeleteByProductAsync(
            productId, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
