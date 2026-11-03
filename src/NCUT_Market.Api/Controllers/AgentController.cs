using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The character in the corner: what she says about the page you are on, and what she says back.
/// </summary>
/// <remarks>
/// <para>
/// A separate controller from <see cref="AiController"/> because the two have opposite access rules.
/// That one is for sellers and everything on it needs a token; this one's hints are deliberately
/// readable signed out — a visitor deciding whether to register is exactly who the advice is for.
/// Splitting them keeps <c>[Authorize]</c> at the class level on the old one rather than carving out
/// an exception inside it.
/// </para>
/// <para>
/// <b>Hints never fail.</b> There is no 404 for a listing she cannot see: a draft that answered
/// differently from a nonexistent id would be a way to find out which ids exist. When there is
/// nothing useful to say she says something generic.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ai/agent")]
public sealed class AgentController(IAgentAssistant agent) : ControllerBase
{
    /// <summary>
    /// What she has to say about the page the reader is on.
    /// </summary>
    /// <param name="page">
    /// The page, as the client names it — <c>home</c>, <c>products</c>, <c>product</c>, <c>create</c>,
    /// <c>mine</c>, <c>messages</c>, <c>notifications</c>, <c>feedback</c>. Anything else gets general
    /// advice, so an unknown value is not an error.
    /// </param>
    /// <param name="productId">The listing being read, when <paramref name="page"/> is <c>product</c>.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">Always. Possibly generic, never empty.</response>
    /// <remarks>
    /// Anonymous callers get static advice and no personal counts; the id, when present, is used only
    /// to decide whether this reader may be told about that listing at all.
    /// </remarks>
    [HttpGet("hints")]
    [ProducesResponseType<AgentHintsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentHintsResponse>> Hints(
        [FromQuery] string? page,
        [FromQuery] long? productId,
        CancellationToken cancellationToken)
    {
        var hints = await agent.GetHintsAsync(
            User.GetUserIdOrNull(), page, productId, cancellationToken);

        return Ok(hints);
    }

    /// <summary>
    /// Answers one free-form question about the page the reader is on.
    /// </summary>
    /// <param name="request">The question, the page, and the turns so far.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The answer, trimmed to fit the panel.</response>
    /// <response code="400">An empty or over-long question.</response>
    /// <response code="401">No token. Signed-out readers may read hints but not ask.</response>
    /// <response code="429">The shared daily allowance is spent.</response>
    /// <response code="503">The model could not be reached, or answered unusably. Not charged.</response>
    [HttpPost("ask")]
    [Authorize]
    [ProducesResponseType<AgentAnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AgentAnswerResponse>> Ask(
        [FromBody] AgentAskRequest request,
        CancellationToken cancellationToken)
    {
        var result = await agent.AskAsync(User.GetUserId(), request, cancellationToken);

        if (!result.Succeeded)
        {
            return ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
        }

        return Ok(result.Value);
    }
}
