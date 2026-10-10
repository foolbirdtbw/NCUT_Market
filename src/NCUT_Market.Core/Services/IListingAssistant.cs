using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Looks at a seller's photos and answers three questions at once: do they look acceptable, what is
/// this worth, and how would one describe it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Advisory only.</b> Nothing here blocks, delays or records a publish. A listing whose photos came
/// back flagged goes up exactly as it would have, and the seller is expected to have been shown a
/// warning they can ignore. Making the judgement binding is a later step — which is why the judgement
/// is produced by a single method inside the implementation rather than spread through the flow, so
/// that the step has one place to attach to.
/// </para>
/// <para>
/// Unlike every other service here, this one talks to a third party. Its failures are therefore not
/// the caller's fault and must never reach the seller as an error they are supposed to act on: a
/// provider outage returns <see cref="ErrorCodes.ServiceUnavailable"/>, and the publish form carries
/// on working.
/// </para>
/// </remarks>
public interface IListingAssistant
{
    /// <summary>
    /// Produces a moderation verdict, a price range and a description from one set of photos.
    /// </summary>
    /// <param name="userId">The caller. Only used to meter the shared key's daily allowance.</param>
    /// <param name="images">The photos. At least one, and no more than the configured maximum.</param>
    /// <param name="categoryId">
    /// What the seller picked, or null if they have not picked yet. When null no price advice is
    /// produced — see <see cref="ListingSuggestionResponse.PriceLow"/>.
    /// </param>
    /// <param name="title">What the seller has typed so far, if anything. Improves the description.</param>
    /// <param name="apiKeyOverride">
    /// The caller's own model key, or null to use the deployment's shared one. Supplying a key skips
    /// the daily allowance entirely — those calls are not on our bill.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidArgument"/> when the photos are unusable, with
    /// <see cref="ErrorCodes.QuotaExceeded"/> when the day's shared allowance is spent, and with
    /// <see cref="ErrorCodes.ServiceUnavailable"/> when the model could not be reached or answered
    /// with something unreadable.
    /// </returns>
    Task<OperationResult<ListingSuggestionResponse>> SuggestAsync(
        long userId,
        IReadOnlyList<AiImage> images,
        long? categoryId,
        string? title,
        string? apiKeyOverride,
        CancellationToken cancellationToken = default);
}
