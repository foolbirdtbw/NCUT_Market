using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Buyer-facing category dictionary. Read-only, and unauthenticated by design.
/// </summary>
[ApiController]
[Route("api/categories")]
public sealed class CategoriesController(ICategoryService categoryService) : ControllerBase
{
    /// <summary>One page of active categories, flat, ordered by sort order.</summary>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty when the page is past the end.</response>
    /// <response code="400">A query-string value was not parseable.</response>
    /// <remarks>
    /// Flat rather than nested: the tree is rebuilt client-side from <c>parentId</c>. A future nested
    /// route would be a separate, unpaginated endpoint — paging root nodes is not a useful shape, and
    /// walking the tree lazily would be an N+1.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<PagedResult<CategoryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<CategoryResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await categoryService.ListAsync(pagination, cancellationToken);

        return Ok(result);
    }
}
