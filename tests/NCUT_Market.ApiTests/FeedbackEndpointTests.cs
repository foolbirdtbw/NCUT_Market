using System.Net;
using System.Net.Http.Json;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Feedbacks;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.ApiTests;

/// <summary>
/// The feedback board: who may read it, who may post and vote, and what only an administrator may do.
/// </summary>
/// <remarks>
/// Every assertion is on a row the test made itself. The fixture's database is shared and never
/// cleaned, so nothing here counts rows or reads a fixed position in the list.
/// </remarks>
public sealed class FeedbackEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task The_board_reads_without_signing_in()
    {
        var response = await fixture.CreateAnonymousClient().GetAsync("/api/feedback?pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResult<FeedbackResponse>>();

        Assert.NotNull(page);

        // The one thing the caller's identity decides. With nobody signed in there is no vote to
        // light up, on any row, ever.
        Assert.All(page.Items, item => Assert.False(item.HasVoted));
    }

    [Fact]
    public async Task Posting_needs_a_token()
    {
        var response = await fixture.CreateAnonymousClient().PostFeedbackAsync(FeedbackTestSupport.NewBody("匿名"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_named_post_shows_its_author_and_an_anonymous_one_does_not()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var named = await client.PublishFeedbackAsync(
            FeedbackTestSupport.NewBody("署名"), FeedbackKind.Bug, anonymous: false);
        var secret = await client.PublishFeedbackAsync(
            FeedbackTestSupport.NewBody("匿名"), FeedbackKind.Feature, anonymous: true);

        var board = await fixture.CreateAnonymousClient().ListFeedbackAsync();

        Assert.Equal(auth.User.Nickname, board.Single(x => x.Id == named.Id).AuthorNickname);

        // Null, not "匿名": whether to print a word there is the frontend's decision.
        Assert.Null(board.Single(x => x.Id == secret.Id).AuthorNickname);
    }

    [Fact]
    public async Task No_response_carries_the_author_id_of_any_post()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        await client.PublishFeedbackAsync(
            FeedbackTestSupport.NewBody("查字段"), FeedbackKind.Bug, anonymous: true);

        // Anonymity is a property of the wire format rather than of one field being blanked, so it is
        // asserted against the raw body: a reviewer adding AuthorId to the DTO fails here, which is
        // the point — the nickname being null would still have looked right.
        var raw = await (await fixture.CreateAnonymousClient().GetAsync("/api/feedback?pageSize=10"))
            .Content.ReadAsStringAsync();

        Assert.DoesNotContain("authorId", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Voting_twice_takes_the_vote_back()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var post = await client.PublishFeedbackAsync(FeedbackTestSupport.NewBody("一进一出"));

        var on = await (await client.VoteFeedbackAsync(post.Id)).ReadVoteAsync();

        Assert.True(on.HasVoted);
        Assert.Equal(1, on.VoteCount);

        var off = await (await client.VoteFeedbackAsync(post.Id)).ReadVoteAsync();

        Assert.False(off.HasVoted);
        Assert.Equal(0, off.VoteCount);
    }

    [Fact]
    public async Task Two_accounts_count_as_two_votes()
    {
        var (first, _) = await fixture.CreateSignedInClientAsync();
        var (second, _) = await fixture.CreateSignedInClientAsync();

        var post = await first.PublishFeedbackAsync(FeedbackTestSupport.NewBody("两票"));

        await first.VoteFeedbackAsync(post.Id);

        var counted = await (await second.VoteFeedbackAsync(post.Id)).ReadVoteAsync();

        Assert.True(counted.HasVoted);
        Assert.Equal(2, counted.VoteCount);

        // HasVoted is per caller, and both of them voted — so both see a lit button on the same row.
        Assert.True((await first.ListFeedbackAsync()).Single(x => x.Id == post.Id).HasVoted);
        Assert.True((await second.ListFeedbackAsync()).Single(x => x.Id == post.Id).HasVoted);
    }

    [Fact]
    public async Task The_list_puts_the_most_voted_first()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        var quiet = await client.PublishFeedbackAsync(FeedbackTestSupport.NewBody("没人赞"), FeedbackKind.Bug);
        var loud = await client.PublishFeedbackAsync(FeedbackTestSupport.NewBody("有人赞"), FeedbackKind.Feature);

        await client.VoteFeedbackAsync(loud.Id);

        var board = (await client.ListFeedbackAsync()).ToList();

        // Relative order, never items[0]: the shared database already holds rows from earlier runs,
        // and anything with a vote sorts above everything without one.
        var loudIndex = board.FindIndex(x => x.Id == loud.Id);
        var quietIndex = board.FindIndex(x => x.Id == quiet.Id);

        Assert.True(loudIndex >= 0 && quietIndex >= 0, "Both rows should be on the first page.");
        Assert.True(loudIndex < quietIndex, "The row with a vote should sort above the one with none.");
    }

    [Fact]
    public async Task Only_an_admin_can_move_the_status()
    {
        var (client, auth) = await fixture.CreateSignedInClientAsync();

        var post = await client.PublishFeedbackAsync(FeedbackTestSupport.NewBody("改状态"));

        // A new post starts where it should, with nobody having looked at it.
        Assert.Equal(FeedbackStatus.Open, post.Status);

        var refused = await client.SetFeedbackStatusAsync(post.Id, FeedbackStatus.Accepted);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, await refused.ReadCodeAsync());

        // Promoting through the database has to take effect on this client's very next request: the
        // role is not in the token it is already holding. No second sign-in.
        await fixture.PromoteToAdminAsync(auth.User.Id);

        var moved = await client.SetFeedbackStatusAsync(post.Id, FeedbackStatus.Accepted);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var updated = await moved.Content.ReadFromJsonAsync<FeedbackResponse>();

        Assert.NotNull(updated);
        Assert.Equal(FeedbackStatus.Accepted, updated.Status);

        // The binder casts any integer to the enum, so nothing but the service stands between a 7 and
        // the status column.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.SetFeedbackStatusAsync(post.Id, (FeedbackStatus)7)).StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_can_delete_and_the_votes_go_with_it()
    {
        var (author, _) = await fixture.CreateSignedInClientAsync();
        var (voter, _) = await fixture.CreateSignedInClientAsync();
        var (admin, adminAuth) = await fixture.CreateSignedInClientAsync();

        var post = await author.PublishFeedbackAsync(FeedbackTestSupport.NewBody("删除"));
        await voter.VoteFeedbackAsync(post.Id);

        // The author is not special here. A board anybody can quietly withdraw from is one where the
        // vote counts stop meaning anything.
        Assert.Equal(HttpStatusCode.Forbidden, (await author.DeleteFeedbackAsync(post.Id)).StatusCode);

        await fixture.PromoteToAdminAsync(adminAuth.User.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteFeedbackAsync(post.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteFeedbackAsync(post.Id)).StatusCode);

        Assert.DoesNotContain(await voter.ListFeedbackAsync(), x => x.Id == post.Id);
    }

    [Fact]
    public async Task Blank_overlong_and_unknown_kinds_are_refused()
    {
        var (client, _) = await fixture.CreateSignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostFeedbackAsync("   ")).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostFeedbackAsync(new string('长', 1001))).StatusCode);

        // Same reason as the status check above: the binder casts any integer to the enum, so a 7
        // would reach the column if the service did not look.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostFeedbackAsync(FeedbackTestSupport.NewBody("坏类型"), (FeedbackKind)7)).StatusCode);
    }
}
