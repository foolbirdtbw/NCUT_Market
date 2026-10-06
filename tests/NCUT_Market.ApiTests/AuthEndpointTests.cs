using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;

namespace NCUT_Market.ApiTests;

/// <summary>
/// Registration, login, and the failure shapes the frontend depends on.
/// </summary>
public sealed class AuthEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Registering_returns_a_usable_token()
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));

        // The token is reported as expiring, not as having expired. A sign error here — Beijing
        // wall-clock written into a claim that is defined in UTC — would put the expiry eight hours
        // out, which this catches because it is compared against the same clock the response used.
        Assert.True(auth.ExpiresAt > DateTime.Now, $"Token already expired at {auth.ExpiresAt}.");
    }

    [Fact]
    public async Task Registering_a_duplicate_username_is_a_conflict()
    {
        var client = fixture.CreateAnonymousClient();
        var request = NewRegistration();

        var first = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/auth/register", request);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await ReadCodeAsync(second));
    }

    [Fact]
    public async Task Logging_in_with_a_wrong_password_is_a_401()
    {
        var client = fixture.CreateAnonymousClient();
        var request = NewRegistration();

        await client.PostAsJsonAsync("/api/auth/register", request);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(request.Username, "not-the-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidCredentials, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_username_and_a_wrong_password_are_indistinguishable()
    {
        // The point is not the status code — it is that the two responses cannot be told apart, so
        // this endpoint cannot be used to discover which usernames exist.
        //
        // traceId is excluded from the comparison because it is per-request by construction: two
        // responses to two different requests never share one, and asserting on it would only ever
        // be testing that the server is still running.
        var client = fixture.CreateAnonymousClient();
        var request = NewRegistration();

        await client.PostAsJsonAsync("/api/auth/register", request);

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(request.Username, "not-the-password"));

        var unknownUser = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("no-such-user-" + Guid.NewGuid().ToString("N")[..8], "whatever"));

        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Equal(await ReadProblemAsync(wrongPassword), await ReadProblemAsync(unknownUser));
    }

    [Fact]
    public async Task Logging_in_with_the_right_password_returns_a_token()
    {
        var client = fixture.CreateAnonymousClient();
        var request = NewRegistration();

        await client.PostAsJsonAsync("/api/auth/register", request);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(request.Username, request.Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.Equal(request.Username, auth.User.Username);
        Assert.Equal(request.Nickname, auth.User.Nickname);
    }

    [Fact]
    public async Task Me_without_a_token_is_a_401_carrying_code_and_traceId()
    {
        // This is the test that proves the whole failure contract survives the authorization
        // pipeline. The framework's default answers a failed [Authorize] with a bare status code and
        // an empty body, which would leave the frontend's error card with nothing to render and no
        // traceId to quote in a log search.
        var client = fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadProblemAsync(response);

        Assert.Equal(ErrorCodes.Unauthorized, problem.Code);

        // Read separately from ProblemShape, which deliberately omits traceId so that two responses
        // can be compared for indistinguishability. Here the traceId is the whole point: without it
        // a user quoting an error has nothing to search the log for.
        var traceId = await ReadTraceIdAsync(response);

        Assert.False(string.IsNullOrWhiteSpace(traceId));
    }

    [Fact]
    public async Task Me_with_a_token_returns_the_account_that_registered()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();

        Assert.NotNull(user);
        Assert.Equal(auth.User.Id, user.Id);
        Assert.Equal(auth.User.Username, user.Username);
    }

    [Fact]
    public async Task Me_with_a_garbage_token_is_a_401()
    {
        var client = fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.Unauthorized, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Registering_with_a_short_password_is_a_validation_error()
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                "shortpw-" + Guid.NewGuid().ToString("N")[..8],
                "abc",
                "测试",
                ApiFixture.NewStudentId()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationError, await ReadCodeAsync(response));
    }

    [Theory]
    // Four digits: too short, which is what a mistyped or truncated paste looks like.
    [InlineData("1234")]
    // Thirteen digits, but the enrolment year cannot start with 1 — this is the 2024/1024 slip.
    [InlineData("1024322030157")]
    // Right length and prefix, but the tail is not digits.
    [InlineData("202432203015X")]
    // The full-width digits a Chinese IME produces without a mode switch. Kept out deliberately:
    // nothing normalises them, so accepting this would mean storing a number nobody can search for.
    [InlineData("２０２４３２２０３０１５７")]
    public async Task Registering_with_a_malformed_student_number_is_a_validation_error(string studentId)
    {
        var client = fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                "user-" + Guid.NewGuid().ToString("N")[..12],
                "test-password-123",
                "测试用户",
                studentId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationError, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Registering_without_a_student_number_is_a_validation_error()
    {
        var client = fixture.CreateAnonymousClient();

        // Blank rather than omitted: System.Text.Json refuses a missing non-nullable parameter before
        // the model binder ever runs, which would make this a 400 for the wrong reason and would not
        // exercise [Required] at all.
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                "user-" + Guid.NewGuid().ToString("N")[..12],
                "test-password-123",
                "测试用户",
                "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ValidationError, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Registering_a_duplicate_student_number_is_a_conflict()
    {
        var client = fixture.CreateAnonymousClient();
        var studentId = ApiFixture.NewStudentId();

        var first = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(studentId));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Different username, same number: one student, one account. A second registration reusing the
        // number must be refused on the number alone, not fall through to the username check.
        var second = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(studentId));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await ReadCodeAsync(second));
    }

    private static RegisterRequest NewRegistration() => NewRegistration(ApiFixture.NewStudentId());

    private static RegisterRequest NewRegistration(string studentId) => new(
        "user-" + Guid.NewGuid().ToString("N")[..12],
        "test-password-123",
        "测试用户",
        studentId);

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await ReadProblemAsync(response);
        return problem.Code;
    }

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

    private static async Task<string?> ReadTraceIdAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null;
    }

    /// <summary>
    /// The parts of a problem response that must be identical between two requests differing only in
    /// the credentials supplied. Deliberately not a record with a <c>traceId</c> member.
    /// </summary>
    private sealed record ProblemShape(string? Code, string? Title, string? Detail);
}
