using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;
using NCUT_Market.Core.DTOs.Users;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The administrator's user directory and the one-time password-reset codes it hands out.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are load-bearing beyond "the endpoint works".
/// </para>
/// <para>
/// The first is that a bad code is <c>INVALID_CREDENTIALS</c> and not <c>UNAUTHORIZED</c>. The frontend's
/// request wrapper signs a user out on any 401 that is not <c>INVALID_CREDENTIALS</c>, so getting this
/// wrong would evict a signed-in user who mistyped a code — a bug that would never show up on this
/// endpoint's own tests. <see cref="A_bad_code_is_invalid_credentials_and_not_unauthorized"/> pins it.
/// </para>
/// <para>
/// The second is that the four ways a redemption can fail are indistinguishable from outside: unknown
/// username, no code outstanding, wrong code, lapsed code. Otherwise this becomes a way to ask the server
/// who has an account and who is mid-recovery.
/// </para>
/// </remarks>
public sealed class UserAdminEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    /// <summary>The shape a generated code is displayed in.</summary>
    /// <remarks>
    /// The alphabet is deliberately missing I, O, 0 and 1, so the character class is narrower than
    /// <c>[A-Z0-9]</c> would suggest — but a wider assertion would not fail on a regression that started
    /// emitting them, and that is the regression worth catching.
    /// </remarks>
    private static readonly Regex CodeShape = new("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-[A-Z0-9]{4}$");

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>A signed-in client that has been promoted through the database.</summary>
    private async Task<HttpClient> CreateAdminAsync()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        await fixture.PromoteToAdminAsync(auth.User.Id);

        return client;
    }

    private static async Task<ResetCodeResponse> IssueCodeAsync(HttpClient admin, long userId)
    {
        var response = await admin.PostAsync($"/api/users/{userId}/reset-password", content: null);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ResetCodeResponse>())
            ?? throw new InvalidOperationException("Issuing a reset code returned no body.");
    }

    private static async Task<PagedResult<UserSummaryResponse>> SearchAsync(
        HttpClient admin,
        string? keyword = null)
    {
        var url = $"/api/users?pageSize={PaginationQuery.MaxPageSize}";

        if (!string.IsNullOrEmpty(keyword))
        {
            url += "&keyword=" + Uri.EscapeDataString(keyword);
        }

        var response = await admin.GetAsync(url);

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PagedResult<UserSummaryResponse>>())!;
    }

    private static Task<HttpResponseMessage> RedeemAsync(
        HttpClient client,
        string username,
        string code,
        string newPassword) =>
        client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new CompleteResetRequest(username, code, newPassword));

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

    // ---------- who may use it ----------

    [Fact]
    public async Task The_directory_and_the_issue_endpoint_are_closed_to_anonymous_callers()
    {
        // Unlike the dictionary controllers there is no anonymous read here. Nothing in the shop needs a
        // list of who has an account, and the one screen that does is the administrator's.
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/api/users/1/reset-password", content: null)).StatusCode);
    }

    [Fact]
    public async Task They_are_closed_to_ordinary_users()
    {
        var (user, auth) = await fixture.CreateSignedInClientAsync();

        var listed = await user.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await listed.ReadCodeAsync());

        // Issuing against a real id, not a made-up one: a 404 here would mean the handler checked the
        // target before it checked the caller, which leaks which account ids exist.
        var issued = await user.PostAsync(
            $"/api/users/{auth.User.Id}/reset-password", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, issued.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await issued.ReadCodeAsync());
    }

    // ---------- the directory ----------

    [Fact]
    public async Task An_admin_can_find_an_account_by_username_or_by_nickname()
    {
        var admin = await CreateAdminAsync();
        var (_, target) = await fixture.CreateSignedInClientAsync();

        var suffix = NewSuffix();

        // The nickname is rewritten to something unique so the keyword search has exactly one hit — the
        // fixture registers everyone as 测试用户, which would match every account in the database.
        var nickname = $"昵称-{suffix}";

        await fixture.RenameAsync(target.User.Id, nickname);

        var byUsername = await SearchAsync(admin, target.User.Username);

        Assert.Contains(byUsername.Items, x => x.Id == target.User.Id);

        var byNickname = await SearchAsync(admin, nickname);

        var found = Assert.Single(byNickname.Items, x => x.Id == target.User.Id);

        Assert.Equal(target.User.Username, found.Username);
        Assert.Equal(UserRole.User, found.Role);
        Assert.Equal(UserStatus.Active, found.Status);
        // Nothing is outstanding on a fresh account, and the column is what the page keys "待使用" off.
        Assert.Null(found.PasswordResetExpiresAt);
    }

    [Fact]
    public async Task An_admin_can_find_an_account_by_student_number()
    {
        var admin = await CreateAdminAsync();

        // Registered through the public endpoint rather than through the fixture's helper, because the
        // point is to search for a number the administrator was actually told. A number the fixture
        // invented and never sent would prove nothing about the round trip.
        var studentId = ApiFixture.NewStudentId();
        var nickname = $"学号-{NewSuffix()}";

        var registered = await fixture.CreateAnonymousClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                "user-" + NewSuffix(),
                "test-password-123",
                nickname,
                studentId));

        registered.EnsureSuccessStatusCode();

        var auth = (await registered.Content.ReadFromJsonAsync<AuthResponse>())!;

        // The whole number first, which is what the administrator reads off a student card.
        var byFull = await SearchAsync(admin, studentId);

        var found = Assert.Single(byFull.Items, x => x.Id == auth.User.Id);

        Assert.Equal(studentId, found.StudentId);

        // Then the middle block, because the sample number's 中间五位 means an administrator who only
        // half-remembers it can still land on the account — the same substring rule the page advertises.
        var byFragment = await SearchAsync(admin, studentId.Substring(4, 5));

        Assert.Contains(byFragment.Items, x => x.Id == auth.User.Id);
    }

    [Fact]
    public async Task An_account_with_no_student_number_is_listed_and_simply_does_not_match()
    {
        // Rows that predate the column are NULL there, and MySQL treats NULL as distinct in the unique
        // index — which is exactly why the column is nullable. What has to hold is that such a row is
        // still an ordinary account in every other respect.
        var admin = await CreateAdminAsync();
        var (_, target) = await fixture.CreateSignedInClientAsync();

        await fixture.ClearStudentIdAsync(target.User.Id);

        // No keyword: the account is there, and its student number comes back null rather than throwing
        // or being quietly dropped from the projection.
        var all = await SearchAsync(admin, target.User.Username);

        var found = Assert.Single(all.Items, x => x.Id == target.User.Id);

        Assert.Null(found.StudentId);

        // And NULL LIKE anything is not true, so the search clause skips it instead of erroring.
        var byNumber = await SearchAsync(admin, "2024322030157");

        Assert.DoesNotContain(byNumber.Items, x => x.Id == target.User.Id);
    }

    [Fact]
    public async Task An_empty_keyword_lists_everyone_in_pages()
    {
        var admin = await CreateAdminAsync();

        var page = await SearchAsync(admin);

        Assert.True(page.TotalCount > 0);
        Assert.True(page.Items.Count <= PaginationQuery.MaxPageSize);
        Assert.Equal(1, page.Page);

        // Every account this class registered is in there, which is the point of "no keyword means all".
        Assert.All(page.Items, x => Assert.False(string.IsNullOrWhiteSpace(x.Username)));
    }

    [Fact]
    public async Task A_keyword_that_matches_nothing_is_an_empty_page_not_an_error()
    {
        var admin = await CreateAdminAsync();

        var page = await SearchAsync(admin, "没有这个人-" + NewSuffix());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    // ---------- issuing ----------

    [Fact]
    public async Task Issuing_a_code_gives_a_well_formed_one_that_lasts_a_day()
    {
        var admin = await CreateAdminAsync();
        var (_, target) = await fixture.CreateSignedInClientAsync();

        var before = DateTime.Now;
        var issued = await IssueCodeAsync(admin, target.User.Id);

        Assert.Matches(CodeShape, issued.ResetCode);

        // Expiry is compared against the same wall clock the response used, so an eight-hour sign error —
        // the Beijing-versus-UTC trap this codebase has fallen into before — fails here rather than in a
        // day.
        Assert.True(issued.ExpiresAt > before, $"Code already expired at {issued.ExpiresAt}.");
        Assert.True(issued.ExpiresAt <= before.AddHours(24).AddMinutes(1));

        // The digest is on the row and the plaintext is not.
        var (storedCode, storedExpiry) = await fixture.ResetCodeStateAsync(target.User.Id);

        Assert.NotNull(storedCode);
        Assert.DoesNotContain(issued.ResetCode, storedCode!, StringComparison.Ordinal);
        Assert.True(SameInstant(issued.ExpiresAt, storedExpiry));

        // And the directory now shows the account as mid-recovery.
        var found = (await SearchAsync(admin, target.User.Username)).Items.Single(x => x.Id == target.User.Id);

        Assert.True(SameInstant(issued.ExpiresAt, found.PasswordResetExpiresAt));
    }

    [Fact]
    public async Task Issuing_for_an_account_that_does_not_exist_is_a_404()
    {
        var admin = await CreateAdminAsync();

        var response = await admin.PostAsync("/api/users/999999999/reset-password", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await response.ReadCodeAsync());
    }

    // ---------- the round trip ----------

    [Fact]
    public async Task Redeeming_a_code_sets_the_new_password_and_signs_the_user_in()
    {
        var admin = await CreateAdminAsync();
        var (lockedOut, target) = await fixture.CreateSignedInClientAsync();

        var issued = await IssueCodeAsync(admin, target.User.Id);

        var redeemed = await RedeemAsync(
            lockedOut, target.User.Username, issued.ResetCode, "brand-new-password-456");

        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);

        // A token comes back for the same reason register returns one: the user has just proved who they
        // are, and making them sign in again would be a round trip to learn nothing.
        var auth = await redeemed.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.NotNull(auth);
        Assert.Equal(target.User.Id, auth.User.Id);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));

        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(anonymous, target.User.Username, "brand-new-password-456")).StatusCode);

        var old = await LoginAsync(anonymous, target.User.Username, "test-password-123");

        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await old.ReadCodeAsync());
    }

    [Fact]
    public async Task Redeeming_clears_both_columns()
    {
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();

        var issued = await IssueCodeAsync(admin, target.User.Id);

        Assert.Equal(
            HttpStatusCode.OK,
            (await RedeemAsync(client, target.User.Username, issued.ResetCode, "brand-new-password-456")).StatusCode);

        // Not just "the code stopped working" — the row is emptied, so a database dump taken afterwards
        // holds nothing that could still be redeemed.
        var (code, expiry) = await fixture.ResetCodeStateAsync(target.User.Id);

        Assert.Null(code);
        Assert.Null(expiry);

        Assert.Null(
            (await SearchAsync(admin, target.User.Username)).Items.Single(x => x.Id == target.User.Id)
                .PasswordResetExpiresAt);
    }

    [Fact]
    public async Task A_code_works_only_once()
    {
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();

        var issued = await IssueCodeAsync(admin, target.User.Id);

        Assert.Equal(
            HttpStatusCode.OK,
            (await RedeemAsync(client, target.User.Username, issued.ResetCode, "brand-new-password-456")).StatusCode);

        var again = await RedeemAsync(client, target.User.Username, issued.ResetCode, "third-password-789");

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await again.ReadCodeAsync());

        // Refused, and not applied on the way to being refused.
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(anonymous, target.User.Username, "brand-new-password-456")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LoginAsync(anonymous, target.User.Username, "third-password-789")).StatusCode);
    }

    [Fact]
    public async Task A_lapsed_code_is_refused()
    {
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();

        var issued = await IssueCodeAsync(admin, target.User.Id);

        await fixture.AgeResetCodeAsync(target.User.Id, TimeSpan.FromHours(25));

        var response = await RedeemAsync(client, target.User.Username, issued.ResetCode, "brand-new-password-456");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task A_bad_code_is_invalid_credentials_and_not_unauthorized()
    {
        // This is the one that matters outside the API. web/js/api.js signs a user out on a 401 whose
        // code is anything other than INVALID_CREDENTIALS, so a mistyped code answered with UNAUTHORIZED
        // would evict whoever was signed in — and no test of the reset flow itself would notice.
        var (client, target) = await fixture.CreateSignedInClientAsync();

        var response = await RedeemAsync(client, target.User.Username, "ZZZZ-ZZZZ", "brand-new-password-456");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await response.ReadCodeAsync());
    }

    [Fact]
    public async Task Every_way_a_redemption_can_fail_looks_the_same_from_outside()
    {
        // Four causes — unknown username, nothing outstanding, wrong code, lapsed code — and one answer.
        // Telling them apart would turn this endpoint into a way to ask who has an account and who is
        // mid-recovery. traceId is excluded for the reason AuthEndpointTests excludes it: it is
        // per-request by construction.
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();
        var (_, lapsedTarget) = await fixture.CreateSignedInClientAsync();

        var issued = await IssueCodeAsync(admin, target.User.Id);
        await fixture.AgeResetCodeAsync(target.User.Id, TimeSpan.FromHours(25));

        var noSuchUser = await RedeemAsync(
            client, "no-such-user-" + NewSuffix(), issued.ResetCode, "brand-new-password-456");

        var nothingOutstanding = await RedeemAsync(
            client, lapsedTarget.User.Username, issued.ResetCode, "brand-new-password-456");

        var wrongCode = await RedeemAsync(
            client, target.User.Username, "ZZZZ-ZZZZ", "brand-new-password-456");

        var lapsedCode = await RedeemAsync(
            client, target.User.Username, issued.ResetCode, "brand-new-password-456");

        var expected = await ReadProblemAsync(lapsedCode);

        foreach (var response in new[] { noSuchUser, nothingOutstanding, wrongCode })
        {
            Assert.Equal(lapsedCode.StatusCode, response.StatusCode);
            Assert.Equal(expected, await ReadProblemAsync(response));
        }
    }

    // ---------- issuing again, and neighbours ----------

    [Fact]
    public async Task Issuing_again_invalidates_the_previous_code()
    {
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();

        var first = await IssueCodeAsync(admin, target.User.Id);
        var second = await IssueCodeAsync(admin, target.User.Id);

        // This is the whole of "revoke a code I handed to the wrong person": there is no separate
        // endpoint for it, and no separate state to get stuck in.
        Assert.NotEqual(first.ResetCode, second.ResetCode);

        var stale = await RedeemAsync(client, target.User.Username, first.ResetCode, "brand-new-password-456");

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await RedeemAsync(client, target.User.Username, second.ResetCode, "brand-new-password-456")).StatusCode);
    }

    [Fact]
    public async Task Issuing_for_one_account_leaves_another_alone()
    {
        var admin = await CreateAdminAsync();
        var (first, firstTarget) = await fixture.CreateSignedInClientAsync();
        var (second, secondTarget) = await fixture.CreateSignedInClientAsync();

        await IssueCodeAsync(admin, firstTarget.User.Id);

        // The other account's credentials still work and it has no code outstanding — the write went to
        // one row, and a missing WHERE clause on a bulk update would show up right here.
        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(second, secondTarget.User.Username, "test-password-123")).StatusCode);

        var (code, expiry) = await fixture.ResetCodeStateAsync(secondTarget.User.Id);

        Assert.Null(code);
        Assert.Null(expiry);

        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(first, firstTarget.User.Username, "test-password-123")).StatusCode);
    }

    [Fact]
    public async Task A_code_can_be_issued_for_a_disabled_account_but_cannot_be_redeemed()
    {
        // Deliberate asymmetry. Issuing is allowed so the administrator gets a code rather than a
        // confusing 403, and the refusal arrives where it is actionable — in front of the user, who is
        // the one person who can go and ask why the account was blocked. The status check sits after the
        // code check for the same reason login's does: answering "this account is disabled" before
        // validating the code would hand out the existence of accounts.
        var admin = await CreateAdminAsync();
        var (client, target) = await fixture.CreateSignedInClientAsync();

        await fixture.DisableAccountAsync(target.User.Id);

        var issued = await IssueCodeAsync(admin, target.User.Id);

        var redeemed = await RedeemAsync(
            client, target.User.Username, issued.ResetCode, "brand-new-password-456");

        Assert.Equal(HttpStatusCode.Forbidden, redeemed.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await redeemed.ReadCodeAsync());

        // Refused, and the password left exactly as it was.
        var anonymous = fixture.CreateAnonymousClient();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await LoginAsync(anonymous, target.User.Username, "test-password-123")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LoginAsync(anonymous, target.User.Username, "brand-new-password-456")).StatusCode);
    }

    /// <summary>Whether two timestamps name the same instant at the resolution the column can hold.</summary>
    /// <remarks>
    /// <c>password_reset_expires_at</c> is <c>datetime(3)</c>, while the value the API hands back in the
    /// response still carries the sub-millisecond part of the tick it was computed from. Asserting exact
    /// equality would be asserting something the schema never promised.
    /// </remarks>
    private static bool SameInstant(DateTime expected, DateTime? actual) =>
        actual is DateTime value && Math.Abs((expected - value).TotalMilliseconds) < 1;

    private static async Task<ProblemShape> ReadProblemAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new ProblemShape(
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    /// <summary>
    /// The parts of a problem response that must match between two requests differing only in why the
    /// code was rejected. Deliberately not a record with a <c>traceId</c> member.
    /// </summary>
    private sealed record ProblemShape(string? Code, string? Title, string? Detail);
}
