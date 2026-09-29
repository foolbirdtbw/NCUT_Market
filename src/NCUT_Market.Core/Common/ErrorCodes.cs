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

    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";

    public const string InternalError = "INTERNAL_ERROR";

    public const string NotImplemented = "NOT_IMPLEMENTED";

    public const string Timeout = "TIMEOUT";
}
