using NCUT_Market.Core.DTOs.Ai;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Sends one multimodal prompt to the configured model and returns its raw reply.
/// </summary>
/// <remarks>
/// <para>
/// The only interface in the project that talks to a machine we do not run. It exists mainly so the
/// test suite can replace it: every other test here goes through <c>WebApplicationFactory</c> and
/// never opens a socket, and a real model provider must not change that.
/// </para>
/// <para>
/// It knows nothing about listings, quotas or JSON shapes — it carries a prompt and some pictures,
/// and hands back whatever text came out. Interpreting that text is the caller's job, which is what
/// keeps the provider's wire format in exactly one class.
/// </para>
/// </remarks>
public interface IAiChatClient
{
    /// <summary>
    /// Asks the model one question, with pictures attached.
    /// </summary>
    /// <param name="apiKey">The key to authenticate with, passed in rather than configured here.</param>
    /// <param name="images">The photos to attach, in the order they should be presented.</param>
    /// <param name="prompt">The instruction text.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The reply text, or null when there is nothing usable — the provider could not be reached, timed
    /// out, answered with a non-success status, or returned a body with no content in it.
    /// </returns>
    /// <remarks>
    /// <b>Returns null rather than throwing.</b> Every one of those outcomes is an expected failure of
    /// somebody else's service, and the project's convention is that expected failures travel as
    /// values — <c>OperationResult</c> at the service boundary, null at this one. Throwing would push
    /// the whole category through the exception handler, which exists for the unexpected.
    /// <para>
    /// Implementations must not log, echo, or otherwise retain <paramref name="apiKey"/>. It may be a
    /// user's own key, handed over for the length of one request.
    /// </para>
    /// </remarks>
    Task<string?> CompleteAsync(
        string apiKey,
        IReadOnlyList<AiImage> images,
        string prompt,
        CancellationToken cancellationToken = default);
}
