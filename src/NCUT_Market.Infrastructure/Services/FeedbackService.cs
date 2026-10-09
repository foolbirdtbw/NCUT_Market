using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Feedbacks;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class FeedbackService(AppDbContext dbContext) : IFeedbackService
{
    public async Task<PagedResult<FeedbackResponse>> ListAsync(
        PaginationQuery pagination,
        long? viewerId,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Feedbacks.AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query

            // What the board is for: the thing the most people want is the first thing read. Ties fall
            // back to newest, and then to id, so the order is total rather than merely stable.
            .OrderByDescending(x => x.Votes.Count)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(Projection(viewerId))
            .ToListAsync(cancellationToken);

        return pagination.ToResult(items, totalCount);
    }

    public async Task<OperationResult<FeedbackResponse>> CreateAsync(
        long userId,
        CreateFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        // The kind arrives as a number in the body and the model binder casts any integer to the enum,
        // so nothing above this line rejects a 7. Checked here rather than trusted.
        if (request.Kind is not (FeedbackKind.Bug or FeedbackKind.Feature))
        {
            return OperationResult<FeedbackResponse>.Failure(
                ErrorCodes.ValidationError,
                "请选择反馈类型。");
        }

        var feedback = new Feedback
        {
            AuthorId = userId,
            Content = request.Content.Trim(),

            // Written even when the post is anonymous. Anonymity is about what the board shows, not
            // about the row forgetting who wrote it.
            IsAnonymous = request.IsAnonymous,
            Kind = request.Kind
        };

        dbContext.Feedbacks.Add(feedback);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<FeedbackResponse>.Success(
            await ReadAsync(feedback.Id, userId, cancellationToken));
    }

    public async Task<OperationResult<FeedbackVoteResponse>> ToggleVoteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Feedbacks.AnyAsync(x => x.Id == id, cancellationToken))
        {
            return OperationResult<FeedbackVoteResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这条反馈。");
        }

        var vote = await dbContext.FeedbackVotes
            .FirstOrDefaultAsync(x => x.FeedbackId == id && x.UserId == userId, cancellationToken);

        var hasVoted = vote is null;

        if (vote is null)
        {
            dbContext.FeedbackVotes.Add(new FeedbackVote { FeedbackId = id, UserId = userId });
        }
        else
        {
            dbContext.FeedbackVotes.Remove(vote);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Recounted rather than nudged by one. The number returned here is the number the button will
        // show, and "the count I already had, plus my vote" is a guess about everybody else's.
        var voteCount = await dbContext.FeedbackVotes
            .CountAsync(x => x.FeedbackId == id, cancellationToken);

        return OperationResult<FeedbackVoteResponse>.Success(
            new FeedbackVoteResponse(voteCount, hasVoted));
    }

    public async Task<OperationResult<FeedbackResponse>> SetStatusAsync(
        long id,
        long userId,
        UpdateFeedbackStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<FeedbackResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        // Same reason as CreateAsync's kind check: the binder casts any integer to the enum.
        if (request.Status is not (FeedbackStatus.Open or FeedbackStatus.Accepted
            or FeedbackStatus.Done or FeedbackStatus.Rejected))
        {
            return OperationResult<FeedbackResponse>.Failure(
                ErrorCodes.ValidationError,
                "没有这个处理状态。");
        }

        var feedback = await dbContext.Feedbacks
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (feedback is null)
        {
            return OperationResult<FeedbackResponse>.Failure(ErrorCodes.NotFound, "找不到这条反馈。");
        }

        feedback.Status = request.Status;
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<FeedbackResponse>.Success(
            await ReadAsync(feedback.Id, userId, cancellationToken));
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Admin before the row is looked up, so a non-admin gets the same 403 whether or not the id
        // they asked about exists.
        if (!await dbContext.IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var feedback = await dbContext.Feedbacks
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (feedback is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这条反馈。");
        }

        // The votes leave with it through the cascade on feedback_votes.feedback_id, so there is no
        // second RemoveRange to keep in step here.
        dbContext.Feedbacks.Remove(feedback);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// The board's one projection, so a mutation's response reads exactly like the row the client
    /// already has.
    /// </summary>
    /// <remarks>
    /// The viewer is folded down to a plain <c>long</c> before the expression is built. A nullable
    /// comparison inside the query would leave EF deciding what <c>user_id = NULL</c> means; 0 is not a
    /// user id — the column is auto-increment and starts at 1 — so an anonymous caller matches nothing,
    /// which is exactly what "nobody has voted" should read as.
    /// </remarks>
    private static Expression<Func<Feedback, FeedbackResponse>> Projection(long? viewerId)
    {
        var voter = viewerId ?? 0;

        return x => new FeedbackResponse(
            x.Id,
            x.Content,
            x.Kind,
            x.Status,

            // The nickname is not even read for an anonymous post, so there is no path on which it
            // could reach the wire by accident.
            x.IsAnonymous ? null : x.Author.Nickname,
            x.CreatedAt,
            x.Votes.Count,
            x.Votes.Any(vote => vote.UserId == voter));
    }

    /// <summary>
    /// One row through <see cref="Projection"/>.
    /// </summary>
    /// <remarks>
    /// Called only after a write that just succeeded, so the row is known to exist; <c>FirstAsync</c>
    /// throwing rather than a nullable return is the honest shape for that — a missing row here would
    /// be a server-side defect, not a request that deserves a 404.
    /// </remarks>
    private async Task<FeedbackResponse> ReadAsync(
        long id,
        long viewerId,
        CancellationToken cancellationToken) =>
        await dbContext.Feedbacks
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(Projection(viewerId))
            .FirstAsync(cancellationToken);
}
