using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Users;

namespace NCUT_Market.Core.Services;

/// <summary>
/// The administrator's view of the <c>users</c> table: who is registered, and handing out the codes
/// that let someone who has forgotten their password set a new one.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is administrator-only, and the check reads <c>users.role</c> from the database on
/// each call rather than trusting anything in the token. That is the same rule the dictionary and
/// announcement services follow, and it is why each method takes the caller's id rather than only the
/// target's.
/// </para>
/// <para>
/// There is no method here that sets a password. An administrator mints a code and the user redeems it
/// at <c>IAuthService.CompleteResetAsync</c>, so nothing an administrator sees is ever a credential for
/// the account being recovered.
/// </para>
/// </remarks>
public interface IUserService
{
    /// <summary>
    /// One page of accounts, optionally narrowed to those matching a keyword.
    /// </summary>
    /// <param name="adminId">The signed-in account, which must be an active administrator.</param>
    /// <param name="keyword">
    /// Matched against both username and nickname, as a substring. Null or blank returns everyone.
    /// </param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The page, or <see cref="ErrorCodes.Forbidden"/> when the caller is not an administrator.</returns>
    Task<OperationResult<PagedResult<UserSummaryResponse>>> SearchAsync(
        long adminId,
        string? keyword,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a one-time password-reset code for an account and returns it to the administrator.
    /// </summary>
    /// <param name="adminId">The signed-in account, which must be an active administrator.</param>
    /// <param name="targetUserId">The account the user is locked out of.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The code and the moment it stops working. Fails with <see cref="ErrorCodes.Forbidden"/> when the
    /// caller is not an administrator, and <see cref="ErrorCodes.NotFound"/> when no such account
    /// exists.
    /// </returns>
    /// <remarks>
    /// Only a digest of the code is stored, so this response is the one and only time it is readable.
    /// Calling again is how a lost code is dealt with: it mints a new one and silently invalidates the
    /// old, which is the entire revocation story.
    /// </remarks>
    Task<OperationResult<ResetCodeResponse>> IssueResetCodeAsync(
        long adminId,
        long targetUserId,
        CancellationToken cancellationToken = default);
}
