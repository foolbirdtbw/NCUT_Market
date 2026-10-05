using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Categories;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The category dictionary: public to read, administrator-only to change.
/// </summary>
/// <remarks>
/// <see cref="List"/> is anonymous because a product search filter and the publish form both read it
/// without a token. That makes "categories are hidden from ordinary users" a statement about the
/// admin screens, not about this endpoint — a signed-in user can still call it and will still get a
/// page back. See the note in <c>web/README.md</c>.
///
/// The write actions require a token <em>and</em> an admin account, and the second half is checked
/// inside <see cref="ICategoryService"/> against the database rather than the token, so
/// <c>[Authorize]</c> here only rules out the anonymous case.
/// </remarks>
[ApiController]
[Route("api/categories")]
[Authorize]
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
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<CategoryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<CategoryResponse>>> List(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await categoryService.ListAsync(pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>Adds a category.</summary>
    /// <param name="request">The category to add.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The new category.</response>
    /// <response code="400">The name was blank or too long, or the parent does not exist.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="409">A sibling under the same parent already has that name.</response>
    [HttpPost]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponse>> Create(
        [FromBody] CreateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await categoryService.CreateAsync(User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? CreatedAtAction(nameof(List), new { }, result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Rewrites a category in place.</summary>
    /// <param name="id">The category to rewrite.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The category as saved.</response>
    /// <response code="400">
    /// The name was blank or too long, the parent does not exist, or the parent is this category or
    /// one of its descendants.
    /// </response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such category.</response>
    /// <response code="409">A sibling under the same parent already has that name.</response>
    [HttpPut("{id:long}")]
    [ProducesResponseType<CategoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryResponse>> Update(
        long id,
        [FromBody] UpdateCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await categoryService.UpdateAsync(id, User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Deletes a category outright.</summary>
    /// <param name="id">The category to delete.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such category.</response>
    /// <response code="409">The category still has children, or listings filed under it.</response>
    /// <remarks>
    /// A hard delete. The table has a <c>status</c> column, but retiring a category by flagging it
    /// would leave the row and its foreign keys in place while removing it from every list — a state
    /// no other part of the API is written to handle.
    /// </remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await categoryService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
