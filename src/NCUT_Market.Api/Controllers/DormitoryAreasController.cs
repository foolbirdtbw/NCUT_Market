using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The dormitory-area dictionary: public to read, administrator-only to change.
/// </summary>
/// <remarks>
/// Both reads are anonymous because the search filter and the publish form use them without a token.
/// As with categories, "hidden from ordinary users" is about the admin screens, not this endpoint.
///
/// The write actions require a token <em>and</em> an admin account, and the second half is checked
/// inside <see cref="IDormitoryAreaService"/> against the database rather than the token, so
/// <c>[Authorize]</c> here only rules out the anonymous case.
/// </remarks>
[ApiController]
[Route("api/dormitory-areas")]
[Authorize]
public sealed class DormitoryAreasController(IDormitoryAreaService dormitoryAreaService) : ControllerBase
{
    /// <summary>One page of active dormitory areas, ordered by sort order.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty when the page is past the end.</response>
    /// <response code="400">A query-string value was not parseable.</response>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<DormitoryAreaResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<DormitoryAreaResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.ListAsync(pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>A single active dormitory area.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The area.</response>
    /// <response code="404">
    /// No active area with that id. A deactivated area returns this too, rather than revealing that a
    /// hidden row exists.
    /// </response>
    /// <remarks>
    /// <c>[AllowAnonymous]</c> is load-bearing. Without it the class-level <c>[Authorize]</c> would
    /// apply, and this route would start refusing the anonymous callers it was written for.
    /// </remarks>
    [HttpGet("{id:long}")]
    [AllowAnonymous]
    [ProducesResponseType<DormitoryAreaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DormitoryAreaResponse>> GetById(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.GetByIdAsync(id, cancellationToken);

        if (!result.Succeeded)
        {
            return ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
        }

        return Ok(result.Value);
    }

    /// <summary>Adds a dormitory area.</summary>
    /// <param name="request">The area to add.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The new area.</response>
    /// <response code="400">The name was blank or too long.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="409">An area already carries that name.</response>
    [HttpPost]
    [ProducesResponseType<DormitoryAreaResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DormitoryAreaResponse>> Create(
        [FromBody] CreateDormitoryAreaRequest request,
        CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.CreateAsync(User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Rewrites a dormitory area in place.</summary>
    /// <param name="id">The area to rewrite.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The area as saved.</response>
    /// <response code="400">The name was blank or too long.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such area.</response>
    /// <response code="409">Another area already carries that name.</response>
    [HttpPut("{id:long}")]
    [ProducesResponseType<DormitoryAreaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DormitoryAreaResponse>> Update(
        long id,
        [FromBody] UpdateDormitoryAreaRequest request,
        CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.UpdateAsync(id, User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Deletes a dormitory area outright.</summary>
    /// <param name="id">The area to delete.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such area.</response>
    /// <response code="409">Listings still point at it.</response>
    /// <remarks>
    /// A hard delete, for the same reason as categories: the <c>status</c> column exists but nothing
    /// in the API is written to cope with a row that is present and invisible at once.
    /// </remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
