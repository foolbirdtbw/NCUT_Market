using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.DTOs.Notifications;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class NotificationService(AppDbContext dbContext) : INotificationService
{
    public async Task<PagedResult<NotificationResponse>> ListAsync(
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Notifications
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        // Id breaks ties, so two notifications written in the same millisecond cannot swap places
        // between pages and be shown twice or skipped.
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new NotificationResponse(
                x.Id,
                x.Type,
                x.Title,
                x.Content,
                x.RelatedProductId,

                // Read live rather than frozen, unlike Title and Content. It is not part of what the
                // notification says, it is what the client groups by, so there is nothing to keep
                // readable after the listing goes — and after it goes the FK is null and this is null
                // with it, which is exactly what tells the client this notice stands on its own.
                x.RelatedProduct == null ? null : x.RelatedProduct.Title,
                x.IsRead,
                x.CreatedAt,
                x.ReadAt))
            .ToListAsync(cancellationToken);

        return pagination.ToResult(items, totalCount);
    }

    public async Task<OperationResult<UnreadCountResponse>> GetUnreadCountAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var count = await dbContext.Notifications
            .CountAsync(x => x.UserId == userId && !x.IsRead, cancellationToken);

        return OperationResult<UnreadCountResponse>.Success(new UnreadCountResponse(count));
    }

    public async Task<OperationResult<bool>> MarkReadAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Recipientship is part of the lookup, so somebody else's notification is indistinguishable
        // from one that does not exist.
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);

        if (notification is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这条通知。");
        }

        // Idempotent on purpose. Re-opening a notification — which is exactly what a page reload or a
        // click on an already-read row does — must not overwrite the first ReadAt with a later one.
        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = AppDbContext.AuditNow;

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return OperationResult<bool>.Success(true);
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Recipientship is part of the lookup, so somebody else's notification is indistinguishable
        // from one that does not exist.
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);

        if (notification is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这条通知。");
        }

        // A real delete, unlike hiding a conversation: this row has exactly one reader and nothing
        // else points at it — the product's FK already tolerates the row vanishing. There is no other
        // side of it to preserve.
        dbContext.Notifications.Remove(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    public async Task<OperationResult<bool>> DeleteByProductAsync(
        long productId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Notifications
            .Where(x => x.UserId == userId && x.RelatedProductId == productId)
            .ToListAsync(cancellationToken);

        dbContext.Notifications.RemoveRange(rows);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }
}
