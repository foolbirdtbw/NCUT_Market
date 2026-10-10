using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The listing assistant: what it refuses, what it costs, and what it does with a caller's own key.
/// </summary>
/// <remarks>
/// <para>
/// No test here reaches a model provider — the fixture replaces <c>IAiChatClient</c> with a stand-in,
/// so these exercise the parts that are ours. See <see cref="FakeAiChatClient"/>.
/// </para>
/// <para>
/// Every assertion is on a request this test made itself. The shared test database accumulates, and
/// the assistant's daily allowance lives in a cache shared by this class, so each test registers its
/// own account rather than assuming a clean slate.
/// </para>
/// </remarks>
public sealed class AiEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task The_assistant_needs_a_token()
    {
        var response = await fixture.CreateAnonymousClient()
            .AskAssistantAsync(AiTestSupport.NewTitle());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Posting_no_photos_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(AiTestSupport.NewTitle(), photos: 0);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task A_photo_over_the_size_ceiling_is_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        // Three megabytes against a two-megabyte ceiling. Under the request limit, so this reaches
        // the service rather than being turned away by the framework with a message about body size.
        var tooBig = new byte[3 * 1024 * 1024];

        var response = await client.AskAssistantAsync(AiTestSupport.NewTitle(), photo: tooBig);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task A_shared_key_gets_three_tries_a_day()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var title = AiTestSupport.NewTitle();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var allowed = await client.AskAssistantAsync(title);

            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var refused = await client.AskAssistantAsync(title);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, await refused.ReadCodeAsync());
    }

    [Fact]
    public async Task A_caller_who_brings_their_own_key_is_not_counted()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var title = AiTestSupport.NewTitle();

        // Past the shared allowance of three, which they never touch.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await client.AskAssistantAsync(title, apiKey: AiTestSupport.SentinelKey);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var suggestion = await response.Content.ReadFromJsonAsync<ListingSuggestionResponse>();

            Assert.NotNull(suggestion);

            // Null rather than a number: there is no allowance to have a remainder of, and reporting
            // one would be reporting the shared key's meter on somebody else's request.
            Assert.Null(suggestion.RemainingToday);
        }
    }

    [Fact]
    public async Task A_caller_s_own_key_never_comes_back()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(
            AiTestSupport.NewTitle(), apiKey: AiTestSupport.SentinelKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Asserted against the raw body rather than against the fields we expect: a key that leaked
        // into some field nobody thought to check would still be a leak, and this fails on it.
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(AiTestSupport.SentinelKey, raw, StringComparison.Ordinal);

        // It did get used, though — outbound, where it belongs. Without this the test above would
        // pass just as happily if the header were being dropped on the floor.
        Assert.Contains(AiTestSupport.SentinelKey, fixture.Ai.Keys);
    }

    [Fact]
    public async Task A_broken_answer_degrades_instead_of_failing()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(
            AiTestSupport.NewTitle(FakeAiChatClient.BrokenAnswerMarker));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, await response.ReadCodeAsync());

        // The provider's answer is ours to read, not ours to relay. Echoing it would put text we do
        // not control — and in the real thing, possibly the key — in front of the seller.
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(FakeAiChatClient.LeakMarker, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_price_advice_is_grounded_in_real_listings()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        // Prices no other listing in the database is likely to carry, so finding them in the prompt
        // means these two listings put them there.
        var lamp = await client.PublishListingAsync(
            fixture, AiTestSupport.NewTitle("lamp-"), price: 4321m, categoryId: fixture.ChildCategoryId);
        var chair = await client.PublishListingAsync(
            fixture, AiTestSupport.NewTitle("chair-"), price: 8765m, categoryId: fixture.ChildCategoryId);

        var response = await client.AskAssistantAsync(
            AiTestSupport.NewTitle(), categoryId: fixture.ChildCategoryId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(fixture.Ai.Prompts, prompt =>
            prompt.Contains(lamp.Title, StringComparison.Ordinal)
            && prompt.Contains(chair.Title, StringComparison.Ordinal)
            && prompt.Contains("4321", StringComparison.Ordinal)
            && prompt.Contains("8765", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_flagged_photo_still_returns_a_usable_answer()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(
            AiTestSupport.NewTitle(FakeAiChatClient.FlaggedMarker));

        // A 200, not a 4xx. The verdict is advice — the seller is shown a warning and left free to
        // publish — so the client's ordinary failure handling is never what decides the question.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var suggestion = await response.Content.ReadFromJsonAsync<ListingSuggestionResponse>();

        Assert.NotNull(suggestion);
        Assert.True(suggestion.Flagged);
        Assert.False(string.IsNullOrWhiteSpace(suggestion.FlagReason));

        // Flagged photos still get a description: refusing to help with the listing is not what the
        // warning means.
        Assert.False(string.IsNullOrWhiteSpace(suggestion.Description));
    }

    [Fact]
    public async Task Without_a_category_there_is_no_price_to_give()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(AiTestSupport.NewTitle());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var suggestion = await response.Content.ReadFromJsonAsync<ListingSuggestionResponse>();

        Assert.NotNull(suggestion);

        // Not a guess dressed up as advice: with no category there are no local sales to anchor
        // against, and the client says so rather than showing a number.
        Assert.Null(suggestion.PriceLow);
        Assert.Null(suggestion.PriceHigh);

        Assert.False(string.IsNullOrWhiteSpace(suggestion.Description));
    }

    [Fact]
    public async Task With_a_category_the_price_comes_back()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.AskAssistantAsync(
            AiTestSupport.NewTitle(), categoryId: fixture.ChildCategoryId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var suggestion = await response.Content.ReadFromJsonAsync<ListingSuggestionResponse>();

        Assert.NotNull(suggestion);
        Assert.Equal(120m, suggestion.PriceLow);
        Assert.Equal(180m, suggestion.PriceHigh);
    }
}
