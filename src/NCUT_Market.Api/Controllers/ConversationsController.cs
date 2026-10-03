using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Private threads between a buyer and a seller, one per listing per buyer.
/// </summary>
/// <remarks>
/// <para>
/// Every action requires a token — there is no anonymous view of a private conversation, not even a
/// count. <c>[Authorize]</c> here establishes only that somebody is signed in; whether that somebody
/// is one of the thread's two participants is decided in <see cref="IConversationService"/>, which
/// reports a non-participant as 404 rather than 403. Unlike a listing, a thread is never public, so a
/// 403 would confirm that a thread with that id exists.
/// </para>
/// <para>
/// Deliberately no admin access. Reading somebody else's private messages is not an administrative
/// power this project has, and it should not acquire one by accident.
/// </para>
/// </remarks>
[ApiController]
[Route("api/conversations")]
[Authorize]
public sealed class ConversationsController(IConversationService conversationService) : ControllerBase
{
    /// <summary>The signed-in user's threads, most recently active first.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty.</response>
    /// <response code="401">No token.</response>
    [HttpGet]
    [ProducesResponseType<PagedResult<ConversationSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<ConversationSummaryResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await conversationService.ListAsync(User.GetUserId(), pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>How many threads hold an unread message — the number behind the header badge.</summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The count. Zero when signed in with nothing waiting.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// Declared before <c>{id:long}</c>, though the templates do not actually collide: "unread-count"
    /// is not a long, so the constrained route cannot match it. Same shape as "mine" in
    /// <c>ProductsController</c>.
    /// </remarks>
    [HttpGet("unread-count")]
    [ProducesResponseType<UnreadCountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UnreadCountResponse>> UnreadCount(CancellationToken cancellationToken)
    {
        var result = await conversationService.GetUnreadCountAsync(User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Opens a thread about a listing, or returns the one that already exists.</summary>
    /// <param name="request">The listing to talk about.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The thread, newly created or already open.</response>
    /// <response code="400">The listing belongs to the caller.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such listing, or one the caller cannot see.</response>
    /// <remarks>
    /// 200 rather than 201 in both cases, and no <c>Location</c> header. This is find-or-create: the
    /// second click on "联系卖家" is the same request as the first and the client's only interest is
    /// the thread's id. Answering 201 once and 200 thereafter would make the status depend on state
    /// the caller does not care about.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<ConversationSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationSummaryResponse>> Start(
        [FromBody] StartConversationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await conversationService.StartAsync(User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>A thread and the most recent page of its messages.</summary>
    /// <param name="id">Thread id.</param>
    /// <param name="pagination">Page and page size, counted back from the newest message.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The thread.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such thread, or the caller is not in it.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ConversationDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetailResponse>> GetById(
        long id,
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await conversationService.GetAsync(id, User.GetUserId(), pagination, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Posts a message into a thread.</summary>
    /// <param name="id">Thread id.</param>
    /// <param name="request">The text.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The stored message.</response>
    /// <response code="400">The text was blank or longer than 500 characters.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such thread, or the caller is not in it.</response>
    /// <remarks>
    /// No <c>Location</c> header: a message has no URL of its own — it is only ever read as part of
    /// its thread — so <c>CreatedAtAction</c> would have to point at a resource that does not
    /// correspond to what was created.
    /// </remarks>
    [HttpPost("{id:long}/messages")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> Send(
        long id,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await conversationService.SendAsync(id, User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Marks the thread read for the calling side.</summary>
    /// <param name="id">Thread id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Marked read.</response>
    /// <response code="401">No token.</response>
    /// <response code="404">No such thread, or the caller is not in it.</response>
    /// <remarks>
    /// A separate call rather than a side effect of <see cref="GetById"/>, so that a read endpoint
    /// stays a read endpoint. The frontend calls this immediately after loading a thread.
    /// </remarks>
    [HttpPost("{id:long}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(long id, CancellationToken cancellationToken)
    {
        var result = await conversationService.MarkReadAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
