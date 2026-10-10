namespace NCUT_Market.Core.Common;

/// <summary>
/// The wire vocabulary for the <c>code</c> extension member of a ProblemDetails.
/// <para>
/// These are plain strings on the wire — this type removes typos, it does not turn the code into an
/// enum, and clients still branch on the literal value. Never rename a member that has shipped; add
/// a new one instead.
/// </para>
/// <para>
/// One code per HTTP status: a code produced by <c>Program.cs</c>'s status-derived default must be
/// the same code <c>ApiExceptionHandler</c> uses for that status, or a client branching on
/// <c>code</c> would see two values for one condition. Domain codes that are deliberately more
/// specific than their status get added here as the features that need them land.
/// </para>
/// </summary>
public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";

    public const string BadRequest = "BAD_REQUEST";

    public const string InvalidArgument = "INVALID_ARGUMENT";

    public const string Unauthorized = "UNAUTHORIZED";

    public const string Forbidden = "FORBIDDEN";

    public const string NotFound = "NOT_FOUND";

    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";

    public const string Conflict = "CONFLICT";

    /// <summary>
    /// The credentials were well-formed but wrong — an unknown username or a bad password.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="Unauthorized"/>, which means "no valid token on the
    /// request at all". A login attempt that fails and a request carrying an expired token are
    /// different problems with different fixes, and a client that cannot tell them apart will show
    /// "please log in again" to someone who never was logged in.
    /// </remarks>
    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    /// <summary>
    /// The action is not legal for the resource's current state — publishing a sold listing,
    /// deleting a published one.
    /// </summary>
    /// <remarks>
    /// Carried on a 409 alongside <see cref="Conflict"/>, which it refines: 409 says "the request
    /// conflicts with the current state", and this says specifically that the state machine, rather
    /// than a uniqueness constraint, is what refused it.
    /// </remarks>
    public const string InvalidState = "INVALID_STATE";

    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";

    public const string InternalError = "INTERNAL_ERROR";

    public const string NotImplemented = "NOT_IMPLEMENTED";

    public const string Timeout = "TIMEOUT";

    /// <summary>
    /// The caller has used up a metered allowance — today's share of the listing assistant's model
    /// calls.
    /// </summary>
    /// <remarks>
    /// A 429, and deliberately not the generic name for one: the status says "slow down", while this
    /// says which allowance ran out and, on this endpoint, that bringing your own key sidesteps it
    /// entirely. A client that shows "请求太频繁" to someone who simply had three questions today
    /// has thrown away the only actionable part.
    /// </remarks>
    public const string QuotaExceeded = "QUOTA_EXCEEDED";

    /// <summary>
    /// A dependency outside this deployment could not be reached, or answered unusably.
    /// </summary>
    /// <remarks>
    /// Distinguished from <see cref="InternalError"/> because the two are different incidents for
    /// whoever is on call: this one means somebody else's service is down and the feature degrades,
    /// while a 500 means our own code threw. The listing assistant's model provider is its only
    /// source today.
    /// </remarks>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}
