using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The listing assistant: one call that looks at a seller's photos and comes back with a moderation
/// verdict, a price range and a description.
/// </summary>
/// <remarks>
/// <para>
/// Its own controller rather than another action on <c>ProductsController</c>, even though it is about
/// listings: it takes multipart input, spends a metered allowance, and talks to a third party. None of
/// that belongs in the class that owns the listing lifecycle.
/// </para>
/// <para>
/// <b>Signing in is required and no role is.</b> The assistant is for sellers, so anybody with an
/// account may use it — the allowance is what keeps one account from spending the deployment's budget
/// on everybody else's behalf.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ai")]
[Authorize]
public sealed class AiController(IListingAssistant assistant) : ControllerBase
{
    /// <summary>
    /// Where a caller's own model key travels.
    /// </summary>
    /// <remarks>
    /// Not <c>Authorization</c>, which the bearer middleware has already claimed for the session
    /// token — a request carrying both would have one of them silently overwritten. Not a query
    /// string either, which would put the key in browser history, in referrers and in every access log
    /// between here and the client.
    /// </remarks>
    private const string KeyHeader = "X-Ai-Key";

    /// <summary>
    /// Largest request body accepted.
    /// </summary>
    /// <remarks>
    /// Has to clear <c>AiOptions.MaxImages × AiOptions.MaxImageBytes</c> plus multipart overhead, or
    /// a set of photos the service would have accepted is refused by the framework first, with a
    /// message about request size that says nothing about which photo was the problem.
    /// </remarks>
    private const long MaxRequestBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Reads a set of photos and suggests a price and a description.
    /// </summary>
    /// <param name="request">The photos, and whatever the seller has filled in so far.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The suggestion. A flagged verdict is still a 200 — see the remarks.</response>
    /// <response code="400">No photos, too many, or one that is not a usable image.</response>
    /// <response code="401">No token.</response>
    /// <response code="429">The day's allowance is spent.</response>
    /// <response code="503">The model could not be reached, or answered unusably.</response>
    /// <remarks>
    /// <para>
    /// <b>A photo the model disliked is a 200, not an error.</b> The verdict is advice: the seller is
    /// meant to be shown it and left free to publish anyway. Returning 4xx would make the client's
    /// ordinary failure handling the place that decides whether a listing goes up, which is exactly
    /// the coupling this version is avoiding.
    /// </para>
    /// <para>
    /// Everything is optional except the photos. A seller who has typed nothing yet still gets a
    /// usable description; they just do not get a price, because there is no category to price
    /// against.
    /// </para>
    /// </remarks>
    [HttpPost("listing-draft")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ListingSuggestionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ListingSuggestionResponse>> ListingDraft(
        [FromForm] ListingDraftRequest request,
        CancellationToken cancellationToken)
    {
        var offered = Request.Headers[KeyHeader].ToString();

        var images = new List<AiImage>(request.Images.Count);

        foreach (var file in request.Images)
        {
            await using var content = file.OpenReadStream();
            using var buffer = new MemoryStream();

            await content.CopyToAsync(buffer, cancellationToken);

            images.Add(new AiImage(buffer.ToArray(), file.ContentType));
        }

        var result = await assistant.SuggestAsync(
            User.GetUserId(),
            images,
            request.CategoryId,
            request.Title,
            string.IsNullOrWhiteSpace(offered) ? null : offered,
            cancellationToken);

        if (!result.Succeeded)
        {
            return ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
        }

        return Ok(result.Value);
    }
}

/// <summary>
/// The multipart body of <see cref="AiController.ListingDraft"/>.
/// </summary>
/// <remarks>
/// Declared here rather than in <c>Core</c> because it holds <see cref="IFormFile"/>, which belongs to
/// ASP.NET Core — the domain only ever sees <see cref="AiImage"/>. Field names on the wire are the
/// property names lowercased by the model binder: <c>images</c> (repeated), <c>categoryId</c>,
/// <c>title</c>.
/// </remarks>
public sealed class ListingDraftRequest
{
    /// <summary>The photos. At least one; the ceiling is enforced by the service.</summary>
    public List<IFormFile> Images { get; init; } = [];

    /// <summary>What the seller picked, or null if they have not picked yet.</summary>
    public long? CategoryId { get; init; }

    /// <summary>What the seller has typed so far, if anything.</summary>
    public string? Title { get; init; }
}
