using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Announcements;
using NCUT_Market.Core.Enums;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Site announcements: what the public sees, and who may write one.
/// </summary>
/// <remarks>
/// The admin check reads <c>users.role</c> from the database rather than from the token, so these
/// tests promote an account with <see cref="ApiFixture.PromoteToAdminAsync"/> and then use the
/// <em>same</em> client — no re-login. That is the property the design was chosen for, and it is
/// asserted here rather than left to a manual check.
/// </remarks>
public sealed class AnnouncementEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>Beijing wall-clock now, the same clock the columns are written from.</summary>
    private static DateTime Now => AppDbContext.AuditNow;

    [Fact]
    public async Task Readers_see_only_announcements_that_are_live()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var draft = await fixture.SeedAnnouncementAsync(
            $"公告-草稿-{suffix}", AnnouncementStatus.Draft, publishedAt: null, expiredAt: null);
        var future = await fixture.SeedAnnouncementAsync(
            $"公告-还没到-{suffix}", AnnouncementStatus.Published, Now.AddDays(1), expiredAt: null);
        var expired = await fixture.SeedAnnouncementAsync(
            $"公告-过期了-{suffix}", AnnouncementStatus.Published, Now.AddDays(-2), Now.AddDays(-1));
        var live = await fixture.SeedAnnouncementAsync(
            $"公告-生效中-{suffix}", AnnouncementStatus.Published, Now.AddHours(-1), expiredAt: null);

        var visible = await fixture.CreateAnonymousClient().ListAnnouncementsAsync();

        Assert.Contains(visible, x => x.Id == live);
        Assert.DoesNotContain(visible, x => x.Id == draft);
        Assert.DoesNotContain(visible, x => x.Id == future);
        Assert.DoesNotContain(visible, x => x.Id == expired);
    }

    [Fact]
    public async Task The_newest_live_announcement_comes_first()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var older = await fixture.SeedAnnouncementAsync(
            $"公告-旧-{suffix}", AnnouncementStatus.Published, Now.AddHours(-5), expiredAt: null);
        var newer = await fixture.SeedAnnouncementAsync(
            $"公告-新-{suffix}", AnnouncementStatus.Published, Now.AddHours(-1), expiredAt: null);

        var visible = await fixture.CreateAnonymousClient().ListAnnouncementsAsync();

        // The banner is the first row of this list, so the ordering is the feature.
        Assert.True(
            visible.ToList().FindIndex(x => x.Id == newer) < visible.ToList().FindIndex(x => x.Id == older),
            "The more recently published announcement should sort first.");
    }

    [Fact]
    public async Task Publishing_is_refused_to_anonymous_callers_and_ordinary_users()
    {
        var anonymous = fixture.CreateAnonymousClient();
        var (user, _) = await fixture.CreateSignedInClientAsync();

        var request = new CreateAnnouncementRequest("公告-越权", "正文", null);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/announcements", request)).StatusCode);

        var forbidden = await user.PostAsJsonAsync("/api/announcements", request);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await forbidden.ReadCodeAsync());
    }

    [Fact]
    public async Task An_admin_can_publish_without_signing_in_again_and_delete_it_afterwards()
    {
        var (admin, auth) = await fixture.CreateSignedInClientAsync();

        string title = $"公告-管理员-{Guid.NewGuid().ToString("N")[..8]}";

        // Promoting through the database must take effect on this client's very next request: the
        // role is not in the token it is already holding.
        await fixture.PromoteToAdminAsync(auth.User.Id);

        var published = await admin.PostAsJsonAsync(
            "/api/announcements", new CreateAnnouncementRequest(title, "正文", null));

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var created = await published.Content.ReadFromJsonAsync<AnnouncementResponse>();
        Assert.NotNull(created);
        Assert.Equal(title, created.Title);

        var visible = await fixture.CreateAnonymousClient().ListAnnouncementsAsync();
        Assert.Contains(visible, x => x.Id == created.Id);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/announcements/{created.Id}")).StatusCode);

        var afterDelete = await fixture.CreateAnonymousClient().ListAnnouncementsAsync();
        Assert.DoesNotContain(afterDelete, x => x.Id == created.Id);
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_rejected_in_chinese()
    {
        var (admin, auth) = await fixture.CreateSignedInClientAsync();
        await fixture.PromoteToAdminAsync(auth.User.Id);

        var response = await admin.PostAsJsonAsync(
            "/api/announcements",
            new CreateAnnouncementRequest("公告-过期时间", "正文", Now.AddDays(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("过期时间", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expiry_one_minute_out_is_accepted()
    {
        var (admin, auth) = await fixture.CreateSignedInClientAsync();
        await fixture.PromoteToAdminAsync(auth.User.Id);

        var response = await admin.PostAsJsonAsync(
            "/api/announcements",
            new CreateAnnouncementRequest(
                $"公告-快过期-{Guid.NewGuid().ToString("N")[..8]}", "正文", Now.AddMinutes(1)));

        // The boundary is "strictly after now", so a minute out is still live.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
