using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The floating assistant: <c>GET /api/ai/agent/hints</c> and <c>POST /api/ai/agent/ask</c>.
/// </summary>
/// <remarks>
/// <para>
/// Most assertions here are on <see cref="AgentHint.Kind"/> rather than the Chinese sentence, so the
/// wording can be rewritten without turning these into a copy-editing test.
/// </para>
/// <para>
/// Every account is freshly registered, so the per-user daily counter behind the shared
/// <c>IMemoryCache</c> never carries over between tests.
/// </para>
/// </remarks>
public sealed class AgentEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Hints_are_available_without_a_token()
    {
        var client = fixture.CreateAnonymousClient();

        var hints = await HintsAsync(client, "home");

        Assert.NotEmpty(hints.Hints);

        // No allowance is reported to a visitor: there is nobody to charge and nothing to ask with.
        Assert.Null(hints.RemainingToday);

        // And nothing personal leaks into an anonymous answer.
        Assert.DoesNotContain(hints.Hints, hint => hint.Kind == "drafts");
    }

    [Fact]
    public async Task Hints_never_reveal_a_draft_to_a_stranger()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var draft = await seller.CreateDraftAsync(fixture, NewTitle("draft-"));

        var stranger = fixture.CreateAnonymousClient();

        var seenByStranger = await HintsAsync(stranger, "product", draft.Id);

        // The rule is the same one ProductService.GetByIdAsync follows for the detail page itself:
        // a stranger is told nothing about a listing that is not live. Answering differently here
        // would make the assistant a way to ask "does this id exist".
        Assert.DoesNotContain(seenByStranger.Hints, hint => hint.Kind == "no-questions");

        var seenBySeller = await HintsAsync(seller, "product", draft.Id);

        Assert.Contains(seenBySeller.Hints, hint => hint.Kind == "no-questions");
    }

    [Fact]
    public async Task Mine_hints_report_drafts()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        await client.CreateDraftAsync(fixture, NewTitle("draft-"));

        var hints = await HintsAsync(client, "mine");

        var draftHint = Assert.Single(hints.Hints, hint => hint.Kind == "drafts");

        Assert.Equal("#/mine", draftHint.Href);

        // Signed in, so an allowance figure is meaningful and must be present.
        Assert.NotNull(hints.RemainingToday);
    }

    [Fact]
    public async Task Messages_hints_report_unread_threads()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, NewTitle("thread-"));

        var conversation = await buyer.StartConversationAsync(listing.Id);
        await buyer.SendMessageAsync(conversation.Id, "还在吗？");

        var hints = await HintsAsync(seller, "messages");

        Assert.Contains(hints.Hints, hint => hint.Kind == "unread-messages");

        // The other side of the same thread has nothing unread — it was their own message.
        var buyerHints = await HintsAsync(buyer, "messages");

        Assert.DoesNotContain(buyerHints.Hints, hint => hint.Kind == "unread-messages");
    }

    [Fact]
    public async Task Product_hints_state_the_local_average()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();

        // Four live listings in one category: the reader is shown one of them, so three remain as
        // comparables — the minimum that makes an average worth stating.
        await seller.PublishListingAsync(fixture, NewTitle("price-"), price: 101m);
        await seller.PublishListingAsync(fixture, NewTitle("price-"), price: 202m);
        await seller.PublishListingAsync(fixture, NewTitle("price-"), price: 303m);
        var viewed = await seller.PublishListingAsync(fixture, NewTitle("price-"), price: 404m);

        var (browser, _) = await fixture.CreateSignedInClientAsync();
        var hints = await HintsAsync(browser, "product", viewed.Id);

        var average = Assert.Single(hints.Hints, hint => hint.Kind == "price-average");

        Assert.Contains("同类在售商品近期均价大约 ¥", average.Text);

        // Anybody's listing, but the price sample is public information — a signed-out visitor gets it too.
        var anonymous = await HintsAsync(fixture.CreateAnonymousClient(), "product", viewed.Id);

        Assert.Contains(anonymous.Hints, hint => hint.Kind == "price-average");
    }

    [Fact]
    public async Task Asking_without_a_token_is_a_401()
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/ai/agent/ask", new AgentAskRequest("还能便宜吗？", "home", null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.Unauthorized, await response.ReadCodeAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_question_is_rejected(string message)
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/ai/agent/ask", new AgentAskRequest(message, "home", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task An_overlong_question_is_rejected()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/ai/agent/ask", new AgentAskRequest(new string('问', 301), "home", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task The_page_facts_reach_the_prompt()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var title = NewTitle("grounded-");

        var listing = await seller.PublishListingAsync(fixture, title, price: 66m);

        var (browser, _) = await fixture.CreateSignedInClientAsync();

        var response = await AskAsync(
            browser, $"{FakeAiChatClient.AgentAnswerMarker} 这件能便宜吗", "product", listing.Id);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<AgentAnswerResponse>();
        Assert.Equal(FakeAiChatClient.AgentAnswer, answer!.Answer);

        var prompt = fixture.Ai.Prompts[^1];

        // The listing's own title and price, and which page the question was asked on.
        Assert.Contains(title, prompt, StringComparison.Ordinal);
        Assert.Contains("¥66", prompt, StringComparison.Ordinal);
        Assert.Contains("商品详情", prompt, StringComparison.Ordinal);

        // One call, and it is asked for JSON: the reply is either the answer or a search request.
        Assert.True(fixture.Ai.JsonModes[^1]);
    }

    [Fact]
    public async Task A_failed_answer_is_refunded()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var failed = await AskAsync(
            client, $"{FakeAiChatClient.EmptyAnswerMarker} 随便问问", "home", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, await failed.ReadCodeAsync());

        // The attempt went back, so the full allowance is still there.
        for (var index = 0; index < ApiFixture.TestDailyQuota; index++)
        {
            var ok = await AskAsync(
                client, $"{FakeAiChatClient.AgentAnswerMarker} 第 {index} 问", "home", null);

            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var refused = await AskAsync(
            client, $"{FakeAiChatClient.AgentAnswerMarker} 再问一句", "home", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, await refused.ReadCodeAsync());
    }

    [Fact]
    public async Task The_allowance_is_shared_with_the_listing_assistant()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        // Spend the whole day on the publish page, then try the chat.
        for (var index = 0; index < ApiFixture.TestDailyQuota; index++)
        {
            (await client.AskAssistantAsync(
                AiTestSupport.NewTitle(), fixture.ChildCategoryId)).EnsureSuccessStatusCode();
        }

        var response = await AskAsync(
            client, $"{FakeAiChatClient.AgentAnswerMarker} 还能问吗", "home", null);

        // One counter, not two. This is the test that holds the AiQuota extraction down.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Only_the_newest_history_turns_reach_the_prompt()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var history = new List<AgentTurn>();

        for (var index = 0; index < 20; index++)
        {
            history.Add(new AgentTurn(
                index % 2 == 0 ? "user" : "assistant", $"OLDEST-MARKER-{index}"));
        }

        history.Add(new AgentTurn("user", "NEWEST-MARKER"));

        var response = await AskAsync(
            client, $"{FakeAiChatClient.AgentAnswerMarker} 那我该怎么开口", "home", null, history);

        response.EnsureSuccessStatusCode();

        var prompt = fixture.Ai.Prompts[^1];

        // The newest turn survives; the first ones are cut, so an unbounded client cannot grow the
        // prompt at the server's expense.
        Assert.Contains("NEWEST-MARKER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("OLDEST-MARKER-0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_answer_is_cut_and_fences_are_dropped()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var longAnswer = await AskAsync(
            client, $"{FakeAiChatClient.LongAnswerMarker} 说详细点", "home", null);

        longAnswer.EnsureSuccessStatusCode();

        var cut = await longAnswer.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        Assert.NotNull(cut);
        Assert.True(cut.Answer.Length <= 301, $"Answer was {cut.Answer.Length} characters.");
        Assert.EndsWith("…", cut.Answer, StringComparison.Ordinal);

        var fenced = await AskAsync(
            client, $"{FakeAiChatClient.FenceAnswerMarker} 再说一次", "home", null);

        fenced.EnsureSuccessStatusCode();

        var clean = await fenced.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        Assert.Equal("被围栏包起来的回答", clean!.Answer);
    }

    [Fact]
    public async Task A_search_question_returns_matching_listings_and_links()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var title = NewTitle("自行车-");

        var listing = await seller.PublishListingAsync(fixture, title, price: 88m);

        var (browser, _) = await fixture.CreateSignedInClientAsync();

        var response = await AskAsync(
            browser, $"{FakeAiChatClient.SearchIntentMarker} 帮我找找自行车", "products", null);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        Assert.Equal(FakeAiChatClient.AgentAnswer, answer!.Answer);

        var hit = Assert.Single(answer.Results, item => item.Id == listing.Id);
        Assert.Equal(title, hit.Title);
        Assert.Equal(88m, hit.Price);

        // Two calls for this question: intent first, summary second.
        Assert.True(fixture.Ai.JsonModes[^2]);
        Assert.False(fixture.Ai.JsonModes[^1]);

        var prompt = fixture.Ai.Prompts[^1];

        Assert.Contains(title, prompt, StringComparison.Ordinal);
        Assert.Contains("¥88", prompt, StringComparison.Ordinal);

        // The model gets titles and prices, never ids or links. The client builds the link.
        Assert.DoesNotContain("#/products/", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_with_no_matches_answers_without_another_call()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await AskAsync(
            client, $"{FakeAiChatClient.SearchMissMarker} 帮我找找不存在的东西", "products", null);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        Assert.Empty(answer!.Results);
        Assert.Contains("没找到", answer.Answer, StringComparison.Ordinal);

        // Still on the intent call: the stand-in would have answered a second one with blanks, so a
        // pointless summary request would have turned this into a 503 instead of passing quietly.
        Assert.True(fixture.Ai.JsonModes[^1]);
    }

    [Fact]
    public async Task A_failed_search_summary_is_refunded()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();

        await seller.PublishListingAsync(fixture, NewTitle("自行车-"), price: 12m);

        var (client, _) = await fixture.CreateSignedInClientAsync();

        var failed = await AskAsync(
            client, $"{FakeAiChatClient.SearchFailMarker} 找找自行车", "products", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, await failed.ReadCodeAsync());

        // The intent call succeeded, the summary call did not; the whole question still went back.
        for (var index = 0; index < ApiFixture.TestDailyQuota; index++)
        {
            var ok = await AskAsync(
                client, $"{FakeAiChatClient.AgentAnswerMarker} 第 {index} 问", "home", null);

            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var refused = await AskAsync(
            client, $"{FakeAiChatClient.AgentAnswerMarker} 再问一句", "home", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, await refused.ReadCodeAsync());
    }

    [Fact]
    public async Task A_category_name_narrows_the_search()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();

        var inChild = await seller.PublishListingAsync(
            fixture, NewTitle("自行车-"), price: 20m, categoryId: fixture.ChildCategoryId);

        var elsewhere = await seller.PublishListingAsync(
            fixture, NewTitle("自行车-"), price: 30m, categoryId: fixture.OtherCategoryId);

        var (browser, _) = await fixture.CreateSignedInClientAsync();

        var response = await AskAsync(
            browser, $"{FakeAiChatClient.SearchCategoryMarker} 找找自行车", "products", null);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        Assert.Contains(answer!.Results, item => item.Id == inChild.Id);
        Assert.DoesNotContain(answer.Results, item => item.Id == elsewhere.Id);
    }

    [Fact]
    public async Task A_plain_question_returns_no_results()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await AskAsync(
            client, $"{FakeAiChatClient.AgentAnswerMarker} 这件能便宜吗", "home", null);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<AgentAnswerResponse>();

        // Empty, not null: the client renders an empty list the same way it renders a missing one,
        // but only one of them is a contract.
        Assert.Empty(answer!.Results);
        Assert.True(fixture.Ai.JsonModes[^1]);
    }

    private async Task<AgentHintsResponse> HintsAsync(HttpClient client, string page, long? productId = null)
    {
        var url = $"/api/ai/agent/hints?page={page}"
            + (productId is long id ? $"&productId={id}" : string.Empty);

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AgentHintsResponse>()
            ?? throw new InvalidOperationException("The agent returned no body.");
    }

    private static Task<HttpResponseMessage> AskAsync(
        HttpClient client,
        string message,
        string page,
        long? productId,
        IReadOnlyList<AgentTurn>? history = null) =>
        client.PostAsJsonAsync(
            "/api/ai/agent/ask", new AgentAskRequest(message, page, productId, history));

    private static string NewTitle(string prefix) =>
        prefix + Guid.NewGuid().ToString("N")[..10];
}
