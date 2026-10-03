using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Announcements;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Site-wide announcements: the banner at the top of every page and the page they link to.
/// </summary>
/// <remarks>
/// Reading is anonymous — an announcement nobody has to sign in to see is the point of one. Writing
/// requires a token <em>and</em> an admin account, and the second half of that is checked inside
/// <see cref="IAnnouncementService"/> against the database rather than against the token, so
/// <c>[Authorize]</c> here only rules out the anonymous case.
/// </remarks>
[ApiController]
[Route("api/announcements")]
[Authorize]
public sealed class AnnouncementsController(IAnnouncementService announcementService) : ControllerBase
{
    /// <summary>The announcements that are live right now, newest first.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty.</response>
    /// <remarks>
    /// Only announcements that have gone live and have not expired. Draft rows exist in the table but
    /// nothing in the API creates one, and they never appear here.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<AnnouncementResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AnnouncementResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await announcementService.ListAsync(pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>Publishes an announcement immediately.</summary>
    /// <param name="request">The announcement.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The published announcement.</response>
    /// <response code="400">The title or body was blank or too long, or the expiry is in the past.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    [HttpPost]
    [ProducesResponseType<AnnouncementResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AnnouncementResponse>> Publish(
        [FromBody] CreateAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        var result = await announcementService.PublishAsync(User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? CreatedAtAction(nameof(List), new { }, result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Deletes an announcement outright.</summary>
    /// <param name="id">Announcement id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such announcement.</response>
    /// <remarks>
    /// A hard delete rather than a move to <c>Expired</c>: retracting a typo means removing it, and
    /// nothing here needs the row kept.
    /// </remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await announcementService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
