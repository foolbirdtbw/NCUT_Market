using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Self-service account deletion: <c>DELETE /api/auth/me</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deletion here is not a physical delete — nine foreign keys point at <c>users</c> and all of them are
/// <c>RESTRICT</c>, because the conversations, messages and trade columns behind them belong to the
/// other party too. What the endpoint does is stop the row identifying anyone. That makes the
/// interesting assertions structural rather than about the status code: what the row looks like
/// afterwards, what happened to the listings, and — the ones that matter most — what did <em>not</em>
/// happen to everybody else.
/// </para>
/// <para>
/// The endpoint answers 204 with no body, and the identity columns are on no response DTO and
/// deliberately never will be, so almost everything here is read back out of the row through
/// <c>ApiFixture</c>'s read helpers.
/// </para>
/// </remarks>
public sealed class AccountDeletionEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Deleting_an_account_returns_204()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var response = await client.DeleteAsync("/api/auth/me");

        // No body, like every other successful delete in this API. The frontend's api.del does not read
        // one, and a 200 carrying some farewell DTO would be a shape nothing consumes.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_without_a_token_is_a_401()
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.DeleteAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The code, not just the status: web/js/api.js keys its "your token expired, sign out" branch on
        // exactly this value, and the framework's default bare 401 has no body to read it from.
        Assert.Equal(ErrorCodes.Unauthorized, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Deleting_an_account_anonymizes_the_row()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var response = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var (username, nickname, studentId, status) = await fixture.UserStateAsync(auth.User.Id);

        // The row is still there — this was never a delete — and it no longer names anybody.
        Assert.StartsWith("deleted_", username);
        Assert.NotEqual(auth.User.Username, username);

        // This one line is what anonymizes the rest of the site at a stroke: every public surface renders
        // the nickname and never the username, so listings, threads, messages and feedback all start
        // reading 已注销用户 at once.
        Assert.Equal("已注销用户", nickname);

        // Nulled rather than scrambled, which is what leaves the number free for the same person to
        // register with again — the unique index treats NULLs as distinct.
        Assert.Null(studentId);

        // Disabled, which is also what takes it out of the online count: OnlineService filters on Active.
        Assert.Equal(UserStatus.Disabled, status);
    }

    [Fact]
    public async Task The_old_credentials_stop_working_after_deletion()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();
        var originalUsername = auth.User.Username;

        var response = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var anonymous = fixture.CreateAnonymousClient();

        var byOldName = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(originalUsername, "test-password-123"));

        Assert.Equal(HttpStatusCode.Unauthorized, byOldName.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await byOldName.ReadCodeAsync());

        // What is being tested is the password, not the username. Signing in under the row's *new* name
        // with the old password has to fail too, and that is only true if the hash was replaced with one
        // nobody holds. Without this second half, a deletion that only renamed the row and left the
        // password alone would pass.
        var (currentUsername, _, _, _) = await fixture.UserStateAsync(auth.User.Id);

        var byNewName = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(currentUsername, "test-password-123"));

        Assert.Equal(HttpStatusCode.Unauthorized, byNewName.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await byNewName.ReadCodeAsync());
    }

    [Fact]
    public async Task The_student_number_is_free_again_after_deletion()
    {
        // CreateSignedInClientAsync cannot be used here: it hides the number it registered with, and
        // reusing that same number is the whole point.
        var studentId = ApiFixture.NewStudentId();

        var (client, first) = await RegisterAsync(studentId);

        var response = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Same number, a brand-new username: one student, one account — and the account they left is not
        // standing in the way of a fresh one. This is the assertion the whole feature exists for.
        var (_, second) = await RegisterAsync(studentId);

        Assert.NotEqual(first.User.Id, second.User.Id);

        // And the unique index is still doing its job, so the success above is a slot that came free
        // rather than a check that quietly stopped running.
        var anonymous = fixture.CreateAnonymousClient();

        var third = await anonymous.PostAsJsonAsync("/api/auth/register", NewRegistration(studentId));

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await third.ReadCodeAsync());
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_published_listings_offline()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var title = "注销-" + Guid.NewGuid().ToString("N")[..8];

        var draft = await client.CreateDraftAsync(fixture, title + "-草稿");
        var listing = await client.PublishListingAsync(fixture, title + "-在售");

        var anonymous = fixture.CreateAnonymousClient();

        // Live before, so the 404 after is a change and not the starting state.
        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/products/{listing.Id}")).StatusCode);

        var response = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Offline is invisible, not merely filtered out of the feed: reading it by id is a 404 now.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/products/{listing.Id}")).StatusCode);

        var (listingStatus, _, _, _, _, _) = await fixture.TradeStateAsync(listing.Id);
        Assert.Equal(ProductStatus.Offline, listingStatus);

        // The draft is left exactly as it was. Nothing sweeps drafts — the scan
        // idx_products_status_last_activity_at was added to drive was never written — and moving it
        // offline would be inventing a state change for a row nobody but its former owner could see.
        var (draftStatus, _, _, _, _, _) = await fixture.TradeStateAsync(draft.Id);
        Assert.Equal(ProductStatus.Draft, draftStatus);

        // Last activity is stamped, the way ProductService.OfflineAsync stamps it: taking a listing down
        // is a real edit and the feed's freshness ordering has to see it as one.
        Assert.True(await fixture.LastActivityAtAsync(listing.Id) >= listing.PublishedAt);
    }

    [Fact]
    public async Task Deleting_one_account_leaves_another_account_alone()
    {
        var (leaving, _) = await fixture.CreateSignedInClientAsync();
        var (staying, stayingAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await staying.PublishListingAsync(
            fixture,
            "留下的-" + Guid.NewGuid().ToString("N")[..8]);

        var response = await leaving.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Taking a closed account's listings down is a bulk update over every row that seller owns. This
        // is the only test that would notice if the filter on seller_id went missing, and what it would
        // look like without one is another student's listing vanishing.
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/products/{listing.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await staying.GetAsync("/api/auth/me")).StatusCode);

        var login = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(stayingAuth.User.Username, "test-password-123"));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_leaves_the_other_partys_thread_intact()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, _) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(
            fixture,
            "私信-" + Guid.NewGuid().ToString("N")[..8]);

        var conversation = await buyer.StartConversationAsync(listing.Id);
        var message = await buyer.SendMessageAsync(conversation.Id, "还在吗？");

        var response = await seller.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Everything behind conversation_id belongs to the buyer as much as to the seller, which is
        // exactly why users cannot be physically deleted. The thread stays, the message stays, and the
        // only thing that changed is what the departed party is called.
        var detail = await buyer.GetConversationAsync(conversation.Id);

        Assert.Equal("已注销用户", detail.PeerNickname);
        Assert.Contains(detail.Messages.Items, x => x.Id == message.Id);
        Assert.Equal("还在吗？", detail.Messages.Items.Single(x => x.Id == message.Id).Content);
    }

    [Fact]
    public async Task Deleting_an_account_leaves_an_in_flight_trade_alone()
    {
        var (seller, _) = await fixture.CreateSignedInClientAsync();
        var (buyer, buyerAuth) = await fixture.CreateSignedInClientAsync();

        var listing = await seller.PublishListingAsync(
            fixture,
            "交易中-" + Guid.NewGuid().ToString("N")[..8]);

        var conversation = await buyer.StartConversationAsync(listing.Id);

        (await buyer.ProposeTradeAsync(conversation.Id)).EnsureSuccessStatusCode();
        (await seller.AcceptTradeAsync(conversation.Id)).EnsureSuccessStatusCode();

        var response = await seller.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // InTransaction is left alone on purpose, and this is the test that holds that decision down.
        // Every phase of the trade sweep filters on this status and ConfirmOnceAsync refuses anything
        // else, so forcing the listing Offline would strand TransactionBuyerId on a row no sweep will
        // ever look at again and take the confirm buttons away from both parties. Left as it is, the
        // day-long sweep settles or rolls it back by itself.
        var (status, buyerId, _, _, _, _) = await fixture.TradeStateAsync(listing.Id);

        Assert.Equal(ProductStatus.InTransaction, status);
        Assert.Equal(buyerAuth.User.Id, buyerId);
    }

    [Fact]
    public async Task Deleting_an_account_clears_an_outstanding_reset_code()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var (admin, adminAuth) = await fixture.CreateSignedInClientAsync();
        await fixture.PromoteToAdminAsync(adminAuth.User.Id);

        var issued = await admin.PostAsync($"/api/users/{auth.User.Id}/reset-password", null);
        issued.EnsureSuccessStatusCode();

        var before = await fixture.ResetCodeStateAsync(auth.User.Id);
        Assert.NotNull(before.Code);

        var response = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await fixture.ResetCodeStateAsync(auth.User.Id);

        // Not because a disabled account could redeem one — CompleteResetAsync refuses those. It is
        // because an administrator flipping Status back to Active would otherwise resurrect a code that
        // still works, and the reset columns are cleared as a pair so that invariant holds.
        Assert.Null(after.Code);
        Assert.Null(after.ExpiresAt);
    }

    [Fact]
    public async Task Deleting_an_account_twice_still_returns_204()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var first = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var afterFirst = await fixture.UserStateAsync(auth.User.Id);

        // A token issued before deletion keeps working on ordinary member endpoints for the rest of its
        // week, so this second call is reachable in production and must not answer 404 for a row that is
        // still there.
        var second = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // And the replacement username is left where it was. The uniqueness pre-check excludes the row's
        // own id; without that exclusion a second delete would read its own deleted_7 as a collision and
        // append another random suffix, every time.
        var afterSecond = await fixture.UserStateAsync(auth.User.Id);

        Assert.Equal(afterFirst.Username, afterSecond.Username);
    }

    /// <summary>Registers an account with a student number the caller chose, and signs it in.</summary>
    /// <remarks>
    /// <c>ApiFixture.CreateSignedInClientAsync</c> mints its own number and never reports it, which is
    /// right for every other test and useless for the one asserting the number came free again.
    /// </remarks>
    private async Task<(HttpClient Client, AuthResponse Auth)> RegisterAsync(string studentId)
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(studentId));
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new InvalidOperationException("Registration returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);

        return (client, auth);
    }

    private static RegisterRequest NewRegistration(string studentId) => new(
        "user-" + Guid.NewGuid().ToString("N")[..12],
        "test-password-123",
        "测试用户",
        studentId);
}
