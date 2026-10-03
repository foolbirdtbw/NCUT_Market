using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Announcements;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class AnnouncementService(AppDbContext dbContext) : IAnnouncementService
{
    public async Task<PagedResult<AnnouncementResponse>> ListAsync(
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        // The application clock, never MySQL's NOW(). Every timestamp column here is Beijing
        // wall-clock and this MySQL instance is pinned to UTC, so the two clocks are eight hours
        // apart: comparing against NOW() would hide an announcement for eight hours after it was
        // published, and resurrect it for eight hours after it expired.
        var now = AppDbContext.AuditNow;

        var query = dbContext.Announcements
            .AsNoTracking()
            .Where(x => x.Status == AnnouncementStatus.Published
                && x.PublishedAt <= now
                && (x.ExpiredAt == null || x.ExpiredAt > now));

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.PublishedAt)
            .ThenByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new AnnouncementResponse(
                x.Id,
                x.Title,
                x.Content,
                // Non-null by the filter above; the request DTO's nullable counterpart is not a
                // state this list can return.
                x.PublishedAt!.Value,
                x.ExpiredAt))
            .ToListAsync(cancellationToken);

        return pagination.ToResult(items, totalCount);
    }

    public async Task<OperationResult<AnnouncementResponse>> PublishAsync(
        long userId,
        CreateAnnouncementRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<AnnouncementResponse>.Failure(
                ErrorCodes.Forbidden,
                "你没有权限做这件事。");
        }

        var now = AppDbContext.AuditNow;

        // DataAnnotations can express "this is a date" but not "this is a date in the future", so
        // the check lives here. It has to be against AuditNow rather than DateTime.Now for the same
        // reason the list filter is.
        if (request.ExpiredAt is { } expiredAt && expiredAt <= now)
        {
            return OperationResult<AnnouncementResponse>.Failure(
                ErrorCodes.ValidationError,
                "过期时间要晚于现在。");
        }

        var announcement = new Announcement
        {
            Title = request.Title.Trim(),
            Content = request.Content.Trim(),

            // Published on arrival: the API has no draft state. The column default is Draft for rows
            // written by anything else.
            Status = AnnouncementStatus.Published,
            PublishedAt = now,
            ExpiredAt = request.ExpiredAt
        };

        dbContext.Announcements.Add(announcement);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AnnouncementResponse>.Success(new AnnouncementResponse(
            announcement.Id,
            announcement.Title,
            announcement.Content,
            now,
            announcement.ExpiredAt));
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(userId, cancellationToken))
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.Forbidden,
                "你没有权限做这件事。");
        }

        var announcement = await dbContext.Announcements
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (announcement is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这条公告。");
        }

        dbContext.Announcements.Remove(announcement);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Whether the account may publish or delete announcements, read from the row on every call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a claim in the token. A role baked into a seven-day JWT would mean promoting
    /// an account with a hand-run UPDATE had no effect until that user signed out and back in — a gap
    /// that is invisible until somebody is staring at a missing admin form with a perfectly valid
    /// token in hand. The cost is one primary-key lookup per administrative request, and neither
    /// publishing nor deleting an announcement is a hot path.
    /// </para>
    /// <para>
    /// <see cref="UserStatus"/> is checked alongside it because the row is already loaded: a token
    /// issued before the account was disabled still satisfies <c>[Authorize]</c>, and this is the
    /// cheapest place to notice.
    /// </para>
    /// </remarks>
    private async Task<bool> IsAdminAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Role, x.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return user is { Role: UserRole.Admin, Status: UserStatus.Active };
    }
}
