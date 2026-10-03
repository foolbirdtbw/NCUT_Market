using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Announcements;

namespace NCUT_Market.ApiTests;

/// <summary>The announcement request sequences the tests repeat.</summary>
internal static class AnnouncementTestSupport
{
    /// <summary>
    /// One page of the live announcements. Asks for the maximum page size, because the fixture's
    /// database is shared across a test class and accumulates rows from every run.
    /// </summary>
    public static async Task<IReadOnlyList<AnnouncementResponse>> ListAnnouncementsAsync(
        this HttpClient client)
    {
        var response = await client.GetAsync(
            $"/api/announcements?pageSize={PaginationQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<PagedResult<AnnouncementResponse>>()
            ?? throw new InvalidOperationException("Listing announcements returned no body.");

        return page.Items;
    }
}
