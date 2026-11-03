using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;
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

        var context = await BuildContextAsync(userId, request.Page, request.ProductId, cancellationToken);
        var prompt = BuildPrompt(context, request.History, question);

        // No images, and explicitly no JSON constraint: an answer is a sentence, not a document.
        var text = await chatClient.CompleteAsync(
            _settings.ApiKey, [], prompt, cancellationToken, jsonResponse: false);

        var answer = CleanAnswer(text);

        if (answer is null)
        {
            // Same reasoning as the listing assistant: a provider outage is not the reader's fault,
            // and charging them for it empties everyone's allowance the first bad afternoon.
            logger.LogWarning("The agent's model call returned nothing usable.");
            quota.Refund(userId);

            return Unavailable();
        }

        return OperationResult<AgentAnswerResponse>.Success(
            new AgentAnswerResponse(answer, remaining));
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
            _ => new AgentContext(slug, General(), null)
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

    private static IReadOnlyList<AgentHint> General() => new[]
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
        IReadOnlyList<AgentTurn>? history,
        string question)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("你是「小北」，北方工业大学校园二手交易平台的小助手。");
        prompt.AppendLine("用简体中文回答，口语、简短，最多 3 句话、120 字以内。");
        prompt.AppendLine("不要用 markdown、列表、代码块或链接，也不要自称 AI。");
        prompt.AppendLine("只能依据下面「页面信息」回答；里面没有的事实，尤其是价格、库存和别人的联系方式，");
        prompt.AppendLine("就直说不知道，不要编造数字。");
        prompt.AppendLine();
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

        prompt.AppendLine();
        prompt.AppendLine($"用户现在问：{question}");

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
}
