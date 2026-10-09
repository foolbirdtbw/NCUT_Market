using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Private messaging: who can start a thread, who can see it, and how unread is counted.
/// </summary>
/// <remarks>
/// Every test uses two separately registered accounts and sometimes a third. The membership check
/// lives in the service rather than in an authorization policy — <c>[Authorize]</c> can establish
/// that somebody is signed in, not that they are one of the two people in this thread — so these are
/// the tests that prove it is actually reached.
/// </remarks>
public sealed class MessageEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Contacting_a_seller_twice_returns_the_same_thread()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-幂等");

        var first = await buyer.StartConversationAsync(listing.Id);
        var second = await buyer.StartConversationAsync(listing.Id);

        Assert.True(first.Id > 0);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task The_seller_sees_the_thread_and_a_stranger_does_not()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-可见性");
        var thread = await buyer.StartConversationAsync(listing.Id);

        Assert.Contains(await seller.ListConversationsAsync(), x => x.Id == thread.Id);
        Assert.Contains(await buyer.ListConversationsAsync(), x => x.Id == thread.Id);
        Assert.DoesNotContain(await stranger.ListConversationsAsync(), x => x.Id == thread.Id);
    }

    [Fact]
    public async Task A_stranger_cannot_read_or_write_a_thread()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-外人");
        var thread = await buyer.StartConversationAsync(listing.Id);

        // 404, not 403: a thread is never public, so a 403 would confirm that this id exists.
        var read = await stranger.GetAsync($"/api/conversations/{thread.Id}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await read.ReadCodeAsync());

        var write = await stranger.PostAsJsonAsync(
            $"/api/conversations/{thread.Id}/messages",
            new SendMessageRequest("我不是这个会话的人"));
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);

        var markRead = await stranger.PostAsync($"/api/conversations/{thread.Id}/read", null);
        Assert.Equal(HttpStatusCode.NotFound, markRead.StatusCode);
    }

    [Fact]
    public async Task Contacting_your_own_listing_is_rejected()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var listing = await seller.PublishListingAsync(fixture, "私信-自己的");

        var response = await seller.PostAsJsonAsync(
            "/api/conversations", new StartConversationRequest(listing.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidArgument, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Contacting_a_listing_you_cannot_see_is_not_found()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        // A draft is invisible to everyone but its seller, so this is 404 rather than 403 — the same
        // rule ProductService.GetByIdAsync applies, and for the same reason.
        var draft = await seller.CreateDraftAsync(fixture, "私信-别人的草稿");

        var response = await buyer.PostAsJsonAsync(
            "/api/conversations", new StartConversationRequest(draft.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Unread_counts_the_threads_waiting_on_you_and_clears_when_you_read()
    {
        var (seller, sellerAuth) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-未读");
        var thread = await buyer.StartConversationAsync(listing.Id);

        // An empty thread is unread for nobody.
        Assert.Equal(0, await seller.UnreadCountAsync());

        await buyer.SendMessageAsync(thread.Id, "在吗？");

        // The buyer's own message is not unread for the buyer.
        Assert.Equal(0, await buyer.UnreadCountAsync());
        Assert.Equal(1, await seller.UnreadCountAsync());

        var markRead = await seller.PostAsync($"/api/conversations/{thread.Id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, markRead.StatusCode);

        Assert.Equal(0, await seller.UnreadCountAsync());

        // Reading it for the seller must not clear it for the buyer.
        await seller.SendMessageAsync(thread.Id, "在的");
        Assert.Equal(1, await buyer.UnreadCountAsync());
        Assert.Equal(0, await seller.UnreadCountAsync());

        Assert.NotEqual(sellerAuth.User.Id, buyerAuth.User.Id);
    }

    [Fact]
    public async Task Reading_a_thread_returns_its_messages_oldest_first()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-顺序");
        var thread = await buyer.StartConversationAsync(listing.Id);

        await buyer.SendMessageAsync(thread.Id, "第一条");
        await seller.SendMessageAsync(thread.Id, "第二条");
        await buyer.SendMessageAsync(thread.Id, "第三条");

        var detail = await buyer.GetConversationAsync(thread.Id);

        Assert.Equal(
            new[] { "第一条", "第二条", "第三条" },
            detail.Messages.Items.Select(x => x.Content).ToArray());
    }

    [Fact]
    public async Task A_blank_or_overlong_message_is_rejected()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-校验");
        var thread = await buyer.StartConversationAsync(listing.Id);

        var blank = await buyer.PostAsJsonAsync(
            $"/api/conversations/{thread.Id}/messages", new SendMessageRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var overlong = await buyer.PostAsJsonAsync(
            $"/api/conversations/{thread.Id}/messages", new SendMessageRequest(new string('长', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);

        // The message is Chinese and names the limit, not the framework's default English text.
        Assert.Contains("500", await overlong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_outlives_its_listing_and_keeps_the_frozen_title()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-删商品");
        var thread = await buyer.StartConversationAsync(listing.Id);

        await buyer.SendMessageAsync(thread.Id, "还在吗？");

        // A published listing has to come down before it can go.
        Assert.Equal(
            HttpStatusCode.OK,
            (await seller.PostAsync($"/api/products/{listing.Id}/offline", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await seller.DeleteAsync($"/api/products/{listing.Id}")).StatusCode);

        var detail = await buyer.GetConversationAsync(thread.Id);

        // The foreign key was SET NULL and the title was frozen at creation, so the buyer keeps both
        // the thread and something to read at the top of it.
        Assert.Null(detail.ProductId);
        Assert.Equal("私信-删商品", detail.ProductTitle);
        Assert.Equal("还在吗？", Assert.Single(detail.Messages.Items).Content);
    }

    [Fact]
    public async Task Messaging_without_a_token_is_unauthorized()
    {
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/conversations")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/conversations/unread-count")).StatusCode);
    }

    [Fact]
    public async Task Hiding_a_thread_takes_it_off_my_list_and_leaves_the_others()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-单方隐藏");
        var thread = await buyer.StartConversationAsync(listing.Id);

        await buyer.SendMessageAsync(thread.Id, "在吗？");
        Assert.Equal(1, await seller.UnreadCountAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteConversationAsync(thread.Id)).StatusCode);

        // Gone for the buyer only. The seller's half of the same row is untouched, and so is the
        // seller's badge — the buyer's own message is still sitting there unread.
        Assert.DoesNotContain(await buyer.ListConversationsAsync(), x => x.Id == thread.Id);
        Assert.Contains(await seller.ListConversationsAsync(), x => x.Id == thread.Id);
        Assert.Equal(1, await seller.UnreadCountAsync());

        // A hidden thread must not keep the hider's own badge lit either.
        Assert.Equal(0, await buyer.UnreadCountAsync());
    }

    [Fact]
    public async Task A_new_message_brings_a_hidden_thread_back()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-隐藏后复活");
        var thread = await buyer.StartConversationAsync(listing.Id);

        await buyer.SendMessageAsync(thread.Id, "在吗？");
        (await buyer.DeleteConversationAsync(thread.Id)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await buyer.ListConversationsAsync(), x => x.Id == thread.Id);

        await seller.SendMessageAsync(thread.Id, "在的");

        // Hiding is reversible by design: nobody can unilaterally mute the other side, and a wrong
        // click cannot lose a message for good.
        Assert.Contains(await buyer.ListConversationsAsync(), x => x.Id == thread.Id);
    }

    [Fact]
    public async Task A_thread_with_a_live_trade_cannot_be_hidden()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (loser, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-交易中不可删");
        var thread = await buyer.StartConversationAsync(listing.Id);
        var otherThread = await loser.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // 确认收货 / 确认收款 exist nowhere but the thread, so hiding it would strand whoever has not
        // confirmed yet. Both sides are blocked, not just the one who owes an action.
        var blocked = await buyer.DeleteConversationAsync(thread.Id);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await blocked.ReadCodeAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await seller.DeleteConversationAsync(thread.Id)).StatusCode);

        // The guard is "this thread is the one that won the listing", not "the listing is in a trade".
        // The other buyer's thread is about the same listing and has nothing to confirm in it.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await loser.DeleteConversationAsync(otherThread.Id)).StatusCode);

        // Once both sides confirm the listing is Sold and the confirm buttons are gone for good, so
        // hiding is allowed again.
        (await buyer.ConfirmReceiptAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.ConfirmPaymentAsync(thread.Id)).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await buyer.DeleteConversationAsync(thread.Id)).StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_hide_a_thread()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-外人删");
        var thread = await buyer.StartConversationAsync(listing.Id);

        var response = await stranger.DeleteConversationAsync(thread.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await response.ReadCodeAsync());

        // And nothing happened to either participant's list.
        Assert.Contains(await buyer.ListConversationsAsync(), x => x.Id == thread.Id);
        Assert.Contains(await seller.ListConversationsAsync(), x => x.Id == thread.Id);
    }

    [Fact]
    public async Task Hiding_a_thread_that_does_not_exist_is_a_404()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await seller.DeleteConversationAsync(long.MaxValue)).StatusCode);
    }

    [Fact]
    public async Task The_read_marker_moves_when_the_other_side_opens_the_thread()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-已读回执");
        var thread = await buyer.StartConversationAsync(listing.Id);

        var sent = await buyer.SendMessageAsync(thread.Id, "这条读了吗？");

        // The buyer sees the seller's marker, and the seller has not read the thread since the message
        // landed — so there is no receipt yet.
        var before = await buyer.GetConversationAsync(thread.Id);
        Assert.True(before.PeerLastReadAt < sent.CreatedAt);

        // Reading the thread is what moves it, and reading it is the POST — a plain GET leaves the
        // marker alone, which is why the client has to make the second call.
        await seller.GetConversationAsync(thread.Id);
        Assert.True((await buyer.GetConversationAsync(thread.Id)).PeerLastReadAt < sent.CreatedAt);

        await seller.PostAsync($"/api/conversations/{thread.Id}/read", null);

        var after = await buyer.GetConversationAsync(thread.Id);
        Assert.True(after.PeerLastReadAt >= sent.CreatedAt);
    }

    [Fact]
    public async Task An_unread_thread_reports_how_many_messages_are_waiting()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "私信-未读条数");
        var thread = await buyer.StartConversationAsync(listing.Id);

        await buyer.SendMessageAsync(thread.Id, "一");
        await seller.PostAsync($"/api/conversations/{thread.Id}/read", null);

        await buyer.SendMessageAsync(thread.Id, "二");
        await buyer.SendMessageAsync(thread.Id, "三");
        await buyer.SendMessageAsync(thread.Id, "四");

        var row = Assert.Single(await seller.ListConversationsAsync(), x => x.Id == thread.Id);

        // A count, not a flag — and the three newest are the ones since the seller's marker.
        Assert.Equal(3, row.UnreadCount);

        // The buyer's own three messages are not unread for the buyer.
        Assert.Equal(0, Assert.Single(await buyer.ListConversationsAsync(), x => x.Id == thread.Id).UnreadCount);
    }
}
