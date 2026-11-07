using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Infrastructure.Ai;

/// <summary>
/// The one class in the project that sends a request to a machine we do not run.
/// </summary>
/// <remarks>
/// <para>
/// Speaks the OpenAI-compatible chat-completions shape, which is what DeepSeek, DashScope's compatible
/// mode, SiliconFlow and several others all implement — so changing vendors is a change to
/// <see cref="AiOptions.BaseUrl"/> and <see cref="AiOptions.Model"/>, not to this file. Everything
/// specific to that protocol lives here and nowhere else: the request layout, the data-URL encoding of
/// the photos, and the path down to <c>choices[0].message.content</c>.
/// </para>
/// <para>
/// <b>The key never leaves this method's arguments.</b> It arrives as a parameter, is used to set one
/// request header, and is not stored in a field, not put in a log message, and not carried into any
/// exception text. It may be a seller's own key, borrowed for the length of one request.
/// </para>
/// </remarks>
internal sealed class AiChatClient(
    HttpClient httpClient,
    IOptions<AiOptions> options,
    ILogger<AiChatClient> logger) : IAiChatClient
{
    private readonly AiOptions _options = options.Value;

    public async Task<string?> CompleteAsync(
        string apiKey,
        IReadOnlyList<AiImage> images,
        string prompt,
        CancellationToken cancellationToken = default,
        bool jsonResponse = true)
    {
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };

        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Content)}"
                }
            });
        }

        // Built as a JsonObject rather than from anonymous types because `content` is a heterogeneous
        // array — a text part, then one image part each — and the declared element type would have to
        // be `object`, which is the one place System.Text.Json's serialization gets surprising. Node
        // types say exactly what the wire format is.
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = parts }
            }
        };

        // Only when the caller asked for a JSON object. Constraining the reply is what makes the
        // listing assistant's answer parseable, and exactly what would ruin a chat reply.
        if (jsonResponse)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var message = await SendAsync(body, apiKey, cancellationToken);

        try
        {
            // A message whose content is not a string — some providers return an array of parts — is
            // not something either caller can use.
            return message?["content"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            logger.LogWarning("The model provider's answer was not JSON.");
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("The model provider's answer had no readable content.");
            return null;
        }
    }

    public async Task<AiChatReply?> ChatAsync(
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0.2,
            ["messages"] = BuildMessages(messages)
        };

        if (tools is { Count: > 0 })
        {
            var definitions = new JsonArray();

            foreach (var tool in tools)
            {
                definitions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.ParametersJson)
                    }
                });
            }

            body["tools"] = definitions;

            // "auto" rather than "required": most questions are answered from the page facts alone,
            // and a model forced to call a tool would search for something nobody asked about.
            body["tool_choice"] = "auto";
        }

        var message = await SendAsync(body, apiKey, cancellationToken);

        if (message is null)
        {
            return null;
        }

        try
        {
            var calls = new List<AiToolCall>();

            if (message["tool_calls"] is JsonArray requested)
            {
                foreach (var call in requested)
                {
                    var id = call?["id"]?.GetValue<string>();
                    var name = call?["function"]?["name"]?.GetValue<string>();

                    if (id is null || name is null)
                    {
                        continue;
                    }

                    // Arguments are kept as the raw string. A model can emit `{}`, a key it made up,
                    // or half an object, and only the service that defined the tool can say what that
                    // means — parsing here would just move the decision into the transport.
                    calls.Add(new AiToolCall(id, name, call?["function"]?["arguments"]?.GetValue<string>() ?? "{}"));
                }
            }

            var content = message["content"] is JsonValue value ? value.GetValue<string>() : null;

            if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("The model provider's answer had neither content nor tool calls.");
                return null;
            }

            return new AiChatReply(content, calls);
        }
        catch (JsonException)
        {
            logger.LogWarning("The model provider's answer was not JSON.");
            return null;
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("The model provider's answer had no readable content.");
            return null;
        }
    }

    /// <summary>
    /// Turns the conversation into the wire shape, including the two fields only a tool loop uses.
    /// </summary>
    private static JsonArray BuildMessages(IReadOnlyList<AiChatMessage> messages)
    {
        var array = new JsonArray();

        foreach (var message in messages)
        {
            var node = new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Content
            };

            if (message.ToolCalls is { Count: > 0 } calls)
            {
                var requested = new JsonArray();

                foreach (var call in calls)
                {
                    requested.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson
                        }
                    });
                }

                node["tool_calls"] = requested;
            }

            if (message.ToolCallId is not null)
            {
                node["tool_call_id"] = message.ToolCallId;
            }

            array.Add(node);
        }

        return array;
    }

    /// <summary>
    /// Posts a body and returns <c>choices[0].message</c>, or null when nothing usable came back.
    /// </summary>
    /// <remarks>
    /// The single place the key is used, the only place failures are classified, and the only place
    /// the response envelope is understood. Both public methods are then just "build a body".
    /// </remarks>
    private async Task<JsonNode?> SendAsync(
        JsonObject body,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        // The one and only use of the key.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout rather than the caller hanging up: the second case must propagate so
            // the request really does stop, and is filtered out by the `when` clause above.
            logger.LogWarning("The model did not answer within {Seconds}s.", _options.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException exception)
        {
            // Logged without any part of the request. A provider's connection error can quote the
            // URL, and the URL is the only thing here that is not secret — but the habit is what
            // keeps the key out of the one message that might have carried it.
            logger.LogWarning(exception, "Could not reach the model provider.");
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Status code only. A provider's error body routinely quotes the offending key back
                // at you — DeepSeek's reads `Invalid API key: sk-...` — so it is never logged and
                // never passed along to the caller, either.
                logger.LogWarning(
                    "The model provider answered {Status}.", (int)response.StatusCode);
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            try
            {
                return JsonNode.Parse(text)?["choices"]?[0]?["message"];
            }
            catch (JsonException)
            {
                logger.LogWarning("The model provider's answer was not JSON.");
                return null;
            }
        }
    }
}
