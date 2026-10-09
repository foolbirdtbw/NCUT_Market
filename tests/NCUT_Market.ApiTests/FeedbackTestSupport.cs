using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Feedbacks;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>The feedback request sequences the tests repeat.</summary>
internal static class FeedbackTestSupport
{
    /// <summary>
    /// One page of the whole board, as whoever holds this client sees it. Asks for the maximum page
    /// size, because the fixture's database is shared across a test class and accumulates rows from
    /// every run.
    /// </summary>
    public static async Task<IReadOnlyList<FeedbackResponse>> ListFeedbackAsync(this HttpClient client)
    {
        var response = await client.GetAsync($"/api/feedback?pageSize={PaginationQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<PagedResult<FeedbackResponse>>()
            ?? throw new InvalidOperationException("Listing feedback returned no body.");

        return page.Items;
    }

    /// <summary>
    /// Posts without asserting anything about the result — the caller usually wants to check whether
    /// it was refused.
    /// </summary>
    public static Task<HttpResponseMessage> PostFeedbackAsync(
        this HttpClient client,
        string content,
        FeedbackKind kind = FeedbackKind.Bug,
        bool anonymous = false) =>
        client.PostAsJsonAsync("/api/feedback", new CreateFeedbackRequest(content, kind, anonymous));

    /// <summary>Posts and reads the row back, for the tests that only care about what it became.</summary>
    public static async Task<FeedbackResponse> PublishFeedbackAsync(
        this HttpClient client,
        string content,
        FeedbackKind kind = FeedbackKind.Bug,
        bool anonymous = false)
    {
        var response = await client.PostFeedbackAsync(content, kind, anonymous);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FeedbackResponse>()
            ?? throw new InvalidOperationException("Creating feedback returned no body.");
    }

    /// <summary>
    /// A body nothing else in the database carries. Uniqueness has to come from the data — rows are
    /// never cleaned up, so a test can only ever assert on the rows it made itself.
    /// </summary>
    public static string NewBody(string label) =>
        $"反馈-{label}-{Guid.NewGuid().ToString("N")[..8]}";

    public static Task<HttpResponseMessage> VoteFeedbackAsync(this HttpClient client, long feedbackId) =>
        client.PostAsJsonAsync($"/api/feedback/{feedbackId}/vote", new { });

    public static Task<HttpResponseMessage> SetFeedbackStatusAsync(
        this HttpClient client,
        long feedbackId,
        FeedbackStatus status) =>
        client.PutAsJsonAsync($"/api/feedback/{feedbackId}/status", new UpdateFeedbackStatusRequest(status));

    public static Task<HttpResponseMessage> DeleteFeedbackAsync(this HttpClient client, long feedbackId) =>
        client.DeleteAsync($"/api/feedback/{feedbackId}");

    /// <summary>Reads a vote response. The count and the flag are asserted together, never apart.</summary>
    public static async Task<FeedbackVoteResponse> ReadVoteAsync(this HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<FeedbackVoteResponse>()
            ?? throw new InvalidOperationException("Voting returned no body.");
    }
}
