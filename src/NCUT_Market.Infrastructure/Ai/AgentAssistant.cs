using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Ai;

/// <summary>
/// The character in the corner: page hints computed from the database, and answers grounded in them.
/// </summary>
/// <remarks>
/// <para>
/// The two halves here are the same code path up to the point where one of them calls the model.
/// <see cref="BuildContextAsync"/> is what both use, which is deliberate: whatever she can say about
/// a page when nobody asks is exactly what she is allowed to know when somebody does. There is no
/// second, richer source of truth behind the chat.
/// </para>
/// <para>
/// <b>Anonymous readers cost no queries.</b> Everything personal is behind
/// <c>userId is not long</c>. The exception is the product page, where the same-category prices are
/// public information and a signed-out visitor benefits from them just as much.
/// </para>
/// </remarks>
internal sealed class AgentAssistant(
    AppDbContext dbContext,
    IConversationService conversations,
    INotificationService notifications,
    IProductService productService,
    IAiChatClient chatClient,
    AiQuota quota,
    IOptions<AiOptions> options,
    ILogger<AgentAssistant> logger) : IAgentAssistant
{
    /// <summary>Longest question the panel will send, matched by the input's <c>maxlength</c>.</summary>
    private const int MaxQuestionChars = 300;

    /// <summary>
    /// How much of an answer the panel shows. Short on purpose: this is a bubble in a corner, not a
    /// page, and a paragraph that overflows it is worse than a cut one.
    /// </summary>
    private const int MaxAnswerChars = 300;

    /// <summary>How much history the browser may send, in turns.</summary>
    private const int MaxHistoryTurns = 8;

    /// <summary>Longest single turn that is kept. Older context is cheaper than an unbounded prompt.</summary>
    private const int MaxTurnChars = 300;

    /// <summary>Minimum same-category listings before an average is worth stating.</summary>
    private const int MinComparables = 3;

    /// <summary>How many listings a search hands back. A bubble in a corner, not a results page.</summary>
    private const int MaxSearchResults = 5;

    /// <summary>
    /// How many words one search may try: the model's keyword plus its synonyms.
    /// </summary>
    /// <remarks>
    /// A shopper's word is not a title's word — "喝茶用的" is a 保温杯 in the table and in no title at
    /// all — and the model is the only part of this that knows what people here call things. Each
    /// extra term is one more indexed query, so the list is capped; the loop stops at the first match.
    /// </remarks>
    private const int MaxSearchTerms = 5;

    /// <summary>Every how many plain questions the profile is rewritten. A search always rewrites it.</summary>
    private const int ProfileEveryN = 3;

    /// <summary>How much of the model's note is kept. The panel never shows it; the prompt does.</summary>
    private const int MaxProfileChars = 300;

    /// <summary>At most this many search terms survive a rewrite.</summary>
    private const int MaxProfileKeywords = 5;

    /// <summary>
    /// How many model turns one question may take before it is made to answer.
    /// </summary>
    /// <remarks>
    /// A model that keeps searching is buying nothing: the listings do not change between its turns,
    /// only its confusion does. Three is enough for "search, look, search a synonym, answer".
    /// </remarks>
    private const int MaxToolSteps = 3;

    /// <summary>The one tool's name. Dispatch and the test fake both key on this string.</summary>
    private const string SearchToolName = "search_products";

    /// <summary>
    /// How many stored exchanges the panel gets back. Enough to scroll through a session's worth,
    /// short enough that opening the panel is not a full transcript read.
    /// </summary>
    private const int HistoryRows = 20;

    /// <summary>
    /// What the model may call. The description is the whole instruction it reads about the tool, so
    /// the cases worth calling on live here rather than only in the prompt.
    /// </summary>
    private static readonly IReadOnlyList<AiToolDefinition> ToolDefinitions =
    [
        new AiToolDefinition(
            SearchToolName,
            "在站内公开的在售商品里查找。用户想买什么、或者追问「有没有」「还有吗」时调用；"
            + "只查得到在售商品，查不到就不存在。",
            """
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "最具体的一个搜索关键词。用户说得笼统（「喝茶用的」）就替他挑最接近的具体东西，比如「茶具」。"
                },
                "also": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "2 到 4 个同义词或相近说法，query 查不到时依次再试，比如 茶杯、水杯、保温杯。"
                },
                "category": {
                  "type": "string",
                  "description": "站内分类名，只能从提示里给出的「站内分类」中原样挑一个；对不上就留空字符串。"
                },
                "maxPrice": {
                  "type": "number",
                  "description": "用户说出的最高价，单位元。「15 以下」就是 15，「预算 50」就是 50；用户没说价格就不要给。"
                }
              },
              "required": ["query"]
            }
            """)
    ];

    private static readonly JsonSerializerOptions JsonFormat = new(JsonSerializerDefaults.Web);

    private static readonly Regex BlankRun = new(@"\n{3,}", RegexOptions.Compiled);

    private readonly AiOptions _settings = options.Value;

    public async Task<AgentHintsResponse> GetHintsAsync(
        long? userId,
        string? page,
        long? productId,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(userId, page, productId, cancellationToken);

        // No signed-in caller means no allowance exists to report. Saying "20 次" to a visitor who
        // cannot ask anything would be worse than saying nothing.
        int? remaining = userId is long id ? quota.Remaining(id) : null;

        return new AgentHintsResponse(context.Hints, remaining);
    }

    public async Task<OperationResult<AgentAnswerResponse>> AskAsync(
        long userId,
        AgentAskRequest request,
        CancellationToken cancellationToken = default)
    {
        var question = request.Message?.Trim();

        if (string.IsNullOrEmpty(question))
        {
            return OperationResult<AgentAnswerResponse>.Failure(
                ErrorCodes.InvalidArgument, "问点什么吧。");
        }

        if (question.Length > MaxQuestionChars)
        {
            return OperationResult<AgentAnswerResponse>.Failure(
                ErrorCodes.InvalidArgument, $"一次最多问 {MaxQuestionChars} 个字。");
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            logger.LogWarning("No AI key is configured and the agent cannot borrow one.");

            return Unavailable();
        }

        if (!quota.TrySpend(userId, out var remaining))
        {
            return OperationResult<AgentAnswerResponse>.Failure(
                ErrorCodes.QuotaExceeded,
                $"今天和小北说话的 {_settings.DailyQuota} 次用完了，明天再来。");
        }

        var profile = await dbContext.AiProfiles
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        var context = await BuildContextAsync(userId, request.Page, request.ProductId, cancellationToken);

        var messages = new List<AiChatMessage>
        {
            AiChatMessage.System(BuildPrompt(
                context,
                await CategoryNamesAsync(cancellationToken),
                string.IsNullOrWhiteSpace(profile?.Summary) ? null : profile.Summary,
                request.History)),
            AiChatMessage.User(question)
        };

        var results = new List<ProductSummaryResponse>();
        var seen = new HashSet<long>();
        var searched = false;
        string? answer = null;

        // The model decides whether to search. Each turn either answers or asks to run a tool; the
        // tool's output goes back as one more message and the loop runs again — bounded, because a
        // model that keeps searching would otherwise bill the deployment for its own confusion.
        for (var step = 0; step < MaxToolSteps; step++)
        {
            var reply = await chatClient.ChatAsync(
                _settings.ApiKey, messages, ToolDefinitions, cancellationToken);

            if (reply is null)
            {
                logger.LogWarning("The agent's model call returned nothing usable.");
                quota.Refund(userId);

                return Unavailable();
            }

            if (!reply.HasToolCalls)
            {
                answer = CleanAnswer(reply.Content);
                break;
            }

            messages.Add(new AiChatMessage("assistant", reply.Content) { ToolCalls = reply.ToolCalls });

            foreach (var call in reply.ToolCalls)
            {
                searched |= call.Name == SearchToolName;

                messages.Add(new AiChatMessage("tool", await RunToolAsync(call, results, seen, cancellationToken))
                {
                    ToolCallId = call.Id
                });
            }
        }

        if (answer is null)
        {
            // Out of steps, or the last turn was another tool call. One more turn with no tools on
            // offer: the model has to say something, and the reader gets a sentence instead of a 503.
            var final = await chatClient.ChatAsync(_settings.ApiKey, messages, tools: null, cancellationToken);

            if (final is null)
            {
                logger.LogWarning("The agent's model call returned nothing usable.");
                quota.Refund(userId);

                return Unavailable();
            }

            answer = CleanAnswer(final.Content);
        }

        if (answer is null)
        {
            // Same reasoning as the listing assistant: a provider outage is not the reader's fault,
            // and charging them for it empties everyone's allowance the first bad afternoon.
            logger.LogWarning("The agent's model call returned nothing usable.");
            quota.Refund(userId);

            return Unavailable();
        }

        var shown = results
            .Take(MaxSearchResults)
            .Select(item => new AgentProductResult(item.Id, item.Title, item.Price))
            .ToList();

        var result = OperationResult<AgentAnswerResponse>.Success(
            new AgentAnswerResponse(answer, remaining, shown));

        // The transcript outlives the tab from here on. Tracked, not saved: the memory update below
        // always calls SaveChanges, and one round trip per question is plenty.
        dbContext.AiMessages.Add(new AiMessage
        {
            UserId = userId,
            Question = question,
            Answer = answer,
            ResultsJson = shown.Count == 0 ? string.Empty : JsonSerializer.Serialize(shown, JsonFormat)
        });

        // After the answer is settled, so a profile call that fails cannot turn a good question into an
        // error. A search is the clearest statement of what someone wants, so it counts as its own
        // signal; plain questions are summarised every few turns.
        await UpdateProfileAsync(userId, profile, request.History, question, searched, cancellationToken);

        return result;
    }

    public async Task<IReadOnlyList<AgentMessageResponse>> GetHistoryAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Newest first to let the index and the LIMIT do their job, then flipped back into the order a
        // conversation is read in.
        var rows = await dbContext.AiMessages
            .AsNoTracking()
            .Where(message => message.UserId == userId)
            .OrderByDescending(message => message.Id)
            .Take(HistoryRows)
            .ToListAsync(cancellationToken);

        rows.Reverse();

        return [.. rows.Select(row => new AgentMessageResponse(
            row.Id,
            row.Question,
            row.Answer,
            ParseResults(row.ResultsJson),
            row.CreatedAt))];
    }

    /// <summary>
    /// Reads back the listings stored with an answer, or none when the column holds nothing usable.
    /// </summary>
    private static List<AgentProductResult> ParseResults(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AgentProductResult>>(json, JsonFormat) ?? [];
        }
        catch (JsonException)
        {
            // Written by this service, so this is a corrupt row rather than a model's mistake; an
            // answer without its list still reads, so the transcript is worth more than the strictness.
            return [];
        }
    }

    /// <summary>
    /// Runs one tool call and turns its rows into the text the model reads next turn.
    /// </summary>
    /// <remarks>
    /// The model never sees ids or links — it asked with a keyword, it gets titles and prices, and the
    /// clickable list under the bubble is built from the query result. A search that finds nothing says
    /// so plainly: the model then has one true sentence to answer with, which is better than a second
    /// call guessing at reasons.
    /// </remarks>
    private async Task<string> RunToolAsync(
        AiToolCall call,
        List<ProductSummaryResponse> results,
        HashSet<long> seen,
        CancellationToken cancellationToken)
    {
        if (call.Name != SearchToolName)
        {
            return "没有这个工具。";
        }

        var args = ParseSearchArguments(call.ArgumentsJson);
        var term = args.Query?.Trim();

        if (string.IsNullOrWhiteSpace(term))
        {
            return "搜索缺少关键词。";
        }

        var categoryId = await ResolveCategoryIdAsync(args.Category, cancellationToken);

        // A ceiling of zero or less is the model misunderstanding the field, not a reader asking for
        // free things; ignoring it degrades to the search without a price, which is still a search.
        var maxPrice = args.MaxPrice > 0 ? args.MaxPrice : null;

        var matches = await SearchProductsAsync(
            SearchTerms(Short(term, 50), args.Also), categoryId, maxPrice, cancellationToken);

        // The panel's list is the union of every search this question ran, in the order found.
        foreach (var item in matches)
        {
            if (seen.Add(item.Id))
            {
                results.Add(item);
            }
        }

        if (matches.Count == 0)
        {
            return "站内没有找到相关的在售商品。";
        }

        var output = new StringBuilder().AppendLine("找到这些在售商品：");

        foreach (var item in matches)
        {
            output.AppendLine($"- {Short(item.Title, 40)}，{Money(item.Price)}，{item.CategoryName}");
        }

        return output.ToString();
    }

    /// <summary>
    /// Reads one tool call's arguments, or an empty set when the model emitted something else.
    /// </summary>
    /// <remarks>
    /// Off-schema arguments are an expected failure of a language model, not an exception worth a 500:
    /// an unusable search is a search with no keyword, which <see cref="RunToolAsync"/> already answers.
    /// </remarks>
    private static AgentSearchArgs ParseSearchArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentSearchArgs>(argumentsJson, JsonFormat)
                ?? new AgentSearchArgs();
        }
        catch (JsonException)
        {
            return new AgentSearchArgs();
        }
    }

    /// <summary>
    /// The rows a search hands back: the first word that matches anything, then a named category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A keyword is a literal substring match, which is far too narrow for how people ask — "喝茶用的"
    /// is a 保温杯 in the table and not a word in any title. So the model's one keyword is a list, and
    /// they are tried in order until one matches; the model writes the synonyms because "what this
    /// campus calls a kettle" is not something this code knows.
    /// </para>
    /// <para>
    /// A named category is the floor under that, not filler around it: it is consulted only when none
    /// of the words matched anything. 离散数学 is filed under 教材书籍 and never says "书", so a search
    /// that only had the category to go on still finds it. A word that did match means the reader said
    /// what they wanted; padding the list with the rest of the category would answer a question they
    /// did not ask.
    /// </para>
    /// <para>
    /// A price ceiling, when the reader stated one, applies to both halves. Without it "15 以下" would
    /// come back with a ¥29 listing and the model could only apologise for it.
    /// </para>
    /// </remarks>
    private async Task<List<ProductSummaryResponse>> SearchProductsAsync(
        IReadOnlyList<string> terms,
        long? categoryId,
        decimal? maxPrice,
        CancellationToken cancellationToken)
    {
        var page = new PaginationQuery(1, MaxSearchResults);
        var found = new List<ProductSummaryResponse>();
        var seen = new HashSet<long>();

        void Collect(IReadOnlyList<ProductSummaryResponse> items)
        {
            foreach (var item in items)
            {
                if (seen.Add(item.Id))
                {
                    found.Add(item);
                }
            }
        }

        foreach (var term in terms)
        {
            Collect((await productService.ListAsync(
                new ProductListQuery { Q = term, CategoryId = categoryId, MaxPrice = maxPrice },
                page,
                cancellationToken)).Items);

            if (found.Count > 0)
            {
                break;
            }
        }

        // Nothing matched on any word: show what the category holds rather than nothing.
        if (categoryId is long id && found.Count == 0)
        {
            Collect((await productService.ListAsync(
                new ProductListQuery { CategoryId = id, MaxPrice = maxPrice }, page, cancellationToken)).Items);
        }

        return [.. found.Take(MaxSearchResults)];
    }

    /// <summary>
    /// The words to try, in order: the model's keyword, then its synonyms.
    /// </summary>
    /// <remarks>
    /// Capped and de-duplicated because the browser is not the only thing writing here — the model is.
    /// Five short terms is four more queries than before, all against an indexed LIKE on a table this
    /// size, and it stops as soon as one of them matches.
    /// </remarks>
    private static List<string> SearchTerms(string rawSearch, List<string>? also)
    {
        IEnumerable<string?> raw = [rawSearch, .. also ?? []];

        return raw
            .Select(item => item?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => Short(item!, 20))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSearchTerms)
            .ToList();
    }

    // ----------------------------------------------------------------------------------------
    // Memory
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Counts the question and, when it is time, rewrites what the assistant remembers about the reader.
    /// </summary>
    /// <remarks>
    /// The model's note is the only thing kept — not the turns it was written from, and not a failure.
    /// If the call comes back unusable the old note stays exactly as it was; the question that just
    /// succeeded is never turned into an error by memory maintenance.
    /// </remarks>
    private async Task UpdateProfileAsync(
        long userId,
        AiProfile? profile,
        IReadOnlyList<AgentTurn>? history,
        string question,
        bool searched,
        CancellationToken cancellationToken)
    {
        if (profile is null)
        {
            // Written out on the first question even when nothing is summarised yet: QuestionCount is
            // the only place "every third question" can be counted from, and an unpersisted counter
            // restarts at one on every request.
            profile = new AiProfile { UserId = userId };
            dbContext.AiProfiles.Add(profile);
        }

        profile.QuestionCount++;

        // A search is the strongest signal there is, so it rewrites the note at once. Plain questions
        // wait for every third one, which is one model call per three turns instead of three.
        if (!searched && profile.QuestionCount % ProfileEveryN != 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return;
        }

        var memo = ParseProfile(await chatClient.CompleteAsync(
            _settings.ApiKey,
            [],
            BuildProfilePrompt(profile, await CategoryNamesAsync(cancellationToken), history, question),
            cancellationToken,
            jsonResponse: true));

        if (memo is not null)
        {
            if (!string.IsNullOrWhiteSpace(memo.Summary))
            {
                profile.Summary = Short(memo.Summary.Trim(), MaxProfileChars);
            }

            if (memo.Keywords is { Count: > 0 })
            {
                profile.Keywords = Short(
                    string.Join(",", memo.Keywords
                        .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                        .Select(keyword => Short(keyword.Trim(), 20))
                        .Take(MaxProfileKeywords)),
                    200);
            }

            if (memo.Category is not null)
            {
                profile.CategoryName = Short(memo.Category.Trim(), 100);
            }
        }

        // Null here means the call failed; the previous note stands and the count still moves on.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string BuildProfilePrompt(
        AiProfile profile,
        IReadOnlyList<string> categories,
        IReadOnlyList<AgentTurn>? history,
        string question)
    {
        var prompt = new StringBuilder()
            .AppendLine("你是「小北」，北方工业大学校园二手交易平台的小助手。")
            .AppendLine("下面是一位用户在站内提过的话。请把它总结成给站内助手看的备忘：")
            .AppendLine("这位用户想买什么、能接受什么价位。不要推测性别、学院、年级、联系方式或任何身份信息。")
            .AppendLine("摘要不超过 80 字。分类名只能从下面的「站内分类」里原样挑一个，挑不出就留空字符串。");

        if (categories.Count > 0)
        {
            prompt.AppendLine($"站内分类：{string.Join("、", categories)}");
        }

        prompt.AppendLine($"之前记下的备忘：{(string.IsNullOrWhiteSpace(profile.Summary) ? "无" : profile.Summary)}");

        if (TrimHistory(history) is { Count: > 0 } turns)
        {
            prompt.AppendLine("最近的对话：");

            foreach (var turn in turns)
            {
                prompt.AppendLine($"{(turn.Role == "user" ? "用户" : "小北")}：{turn.Content}");
            }
        }

        prompt.AppendLine($"用户现在问：{question}");
        prompt.AppendLine("只输出一个 JSON 对象：");
        prompt.AppendLine("{\"summary\":\"…\",\"keywords\":[\"最多 5 个站内搜索词\"],\"category\":\"分类名或空字符串\"}");

        return prompt.ToString();
    }

    /// <summary>
    /// What the hints panel can say on its own: one live listing matching what this reader keeps asking
    /// about. Null when there is no note, or when nothing matching is still for sale.
    /// </summary>
    private async Task<AgentHint?> RecommendationAsync(long userId, CancellationToken cancellationToken)
    {
        var profile = await dbContext.AiProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        if (profile is null)
        {
            return null;
        }

        // The category first — it is the widest thing she remembers — then the search words. Either
        // half alone can match nothing: a category name is never a substring of a title, and a keyword
        // can be a word nobody here uses.
        var terms = SearchTerms(
            profile.CategoryName,
            [.. profile.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries)]);

        if (terms.Count == 0)
        {
            return null;
        }

        var categoryId = await ResolveCategoryIdAsync(profile.CategoryName, cancellationToken);
        var hit = (await SearchProductsAsync(terms, categoryId, null, cancellationToken)).FirstOrDefault();

        return hit is null
            ? null
            : new AgentHint(
                "recommend",
                $"你之前找过{Short(terms[0], 12)}，这件还在卖：{Short(hit.Title, 20)}，{Money(hit.Price)}",
                $"#/products/{hit.Id}");
    }

    /// <summary>
    /// The page facts behind both halves. Nothing here is cached: a stale "2 条未读" after reading
    /// them is worse than one cheap query.
    /// </summary>
    private async Task<AgentContext> BuildContextAsync(
        long? userId,
        string? page,
        long? productId,
        CancellationToken cancellationToken)
    {
        var slug = page?.Trim().ToLowerInvariant() ?? string.Empty;

        // Handled before the anonymous branch: the local price sample is public, and a visitor
        // deciding whether to buy gains as much from it as a signed-in one.
        if (slug == PageSlugs.Product)
        {
            return await ProductAsync(userId, productId, cancellationToken);
        }

        if (userId is not long id)
        {
            return new AgentContext(slug, Anonymous(slug), null);
        }

        return slug switch
        {
            PageSlugs.Home => await HomeAsync(id, cancellationToken),
            PageSlugs.Products => await ProductsAsync(id, cancellationToken),
            PageSlugs.Create => await CreateAsync(id, cancellationToken),
            PageSlugs.Mine => await MineAsync(id, cancellationToken),
            PageSlugs.Messages => await MessagesAsync(id, cancellationToken),
            PageSlugs.Notifications => await NotificationsAsync(id, cancellationToken),
            PageSlugs.Feedback => new AgentContext(slug, Feedback(), null),
            _ => new AgentContext(slug, General(signedIn: true), null)
        };
    }

    // ----------------------------------------------------------------------------------------
    // Per-page hints
    // ----------------------------------------------------------------------------------------

    private async Task<AgentContext> HomeAsync(long userId, CancellationToken cancellationToken)
    {
        var counts = await OwnStatusCountsAsync(userId, cancellationToken);
        var drafts = counts.GetValueOrDefault(ProductStatus.Draft);
        var unread = await UnreadThreadsAsync(userId, cancellationToken);

        var hints = new List<AgentHint>();

        if (drafts > 0)
        {
            hints.Add(new AgentHint("drafts", $"有 {drafts} 件草稿还没发完，接着改就能上架。", "#/mine"));
        }

        if (unread > 0)
        {
            hints.Add(new AgentHint(
                "unread-messages", $"有 {unread} 个私信会话在等你回复。", "#/messages"));
        }

        // Remembered from earlier questions. Added unconditionally rather than only in the empty case:
        // "nothing to do today" and "here is something you wanted" are both worth saying, and the
        // browse fallback below only fires when this produced nothing either.
        if (await RecommendationAsync(userId, cancellationToken) is { } recommendation)
        {
            hints.Add(recommendation);
        }

        if (hints.Count == 0)
        {
            hints.Add(new AgentHint("browse", "今天没有待处理的事，去逛逛商品吧。", "#/products"));
        }

        return new AgentContext(PageSlugs.Home, hints, null);
    }

    private async Task<AgentContext> ProductsAsync(long userId, CancellationToken cancellationToken)
    {
        var counts = await OwnStatusCountsAsync(userId, cancellationToken);
        var online = counts.GetValueOrDefault(ProductStatus.Published);

        var hints = new List<AgentHint>();

        if (online > 0)
        {
            hints.Add(new AgentHint(
                "own-listings", $"你在站上还有 {online} 件商品在卖。", "#/mine"));
        }

        hints.Add(new AgentHint(
            "search-tip", "按分类筛选比只输关键词更快找到想要的东西。", null));

        if (await RecommendationAsync(userId, cancellationToken) is { } recommendation)
        {
            hints.Add(recommendation);
        }

        return new AgentContext(PageSlugs.Products, hints, null);
    }

    private async Task<AgentContext> CreateAsync(long userId, CancellationToken cancellationToken)
    {
        var counts = await OwnStatusCountsAsync(userId, cancellationToken);
        var drafts = counts.GetValueOrDefault(ProductStatus.Draft);

        var hints = new List<AgentHint>();

        if (drafts > 0)
        {
            hints.Add(new AgentHint("drafts", $"上次还有 {drafts} 件草稿没发完。", "#/mine"));
        }

        hints.Add(new AgentHint(
            "listing-tool", "选好照片，我可以照着照片帮你起标题、写描述、估个价。", null));

        if (counts.Count == 0)
        {
            hints.Add(new AgentHint(
                "first-listing",
                "第一件商品：标题写清楚是什么、几成新，比写「闲置出售」有用得多。",
                null));
        }

        return new AgentContext(PageSlugs.Create, hints, null);
    }

    private async Task<AgentContext> MineAsync(long userId, CancellationToken cancellationToken)
    {
        var now = AppDbContext.AuditNow;
        var counts = await OwnStatusCountsAsync(userId, cancellationToken);

        // LastActivityAt, not PublishedAt: it is the clock that only advances on a real edit, which is
        // exactly what "nobody has touched this in a month" means.
        var stale = await dbContext.Products
            .AsNoTracking()
            .CountAsync(
                product => product.SellerId == userId
                    && product.DeletedAt == null
                    && product.Status == ProductStatus.Published
                    && product.LastActivityAt < now.AddDays(-30),
                cancellationToken);

        var hints = new List<AgentHint>();
        var drafts = counts.GetValueOrDefault(ProductStatus.Draft);
        var sold = counts.GetValueOrDefault(ProductStatus.Sold);

        if (drafts > 0)
        {
            hints.Add(new AgentHint("drafts", $"有 {drafts} 件草稿还没发完。", "#/mine"));
        }

        if (stale > 0)
        {
            hints.Add(new AgentHint(
                "stale-listings", $"有 {stale} 件挂了 30 天没动过，考虑降点价或者直接下架。", "#/mine"));
        }

        if (sold > 0)
        {
            hints.Add(new AgentHint("sold", $"已经卖出 {sold} 件。", "#/mine"));
        }

        if (hints.Count == 0)
        {
            hints.Add(new AgentHint("first-listing", "还没有商品？去发第一件吧。", "#/products/new"));
        }

        return new AgentContext(PageSlugs.Mine, hints, null);
    }

    private async Task<AgentContext> MessagesAsync(long userId, CancellationToken cancellationToken)
    {
        var unread = await UnreadThreadsAsync(userId, cancellationToken);

        var proposals = await dbContext.Conversations
            .AsNoTracking()
            .CountAsync(
                conversation => ((conversation.BuyerId == userId && conversation.BuyerDeletedAt == null)
                        || (conversation.SellerId == userId && conversation.SellerDeletedAt == null))
                    && conversation.TransactionProposedAt != null,
                cancellationToken);

        var hints = new List<AgentHint>();

        if (unread > 0)
        {
            hints.Add(new AgentHint(
                "unread-messages", $"有 {unread} 个会话有未读消息。", "#/messages"));
        }

        if (proposals > 0)
        {
            hints.Add(new AgentHint(
                "pending-proposals", $"有 {proposals} 个交易提议等你回复。", "#/messages"));
        }

        if (hints.Count == 0)
        {
            hints.Add(new AgentHint("all-read", "现在没有未读消息，有新动静我会告诉你。", null));
        }

        return new AgentContext(PageSlugs.Messages, hints, null);
    }

    private async Task<AgentContext> NotificationsAsync(long userId, CancellationToken cancellationToken)
    {
        var unread = (await notifications.GetUnreadCountAsync(userId, cancellationToken)).Value?.Count ?? 0;

        var hints = unread > 0
            ? new List<AgentHint>
            {
                new("unread-notifications", $"有 {unread} 条通知还没看。", "#/notifications")
            }
            : new List<AgentHint> { new("all-read", "没有未读通知。", null) };

        return new AgentContext(PageSlugs.Notifications, hints, null);
    }

    private async Task<AgentContext> ProductAsync(
        long? userId,
        long? productId,
        CancellationToken cancellationToken)
    {
        if (productId is not long id)
        {
            return new AgentContext(PageSlugs.Product, ProductFallback(), null);
        }

        var now = AppDbContext.AuditNow;

        var product = await dbContext.Products
            .AsNoTracking()
            .Where(item => item.Id == id && item.DeletedAt == null)
            .Select(item => new
            {
                item.SellerId,
                item.Title,
                item.Price,
                item.CategoryId,
                item.Status,
                item.PublishedAt,
                item.CreatedAt,
                RecentCount = item.Conversations.Count(c => c.LastMessageAt >= now.AddDays(-7)),
                HasProposal = item.Conversations.Any(c => c.TransactionProposedAt != null)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Same rule as ProductService.GetByIdAsync. A draft or an offline listing must not even
        // acknowledge that it exists, or its id becomes a way to probe what other people are not selling.
        var visible = product is not null
            && (product.Status is ProductStatus.Published or ProductStatus.Sold or ProductStatus.InTransaction
                || product.SellerId == userId);

        if (!visible)
        {
            return new AgentContext(PageSlugs.Product, ProductFallback(), null);
        }

        var line = new StringBuilder()
            .Append("- 这件商品：")
            .Append(Short(product!.Title, 40))
            .Append("，")
            .Append(Money(product.Price))
            .Append("，")
            .Append(StatusName(product.Status))
            .ToString();

        var hints = product.SellerId == userId
            ? SellerProductHints(product.HasProposal, product.RecentCount, now, product.PublishedAt, product.CreatedAt)
            : await BuyerProductHintsAsync(product.CategoryId, id, product.Status, cancellationToken);

        return new AgentContext(PageSlugs.Product, hints, line);
    }

    private List<AgentHint> SellerProductHints(
        bool hasProposal,
        int recentCount,
        DateTime now,
        DateTime? publishedAt,
        DateTime createdAt)
    {
        var hints = new List<AgentHint>();

        if (hasProposal)
        {
            hints.Add(new AgentHint(
                "pending-proposals", "有人在等你回交易提议，去私信里处理一下。", "#/messages"));
        }

        if (recentCount > 0)
        {
            hints.Add(new AgentHint(
                "recent-questions", $"最近 7 天有 {recentCount} 个人问过这件商品。", "#/messages"));
        }

        var days = (now - (publishedAt ?? createdAt)).Days;

        if (days >= 14)
        {
            hints.Add(new AgentHint(
                "stale-listing", $"这件已经挂了 {days} 天，可以改改标题或者稍微降点价。", null));
        }

        if (hints.Count == 0)
        {
            hints.Add(new AgentHint(
                "no-questions", "这件还没人问过，把标题和照片写清楚一些会更好卖。", null));
        }

        return hints;
    }

    /// <summary>
    /// What a buyer looking at somebody else's listing should know.
    /// </summary>
    /// <remarks>
    /// The average comes from the same recency window and row cap as the listing assistant's
    /// comparables — a sample of what is actually on the shelf today, not an aggregate over every
    /// listing ever posted. Fewer than <see cref="MinComparables"/> rows and no number is stated,
    /// because an average of two things is not a market price.
    /// </remarks>
    private async Task<List<AgentHint>> BuyerProductHintsAsync(
        long categoryId,
        long productId,
        ProductStatus status,
        CancellationToken cancellationToken)
    {
        var hints = new List<AgentHint>();

        if (status is ProductStatus.Published or ProductStatus.InTransaction)
        {
            var cutoff = AppDbContext.AuditNow.AddDays(-_settings.ComparableWindowDays);

            var prices = await dbContext.Products
                .AsNoTracking()
                .Where(item => item.CategoryId == categoryId
                    && item.Id != productId
                    && item.DeletedAt == null
                    && item.Status == ProductStatus.Published
                    && item.Price > 0
                    && item.PublishedAt >= cutoff)
                .OrderByDescending(item => item.PublishedAt)
                .Take(_settings.MaxComparables)
                .Select(item => item.Price)
                .ToListAsync(cancellationToken);

            hints.Add(prices.Count >= MinComparables
                ? new AgentHint(
                    "price-average",
                    $"同类在售商品近期均价大约 {Money(prices.Average())}，可以照着这个数掂量。",
                    null)
                : new AgentHint("price-unknown", "同类商品还不多，价格可以当面谈。", null));
        }

        hints.Add(new AgentHint(
            "contact-seller", "想买就直接点「联系卖家」，问清楚成色、能不能当面验货。", null));

        return hints;
    }

    // ----------------------------------------------------------------------------------------
    // Static sets
    // ----------------------------------------------------------------------------------------

    private static IReadOnlyList<AgentHint> Anonymous(string slug) => slug switch
    {
        PageSlugs.Home or PageSlugs.Products => new[]
        {
            new AgentHint("browse", "第一次来？先看看最近上架的东西。", "#/products"),
            new AgentHint("sign-in", "登录之后我还能看到你自己的草稿和未读消息。", "#/login")
        },
        PageSlugs.Create => new[]
        {
            new AgentHint("photo-tip", "先拍几张清楚的照片，标题写清楚是什么、几成新。", null),
            new AgentHint("sign-in", "登录之后才能发布。", "#/login")
        },
        PageSlugs.Mine or PageSlugs.Messages or PageSlugs.Notifications => new[]
        {
            new AgentHint("sign-in", "登录之后才能看到自己的这些东西。", "#/login")
        },
        PageSlugs.Feedback => Feedback(),
        _ => General()
    };

    private static IReadOnlyList<AgentHint> Feedback() => new[]
    {
        new AgentHint("feedback-tip", "反馈可以写用起来不顺的地方，也可以提建议。", null),
        new AgentHint("feedback-anonymous", "勾了匿名，昵称就不会出现在反馈板上。", null)
    };

    /// <summary>
    /// Any page that has nothing page-specific to say — announcements, sign-in, a URL nobody
    /// recognises.
    /// </summary>
    /// <remarks>
    /// The sign-in line is only for people who are not signed in. Putting it in the signed-in branch
    /// too would have her telling a logged-in reader to log in.
    /// </remarks>
    private static IReadOnlyList<AgentHint> General(bool signedIn = false) => signedIn
        ? new[]
        {
            new AgentHint("welcome", "我是小北，站里的事尽量问我。", null),
            new AgentHint("find", "想找什么直接说，比如「帮我找找自行车」，我去在售商品里翻。", "#/products")
        }
        : new[]
        {
            new AgentHint("welcome", "我是小北，站里的事尽量问我。", null),
            new AgentHint("sign-in", "登录之后我能看到你自己的草稿和未读消息。", "#/login")
        };

    private static IReadOnlyList<AgentHint> ProductFallback() => new[]
    {
        new AgentHint("contact-seller", "想买就直接点「联系卖家」，问清楚成色和交易方式。", null),
        new AgentHint("price-unknown", "价格可以当面谈，见到实物再定更稳。", null)
    };

    // ----------------------------------------------------------------------------------------
    // Ask
    // ----------------------------------------------------------------------------------------

    private static string BuildPrompt(
        AgentContext context,
        IReadOnlyList<string> categories,
        string? profileSummary,
        IReadOnlyList<AgentTurn>? history)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("你是「小北」，北方工业大学校园二手交易平台的小助手。");
        prompt.AppendLine("用简体中文回答，口语、简短，最多 3 句话、120 字以内。");
        prompt.AppendLine("不要用 markdown、列表、代码块或链接，也不要自称 AI。");
        prompt.AppendLine("只能依据下面「页面信息」和工具的返回回答；里面没有的事实，尤其是价格、库存和别人的联系方式，");
        prompt.AppendLine("就直说不知道，不要编造数字。");
        prompt.AppendLine("如果用户是在找站内商品（例如「帮我找找自行车」「有没有便宜的台灯」「我想买点喝茶用的」），");
        prompt.AppendLine("或者上一轮刚找过、这一句在追问「有没有」「还有吗」「都没有吗」，");
        prompt.AppendLine("调用 search_products 去在售商品里查，不要自己编答案。");
        prompt.AppendLine("query 挑最具体的一种说法；用户说得笼统（「喝茶用的」）就替他挑最接近的具体东西，");
        prompt.AppendLine("再把 2 到 4 个同义词或相近说法放进 also（例如 query 茶具，also 茶杯、水杯、保温杯）。");
        prompt.AppendLine("分类名只能从下面的「站内分类」里原样挑一个，对不上就留空字符串。");
        prompt.AppendLine("用户说了价格上限（「15 以下」「预算 50」）就放进 maxPrice；没说就别传，不要自己编一个数。");
        prompt.AppendLine("查到的商品列表会由系统附在你回复的下方，不要逐条复述标题，也不要编造商品、价格或链接。");
        prompt.AppendLine("其他问题直接回答，不用调用工具。");

        if (categories.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine($"站内分类：{string.Join("、", categories)}");
        }

        prompt.AppendLine();

        if (!string.IsNullOrWhiteSpace(profileSummary))
        {
            prompt.AppendLine($"你对这位用户的印象：{profileSummary}");
        }

        prompt.AppendLine($"当前页面：{PageName(context.Page)}");
        prompt.AppendLine("页面信息：");

        foreach (var hint in context.Hints)
        {
            prompt.AppendLine($"- {hint.Text}");
        }

        if (context.ProductLine is not null)
        {
            prompt.AppendLine(context.ProductLine);
        }

        if (TrimHistory(history) is { Count: > 0 } turns)
        {
            prompt.AppendLine();
            prompt.AppendLine("之前的对话（从旧到新，只用来理解上下文）：");

            foreach (var turn in turns)
            {
                prompt.AppendLine($"{(turn.Role == "user" ? "用户" : "小北")}：{turn.Content}");
            }
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The history the server is willing to accept, whatever the browser sent.
    /// </summary>
    /// <remarks>
    /// The browser is the only store, so this is also the only place the prompt can be grown without
    /// bound. Last eight turns, two known roles, each cut to three hundred characters — enough to
    /// follow "那这件呢" and not enough to matter to the bill.
    /// </remarks>
    private static List<AgentTurn> TrimHistory(IReadOnlyList<AgentTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            return [];
        }

        return history
            .Where(turn => turn.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(turn.Content))
            .TakeLast(MaxHistoryTurns)
            .Select(turn => turn with { Content = Short(turn.Content!.Trim(), MaxTurnChars) })
            .ToList();
    }

    /// <summary>
    /// Turns a model reply into something worth showing, or null when there is nothing.
    /// </summary>
    /// <remarks>
    /// The prompt asks for plain prose; models still sometimes answer with a fenced block. Dropping
    /// the fence is three lines and saves the reader from reading ``` around the sentence.
    /// </remarks>
    private static string? CleanAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var answer = text.Trim();

        if (answer.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = answer.IndexOf('\n');

            if (newline >= 0)
            {
                answer = answer[(newline + 1)..].Trim();
            }
        }

        if (answer.EndsWith("```", StringComparison.Ordinal))
        {
            answer = answer[..^3].Trim();
        }

        answer = BlankRun.Replace(answer, "\n\n");

        if (answer.Length > MaxAnswerChars)
        {
            answer = answer[..MaxAnswerChars] + "…";
        }

        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }

    /// <summary>
    /// Reads the profile call's reply, or null when there is nothing usable in it.
    /// </summary>
    /// <remarks>
    /// First brace to last. A provider that ignores JSON mode and answers in prose lands here as null,
    /// which leaves the old note untouched.
    /// </remarks>
    private static AgentProfileMemo? ParseProfile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentProfileMemo>(text[start..(end + 1)], JsonFormat);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The category names the model is allowed to name, in the order the site shows them.
    /// </summary>
    /// <remarks>
    /// One small query per question. Without this the model has to guess what the site calls things,
    /// and "书" comes back as a word no category is called, so the search stays keyword-only.
    /// </remarks>
    private async Task<List<string>> CategoryNamesAsync(CancellationToken cancellationToken) =>
        await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.Status == CategoryStatus.Active)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Id)
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Turns the category name the model guessed into a real id, or null when it guessed wrong.
    /// </summary>
    /// <remarks>
    /// Asked of the table directly rather than through the paged category list: the dictionary is
    /// small today but the list endpoint sorts by <c>sort_order</c>, so a name could fall off the
    /// first page and quietly turn a narrow search into a keyword-only one. A miss is not an error —
    /// the search falls back to the keyword alone, which is still the thing the reader asked for.
    /// </remarks>
    private async Task<long?> ResolveCategoryIdAsync(string? name, CancellationToken cancellationToken)
    {
        var wanted = name?.Trim();

        if (string.IsNullOrEmpty(wanted))
        {
            return null;
        }

        var exact = await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.Status == CategoryStatus.Active && category.Name == wanted)
            .Select(category => (long?)category.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (exact is long id)
        {
            return id;
        }

        return await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.Status == CategoryStatus.Active
                && (category.Name.Contains(wanted) || wanted.Contains(category.Name)))
            .OrderBy(category => category.Id)
            .Select(category => (long?)category.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ----------------------------------------------------------------------------------------
    // Shared queries and formatting
    // ----------------------------------------------------------------------------------------

    private async Task<Dictionary<ProductStatus, int>> OwnStatusCountsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Products
            .AsNoTracking()
            .Where(product => product.SellerId == userId && product.DeletedAt == null)
            .GroupBy(product => product.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Status, row => row.Count);
    }

    private async Task<int> UnreadThreadsAsync(long userId, CancellationToken cancellationToken) =>
        (await conversations.GetUnreadCountAsync(userId, cancellationToken)).Value?.Count ?? 0;

    private static string PageName(string slug) => slug switch
    {
        PageSlugs.Home => "首页",
        PageSlugs.Products => "商品列表",
        PageSlugs.Product => "商品详情",
        PageSlugs.Create => "发布商品",
        PageSlugs.Mine => "个人主页",
        PageSlugs.Messages => "私信",
        PageSlugs.Notifications => "通知",
        PageSlugs.Feedback => "反馈",
        _ => "其他页面"
    };

    private static string StatusName(ProductStatus status) => status switch
    {
        ProductStatus.Draft => "草稿",
        ProductStatus.Published => "在售",
        ProductStatus.Sold => "已卖出",
        ProductStatus.Offline => "已下架",
        ProductStatus.InTransaction => "交易中",
        _ => "状态未知"
    };

    private static string Money(decimal value) =>
        "¥" + value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Short(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static OperationResult<AgentAnswerResponse> Unavailable() =>
        OperationResult<AgentAnswerResponse>.Failure(
            ErrorCodes.ServiceUnavailable, "小北暂时答不上来，过一会儿再问我。");

    /// <summary>What the client sends as <c>page</c>. Unknown values fall through to general advice.</summary>
    private static class PageSlugs
    {
        public const string Home = "home";
        public const string Products = "products";
        public const string Product = "product";
        public const string Create = "create";
        public const string Mine = "mine";
        public const string Messages = "messages";
        public const string Notifications = "notifications";
        public const string Feedback = "feedback";
    }

    /// <summary>The page, as hints for it, plus the one line about the listing when there is one.</summary>
    private sealed record AgentContext(
        string Page,
        IReadOnlyList<AgentHint> Hints,
        string? ProductLine);

    /// <summary>The arguments <c>search_products</c> takes, as far as this server cares about them.</summary>
    private sealed record AgentSearchArgs
    {
        /// <summary>The one word to try first.</summary>
        public string? Query { get; init; }

        /// <summary>Other words to try when the keyword itself matches nothing.</summary>
        public List<string>? Also { get; init; }

        /// <summary>A category name exactly as the prompt listed it, or blank.</summary>
        public string? Category { get; init; }

        /// <summary>The highest price the reader named, in yuan, or null when they named none.</summary>
        public decimal? MaxPrice { get; init; }
    }

    /// <summary>What the profile call may return.</summary>
    private sealed record AgentProfileMemo
    {
        public string? Summary { get; init; }

        public List<string>? Keywords { get; init; }

        public string? Category { get; init; }
    }
}
