using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Ai;

/// <summary>
/// Turns a seller's photos into a moderation verdict, a price range and a description.
/// </summary>
/// <remarks>
/// <para>
/// One model call answers all three questions, which is why this reads as a pipeline rather than a
/// service: check the input, spend an attempt, gather the local prices that make the price advice
/// worth anything, ask, and interpret the answer.
/// </para>
/// <para>
/// <b>Nothing here blocks a publish.</b> The verdict is returned for the seller to look at. See
/// <see cref="IListingAssistant"/>.
/// </para>
/// </remarks>
internal sealed class ListingAssistant(
    AppDbContext dbContext,
    IAiChatClient chatClient,
    IMemoryCache cache,
    IOptions<AiOptions> options,
    ILogger<ListingAssistant> logger) : IListingAssistant
{
    /// <summary>Longest title the listing form will accept, so a long answer is not lost.</summary>
    private const int MaxTitleChars = 100;

    /// <summary>Longest description the listing form will accept, so a verbose answer is not lost.</summary>
    private const int MaxDescriptionChars = 2000;

    private static readonly string[] AcceptedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly JsonSerializerOptions AnswerFormat =
        new(JsonSerializerDefaults.Web)
        {
            // Providers differ on whether a number comes back as 320 or "320", and a whole answer
            // should not be thrown away over which one this one chose.
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

    private readonly AiOptions _settings = options.Value;

    public async Task<OperationResult<ListingSuggestionResponse>> SuggestAsync(
        long userId,
        IReadOnlyList<AiImage> images,
        long? categoryId,
        string? title,
        string? apiKeyOverride,
        CancellationToken cancellationToken = default)
    {
        var rejection = Validate(images);

        if (rejection is not null)
        {
            return OperationResult<ListingSuggestionResponse>.Failure(
                ErrorCodes.InvalidArgument, rejection);
        }

        // A seller who brought their own key spends their own money, so their calls are neither
        // metered nor counted against the deployment's allowance.
        var bringsOwnKey = !string.IsNullOrWhiteSpace(apiKeyOverride);
        var apiKey = bringsOwnKey ? apiKeyOverride! : _settings.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Neither a key of their own nor one configured here. Reported as our outage rather than
            // theirs, because that is what it is.
            logger.LogWarning("No AI key is configured and the caller did not supply one.");

            return Unavailable();
        }

        int? remaining = null;

        if (!bringsOwnKey)
        {
            if (!TrySpend(userId, out var left))
            {
                return OperationResult<ListingSuggestionResponse>.Failure(
                    ErrorCodes.QuotaExceeded,
                    $"今天的 {_settings.DailyQuota} 次 AI 助手用完了，明天再来；" +
                    "或者在页面下方填自己的 API key，那个不占次数。");
            }

            remaining = left;
        }

        var comparables = await ComparablesAsync(categoryId, cancellationToken);
        var prompt = BuildPrompt(categoryId, title, comparables);

        var answer = await AskAsync(apiKey, images, prompt, cancellationToken);

        if (answer is null)
        {
            // Hand the attempt back. The seller did not get an answer, and charging them for a
            // provider outage would empty everyone's allowance the first time the vendor had a bad
            // afternoon.
            if (!bringsOwnKey)
            {
                Refund(userId);
            }

            return Unavailable();
        }

        // No category means no local prices to anchor against, so a number here would be a guess
        // presented as advice. The client says as much instead of showing it.
        var (low, high) = categoryId is null
            ? (null, null)
            : NormalizeRange(answer.PriceLow, answer.PriceHigh);

        return OperationResult<ListingSuggestionResponse>.Success(
            new ListingSuggestionResponse(
                answer.Flagged,
                Trim(answer.Reason),
                TrimToLimit(answer.Title, MaxTitleChars),
                low,
                high,
                TrimToLimit(answer.Description, MaxDescriptionChars),
                remaining));
    }

    /// <summary>
    /// One call, three answers.
    /// </summary>
    /// <remarks>
    /// <b>This is the seam the content-moderation gate will attach to.</b> Judging the photos is the
    /// part that a dedicated service — 阿里云内容安全, say — does properly and this shared model call
    /// does only as a side effect. That service speaks an entirely different protocol, so swapping it
    /// in means rewriting this one method: call the moderator, call the model for the price and the
    /// description, merge both into one <see cref="ModelAnswer"/> and return that. The signature, the
    /// caller and <see cref="IListingAssistant"/> are all unaffected.
    /// <para>
    /// The other half of the seam is that nothing else in this class looks at images. Whatever the
    /// gate ends up being, the photos reach the model at exactly this point and nowhere else.
    /// </para>
    /// </remarks>
    private async Task<ModelAnswer?> AskAsync(
        string apiKey,
        IReadOnlyList<AiImage> images,
        string prompt,
        CancellationToken cancellationToken)
    {
        var text = await chatClient.CompleteAsync(apiKey, images, prompt, cancellationToken);

        if (text is null)
        {
            return null;
        }

        var answer = ParseAnswer(text);

        if (answer is null)
        {
            // Distinct from an unreachable provider, and worth telling apart in the log: this one
            // means the model answered but ignored the format it was given.
            logger.LogWarning("The model's answer could not be read as the agreed JSON shape.");
        }

        return answer;
    }

    /// <summary>
    /// Pulls the JSON object out of a model's answer.
    /// </summary>
    /// <remarks>
    /// Takes the first <c>{</c> through the last <c>}</c> rather than deserialising the string whole.
    /// <c>response_format</c> is meant to make that unnecessary, but not every OpenAI-compatible
    /// provider honours it, and the usual failure is a correct object wrapped in a sentence or a
    /// markdown fence. One attempt at recovery, no repair loop: if the braces do not hold an object
    /// the answer is genuinely unusable.
    /// </remarks>
    private static ModelAnswer? ParseAnswer(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ModelAnswer>(text[start..(end + 1)], AnswerFormat);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The local listings that make a price suggestion mean something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model has no idea what a second-hand desk lamp goes for in a Beijing dormitory in 2026, and
    /// left to itself it will answer from prices somewhere else entirely. Handing it real listings from
    /// this category turns the task into "place this among those", which is a question it can answer.
    /// </para>
    /// <para>
    /// Newest first, and the sample is a plain recency window rather than a curated one. What makes a
    /// listing useful here is that it reflects today's market, so that is what picks the twenty. Which
    /// of them actually sold is decided after the fact, by labelling each line in the prompt — a price
    /// somebody paid is better evidence than one somebody only asked, but that is a judgement for the
    /// model to make with both in front of it, not a reason to hide every unsold listing from it.
    /// </para>
    /// </remarks>
    private async Task<List<Comparable>> ComparablesAsync(
        long? categoryId,
        CancellationToken cancellationToken)
    {
        if (categoryId is not long id)
        {
            return [];
        }

        var cutoff = AppDbContext.AuditNow.AddDays(-_settings.ComparableWindowDays);

        return await dbContext.Products
            .AsNoTracking()
            .Where(product => product.CategoryId == id
                && product.DeletedAt == null
                && product.PublishedAt != null
                && product.PublishedAt >= cutoff
                && product.Price > 0)
            .OrderByDescending(product => product.PublishedAt)
            .Take(_settings.MaxComparables)
            .Select(product => new Comparable(
                product.Title,
                product.Price,
                product.Condition,
                product.SoldAt != null))
            .ToListAsync(cancellationToken);
    }

    private string BuildPrompt(long? categoryId, string? title, List<Comparable> comparables)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("你是一个校园二手交易平台的发布助手。卖家上传了商品照片，请照着照片把商品信息补全。");
        prompt.AppendLine();
        prompt.AppendLine("只输出一个 JSON 对象，不要输出任何解释文字，不要用 markdown 代码围栏。结构必须是：");
        prompt.AppendLine();
        prompt.AppendLine("{");
        prompt.AppendLine("  \"flagged\": false,");
        prompt.AppendLine("  \"reason\": \"\",");
        prompt.AppendLine("  \"title\": \"\",");
        prompt.AppendLine("  \"priceLow\": 0,");
        prompt.AppendLine("  \"priceHigh\": 0,");
        prompt.AppendLine("  \"description\": \"\"");
        prompt.AppendLine("}");
        prompt.AppendLine();
        prompt.AppendLine("字段说明：");
        prompt.AppendLine("- flagged：布尔值。照片里有没有色情、暴力、恐怖或政治敏感的内容。");
        prompt.AppendLine("- reason：字符串。flagged 为 true 时用一句话说明是什么；否则填空字符串。");
        prompt.AppendLine("- title：字符串。一个可以直接当商品标题用的中文短句，20 个字以内、最多 100 个字，");
        prompt.AppendLine("  说清楚是什么东西，带上成色或者最值得说的那个卖点。不要加引号、不加书名号、");
        prompt.AppendLine("  不写「闲置出售」这类没信息量的词。");
        prompt.AppendLine("- priceLow、priceHigh：数字（不是字符串），单位元，人民币。建议价格区间的下限和上限。");
        prompt.AppendLine("- description：字符串。一段可以直接使用的中文商品描述，120 到 250 字，");
        prompt.AppendLine("  用卖家的口吻写，包含成色、用途、可能的小瑕疵和希望的交易方式。");
        prompt.AppendLine("  不要编造照片里看不出来的信息。");
        prompt.AppendLine();
        prompt.AppendLine("判断规则：");
        prompt.AppendLine("- 普通的学习用品、电子产品、生活用品、衣物、书籍都不算违规。");
        prompt.AppendLine("- 拿不准是什么东西的时候，判 flagged = false。");
        prompt.AppendLine("- 照片看不清商品时，title 和 description 就照着卖家填的标题写，不要瞎猜。");
        prompt.AppendLine("- 卖家自己填了标题也照样给 title：你看过照片，按你看到的给一个更好的。");
        prompt.AppendLine();
        prompt.AppendLine("卖家填的信息：");
        prompt.AppendLine($"- 标题：{TitleFor(title)}");

        if (categoryId is null)
        {
            prompt.AppendLine("- 分类：（还没选）");
            prompt.AppendLine();
            prompt.AppendLine("卖家还没选分类，这次不要给价格建议，priceLow 和 priceHigh 都填 0。");
        }
        else if (comparables.Count == 0)
        {
            prompt.AppendLine("- 分类：（已选）");
            prompt.AppendLine();
            prompt.AppendLine("这个分类下暂时没有可比对的成交记录，价格凭经验给一个合理的区间。");
        }
        else
        {
            prompt.AppendLine("- 分类：（已选）");
            prompt.AppendLine();
            prompt.AppendLine("平台上同类商品的近期价格，是真实数据，请让 priceLow 和 priceHigh 落在它们附近：");

            foreach (var comparable in comparables)
            {
                var state = comparable.Sold ? "已成交" : "在售";
                var name = comparable.Title.Length > 40
                    ? comparable.Title[..40]
                    : comparable.Title;

                prompt.AppendLine(
                    $"- 「{name}」 {comparable.Price.ToString("0.##", CultureInfo.InvariantCulture)} 元，" +
                    $"{ConditionName(comparable.Condition)}，{state}");
            }
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The seller's title, cut to what the listing form itself accepts.
    /// </summary>
    /// <remarks>
    /// The form caps the field at 100 characters, but this endpoint does not go through the form, and
    /// an unbounded title would be pasted into every prompt verbatim. Matching the form's own limit
    /// means the assistant never sees a title the seller could not actually have saved.
    /// <para>
    /// An empty one is spelled out rather than left blank, because the model is being asked to produce
    /// a title and a bare <c>- 标题：</c> reads like a field it is meant to ignore.
    /// </para>
    /// </remarks>
    private static string TitleFor(string? title)
    {
        var trimmed = title?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return "（还没填，请你在 title 里给一个）";
        }

        return trimmed.Length <= MaxTitleChars ? trimmed : trimmed[..MaxTitleChars];
    }

    private static string ConditionName(ProductCondition condition) => condition switch
    {
        ProductCondition.New => "全新",
        ProductCondition.LikeNew => "几乎全新",
        ProductCondition.Good => "轻微使用痕迹",
        ProductCondition.Fair => "明显使用痕迹",
        _ => "成色未说明"
    };

    /// <summary>
    /// Puts a suggestion back in order, or throws it away.
    /// </summary>
    /// <remarks>
    /// A model that returns the bounds the wrong way round is a normal kind of wrong, and swapping
    /// them costs nothing. A lower bound at or below zero is a different kind of wrong — it means the
    /// model answered with a placeholder rather than a price — so both ends are dropped rather than
    /// shown as "0 到 500 元".
    /// </remarks>
    private static (decimal? Low, decimal? High) NormalizeRange(decimal? low, decimal? high)
    {
        // One end is enough: a model that gives a single number has still answered the question, and
        // the range collapses onto it.
        var lower = low ?? high;
        var upper = high ?? low;

        if (lower is null || upper is null || lower <= 0 || upper <= 0)
        {
            return (null, null);
        }

        return lower <= upper ? (lower, upper) : (upper, lower);
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Trims a model-written field, and cuts it to the length the listing form itself accepts.
    /// </summary>
    /// <remarks>
    /// The prompt states the length it wants; a model that ignores that would produce text the seller
    /// cannot save, which reads as the assistant being broken rather than verbose. Cutting to the
    /// form's own limit is the smaller surprise, and it is the same limit in both cases — the
    /// <c>maxlength</c> on the input and the <c>StringLength</c> on the request that stores it.
    /// </remarks>
    private static string? TrimToLimit(string? value, int max)
    {
        var trimmed = Trim(value);

        return trimmed is null || trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>
    /// Checks the photos the client sent.
    /// </summary>
    /// <remarks>
    /// The browser downscales before uploading, but that is a courtesy and not a guarantee — this
    /// endpoint is reachable by anything holding a token, so the ceilings are enforced here rather
    /// than assumed.
    /// </remarks>
    private string? Validate(IReadOnlyList<AiImage> images)
    {
        if (images.Count == 0)
        {
            return "先选至少一张照片。";
        }

        if (images.Count > _settings.MaxImages)
        {
            return $"一次最多看 {_settings.MaxImages} 张照片。";
        }

        foreach (var image in images)
        {
            if (!AcceptedContentTypes.Contains(image.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                return "只认得 JPEG、PNG 和 WebP 三种图片。";
            }

            if (image.Content.Length == 0)
            {
                return "有一张照片是空的，重新传一次试试。";
            }

            if (image.Content.Length > _settings.MaxImageBytes)
            {
                return $"有张照片太大了，单张上限 {_settings.MaxImageBytes / (1024 * 1024)} MB。";
            }
        }

        return null;
    }

    private static OperationResult<ListingSuggestionResponse> Unavailable() =>
        OperationResult<ListingSuggestionResponse>.Failure(
            ErrorCodes.ServiceUnavailable, "AI 助手暂时用不了，自己写也一样能发布。");

    /// <summary>
    /// How many attempts an account has left today, for callers using the deployment's own key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In-process and deliberately so: this deployment runs one instance, and the alternative — a
    /// table — means a write on every assistant call to defend against an abuse that has not happened.
    /// The cost of being wrong is that a restart resets everyone's day, which is a trade this project
    /// makes elsewhere too.
    /// </para>
    /// <para>
    /// The key carries the Beijing date and expires at the end of it, from
    /// <see cref="AppDbContext.AuditNow"/> — the project's one clock. Using UTC would roll everyone's
    /// allowance over at 8am local.
    /// </para>
    /// </remarks>
    private sealed class Counter
    {
        public int Value;
    }

    private static string QuotaKey(DateTime now, long userId) =>
        $"ai-quota:{now:yyyyMMdd}:{userId}";

    private bool TrySpend(long userId, out int remaining)
    {
        var now = AppDbContext.AuditNow;

        var counter = cache.GetOrCreate(
            QuotaKey(now, userId),
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = now.Date.AddDays(1) - now;
                return new Counter();
            })!;

        var spent = Interlocked.Increment(ref counter.Value);

        remaining = Math.Max(0, _settings.DailyQuota - spent);

        return spent <= _settings.DailyQuota;
    }

    private void Refund(long userId)
    {
        if (cache.TryGetValue(QuotaKey(AppDbContext.AuditNow, userId), out Counter? counter)
            && counter is not null)
        {
            Interlocked.Decrement(ref counter.Value);
        }
    }

    /// <summary>A nearby listing, in the form the prompt needs it.</summary>
    private sealed record Comparable(string Title, decimal Price, ProductCondition Condition, bool Sold);

    /// <summary>
    /// The model's answer, as asked for. Properties rather than positional parameters so that a
    /// missing field is a default instead of a deserialization failure — a model that drops
    /// <c>reason</c> should cost the seller a sentence, not the whole answer.
    /// </summary>
    private sealed record ModelAnswer
    {
        public bool Flagged { get; init; }

        public string? Reason { get; init; }

        public string? Title { get; init; }

        public decimal? PriceLow { get; init; }

        public decimal? PriceHigh { get; init; }

        public string? Description { get; init; }
    }
}
