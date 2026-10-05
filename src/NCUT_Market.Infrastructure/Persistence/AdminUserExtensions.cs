using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Infrastructure.Persistence;

/// <summary>
/// The one place that decides whether an account may do administrative work.
/// </summary>
/// <remarks>
/// An extension method on <see cref="AppDbContext"/> rather than a service in the container: it has no
/// state, every caller already holds the context, and there is nothing to substitute in a test that
/// the database itself does not already decide.
/// </remarks>
internal static class AdminUserExtensions
{
    /// <summary>
    /// The message every refusal carries, so the four services that ask this question answer alike.
    /// </summary>
    public const string ForbiddenMessage = "你没有权限做这件事。";

    /// <summary>
    /// Whether the account may perform administrative work, read from the row on every call.
    /// </summary>
    /// <param name="dbContext">The context the caller is already using.</param>
    /// <param name="userId">The signed-in account.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns><see langword="true"/> only for an active admin.</returns>
    /// <remarks>
    /// <para>
    /// Deliberately not a claim in the token. A role baked into a seven-day JWT would mean promoting
    /// an account with a hand-run UPDATE had no effect until that user signed out and back in — a gap
    /// that is invisible until somebody is staring at a missing admin form with a perfectly valid
    /// token in hand. The cost is one primary-key lookup per administrative request, and none of these
    /// is a hot path.
    /// </para>
    /// <para>
    /// <see cref="UserStatus"/> is checked alongside it because the row is already loaded: a token
    /// issued before the account was disabled still satisfies <c>[Authorize]</c>, and this is the
    /// cheapest place to notice.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsAdminAsync(
        this AppDbContext dbContext,
        long userId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Role, x.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return user is { Role: UserRole.Admin, Status: UserStatus.Active };
    }
}
