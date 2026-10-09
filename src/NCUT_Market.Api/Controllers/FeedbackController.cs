using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Feedbacks;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The feedback board: bug reports and feature requests, each with a vote count.
/// </summary>
/// <remarks>
/// Reading is anonymous — a board nobody can browse without an account collects nothing. Posting and
/// voting need a token. Changing a post's status and removing one need an admin account as well, and
/// that half is checked inside <see cref="IFeedbackService"/> against the database rather than against
/// the token, so <c>[Authorize]</c> here only rules out the anonymous case.
/// </remarks>
[ApiController]
[Route("api/feedback")]
[Authorize]
public sealed class FeedbackController(IFeedbackService feedbackService) : ControllerBase
{
    /// <summary>The board, most voted first.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty.</response>
    /// <remarks>
    /// The only thing the caller's identity affects is <c>hasVoted</c> on each row. An anonymous
    /// reader gets every count and no lit buttons.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<FeedbackResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<FeedbackResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await feedbackService.ListAsync(
            pagination,
            User.GetUserIdOrNull(),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Posts a bug report or feature request. It is visible immediately.</summary>
    /// <param name="request">The post.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The post, as it now appears on the board.</response>
    /// <response code="400">The body was blank or too long, or the kind was not one of the two.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// Any account may post; there is no role requirement and nothing to approve first. An anonymous
    /// post still records its author in the table — the flag hides the name, not the row.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<FeedbackResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FeedbackResponse>> Create(
        [FromBody] CreateFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var result = await feedbackService.CreateAsync(User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? CreatedAtAction(nameof(List), new { }, result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Turns your vote on a post on, or off if it was already on.</summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The count after the change, and whether you are now one of them.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such post.</response>
    [HttpPost("{id:long}/vote")]
    [ProducesResponseType<FeedbackVoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackVoteResponse>> Vote(long id, CancellationToken cancellationToken)
    {
        var result = await feedbackService.ToggleVoteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Moves a post to a different status.</summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="request">The new status.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The post, as it now reads.</response>
    /// <response code="400">Not one of the four statuses.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such post.</response>
    [HttpPut("{id:long}/status")]
    [ProducesResponseType<FeedbackResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackResponse>> SetStatus(
        long id,
        [FromBody] UpdateFeedbackStatusRequest request,
        CancellationToken cancellationToken)
    {
        var result = await feedbackService.SetStatusAsync(id, User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Removes a post, and every vote on it with it.</summary>
    /// <param name="id">Feedback id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Removed.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin — including when you wrote the post.</response>
    /// <response code="404">No such post.</response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await feedbackService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
