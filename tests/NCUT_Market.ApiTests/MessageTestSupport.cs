using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The messaging request sequences the tests repeat. Each helper fails the test rather than returning
/// a failing response, because every caller is asserting on what came back, not on the status.
/// </summary>
internal static class MessageTestSupport
{
    /// <summary>Opens a thread about a listing, or returns the existing one.</summary>
    public static async Task<ConversationSummaryResponse> StartConversationAsync(
        this HttpClient client,
        long productId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/conversations", new StartConversationRequest(productId));

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ConversationSummaryResponse>()
            ?? throw new InvalidOperationException("Starting a conversation returned no body.");
    }

    /// <summary>One page of the caller's threads.</summary>
    public static async Task<IReadOnlyList<ConversationSummaryResponse>> ListConversationsAsync(
        this HttpClient client)
    {
        var response = await client.GetAsync("/api/conversations");

        response.EnsureSuccessStatusCode();

        var page = await response.Content
            .ReadFromJsonAsync<PagedResult<ConversationSummaryResponse>>()
            ?? throw new InvalidOperationException("Listing conversations returned no body.");

        return page.Items;
    }

    /// <summary>The number behind the header badge.</summary>
    public static async Task<int> UnreadCountAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/conversations/unread-count");

        response.EnsureSuccessStatusCode();

        var count = await response.Content.ReadFromJsonAsync<UnreadCountResponse>()
            ?? throw new InvalidOperationException("The unread count returned no body.");

        return count.Count;
    }

    /// <summary>A thread and its newest page of messages.</summary>
    public static async Task<ConversationDetailResponse> GetConversationAsync(
        this HttpClient client,
        long conversationId)
    {
        var response = await client.GetAsync($"/api/conversations/{conversationId}");

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ConversationDetailResponse>()
            ?? throw new InvalidOperationException("Reading a conversation returned no body.");
    }

    /// <summary>Posts a message into a thread.</summary>
    public static async Task<MessageResponse> SendMessageAsync(
        this HttpClient client,
        long conversationId,
        string content)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages", new SendMessageRequest(content));

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MessageResponse>()
            ?? throw new InvalidOperationException("Sending a message returned no body.");
    }

    /// <summary>Clears a thread out of the caller's own list.</summary>
    /// <remarks>
    /// The one helper here that hands back the raw response, because it has three legitimate
    /// outcomes — 204, 404 for a thread you are not in, and 409 for one with a live trade — and
    /// which one came back is normally the thing the caller is testing.
    /// </remarks>
    public static Task<HttpResponseMessage> DeleteConversationAsync(
        this HttpClient client,
        long conversationId) =>
        client.DeleteAsync($"/api/conversations/{conversationId}");
}
