using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.DormitoryAreas;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Buyer-facing dormitory-area dictionary. Read-only, and unauthenticated by design.
/// </summary>
[ApiController]
[Route("api/dormitory-areas")]
public sealed class DormitoryAreasController(IDormitoryAreaService dormitoryAreaService) : ControllerBase
{
    /// <summary>One page of active dormitory areas, ordered by sort order.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty when the page is past the end.</response>
    /// <response code="400">A query-string value was not parseable.</response>
    [HttpGet]
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
    [HttpGet("{id:long}")]
    [ProducesResponseType<DormitoryAreaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DormitoryAreaResponse>> GetById(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await dormitoryAreaService.GetByIdAsync(id, cancellationToken);

        if (!result.Succeeded)
        {
            // Built through the factory and then poked with the indexer, rather than passing an
            // `extensions:` dictionary to Problem(). The factory invokes CustomizeProblemDetails on
            // its way out, which has already inserted a status-derived `code` — and Problem() adds
            // the caller's extensions with Add(), so supplying "code" there throws
            // "An item with the same key has already been added" and turns a 404 into a 400. The
            // indexer overwrites instead, which is what lets the service's own code win over the
            // default derived from the status.
            var problemDetails = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext,
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: result.ErrorMessage);

            problemDetails.Extensions["code"] = result.ErrorCode;

            return new ObjectResult(problemDetails)
            {
                StatusCode = problemDetails.Status,
                ContentTypes = { "application/problem+json" }
            };
        }

        return Ok(result.Value);
    }
}
