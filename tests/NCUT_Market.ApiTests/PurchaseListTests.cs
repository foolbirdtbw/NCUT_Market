using System.Net;

namespace NCUT_Market.ApiTests;

/// <summary>
/// "我买到的" — the buyer's side of a completed trade.
/// </summary>
/// <remarks>
/// Every test here registers its own accounts, so "the buyer's list is empty" and "the buyer's list
/// holds exactly one" are both safe to assert even though the database is shared and never cleaned:
/// a brand-new account has no purchase history by construction.
/// </remarks>
public sealed class PurchaseListTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task A_completed_trade_lands_in_the_buyers_list_and_not_in_the_sellers()
    {
        var (seller, sellerAuth) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        // Every account registers as 测试用户, so asserting on the nickname below would hold whatever
        // the projection read. A unique one is what makes "the card names the seller" an assertion.
        var sellerName = "买到-卖家-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.RenameAsync(sellerAuth.User.Id, sellerName);

        var listing = await seller.PublishListingAsync(fixture, "买到-走完的交易");

        Assert.Empty(await buyer.ListBoughtAsync());
        Assert.Empty(await seller.ListBoughtAsync());

        var thread = await buyer.StartConversationAsync(listing.Id);
        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await buyer.ConfirmReceiptAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.ConfirmPaymentAsync(thread.Id)).EnsureSuccessStatusCode();

        var bought = await buyer.ListBoughtAsync();

        Assert.Equal([listing.Id], bought.Select(x => x.Id));
        Assert.Equal(sellerName, bought[0].SellerNickname);

        // The same listing, read the other way round. A list that matched on the listing rather than
        // on who bought it would show it to both of them, and only this half would catch that.
        Assert.Empty(await seller.ListBoughtAsync());
    }

    [Fact]
    public async Task A_listing_the_seller_marked_sold_by_hand_never_appears()
    {
        // Hand-marking sold records no buyer at all — there is no counterparty to write down — so a
        // purchase made that way is unrepresentable and this list cannot show it. The point of the
        // test is that the list does not fall back on Status, which would sweep in every hand-marked
        // listing in the database and attribute it to whoever happens to be asking.
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var traded = await seller.PublishListingAsync(fixture, "买到-平台成交的");
        var byHand = await seller.PublishListingAsync(fixture, "买到-手标售出的");

        var thread = await buyer.StartConversationAsync(traded.Id);
        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await buyer.ConfirmReceiptAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.ConfirmPaymentAsync(thread.Id)).EnsureSuccessStatusCode();

        (await seller.PostAsync($"/api/products/{byHand.Id}/sold", content: null))
            .EnsureSuccessStatusCode();

        var bought = await buyer.ListBoughtAsync();

        Assert.Equal([traded.Id], bought.Select(x => x.Id));
    }

    [Fact]
    public async Task A_listing_the_seller_has_since_removed_stays_on_the_buyers_list()
    {
        // The seller tidying their own page must not erase what somebody else paid for. This is the
        // whole reason DeleteAsync marks a platform-sold listing instead of removing it.
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, "买到-卖家已移除");

        var thread = await buyer.StartConversationAsync(listing.Id);
        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(thread.Id)).EnsureSuccessStatusCode();
        (await buyer.ConfirmReceiptAsync(thread.Id)).EnsureSuccessStatusCode();
        (await seller.ConfirmPaymentAsync(thread.Id)).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await seller.DeleteAsync($"/api/products/{listing.Id}")).StatusCode);

        Assert.Equal([listing.Id], (await buyer.ListBoughtAsync()).Select(x => x.Id));

        // And the other half of the arrangement: the receipt is there, but the page it points at is
        // not — for the buyer as much as for anyone. This is why the client must not turn these rows
        // into links.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await buyer.GetAsync($"/api/products/{listing.Id}")).StatusCode);
    }
}
