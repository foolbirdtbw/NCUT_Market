using System.Net;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The two-sided trade: propose, accept, both sides confirm, and the guards that keep a listing from
/// being sold twice or confirmed from the wrong thread.
/// </summary>
/// <remarks>
/// <para>
/// Every test drives real HTTP against two or three separately registered accounts. The state that
/// matters is read back two ways: the listing through an <em>anonymous</em> client, because
/// "everybody else can still see it" is the point of the two-phase design, and the raw trade columns
/// through the fixture, because the counterparty is deliberately absent from the public projection.
/// </para>
/// <para>
/// The sweep is off under this fixture. Expiry is exercised in <see cref="TransactionSweepTests"/>,
/// which calls the service directly with a clock it controls.
/// </para>
/// </remarks>
public sealed class TransactionEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_full_trade_walks_from_on_sale_to_sold()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-完整");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // Phase one: proposing changes nothing about the listing.
        var proposed = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);
        Assert.Equal(ProductStatus.Published, proposed.Status);

        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        var accepted = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);
        Assert.Equal(ProductStatus.InTransaction, accepted.Status);

        (await buyer.ConfirmReceiptAsync(thread.Id)).EnsureSuccessStatusCode();

        // One confirmation alone does not close it.
        var half = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);
        Assert.Equal(ProductStatus.InTransaction, half.Status);

        (await seller.ConfirmPaymentAsync(thread.Id)).EnsureSuccessStatusCode();

        var sold = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);
        Assert.Equal(ProductStatus.Sold, sold.Status);
        Assert.NotNull(sold.SoldAt);

        var state = await fixture.TradeStateAsync(listing.Id);

        Assert.Equal(ProductStatus.Sold, state.Status);
        Assert.Equal(buyerAuth.User.Id, state.BuyerId);
        Assert.NotNull(state.BuyerConfirmedAt);
        Assert.NotNull(state.SellerConfirmedAt);
    }

    [Fact]
    public async Task A_proposal_leaves_the_listing_on_sale_and_in_the_public_feed()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var visitor = fixture.CreateAnonymousClient();

        var listing = await seller.PublishListingAsync(fixture, "交易-两阶段");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        Assert.Equal(ProductStatus.Published, (await visitor.GetProductAsync(listing.Id)).Status);
        Assert.True(await visitor.IsInPublicFeedAsync(listing.Id, "交易-两阶段"));
    }

    [Fact]
    public async Task Two_buyers_can_each_hold_a_proposal_and_accepting_one_clears_the_other()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (first, firstAuth) = await fixture.CreateSignedInClientAsync();
        var (second, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-两个买家");
        var firstThread = await first.StartConversationAsync(listing.Id);
        var secondThread = await second.StartConversationAsync(listing.Id);

        // 乙：proposals are stored per thread, so these do not collide.
        (await first.ProposeTradeAsync(firstThread.Id)).EnsureSuccessStatusCode();
        (await second.ProposeTradeAsync(secondThread.Id)).EnsureSuccessStatusCode();

        (await seller.AcceptTradeAsync(firstThread.Id)).EnsureSuccessStatusCode();

        var state = await fixture.TradeStateAsync(listing.Id);
        Assert.Equal(firstAuth.User.Id, state.BuyerId);

        // The accepted thread's own proposal marker is gone — it became the trade.
        Assert.Equal((null, null), await fixture.ProposalStateAsync(firstThread.Id));

        // The loser's proposal is dropped too, so the button that would only be refused is gone.
        Assert.Equal((null, null), await fixture.ProposalStateAsync(secondThread.Id));
    }

    [Fact]
    public async Task The_proposer_cannot_accept_their_own_proposal()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-自己接受");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        var response = await buyer.AcceptTradeAsync(thread.Id);

        await response.AssertFailureAsync(HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task The_buyer_confirms_receipt_and_the_seller_confirms_payment_and_not_the_other_way_round()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-确认方向");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // Swapped: each side asking for the other's confirmation is a 403, not a 409.
        var buyerPaying = await buyer.ConfirmPaymentAsync(thread.Id);
        Assert.Equal(HttpStatusCode.Forbidden, buyerPaying.StatusCode);

        var sellerReceiving = await seller.ConfirmReceiptAsync(thread.Id);
        Assert.Equal(HttpStatusCode.Forbidden, sellerReceiving.StatusCode);

        // Neither rejection moved the listing.
        Assert.Equal(ProductStatus.InTransaction, (await fixture.TradeStateAsync(listing.Id)).Status);
    }

    [Fact]
    public async Task The_seller_cannot_confirm_from_a_thread_the_trade_did_not_come_from()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (winner, _) = await fixture.CreateSignedInClientAsync();
        var (other, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-错会话");
        var winningThread = await winner.StartConversationAsync(listing.Id);
        var otherThread = await other.StartConversationAsync(listing.Id);

        (await winner.ProposeTradeAsync(winningThread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(winningThread.Id)).EnsureSuccessStatusCode();

        // The seller is a member of every thread about their own listing, so without the
        // conversation-identity check this would go through and confirm the wrong person's trade.
        var response = await seller.ConfirmPaymentAsync(otherThread.Id);

        await response.AssertFailureAsync(HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());

        Assert.Null((await fixture.TradeStateAsync(listing.Id)).SellerConfirmedAt);
    }

    [Fact]
    public async Task Confirming_before_the_trade_starts_is_refused()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-还没开始");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // Proposed but not accepted: there is no trade yet for either side to confirm.
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.ConfirmReceiptAsync(thread.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.ConfirmPaymentAsync(thread.Id)).StatusCode);
    }

    [Fact]
    public async Task A_listing_that_is_not_on_sale_cannot_be_proposed_on()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-非在售");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await seller.PostAsync($"/api/products/{listing.Id}/offline", null)).EnsureSuccessStatusCode();

        var response = await buyer.ProposeTradeAsync(thread.Id);

        await response.AssertFailureAsync(HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task One_proposal_per_thread_and_one_trade_per_listing()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (second, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-重复");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // A second proposal in the same thread is refused rather than replacing the first.
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.ProposeTradeAsync(thread.Id)).StatusCode);

        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // Somebody else's proposal is now refused outright: the listing is no longer on sale.
        var otherThread = await second.StartConversationAsync(listing.Id);
        var late = await second.ProposeTradeAsync(otherThread.Id);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task A_listing_in_a_trade_stays_readable_and_still_takes_new_threads()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-第三方可见");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        var visitor = fixture.CreateAnonymousClient();

        Assert.Equal(ProductStatus.InTransaction, (await visitor.GetProductAsync(listing.Id)).Status);
        Assert.True(await visitor.IsInPublicFeedAsync(listing.Id, "交易-第三方可见"));

        // And a newcomer can still open a thread about it. That is the "商品突然消失" complaint.
        var newcomerThread = await stranger.StartConversationAsync(listing.Id);
        Assert.True(newcomerThread.Id > 0);

        // But the newcomer is not shown the trade's confirmation columns — the winner stays private.
        var detail = await stranger.GetConversationAsync(newcomerThread.Id);

        Assert.NotNull(detail.Trade);
        Assert.Equal(ProductStatus.InTransaction, detail.Trade!.ProductStatus);
        Assert.Null(detail.Trade.AcceptedAt);
        Assert.Null(detail.Trade.BuyerConfirmedAt);
        Assert.Null(detail.Trade.SellerConfirmedAt);
    }

    [Fact]
    public async Task Marking_sold_or_taking_down_a_listing_with_a_pending_proposal_is_refused()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-快捷通道挡板");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        var sold = await seller.PostAsync($"/api/products/{listing.Id}/sold", null);
        Assert.Equal(HttpStatusCode.Conflict, sold.StatusCode);
        Assert.Equal(ErrorCodes.InvalidState, await sold.ReadCodeAsync());

        var offline = await seller.PostAsync($"/api/products/{listing.Id}/offline", null);
        Assert.Equal(HttpStatusCode.Conflict, offline.StatusCode);

        // Still on sale, untouched by either refusal.
        Assert.Equal(ProductStatus.Published, (await fixture.TradeStateAsync(listing.Id)).Status);
    }

    [Fact]
    public async Task The_offline_shortcut_still_works_with_no_proposal_out()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var listing = await seller.PublishListingAsync(fixture, "交易-快捷通道照常");

        (await seller.PostAsync($"/api/products/{listing.Id}/sold", null)).EnsureSuccessStatusCode();

        Assert.Equal(ProductStatus.Sold, (await fixture.TradeStateAsync(listing.Id)).Status);
    }

    [Fact]
    public async Task The_detail_page_counts_every_thread_and_the_recent_ones()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-询问人数");

        // Three threads, one of them aged past the seven-day window.
        var (fresh, _) = await fixture.CreateSignedInClientAsync();
        var (old, _) = await fixture.CreateSignedInClientAsync();
        var (older, _) = await fixture.CreateSignedInClientAsync();

        var freshThread = await fresh.StartConversationAsync(listing.Id);
        var oldThread = await old.StartConversationAsync(listing.Id);
        var olderThread = await older.StartConversationAsync(listing.Id);

        Assert.True(freshThread.Id > 0);

        await fixture.AgeConversationAsync(oldThread.Id, TimeSpan.FromDays(8));
        await fixture.AgeConversationAsync(olderThread.Id, TimeSpan.FromDays(30));

        var detail = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);

        Assert.Equal(3, detail.InterestedTotal);
        Assert.Equal(1, detail.InterestedRecentCount);

        // Sending a message moves the aged thread back into the window.
        await old.SendMessageAsync(oldThread.Id, "还在吗");

        var after = await fixture.CreateAnonymousClient().GetProductAsync(listing.Id);

        Assert.Equal(3, after.InterestedTotal);
        Assert.Equal(2, after.InterestedRecentCount);
    }

    [Fact]
    public async Task The_other_party_can_decline_a_proposal_and_the_listing_is_untouched()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-拒绝");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        (await seller.CancelTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        Assert.Equal((null, null), await fixture.ProposalStateAsync(thread.Id));

        // A proposal never moved the listing, so undoing one must not move it either.
        Assert.Equal(ProductStatus.Published, (await fixture.TradeStateAsync(listing.Id)).Status);
        Assert.True(await fixture.CreateAnonymousClient().IsInPublicFeedAsync(listing.Id, "交易-拒绝"));

        // The reason this action exists: the proposer is told it was turned down, instead of waiting
        // out the day and being told it expired.
        Assert.Contains(
            await buyer.ListNotificationsAsync(),
            x => x.Type == NotificationType.TransactionCancelled);
    }

    [Fact]
    public async Task The_proposer_can_withdraw_their_own_proposal()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-撤回");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // The same endpoint the seller would have used to decline it — the proposer is not stuck
        // waiting out the day either.
        (await buyer.CancelTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        Assert.Equal((null, null), await fixture.ProposalStateAsync(thread.Id));
        Assert.Equal(ProductStatus.Published, (await fixture.TradeStateAsync(listing.Id)).Status);

        // This time the notice goes the other way: the seller is the one who waited.
        Assert.Contains(
            await seller.ListNotificationsAsync(),
            x => x.Type == NotificationType.TransactionCancelled);
    }

    [Fact]
    public async Task Declining_only_drops_this_threads_proposal()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (firstBuyer, _) = await fixture.CreateSignedInClientAsync();
        var (secondBuyer, secondAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-只清一条");
        var firstThread = await firstBuyer.StartConversationAsync(listing.Id);
        var secondThread = await secondBuyer.StartConversationAsync(listing.Id);

        (await firstBuyer.ProposeTradeAsync(firstThread.Id)).EnsureSuccessStatusCode();
        (await secondBuyer.ProposeTradeAsync(secondThread.Id)).EnsureSuccessStatusCode();

        (await seller.CancelTradeAsync(firstThread.Id)).EnsureSuccessStatusCode();

        Assert.Equal((null, null), await fixture.ProposalStateAsync(firstThread.Id));

        // Accepting clears every other offer on the listing, because a listing sells once and the
        // rest lose their meaning with it. A decline is between two people — the other buyer never
        // hears of it, so copying that sweep here would withdraw their offer behind their back.
        var kept = await fixture.ProposalStateAsync(secondThread.Id);

        Assert.Equal(secondAuth.User.Id, kept.ProposedById);
        Assert.NotNull(kept.ProposedAt);
    }

    [Fact]
    public async Task A_declined_proposal_can_be_made_again_straight_away()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-拒绝后再来");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.CancelTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // No cooldown and nothing remembered: a decline clears the offer, and the listing was on sale
        // throughout, so the precondition for proposing is satisfied again the moment it goes.
        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        var again = await fixture.ProposalStateAsync(thread.Id);

        Assert.Equal(buyerAuth.User.Id, again.ProposedById);
        Assert.NotNull(again.ProposedAt);
    }

    [Fact]
    public async Task Cancelling_a_thread_with_no_proposal_is_refused()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-没提议可拒");
        var thread = await buyer.StartConversationAsync(listing.Id);

        var response = await seller.CancelTradeAsync(thread.Id);

        await response.AssertFailureAsync(HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.InvalidState, await response.ReadCodeAsync());
        Assert.Equal((null, null), await fixture.ProposalStateAsync(thread.Id));
    }

    [Fact]
    public async Task A_stranger_cannot_touch_a_proposal()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();
        var (stranger, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "交易-陌生人拒绝");
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        // Membership is part of the query, so a thread that is not yours reads as one that is not
        // there — the same answer conversation reads give, and one that confirms nothing about ids.
        var response = await stranger.CancelTradeAsync(thread.Id);

        await response.AssertFailureAsync(HttpStatusCode.NotFound);

        var kept = await fixture.ProposalStateAsync(thread.Id);

        Assert.Equal(buyerAuth.User.Id, kept.ProposedById);
        Assert.NotNull(kept.ProposedAt);
    }
}
