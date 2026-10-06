using System.Net;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The notification page behind the header badge.
/// </summary>
/// <remarks>
/// <para>
/// Notifications have no create endpoint at all — they are written by whatever changed the state that
/// warranted one, in the same save. So every test here reaches one through a trade action rather than
/// by asking for it, which is also what makes these the tests that prove the two are committed
/// together.
/// </para>
/// <para>
/// Ownership is enforced in the service, not by a policy: <c>[Authorize]</c> can establish that
/// somebody is signed in, not that a particular row is theirs. A row belonging to another account
/// reports 404 rather than 403, for the same reason a thread does — its existence is not public.
/// </para>
/// </remarks>
public sealed class NotificationEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>Drives a trade far enough to produce a notification for both sides.</summary>
    private async Task<(HttpClient Seller, HttpClient Buyer, long ProductId, long ConversationId)>
        StartTradeAsync(string title)
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, title);
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        return (seller, buyer, listing.Id, thread.Id);
    }

    [Fact]
    public async Task Both_sides_are_told_when_a_trade_starts()
    {
        var (seller, buyer, productId, _) = await StartTradeAsync("通知-交易开始");

        var sellerNotices = await seller.ListNotificationsAsync();
        var buyerNotices = await buyer.ListNotificationsAsync();

        var sellerStart = Assert.Single(sellerNotices, x => x.Type == NotificationType.TransactionAccepted);
        var buyerStart = Assert.Single(buyerNotices, x => x.Type == NotificationType.TransactionAccepted);

        Assert.Equal(productId, sellerStart.RelatedProductId);
        Assert.Equal(productId, buyerStart.RelatedProductId);

        // The body is rendered at write time with the listing's name baked in, which is what keeps it
        // readable after the listing is gone and RelatedProductId is nulled out.
        Assert.Contains("通知-交易开始", sellerStart.Content);
    }

    [Fact]
    public async Task A_new_notification_is_unread_and_marking_it_read_drops_the_count()
    {
        var (seller, _, _, _) = await StartTradeAsync("通知-未读计数");

        var before = await seller.NotificationUnreadCountAsync();
        Assert.True(before > 0);

        var unread = (await seller.ListNotificationsAsync()).First(x => !x.IsRead);

        var mark = await seller.PostAsync($"/api/notifications/{unread.Id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        Assert.Equal(before - 1, await seller.NotificationUnreadCountAsync());

        var after = (await seller.ListNotificationsAsync()).First(x => x.Id == unread.Id);
        Assert.True(after.IsRead);
        Assert.NotNull(after.ReadAt);
    }

    [Fact]
    public async Task Marking_the_same_notification_read_twice_is_not_an_error()
    {
        var (seller, _, _, _) = await StartTradeAsync("通知-重复已读");

        var unread = (await seller.ListNotificationsAsync()).First(x => !x.IsRead);

        (await seller.PostAsync($"/api/notifications/{unread.Id}/read", null)).EnsureSuccessStatusCode();

        var count = await seller.NotificationUnreadCountAsync();

        var again = await seller.PostAsync($"/api/notifications/{unread.Id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        // Second click must not re-stamp ReadAt or move the count.
        Assert.Equal(count, await seller.NotificationUnreadCountAsync());
    }

    [Fact]
    public async Task A_stranger_cannot_read_or_mark_somebody_elses_notification()
    {
        var (seller, _, _, _) = await StartTradeAsync("通知-外人");

        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var target = (await seller.ListNotificationsAsync()).First();

        // The stranger's own list is untouched by the other account's activity.
        Assert.DoesNotContain(await stranger.ListNotificationsAsync(), x => x.Id == target.Id);

        var mark = await stranger.PostAsync($"/api/notifications/{target.Id}/read", null);
        Assert.Equal(HttpStatusCode.NotFound, mark.StatusCode);

        // And it is still unread for its owner.
        Assert.False((await seller.ListNotificationsAsync()).First(x => x.Id == target.Id).IsRead);
    }

    [Fact]
    public async Task The_list_is_newest_first()
    {
        var (seller, buyer, productId, threadId) = await StartTradeAsync("通知-排序");

        // One more notification for each side, later than the acceptance.
        (await buyer.ConfirmReceiptAsync(threadId)).EnsureSuccessStatusCode();

        var sellerNotices = await seller.ListNotificationsAsync();

        Assert.True(sellerNotices.Count >= 2);
        Assert.Equal(productId, sellerNotices[0].RelatedProductId);

        var timestamps = sellerNotices.Select(x => x.CreatedAt).ToList();

        Assert.Equal(timestamps.OrderByDescending(x => x), timestamps);
    }

    [Fact]
    public async Task Counting_notifications_needs_a_token()
    {
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/notifications")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/api/notifications/1/read", null)).StatusCode);
    }
}
