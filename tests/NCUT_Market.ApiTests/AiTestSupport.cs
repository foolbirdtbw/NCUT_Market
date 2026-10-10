using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.Services;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Stands in for the model provider, so no test in this suite ever opens a socket.
/// </summary>
/// <remarks>
/// <para>
/// The whole transport sits behind <see cref="IAiChatClient"/>, so replacing that one registration is
/// enough — there is no fake <c>HttpMessageHandler</c> here and no <c>HttpClient</c> in the picture at
/// all. What this leaves under test is everything that is actually ours: the validation, the
/// allowance, the price lookup, the prompt, and the reading of the model's answer.
/// </para>
/// <para>
/// <b>What it answers is decided by the prompt it is given, not by state on this object.</b> The
/// fixture is shared by every test class in the collection, so a stand-in holding "what to return
/// next" would be racing itself the moment two classes ran at once. Instead the test writes a marker
/// into the listing title, the service pastes that title into the prompt, and this reads it back out.
/// </para>
/// </remarks>
internal sealed class FakeAiChatClient : IAiChatClient
{
    /// <summary>A title marker meaning "answer with something that is not JSON".</summary>
    public const string BrokenAnswerMarker = "BADJSON";

    /// <summary>A title marker meaning "answer as though the photos were objectionable".</summary>
    public const string FlaggedMarker = "FLAG";

    /// <summary>
    /// Text placed inside the unusable answer. Nothing in the pipeline may repeat it back to the
    /// caller — a provider's answer is a thing we read, not a thing we relay.
    /// </summary>
    public const string LeakMarker = "UPSTREAM-ANSWER-MUST-NOT-BE-ECHOED";

    private const string NormalAnswer =
        """
        {"flagged":false,"reason":"","priceLow":120,"priceHigh":180,
         "description":"宿舍闲置的台灯，九成新，用了一个学期，三档亮度都正常，灯罩没有划痕。晚上看书够亮，也不占地方。西区宿舍楼下自取，可以帮忙送到校门口。"}
        """;

    private const string FlaggedAnswer =
        """
        {"flagged":true,"reason":"照片里出现了疑似违规的画面","priceLow":0,"priceHigh":0,
         "description":"这是一段仍然可以用的描述——判定违规并不影响卖家继续编辑和发布。"}
        """;

    private readonly ConcurrentQueue<string> _prompts = new();
    private readonly ConcurrentQueue<string> _keys = new();

    /// <summary>Every prompt this stand-in has been handed, oldest first.</summary>
    /// <remarks>The shared instance means these accumulate across the whole run; tests look for their own.</remarks>
    public IReadOnlyList<string> Prompts => [.. _prompts];

    /// <summary>Every key this stand-in was asked to authenticate with, oldest first.</summary>
    public IReadOnlyList<string> Keys => [.. _keys];

    public Task<string?> CompleteAsync(
        string apiKey,
        IReadOnlyList<AiImage> images,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        _prompts.Enqueue(prompt);
        _keys.Enqueue(apiKey);

        var answer = prompt.Contains(BrokenAnswerMarker, StringComparison.Ordinal)
            ? $"抱歉，我不太明白。{LeakMarker}"
            : prompt.Contains(FlaggedMarker, StringComparison.Ordinal)
                ? FlaggedAnswer
                : NormalAnswer;

        return Task.FromResult<string?>(answer);
    }
}

/// <summary>The one request the assistant tests make, in all its variations.</summary>
internal static class AiTestSupport
{
    /// <summary>Header a caller's own model key travels in.</summary>
    public const string KeyHeader = "X-Ai-Key";

    /// <summary>A key distinctive enough that finding it anywhere is unambiguous.</summary>
    public const string SentinelKey = "sk-SENTINEL-DO-NOT-ECHO-0123456789";

    /// <summary>Posts a set of photos to the assistant.</summary>
    public static async Task<HttpResponseMessage> AskAssistantAsync(
        this HttpClient client,
        string title,
        long? categoryId = null,
        int photos = 1,
        byte[]? photo = null,
        string? apiKey = null)
    {
        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(title), "title");

        if (categoryId is long id)
        {
            form.Add(new StringContent(id.ToString(CultureInfo.InvariantCulture)), "categoryId");
        }

        for (var index = 0; index < photos; index++)
        {
            var part = new ByteArrayContent(photo ?? TestImages.Jpeg(64, 64));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            // A file part carries a filename, which is what makes the binder treat it as an IFormFile
            // rather than a plain string.
            form.Add(part, "images", $"photo-{index}.jpg");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ai/listing-draft")
        {
            Content = form
        };

        if (apiKey is not null)
        {
            request.Headers.Add(KeyHeader, apiKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>A title that no other test will produce, and which carries a marker for the stand-in.</summary>
    public static string NewTitle(string marker = "") =>
        $"{marker}listing-{Guid.NewGuid().ToString("N")[..12]}";
}
