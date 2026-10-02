using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Listings: the public feed and detail view, the seller's own lifecycle actions, and photo
/// management.
/// </summary>
/// <remarks>
/// Read endpoints are anonymous; every mutation requires a token and checks ownership inside
/// <see cref="IProductService"/>. The <c>[Authorize]</c> attribute here establishes that somebody is
/// signed in — it cannot establish that the signed-in user owns the listing, which is why that
/// check lives in the service and returns 403 through the normal error contract.
/// </remarks>
[ApiController]
[Route("api/products")]
public sealed class ProductsController(IProductService productService) : ControllerBase
{
    /// <summary>Largest upload accepted, in bytes. Matches the cap the service enforces.</summary>
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>One page of published listings, filtered and ordered.</summary>
    /// <param name="query">Filters. Every field is optional.</param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty.</response>
    /// <response code="400">A query-string value was not parseable.</response>
    /// <remarks>
    /// Only published listings appear here. Sold ones stay reachable by id so a shared link does not
    /// break, but they are out of the feed.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PagedResult<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ProductSummaryResponse>>> List(
        [FromQuery] ProductListQuery query,
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await productService.ListAsync(query, pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>The signed-in user's own listings, in every status.</summary>
    /// <param name="pagination">Page and page size.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page, including drafts and sold listings.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// Declared before <c>{id:long}</c> in source order, but the route templates do not actually
    /// collide: "mine" is not a long, so <c>{id:long}</c> cannot match it.
    /// </remarks>
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<PagedResult<ProductSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResult<ProductSummaryResponse>>> ListMine(
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await productService.ListMineAsync(User.GetUserId(), pagination, cancellationToken);

        return Ok(result);
    }

    /// <summary>One listing in full.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The listing.</response>
    /// <response code="404">
    /// No such listing, or one that is a draft or offlined and belongs to somebody else. A draft's
    /// own seller sees it; a stranger guessing the id gets the same answer as for an id that does
    /// not exist.
    /// </response>
    [HttpGet("{id:long}")]
    [AllowAnonymous]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductDetailResponse>> GetById(
        long id,
        CancellationToken cancellationToken)
    {
        // No [Authorize], so an absent or expired token must not fail the request — it only means
        // the caller is not the seller. FindFirst returns null when nothing is authenticated.
        var viewerId = TryGetUserId();

        var result = await productService.GetByIdAsync(id, viewerId, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Creates a listing as a draft.</summary>
    /// <param name="request">The listing's details.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The draft, with a Location header pointing at it.</response>
    /// <response code="400">A field was missing or out of range, or the category or area does not exist.</response>
    /// <response code="401">No token.</response>
    /// <remarks>
    /// Always a draft. Photos are uploaded against the returned id and publishing is a separate
    /// call, because a photo cannot exist before the listing it belongs to.
    /// </remarks>
    [HttpPost]
    [Authorize]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProductDetailResponse>> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var result = await productService.CreateAsync(User.GetUserId(), request, cancellationToken);

        if (!result.Succeeded)
        {
            return ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    /// <summary>Replaces a listing's editable fields.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The updated listing.</response>
    /// <response code="400">A field was missing or out of range.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    [HttpPut("{id:long}")]
    [Authorize]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductDetailResponse>> Update(
        long id,
        [FromBody] UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var result = await productService.UpdateAsync(id, User.GetUserId(), request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Puts a listing live, from a draft or from offline.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The published listing.</response>
    /// <response code="400">The listing has no photo yet.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    /// <response code="409">The listing is sold.</response>
    [HttpPost("{id:long}/publish")]
    [Authorize]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductDetailResponse>> Publish(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await productService.PublishAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Takes a published listing down without deleting it.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The offlined listing.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    /// <response code="409">The listing is not published.</response>
    [HttpPost("{id:long}/offline")]
    [Authorize]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductDetailResponse>> Offline(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await productService.OfflineAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Marks a published listing as sold.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The sold listing.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    /// <response code="409">The listing is not published.</response>
    [HttpPost("{id:long}/sold")]
    [Authorize]
    [ProducesResponseType<ProductDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductDetailResponse>> MarkSold(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await productService.MarkSoldAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Deletes a draft or offlined listing, along with its photos.</summary>
    /// <param name="id">Primary key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    /// <response code="409">The listing is published or sold.</response>
    [HttpDelete("{id:long}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await productService.DeleteAsync(id, User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Attaches one photo to a listing.</summary>
    /// <param name="id">Primary key of the listing.</param>
    /// <param name="file">The image. JPEG, PNG or WebP, at most 5 MB.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">The stored photo, with the URLs to render it.</response>
    /// <response code="400">The file is too large, or the listing already holds nine photos.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing.</response>
    /// <response code="415">The bytes are not a supported image format.</response>
    /// <remarks>
    /// One file per request. A multi-file upload would need either a partial-success response shape
    /// or all-or-nothing semantics, and neither is worth it for a form that uploads nine files at
    /// most — the client can send them in sequence and show progress per file.
    /// </remarks>
    [HttpPost("{id:long}/images")]
    [Authorize]
    [RequestSizeLimit(MaxUploadBytes + 1024)]
    [ProducesResponseType<ProductImageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<ProductImageResponse>> AddImage(
        long id,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await using var content = file.OpenReadStream();

        var result = await productService.AddImageAsync(
            id,
            User.GetUserId(),
            content,
            file.FileName,
            file.ContentType,
            cancellationToken);

        if (!result.Succeeded)
        {
            return ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Removes one photo from a listing.</summary>
    /// <param name="id">Primary key of the listing.</param>
    /// <param name="imageId">Primary key of the photo.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">The listing belongs to somebody else.</response>
    /// <response code="404">No such listing, or no such photo on it.</response>
    [HttpDelete("{id:long}/images/{imageId:long}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteImage(
        long id,
        long imageId,
        CancellationToken cancellationToken)
    {
        var result = await productService.DeleteImageAsync(
            id,
            User.GetUserId(),
            imageId,
            cancellationToken);

        return result.Succeeded
            ? NoContent()
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>
    /// The signed-in user's id, or null when the request carries no usable token.
    /// </summary>
    /// <remarks>
    /// Used only by endpoints that are anonymous but behave differently for their owner. It does not
    /// validate anything — the bearer handler has already done that, and a token it rejected leaves
    /// an unauthenticated principal here.
    /// </remarks>
    private long? TryGetUserId()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;

        return long.TryParse(subject, out var userId) ? userId : null;
    }
}
