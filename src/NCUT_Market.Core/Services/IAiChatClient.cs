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
    /// <param name="jsonResponse">
    /// Ask the provider to constrain the reply to a JSON object. On by default because the listing
    /// assistant depends on it; the free-form chat turns it off, since a JSON object is the one shape
    /// a conversational answer must not have.
    /// </param>
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
        CancellationToken cancellationToken = default,
        bool jsonResponse = true);

    /// <summary>
    /// Runs one turn of a conversation that may end in tool calls instead of prose.
    /// </summary>
    /// <param name="apiKey">The key to authenticate with, passed in rather than configured here.</param>
    /// <param name="messages">The conversation so far, oldest first.</param>
    /// <param name="tools">
    /// What the model may call this turn. Null or empty means it must answer in prose — which is also
    /// how the caller forces a final answer out of a loop that has run out of steps.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The prose, the requested tool calls, or both; null when there is nothing usable, on exactly the
    /// same terms as <see cref="CompleteAsync"/>.
    /// </returns>
    /// <remarks>
    /// <b>The caller runs the tools and sends the results back.</b> Nothing here executes anything:
    /// this method is transport, and the decision about what a tool name is allowed to do belongs to
    /// the service that defined it.
    /// </remarks>
    Task<AiChatReply?> ChatAsync(
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken = default);
}
