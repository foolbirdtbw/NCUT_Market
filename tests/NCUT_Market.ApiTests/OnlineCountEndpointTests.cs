using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.DTOs.Online;
using NCUT_Market.Core.Enums;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The number in the site footer: how many accounts have been seen recently.
/// </summary>
/// <remarks>
/// <para>
/// Two things carry weight beyond "the endpoint answers".
/// </para>
/// <para>
/// The first is that the read stays anonymous. The footer is on every page, including the ones a
/// signed-out visitor is reading, so a missing <c>[AllowAnonymous]</c> behind the controller's
/// class-level <c>[Authorize]</c> would leave the counter blank for exactly those people — while every
/// test that signs in first keeps passing. <see cref="The_count_is_readable_without_a_token"/> pins it.
/// </para>
/// <para>
/// The second is that the middleware which stamps <c>last_seen_at</c> runs at all. It is invisible from
/// outside — nothing in any response mentions presence — so the only way to know it fired is to look at
/// the row. <see cref="A_request_with_a_token_marks_the_account_as_seen"/> does that, and
/// <see cref="A_request_without_a_token_marks_nobody"/> is its other half: the guard has to distinguish
/// the two, and a version that stamped unconditionally would pass the first test alone.
/// </para>
/// <para>
/// The counting tests plant their own blocks of accounts and assert a range, never an exact number.
/// The test database is shared by every class in the project, is never cleaned, and xUnit runs those
/// classes in parallel, so accounts the rest of the suite stamps are constantly joining the count —
/// <c>Assert.Equal(30, count)</c> would fail intermittently and read like a product bug rather than the
/// test's own mistake. Thirty planted rows make this test's own contribution the signal and everyone
/// else's the noise, and the range's upper bound leaves room for the couple of seconds' worth of that
/// noise the assertion can actually see. This is the same shape of accommodation as paging through
/// every category in <see cref="DictionaryAdminEndpointTests"/>: a shared fixture makes the absolute
/// assertion false, so the test either gets stronger or says plainly what it is really checking.
/// </para>
/// </remarks>
public sealed class OnlineCountEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>Comfortably past whatever window the service uses, so a planted row has fallen out.</summary>
    private static readonly TimeSpan LongIdle = TimeSpan.FromHours(2);

    /// <summary>
    /// A block big enough that no single background account can move an assertion, and small enough
    /// that planting one costs one INSERT statement.
    /// </summary>
    private const int Block = 30;

    /// <summary>
    /// How much room the planted count's upper bound leaves for accounts the rest of the suite stamps
    /// while this test runs. Half the block, which is far more than a few seconds can account for and
    /// far less than the block a broken predicate would wrongly include.
    /// </summary>
    private const int Slack = Block / 2;

    private static async Task<int> ReadCountAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/online/count");

        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<OnlineCountResponse>())
            ?? throw new InvalidOperationException("The online count returned no body.");

        return body.OnlineCount;
    }

    // ---------- who may read it ----------

    [Fact]
    public async Task The_count_is_readable_without_a_token()
    {
        // The footer is on every page, so this endpoint is read by people who have not signed in. The
        // controller carries a class-level [Authorize] and the read action carries [AllowAnonymous];
        // deleting the latter would break the footer for visitors and no other test here would notice.
        var anonymous = fixture.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/api/online/count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<OnlineCountResponse>())!;

        Assert.True(body.OnlineCount >= 0);
    }

    [Fact]
    public async Task A_signed_out_visitor_sees_the_same_number_as_a_signed_in_one()
    {
        // Nothing about the count is per-caller: it describes the site, not the person asking. A version
        // that quietly scoped the query to the caller would still answer 200 with a plausible number, so
        // the planted block is what makes the difference visible — a per-caller count would report zero.
        var (signedIn, _) = await fixture.CreateSignedInClientAsync();

        await fixture.ClearLastSeenAsync();
        await fixture.SeedAccountsAsync(Block, TimeSpan.Zero);

        // Read through the service first: it is the same query the endpoint runs, without the risk that
        // the two readings straddle a background account and disagree by one.
        var expected = await fixture.CountOnlineAsync();

        Assert.InRange(expected, Block, Block + Slack);

        Assert.InRange(await ReadCountAsync(fixture.CreateAnonymousClient()), Block, Block + Slack);
        Assert.InRange(await ReadCountAsync(signedIn), Block, Block + Slack);
    }

    // ---------- the middleware ----------

    [Fact]
    public async Task A_request_with_a_token_marks_the_account_as_seen()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        Assert.Null(await fixture.LastSeenAtAsync(auth.User.Id));

        var before = AppDbContext.AuditNow;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var seen = await fixture.LastSeenAtAsync(auth.User.Id);

        Assert.NotNull(seen);
        Assert.True(seen >= before, $"last_seen_at was {seen}, before the request ran ({before}).");
    }

    [Fact]
    public async Task A_request_without_a_token_marks_nobody()
    {
        var anonymous = fixture.CreateAnonymousClient();

        // Registered but never used, so its token has never been presented on a request. Registering is
        // itself anonymous — the fixture only attaches the bearer header once it has the response — and
        // nothing else in this class clears or stamps this account's row.
        var (unused, auth) = await fixture.CreateSignedInClientAsync();

        Assert.Null(await fixture.LastSeenAtAsync(auth.User.Id));

        await anonymous.GetAsync("/api/online/count");

        // The guard on the middleware is what makes this hold. Stamping unconditionally would leave a
        // timestamp here, and every anonymous visitor would be recorded as some account or other.
        Assert.Null(await fixture.LastSeenAtAsync(auth.User.Id));

        // And the unused client is still usable — nothing above invalidated it.
        Assert.Equal(HttpStatusCode.OK, (await unused.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_heartbeat_does_not_touch_updated_at()
    {
        // The reason TouchAsync goes through ExecuteUpdateAsync: ApplyAuditTimestamps stamps UpdatedAt
        // unconditionally on anything the change tracker sees as Modified, so the ordinary
        // load-mutate-save route would turn users.updated_at into a second presence column and quietly
        // destroy its meaning as an audit stamp.
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var before = await fixture.UpdatedAtAsync(auth.User.Id);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }

        Assert.NotNull(await fixture.LastSeenAtAsync(auth.User.Id));
        Assert.Equal(before, await fixture.UpdatedAtAsync(auth.User.Id));
    }

    // ---------- who is counted ----------

    [Fact]
    public async Task A_disabled_account_still_gets_marked_but_does_not_count()
    {
        // The two halves are deliberately different. TouchAsync is mechanical — it records that a valid
        // token arrived and has no opinion about the account. Counting is not: a blocked account can do
        // nothing, so leaving it in the number would inflate the one thing the number is for.
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        await fixture.DisableAccountAsync(auth.User.Id);

        // The stamp lands whatever the endpoint goes on to answer, because the middleware runs before
        // the request reaches it — the account is still a valid token holder.
        await client.GetAsync("/api/auth/me");

        Assert.NotNull(await fixture.LastSeenAtAsync(auth.User.Id));

        // And the same timestamp, on a whole block of blocked accounts, buys them nothing.
        await fixture.ClearLastSeenAsync();
        await fixture.SeedAccountsAsync(Block, TimeSpan.Zero);
        await fixture.SeedAccountsAsync(Block, TimeSpan.Zero, UserStatus.Disabled);

        Assert.InRange(await fixture.CountOnlineAsync(), Block, Block + Slack);
    }

    [Fact]
    public async Task Accounts_idle_past_the_window_do_not_count()
    {
        // This is the product decision the whole feature is: "online" means "seen in the last five
        // minutes", and the number is only that if rows older than the window stop counting. Two blocks
        // identical except for their timestamps make a broken cutoff impossible to miss — one whose
        // window was effectively infinite would report sixty, and one that ignored the column entirely
        // would report the same. The lower bound is the sibling assertion: the fresh block really is in.
        await fixture.ClearLastSeenAsync();

        await fixture.SeedAccountsAsync(Block, TimeSpan.Zero);
        await fixture.SeedAccountsAsync(Block, LongIdle);

        Assert.InRange(await fixture.CountOnlineAsync(), Block, Block + Slack);
    }
}
