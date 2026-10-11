namespace NCUT_Market.Infrastructure.Ai;

/// <summary>
/// Where the listing assistant's model lives and how much of it we are willing to pay for.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BaseUrl"/> and <see cref="Model"/> are both settable because the choice of provider is
/// the least settled thing about this feature: the protocol spoken here is the OpenAI-compatible one,
/// which several vendors implement, and switching between them is meant to be a configuration change
/// rather than a code change. <c>Ai__Model</c> in particular has no useful default — the model must be
/// one that accepts images, and naming a text-only one produces a 400 on every call.
/// </para>
/// <para>
/// In the deployment these come from the server's <c>.env</c> as <c>Ai__ApiKey</c>,
/// <c>Ai__Model</c> and optionally <c>Ai__BaseUrl</c>. The key is the deployment's own, used on
/// behalf of sellers who have not brought one of their own.
/// </para>
/// </remarks>
public sealed class AiOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// Root of the OpenAI-compatible API. The client appends <c>chat/completions</c> to it.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>
    /// The model to call. Must accept image input — this is the one setting that will silently break
    /// the feature if it names a text-only model.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The deployment's own API key. A caller may bring their own instead, which skips
    /// <see cref="DailyQuota"/>; when neither is present the assistant reports itself unavailable
    /// rather than calling without credentials.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// How long one model call may take. Well above a typical answer, because these calls are slow and
    /// a needlessly tight ceiling turns a working feature into an intermittent one.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Model calls per account per day, for callers using <see cref="ApiKey"/>. Callers supplying
    /// their own key are not counted against this, since they are not spending our money.
    /// </summary>
    /// <remarks>
    /// Generous on purpose while the site is still finding users: a seller who runs out mid-listing
    /// has been given a reason to stop using the assistant, and the cost of being wrong in that
    /// direction is a few cents of tokens. Turn it down once the number of accounts using it starts
    /// to matter — it is a setting, so that needs no rebuild.
    /// </remarks>
    public int DailyQuota { get; set; } = 20;

    /// <summary>
    /// How many photos one request may carry. One is enough to judge a photo; the rest are for
    /// helping the description, and each one costs tokens on every call.
    /// </summary>
    public int MaxImages { get; set; } = 3;

    /// <summary>Largest single photo accepted, in bytes, after the client has downscaled it.</summary>
    public int MaxImageBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// How far back to look for comparable listings when grounding a price suggestion. Long enough to
    /// find a sample in a quiet category, short enough that last year's prices are not treated as
    /// today's.
    /// </summary>
    public int ComparableWindowDays { get; set; } = 180;

    /// <summary>How many comparable listings to put in front of the model.</summary>
    public int MaxComparables { get; set; } = 20;
}
