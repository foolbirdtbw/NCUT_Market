using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Users;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Security;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class UserService(AppDbContext dbContext) : IUserService
{
    /// <summary>How long a minted reset code stays usable.</summary>
    /// <remarks>
    /// A day, not an hour. The code is handed over in person, and the user may well not act on it until
    /// the next time they are at a computer — an expiry that lapses before the handover has finished
    /// being useful would just mean the administrator mints a second one.
    /// </remarks>
    private const int ResetCodeLifetimeHours = 24;

    public async Task<OperationResult<PagedResult<UserSummaryResponse>>> SearchAsync(
        long adminId,
        string? keyword,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(adminId, cancellationToken))
        {
            return OperationResult<PagedResult<UserSummaryResponse>>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var users = dbContext.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = LikePattern.Contains(keyword.Trim());

            // Username, nickname or student number, because an administrator may have been given any of
            // the three — the login name off a form, the display name off a listing, or the number read
            // off a student card. Case is handled by the column collation (utf8mb4_0900_ai_ci,
            // case-insensitive), not here. The student number is matched as a substring like the other
            // two, so the middle block of a number finds it without the administrator typing all 13
            // digits. Rows predating the column have NULL there, and NULL LIKE anything is not true,
            // so they simply do not match this clause.
            users = users.Where(x =>
                EF.Functions.Like(x.Username, pattern, LikePattern.Escape) ||
                EF.Functions.Like(x.Nickname, pattern, LikePattern.Escape) ||
                EF.Functions.Like(x.StudentId, pattern, LikePattern.Escape));
        }

        var totalCount = await users.CountAsync(cancellationToken);

        // Newest first, the same convention as every other list in this API. By id rather than by
        // created_at: it is the primary key, so it orders identically without a second column in the
        // sort or an index to cover it.
        var items = await users
            .OrderByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new UserSummaryResponse(
                x.Id,
                x.Username,
                x.Nickname,
                x.StudentId,
                x.Role,
                x.Status,
                x.CreatedAt,
                x.PasswordResetExpiresAt))
            .ToListAsync(cancellationToken);

        return OperationResult<PagedResult<UserSummaryResponse>>.Success(
            pagination.ToResult(items, totalCount));
    }

    public async Task<OperationResult<ResetCodeResponse>> IssueResetCodeAsync(
        long adminId,
        long targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.IsAdminAsync(adminId, cancellationToken))
        {
            return OperationResult<ResetCodeResponse>.Failure(
                ErrorCodes.Forbidden,
                AdminUserExtensions.ForbiddenMessage);
        }

        var user = await dbContext.Users
            .FirstOrDefaultAsync(x => x.Id == targetUserId, cancellationToken);

        if (user is null)
        {
            return OperationResult<ResetCodeResponse>.Failure(ErrorCodes.NotFound, "找不到这个用户。");
        }

        var code = ResetCode.Generate();

        // Beijing wall-clock, like every other timestamp this schema holds, and read from AuditNow rather
        // than DateTime.UtcNow so the deadline is on the same clock the redemption path compares against.
        var expiresAt = AppDbContext.AuditNow.AddHours(ResetCodeLifetimeHours);

        // Overwriting both halves is what makes a second call revoke the first — no separate endpoint, no
        // second state to keep consistent. Storing the digest rather than the code is what makes this
        // response the only place the code ever appears.
        user.PasswordResetCode = ResetCode.Hash(ResetCode.Normalize(code));
        user.PasswordResetExpiresAt = expiresAt;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ResetCodeResponse>.Success(new ResetCodeResponse(code, expiresAt));
    }
}
