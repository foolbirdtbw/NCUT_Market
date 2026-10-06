using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Core.DTOs.Online;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// How many people are using the site right now. Backs the counter in the footer.
/// </summary>
/// <remarks>
/// <para>
/// The class-level <c>[Authorize]</c> plus the <c>[AllowAnonymous]</c> on the read action is the same
/// shape the two dictionary controllers use, and here as there the second attribute is the
/// load-bearing one: the footer is on every page, including the ones a signed-out visitor is looking
/// at, so a missing <c>[AllowAnonymous]</c> would leave the counter blank for exactly the people most
/// likely to be reading it — while every test that signed in first would still pass.
/// </para>
/// <para>
/// Anonymous is also safe here in a way it would not be for a user directory: the response is a single
/// integer describing nobody in particular.
/// </para>
/// </remarks>
[ApiController]
[Route("api/online")]
[Authorize]
public sealed class OnlineController(IOnlineService onlineService) : ControllerBase
{
    /// <summary>The number of active accounts seen inside the presence window.</summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The current count. Zero when nobody has been seen recently.</response>
    /// <remarks>
    /// A reading, not a measurement of the caller: it counts accounts that made a request within the
    /// last few minutes, and the window lives in the service rather than being a parameter here. The
    /// caller is not necessarily among them — their own request is recorded before this runs, so in
    /// practice they usually are, but nothing about the number depends on it.
    /// </remarks>
    [HttpGet("count")]
    [AllowAnonymous]
    [ProducesResponseType<OnlineCountResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OnlineCountResponse>> Count(CancellationToken cancellationToken)
    {
        var count = await onlineService.CountAsync(cancellationToken);

        return Ok(new OnlineCountResponse(count));
    }
}
