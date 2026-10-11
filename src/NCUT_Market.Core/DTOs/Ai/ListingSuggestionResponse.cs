namespace NCUT_Market.Core.DTOs.Ai;

/// <summary>
/// What the listing assistant came back with: whether the photos look acceptable, a title, roughly
/// what the thing is worth, and a description the seller can edit.
/// </summary>
/// <param name="Flagged">
/// True when the model judged the photos to contain sexual, violent, terror-related or politically
/// sensitive content.
/// </param>
/// <param name="FlagReason">One sentence naming what was objectionable, or null when nothing was.</param>
/// <param name="Title">
/// A listing title the seller can use as it stands, within the 100 characters the form accepts, or
/// null when the model failed to produce one. Suggested whether or not the seller had already typed
/// a title — the model has seen the photos either way, so its version is worth offering.
/// </param>
/// <param name="PriceLow">Bottom of the suggested range, in yuan, or null when no advice was possible.</param>
/// <param name="PriceHigh">Top of the suggested range. Never below <paramref name="PriceLow"/>.</param>
/// <param name="Description">A ready-to-use description. Always present unless the model failed to produce one.</param>
/// <param name="RemainingToday">
/// How many shared-key attempts the caller has left today, after this one. Null when the caller
/// supplied their own key — those are not counted, so there is no number to report.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Flagged"/> is advice, not a gate.</b> Nothing in this version refuses a publish on
/// the strength of it, and the client is expected to render it as a warning and let the seller carry
/// on. The point of returning it on the same response as the price and the description is that one
/// model call answers all three questions.
/// </para>
/// <para>
/// The price fields are null together or not at all. They go null when the seller has not picked a
/// category yet, because a suggestion with no local sale prices behind it is a guess dressed up as
/// advice, and the client would have no way to tell the two apart.
/// </para>
/// </remarks>
public sealed record ListingSuggestionResponse(
    bool Flagged,
    string? FlagReason,
    string? Title,
    decimal? PriceLow,
    decimal? PriceHigh,
    string? Description,
    int? RemainingToday);
