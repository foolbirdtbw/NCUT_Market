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

    /// <summary>A title marker meaning "answer with a title longer than the form will accept".</summary>
    public const string LongTitleMarker = "LONGTITLE";

    /// <summary>How long that title is. Comfortably past the form's 100-character ceiling.</summary>
    public const int LongTitleLength = 150;

    /// <summary>A question marker meaning "the provider answered with nothing at all".</summary>
    public const string EmptyAnswerMarker = "EMPTYANSWER";

    /// <summary>A question marker meaning "the answer is far longer than the panel can show".</summary>
    public const string LongAnswerMarker = "LONGANSWER";

    /// <summary>A question marker meaning "the answer arrived wrapped in a markdown fence".</summary>
    public const string FenceAnswerMarker = "FENCEANSWER";

    /// <summary>A question marker meaning "answer as the chat assistant would".</summary>
    public const string AgentAnswerMarker = "AGENTTEST";

    /// <summary>What <see cref="AgentAnswerMarker"/> produces.</summary>
    public const string AgentAnswer = "照片拍清楚一点，标题里写清是什么和几成新。";

    /// <summary>How long <see cref="LongAnswerMarker"/>'s answer is.</summary>
    public const int LongAnswerLength = 500;

    /// <summary>A question marker meaning "the assistant should search for the fixed keyword".</summary>
    public const string SearchIntentMarker = "FINDLISTINGS";

    /// <summary>
    /// A question marker meaning "search the fixed keyword inside the fixture's child category". The
    /// keyword matches no title on purpose, so the category is the only thing that can find anything.
    /// </summary>
    public const string SearchCategoryMarker = "FINDINCATEGORY";

    /// <summary>A question marker meaning "match by keyword inside the child category, category and all".</summary>
    public const string SearchPadMarker = "FINDPAD";

    /// <summary>A question marker meaning "search, with a ceiling of ¥50 the reader stated".</summary>
    public const string SearchPriceMarker = "FINDUNDER";

    /// <summary>A question marker meaning "search for a keyword nothing is listed under".</summary>
    public const string SearchMissMarker = "FINDNOTHING";

    /// <summary>A question marker meaning "the search finds rows, but the summary call comes back empty".</summary>
    public const string SearchFailMarker = "SEARCHFAIL";

    /// <summary>A question marker meaning "the keyword matches nothing, but the synonym does".</summary>
    public const string SearchSynonymMarker = "FINDWITHALSO";

    /// <summary>
    /// Text only the profile prompt carries, which is what makes a call identifiable as "summarise
    /// this reader" — the question itself can appear in both kinds of prompt.
    /// </summary>
    public const string ProfilePromptMarker = "给站内助手看的备忘";

    /// <summary>What the profile call returns. The category is the fixture's child category.</summary>
    public const string ProfileAnswer =
        """{"summary":"这位同学在找二手教材，预算不高。","keywords":["离散数学"],"category":"测试-子分类"}""";

    private const string NormalAnswer =
        """
        {"flagged":false,"reason":"","title":"宿舍台灯 九成新 三档调光",
         "priceLow":120,"priceHigh":180,
         "description":"宿舍闲置的台灯，九成新，用了一个学期，三档亮度都正常，灯罩没有划痕。晚上看书够亮，也不占地方。西区宿舍楼下自取，可以帮忙送到校门口。"}
        """;

    private const string FlaggedAnswer =
        """
        {"flagged":true,"reason":"照片里出现了疑似违规的画面","title":"待确认的商品",
         "priceLow":0,"priceHigh":0,
         "description":"这是一段仍然可以用的描述——判定违规并不影响卖家继续编辑和发布。"}
        """;

    /* 长度由代码算出来，不是手打的一百五十个字：手打的数不清，而这条测试的全部意义
     * 就是那个长度。 */
    private static readonly string LongTitleAnswer = string.Concat(
        "{\"title\":\"", new string('标', LongTitleLength), "\",\"description\":\"还是给一段描述。\"}");

    private readonly ConcurrentQueue<string> _prompts = new();
    private readonly ConcurrentQueue<string> _keys = new();
    private readonly ConcurrentQueue<string> _chatPrompts = new();
    private readonly ConcurrentQueue<bool> _toolsOffered = new();

    /// <summary>Every prompt this stand-in has been handed, oldest first.</summary>
    /// <remarks>The shared instance means these accumulate across the whole run; tests look for their own.</remarks>
    public IReadOnlyList<string> Prompts => [.. _prompts];

    /// <summary>Every key this stand-in was asked to authenticate with, oldest first.</summary>
    public IReadOnlyList<string> Keys => [.. _keys];

    /// <summary>Every conversation handed to <see cref="ChatAsync"/>, oldest first, messages joined.</summary>
    public IReadOnlyList<string> ChatPrompts => [.. _chatPrompts];

    /// <summary>Whether each chat turn was allowed to call a tool, oldest first.</summary>
    public IReadOnlyList<bool> ToolsOffered => [.. _toolsOffered];

    public Task<string?> CompleteAsync(
        string apiKey,
        IReadOnlyList<AiImage> images,
        string prompt,
        CancellationToken cancellationToken = default,
        bool jsonResponse = true)
    {
        _prompts.Enqueue(prompt);
        _keys.Enqueue(apiKey);

        // The profile prompt is checked before anything else: it carries the user's question verbatim,
        // so a reader asking about another marker would otherwise get that marker's answer here.
        var answer = prompt.Contains(ProfilePromptMarker, StringComparison.Ordinal)
            ? ProfileAnswer
            : prompt.Contains(EmptyAnswerMarker, StringComparison.Ordinal)
                ? "   "
                : prompt.Contains(LongAnswerMarker, StringComparison.Ordinal)
                    ? new string('答', LongAnswerLength)
                    : prompt.Contains(FenceAnswerMarker, StringComparison.Ordinal)
                        ? "```\n被围栏包起来的回答\n```"
                        : prompt.Contains(BrokenAnswerMarker, StringComparison.Ordinal)
                            ? $"抱歉，我不太明白。{LeakMarker}"
                            : prompt.Contains(LongTitleMarker, StringComparison.Ordinal)
                                ? LongTitleAnswer
                                : prompt.Contains(FlaggedMarker, StringComparison.Ordinal)
                                    ? FlaggedAnswer
                                    : NormalAnswer;

        return Task.FromResult<string?>(answer);
    }

    /// <summary>
    /// The agent's conversation. A marker in the question makes it ask for a search on the first turn,
    /// and answer in prose once the tool's rows come back.
    /// </summary>
    /// <remarks>
    /// Whether a turn may call a tool is read from <paramref name="tools"/>, and whether it already has
    /// results is read from a <c>tool</c> message in the conversation — both things the service
    /// genuinely sends, so this needs no state of its own and cannot race another test class.
    /// </remarks>
    public Task<AiChatReply?> ChatAsync(
        string apiKey,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken = default)
    {
        var joined = string.Join("\n", messages.Select(message => message.Content ?? string.Empty));

        _chatPrompts.Enqueue(joined);
        _keys.Enqueue(apiKey);
        _toolsOffered.Enqueue(tools is { Count: > 0 });

        var answered = messages.Any(message => message.Role == "tool");

        if (!answered && tools is { Count: > 0 })
        {
            var arguments = joined.Contains(SearchIntentMarker, StringComparison.Ordinal)
                ? """{"query":"自行车"}"""
                : joined.Contains(SearchCategoryMarker, StringComparison.Ordinal)
                    ? """{"query":"ZZZ-NO-SUCH-THING","category":"测试-子分类"}"""
                    : joined.Contains(SearchPadMarker, StringComparison.Ordinal)
                        ? """{"query":"自行车","category":"测试-子分类"}"""
                        : joined.Contains(SearchPriceMarker, StringComparison.Ordinal)
                            ? """{"query":"自行车","maxPrice":50}"""
                            : joined.Contains(SearchMissMarker, StringComparison.Ordinal)
                                ? """{"query":"ZZZ-NO-SUCH-LISTING"}"""
                                : joined.Contains(SearchFailMarker, StringComparison.Ordinal)
                                    ? """{"query":"自行车"}"""
                                    : joined.Contains(SearchSynonymMarker, StringComparison.Ordinal)
                                        ? """{"query":"ZZZ-NO-SUCH-THING","also":["自行车"]}"""
                                        : null;

            if (arguments is not null)
            {
                return Task.FromResult<AiChatReply?>(new AiChatReply(
                    null,
                    [new AiToolCall("call-1", "search_products", arguments)]));
            }
        }

        // The rows came back and this provider still has nothing to say — the shape a summarising
        // failure takes now that the summary is the model's own next turn. An empty answer takes the
        // same exit: a reply with neither prose nor a tool call.
        if ((answered && joined.Contains(SearchFailMarker, StringComparison.Ordinal))
            || joined.Contains(EmptyAnswerMarker, StringComparison.Ordinal))
        {
            return Task.FromResult<AiChatReply?>(new AiChatReply(null, []));
        }

        var answer = joined.Contains(LongAnswerMarker, StringComparison.Ordinal)
            ? new string('答', LongAnswerLength)
            : joined.Contains(FenceAnswerMarker, StringComparison.Ordinal)
                ? "```\n被围栏包起来的回答\n```"
                : joined.Contains(BrokenAnswerMarker, StringComparison.Ordinal)
                    ? $"抱歉，我不太明白。{LeakMarker}"
                    : AgentAnswer;

        return Task.FromResult<AiChatReply?>(new AiChatReply(answer, []));
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
