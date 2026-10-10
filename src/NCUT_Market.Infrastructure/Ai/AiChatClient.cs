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
        CancellationToken cancellationToken = default)
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
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = parts }
            }
        };

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
                return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                logger.LogWarning("The model provider's answer was not JSON.");
                return null;
            }
            catch (InvalidOperationException)
            {
                // A `content` that is not a string — some providers return an array of parts.
                logger.LogWarning("The model provider's answer had no readable content.");
                return null;
            }
        }
    }
}
