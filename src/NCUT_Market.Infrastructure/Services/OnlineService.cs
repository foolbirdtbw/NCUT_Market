using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class OnlineService(AppDbContext dbContext) : IOnlineService
{
    /// <summary>How long an account stays "online" after its last request.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public async Task TouchAsync(long userId, CancellationToken cancellationToken = default)
    {
        var now = AppDbContext.AuditNow;

        // ExecuteUpdateAsync rather than load-mutate-save, and that is the whole reason this method
        // looks the way it does. ApplyAuditTimestamps stamps UpdatedAt unconditionally on any entry
        // the change tracker sees as Modified, so the ordinary route would make users.updated_at mean
        // "the last time this person loaded a page" — an audit column quietly turned into a heartbeat.
        //
        // Going straight to UPDATE skips the change tracker and the stamping with it, so the statement
        // below is the only thing written. The codebase's rule for ExecuteUpdateAsync is that the file
        // must reference AppDbContext.AuditNow and set its timestamps by hand; it does, and it
        // deliberately leaves updated_at alone.
        await dbContext.Users
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LastSeenAt, now),
                cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        // The application clock, never MySQL's NOW(). Every timestamp column in this schema is Beijing
        // wall-clock time while the MySQL instance runs on UTC, so the two are eight hours apart and a
        // comparison against NOW() would count everyone who was here eight hours ago.
        var cutoff = AppDbContext.AuditNow - Window;

        return await dbContext.Users
            .AsNoTracking()
            .CountAsync(
                x => x.Status == UserStatus.Active && x.LastSeenAt >= cutoff,
                cancellationToken);
    }
}
