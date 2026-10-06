using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.DTOs.Notifications;
using NCUT_Market.Core.DTOs.Products;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The trade request sequences the tests repeat.
/// </summary>
/// <remarks>
/// The four trade actions all return 204 with no body, so the helpers cannot hand back anything
/// useful the way <c>MessageTestSupport</c>'s do. They return the raw response instead and leave the
/// assertion to the caller — a trade action's status code is usually the thing under test.
/// </remarks>
internal static class TransactionTestSupport
{
    /// <summary>Offers to buy from inside a thread. The listing stays on sale.</summary>
    public static Task<HttpResponseMessage> ProposeTradeAsync(this HttpClient client, long conversationId) =>
        client.PostAsync($"/api/conversations/{conversationId}/transaction", null);

    /// <summary>Takes up the offer in a thread.</summary>
    public static Task<HttpResponseMessage> AcceptTradeAsync(this HttpClient client, long conversationId) =>
        client.PostAsync($"/api/conversations/{conversationId}/transaction/accept", null);

    /// <summary>The buyer confirms receipt.</summary>
    public static Task<HttpResponseMessage> ConfirmReceiptAsync(this HttpClient client, long conversationId) =>
        client.PostAsync($"/api/conversations/{conversationId}/transaction/receipt", null);

    /// <summary>The seller confirms payment.</summary>
    public static Task<HttpResponseMessage> ConfirmPaymentAsync(this HttpClient client, long conversationId) =>
        client.PostAsync($"/api/conversations/{conversationId}/transaction/payment", null);

    /// <summary>A listing as an anonymous visitor sees it.</summary>
    /// <remarks>
    /// Anonymous on purpose wherever it is used: "the listing stays visible to everybody else" is
    /// half of what the two-phase design is for, and reading it with a participant's token would not
    /// prove that.
    /// </remarks>
    public static async Task<ProductDetailResponse> GetProductAsync(this HttpClient client, long productId)
    {
        var response = await client.GetAsync($"/api/products/{productId}");

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ProductDetailResponse>()
            ?? throw new InvalidOperationException("Reading a listing returned no body.");
    }

    /// <summary>Whether a listing appears in the public feed.</summary>
    /// <param name="productId">The listing to look for.</param>
    /// <param name="title">Its title, used as the search keyword to keep the page small.</param>
    /// <remarks>
    /// Matched by id rather than by title. Rows are never cleaned up and titles repeat across runs, so
    /// a title match would pass on some previous run's listing even after this one had been filtered
    /// out of the feed — the exact thing the caller is trying to detect.
    /// </remarks>
    public static async Task<bool> IsInPublicFeedAsync(this HttpClient client, long productId, string title)
    {
        var response = await client.GetAsync(
            "/api/products?pageSize=100&q=" + Uri.EscapeDataString(title));

        response.EnsureSuccessStatusCode();

        var page = await response.Content
            .ReadFromJsonAsync<PagedResult<ProductSummaryResponse>>()
            ?? throw new InvalidOperationException("Listing products returned no body.");

        return page.Items.Any(x => x.Id == productId);
    }

    /// <summary>One page of the caller's notifications, newest first.</summary>
    public static async Task<IReadOnlyList<NotificationResponse>> ListNotificationsAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/notifications");

        response.EnsureSuccessStatusCode();

        var page = await response.Content
            .ReadFromJsonAsync<PagedResult<NotificationResponse>>()
            ?? throw new InvalidOperationException("Listing notifications returned no body.");

        return page.Items;
    }

    /// <summary>The number behind the notification badge.</summary>
    public static async Task<int> NotificationUnreadCountAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/notifications/unread-count");

        response.EnsureSuccessStatusCode();

        var count = await response.Content.ReadFromJsonAsync<UnreadCountResponse>()
            ?? throw new InvalidOperationException("The notification count returned no body.");

        return count.Count;
    }

    /// <summary>Asserts a response is the expected failure and returns its <c>code</c> member.</summary>
    public static async Task AssertFailureAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);

        var code = await response.ReadCodeAsync();

        Assert.NotNull(code);
    }

    /// <summary>Every notification type the caller has received, newest first.</summary>
    public static async Task<NotificationType[]> NotificationTypesAsync(this HttpClient client) =>
        [.. (await client.ListNotificationsAsync()).Select(x => x.Type)];
}
