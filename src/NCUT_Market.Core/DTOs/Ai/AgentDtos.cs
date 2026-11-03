namespace NCUT_Market.Core.DTOs.Ai;

/// <summary>
/// One thing the assistant says on a page, computed from what is actually in the database.
/// </summary>
/// <param name="Kind">
/// A stable slug for what the hint is about — <c>drafts</c>, <c>unread-messages</c>,
/// <c>price-average</c> and so on. Tests assert on this rather than the sentence, so the Chinese can
/// be reworded without breaking them, and it is what a stylesheet would hang an icon off later.
/// </param>
/// <param name="Text">The sentence to show, already in Chinese and already counted.</param>
/// <param name="Href">Where to send the reader, as an in-page hash route, or null when there is nowhere to go.</param>
public sealed record AgentHint(string Kind, string Text, string? Href);

/// <summary>
/// The hints for one page, plus what is left of today's allowance.
/// </summary>
/// <param name="Hints">At least one, so the panel is never empty. At most a handful.</param>
/// <param name="RemainingToday">
/// How many shared-key model calls the caller has left today. Null when nobody is signed in — there
/// is no allowance to report, and reporting a number would suggest anonymous chat is available.
/// </param>
public sealed record AgentHintsResponse(IReadOnlyList<AgentHint> Hints, int? RemainingToday);

/// <summary>One earlier turn of the conversation, as the browser remembers it.</summary>
/// <param name="Role">Either <c>user</c> or <c>assistant</c>; anything else is dropped.</param>
/// <param name="Content">What was said. Trimmed and capped on the way in.</param>
public sealed record AgentTurn(string? Role, string? Content);

/// <summary>
/// A question typed into the assistant's panel.
/// </summary>
/// <param name="Message">The question itself.</param>
/// <param name="Page">
/// Which page the reader is on — <c>home</c>, <c>products</c>, <c>product</c>, <c>create</c>,
/// <c>mine</c>, <c>messages</c>, <c>notifications</c> or <c>feedback</c>. Anything else is treated as
/// a general page.
/// </param>
/// <param name="ProductId">The listing being looked at, when the page is a product page.</param>
/// <param name="History">
/// Earlier turns, oldest first. Held only by the browser: nothing here is stored server-side, so the
/// server re-derives the context from <paramref name="Page"/> and <paramref name="ProductId"/> every
/// time and uses this only to follow the thread of the conversation.
/// </param>
public sealed record AgentAskRequest(
    string? Message,
    string? Page,
    long? ProductId,
    IReadOnlyList<AgentTurn>? History);

/// <summary>One listing a search turned up: enough to link to it and show a price.</summary>
/// <param name="Id">Primary key; the client builds <c>#/products/{id}</c> from it.</param>
/// <param name="Title">The listing's title, as stored.</param>
/// <param name="Price">In yuan.</param>
public sealed record AgentProductResult(long Id, string Title, decimal Price);

/// <summary>The model's answer, plus any listings a search turned up.</summary>
/// <param name="Answer">Plain text. Never markdown, never more than a few sentences.</param>
/// <param name="RemainingToday">Shared-key calls left today, after this one.</param>
/// <param name="Results">
/// Empty when no search ran. The model never sees these ids — the list is built by the server from
/// the real query, and the client turns it into links, so a hallucinated listing cannot appear here.
/// </param>
public sealed record AgentAnswerResponse(
    string Answer,
    int? RemainingToday,
    IReadOnlyList<AgentProductResult> Results);
