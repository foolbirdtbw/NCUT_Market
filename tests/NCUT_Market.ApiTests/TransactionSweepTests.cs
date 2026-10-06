using Microsoft.Extensions.DependencyInjection;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The timeout sweep, driven directly rather than by its timer.
/// </summary>
/// <remarks>
/// <para>
/// <c>SweepAsync</c> takes the current instant as a parameter precisely so this is possible: the
/// deadlines are measured in days and the job ticks every fifteen minutes, so waiting for the real
/// thing would mean a test that cannot run. The aged states come from fixture helpers that write the
/// columns straight to the database, because no request can produce a day-old row.
/// </para>
/// <para>
/// The clock passed in is <c>AppDbContext.AuditNow</c> shifted forward, rather than
/// <c>DateTime.UtcNow</c>. Every timestamp in these tables is Beijing wall-clock, so the sweep has to
/// be handed a value on the same scale or every cutoff lands eight hours away from where it belongs.
/// </para>
/// <para>
/// Notably, <em>no test here asserts on the sweep's return value</em>. The sweep is global — it scans
/// the whole products table, and the test database is shared by every test class in the run and never
/// cleaned — so a count of what one call changed says nothing about the rows this test planted. Each
/// claim is made about its own listing instead, read back from the database.
/// </para>
/// </remarks>
public sealed class TransactionSweepTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>The instant the sweep is told it is: well past every deadline these tests plant.</summary>
    private static DateTime Later => AppDbContext.AuditNow.AddDays(2);

    /// <summary>A listing, the thread it is being traded through, and the two clients.</summary>
    private sealed record Scenario(
        long ProductId,
        long ConversationId,
        string Title,
        long SellerId,
        long BuyerId,
        HttpClient Seller,
        HttpClient Buyer);

    private async Task<T> WithTransactionServiceAsync<T>(Func<ITransactionService, Task<T>> action)
    {
        await using var scope = fixture.Services.CreateAsyncScope();

        return await action(scope.ServiceProvider.GetRequiredService<ITransactionService>());
    }

    private Task<int> SweepAsync(DateTime now) =>
        WithTransactionServiceAsync(service => service.SweepAsync(now));

    /// <summary>Runs a deal up to "proposal waiting for an answer".</summary>
    private async Task<Scenario> ProposeAsync(string title)
    {
        var (seller, sellerAuth) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(fixture, title);
        var thread = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(thread.Id)).EnsureSuccessStatusCode();

        return new Scenario(
            listing.Id, thread.Id, title,
            sellerAuth.User.Id, buyerAuth.User.Id, seller, buyer);
    }

    /// <summary>Runs a deal all the way into a live trade.</summary>
    private async Task<Scenario> StartTradeAsync(string title)
    {
        var scenario = await ProposeAsync(title);

        (await scenario.Seller.AcceptTradeAsync(scenario.ConversationId)).EnsureSuccessStatusCode();

        return scenario;
    }

    [Fact]
    public async Task A_proposal_nobody_answered_is_dropped_and_the_proposer_told()
    {
        var scenario = await ProposeAsync("扫描-提议过期");

        await fixture.AgeProposalAsync(scenario.ConversationId, TimeSpan.FromDays(2));

        await SweepAsync(Later);

        Assert.Equal((null, null), await fixture.ProposalStateAsync(scenario.ConversationId));

        Assert.Contains(
            await scenario.Buyer.ListNotificationsAsync(),
            x => x.Type == NotificationType.TransactionCancelled);

        // The listing itself was never touched by any of it.
        Assert.Equal(ProductStatus.Published, (await fixture.TradeStateAsync(scenario.ProductId)).Status);
    }

    [Fact]
    public async Task A_trade_nobody_confirmed_goes_back_on_sale_with_every_column_cleared()
    {
        var scenario = await StartTradeAsync("扫描-双方都没确认");

        await fixture.AgeTransactionAsync(scenario.ProductId, TimeSpan.FromDays(2));

        await SweepAsync(Later);

        var state = await fixture.TradeStateAsync(scenario.ProductId);

        Assert.Equal(ProductStatus.Published, state.Status);

        // All four, not just the status. Leaving accepted_at behind would produce a listing that is
        // Published with a transaction timestamp — a state no transition can reach, and one that
        // neither sweep phase would ever look at again.
        Assert.Null(state.BuyerId);
        Assert.Null(state.AcceptedAt);
        Assert.Null(state.BuyerConfirmedAt);
        Assert.Null(state.SellerConfirmedAt);

        Assert.True(await fixture.CreateAnonymousClient()
            .IsInPublicFeedAsync(scenario.ProductId, scenario.Title));

        Assert.Contains(await scenario.Seller.ListNotificationsAsync(),
            x => x.Type == NotificationType.TransactionCancelled);
        Assert.Contains(await scenario.Buyer.ListNotificationsAsync(),
            x => x.Type == NotificationType.TransactionCancelled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_trade_with_one_confirmation_is_closed_by_the_sweep(bool buyerConfirmed)
    {
        var scenario = await StartTradeAsync(buyerConfirmed ? "扫描-买方确认了" : "扫描-卖方确认了");

        if (buyerConfirmed)
        {
            (await scenario.Buyer.ConfirmReceiptAsync(scenario.ConversationId)).EnsureSuccessStatusCode();
        }
        else
        {
            (await scenario.Seller.ConfirmPaymentAsync(scenario.ConversationId)).EnsureSuccessStatusCode();
        }

        await fixture.AgeTransactionAsync(scenario.ProductId, TimeSpan.FromDays(2));

        await SweepAsync(Later);

        var state = await fixture.TradeStateAsync(scenario.ProductId);

        Assert.Equal(ProductStatus.Sold, state.Status);

        // The silent side is taken to have agreed, so both columns end up set — whichever one the
        // sweep had to fill in itself.
        Assert.NotNull(state.BuyerConfirmedAt);
        Assert.NotNull(state.SellerConfirmedAt);

        Assert.Contains(await scenario.Seller.ListNotificationsAsync(),
            x => x.Type == NotificationType.ProductSold);
        Assert.Contains(await scenario.Buyer.ListNotificationsAsync(),
            x => x.Type == NotificationType.ProductSold);
    }

    [Fact]
    public async Task Sweeping_again_changes_nothing()
    {
        var scenario = await StartTradeAsync("扫描-幂等");

        await fixture.AgeTransactionAsync(scenario.ProductId, TimeSpan.FromDays(2));

        await SweepAsync(Later);

        var afterFirst = await fixture.TradeStateAsync(scenario.ProductId);

        Assert.Equal(ProductStatus.Published, afterFirst.Status);

        await SweepAsync(Later);

        // Every phase re-evaluates its full predicate against current data, so a second pass finds a
        // listing that is no longer InTransaction and a proposal that has already been cleared. The
        // version token is in here too: a phase that touched the row anyway would bump it.
        Assert.Equal(afterFirst, await fixture.TradeStateAsync(scenario.ProductId));
        Assert.Equal((null, null), await fixture.ProposalStateAsync(scenario.ConversationId));
    }

    [Fact]
    public async Task A_trade_inside_its_deadline_is_left_alone()
    {
        var scenario = await StartTradeAsync("扫描-还没到期");

        // Aged, but not past the deadline.
        await fixture.AgeTransactionAsync(scenario.ProductId, TimeSpan.FromHours(6));

        var before = await fixture.TradeStateAsync(scenario.ProductId);

        await SweepAsync(AppDbContext.AuditNow);

        Assert.Equal(ProductStatus.InTransaction, (await fixture.TradeStateAsync(scenario.ProductId)).Status);

        // Not even the token moved, so nothing wrote to this row at all.
        Assert.Equal(before, await fixture.TradeStateAsync(scenario.ProductId));
    }

    [Fact]
    public async Task A_completed_trade_is_not_disturbed_by_a_later_sweep()
    {
        var scenario = await StartTradeAsync("扫描-已完成不动");

        (await scenario.Buyer.ConfirmReceiptAsync(scenario.ConversationId)).EnsureSuccessStatusCode();
        (await scenario.Seller.ConfirmPaymentAsync(scenario.ConversationId)).EnsureSuccessStatusCode();

        // Aged past the deadline even though it is already finished — phase two filters on
        // InTransaction, so a Sold row must be invisible to it, and phase three must not roll it back.
        await fixture.AgeTransactionAsync(scenario.ProductId, TimeSpan.FromDays(2));

        await SweepAsync(Later);

        var state = await fixture.TradeStateAsync(scenario.ProductId);

        Assert.Equal(ProductStatus.Sold, state.Status);
        Assert.Equal(scenario.BuyerId, state.BuyerId);
        Assert.NotNull(state.BuyerConfirmedAt);
        Assert.NotNull(state.SellerConfirmedAt);
    }
}
