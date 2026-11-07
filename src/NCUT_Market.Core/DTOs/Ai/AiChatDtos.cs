namespace NCUT_Market.Core.DTOs.Ai;

/// <summary>
/// One turn in a conversation the model may answer with a tool call instead of prose.
/// </summary>
/// <param name="Role">
/// <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c> — the four the chat-completions shape
/// defines. Anything else is the caller's bug, not the provider's.
/// </param>
/// <param name="Content">The text. Null on an assistant turn that only asks for tools.</param>
/// <remarks>
/// Deliberately a flat record with two optional extras rather than a type per role: the wire format is
/// the same object with different fields present, and a hierarchy here would be three classes that
/// each exist to be pattern-matched back into the same JSON.
/// </remarks>
public sealed record AiChatMessage(string Role, string? Content)
{
    public static AiChatMessage System(string content) => new("system", content);

    public static AiChatMessage User(string content) => new("user", content);

    /// <summary>What the model asked to run, on an <c>assistant</c> turn.</summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>Which call a <c>tool</c> turn answers.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>A capability offered to the model, described as a JSON Schema.</summary>
/// <param name="Name">The function name the model calls. Stable — it is what the server dispatches on.</param>
/// <param name="Description">
/// What the tool is for, in the model's language. This is the whole specification the model reads, so
/// the cases it should call on belong here rather than in the prompt.
/// </param>
/// <param name="ParametersJson">A JSON Schema object describing the arguments.</param>
public sealed record AiToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>One tool the model wants to run.</summary>
/// <param name="Id">Provider-assigned id, echoed back on the <c>tool</c> message that answers it.</param>
/// <param name="Name">Which tool — matched against the definitions that were offered.</param>
/// <param name="ArgumentsJson">
/// A JSON object. A string, not a parsed value: a model can emit a truncated or off-schema one, and
/// parsing belongs to whoever knows what the arguments are supposed to mean.
/// </param>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One model turn: prose, a request to run tools, or both.
/// </summary>
/// <param name="Content">The reply text, or null when the turn is only tool calls.</param>
/// <param name="ToolCalls">Empty when the model answered in prose.</param>
public sealed record AiChatReply(string? Content, IReadOnlyList<AiToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}
