using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Account registration, login, lookup, and deletion. Read-only with respect to everything except the
/// <c>users</c> table and — during deletion only — the caller's own <c>products</c> rows.
/// </summary>
/// <remarks>
/// Expected failures — a taken username, a wrong password, a disabled account — travel as
/// <see cref="OperationResult{T}"/>. Exceptions are reserved for the genuinely unexpected.
/// </remarks>
public interface IAuthService
{
    /// <summary>
    /// Creates an account and signs it in.
    /// </summary>
    /// <param name="request">The new account's details.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The issued token on success. Fails with <see cref="ErrorCodes.Conflict"/> when the username
    /// is taken.
    /// </returns>
    Task<OperationResult<AuthResponse>> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies credentials and issues a token.
    /// </summary>
    /// <param name="request">The credentials to check.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The issued token on success. Fails with <see cref="ErrorCodes.InvalidCredentials"/> when the
    /// username is unknown or the password is wrong — deliberately the same code for both, so the
    /// response does not reveal which usernames exist.
    /// </returns>
    Task<OperationResult<AuthResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a one-time reset code for a new password, and signs the account in.
    /// </summary>
    /// <param name="request">The username, the code an administrator handed over, and the new password.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The issued token on success. Fails with <see cref="ErrorCodes.InvalidCredentials"/> when the
    /// username is unknown, no code is outstanding, the code is wrong, or it has lapsed — deliberately
    /// one code and one message for all four, so the response reveals neither which usernames exist nor
    /// which accounts currently have a reset in flight.
    /// </returns>
    /// <remarks>
    /// This is the other half of <c>IUserService.IssueResetCodeAsync</c>. It lives here rather than
    /// there because it issues a token, which makes it a sibling of login and registration rather than
    /// an administrative action — the caller is the account holder, unauthenticated, not an
    /// administrator.
    /// </remarks>
    Task<OperationResult<AuthResponse>> CompleteResetAsync(
        CompleteResetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the account a token belongs to.
    /// </summary>
    /// <param name="userId">The id carried in the token's subject claim.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// The account, or <see cref="ErrorCodes.NotFound"/>. A disabled account reports not-found
    /// rather than forbidden: it cannot hold a valid token, so reaching here with one means the
    /// account was disabled after the token was issued, and the honest answer is that the identity
    /// no longer resolves.
    /// </returns>
    Task<OperationResult<CurrentUserResponse>> GetByIdAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the caller's own account: disables it, wipes the fields that identify the person, and
    /// takes their published listings down.
    /// </summary>
    /// <param name="userId">The id carried in the token's subject claim. A caller may only delete itself.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// True on success; <see cref="ErrorCodes.NotFound"/> if there is no such row. Deleting twice is
    /// not an error and still succeeds — see the remarks.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Not a physical delete, and that is not a shortcut.</b> Nine foreign keys point at
    /// <c>users</c> and every one of them is RESTRICT. The rows behind several of them — conversations,
    /// messages, the trade columns on a sold listing — belong to the other party as much as to the
    /// person leaving, so deleting the account cannot be allowed to take them along. What "deletion"
    /// means here is that the row stops identifying anyone: nickname, student number, username and
    /// avatar are wiped, the password is replaced with a value nobody holds, any outstanding reset
    /// code is cleared, and the account is disabled.
    /// </para>
    /// <para>
    /// Wiping <c>nickname</c> is what anonymizes the rest of the site at a stroke: every public surface
    /// renders the nickname rather than the username, so listings, threads, messages and feedback all
    /// begin reading "已注销用户". The student number is nulled rather than scrambled, which is what
    /// lets the same person register again later — the unique index treats NULLs as distinct.
    /// </para>
    /// <para>
    /// <b>Idempotent on purpose.</b> A token issued before deletion keeps working on ordinary member
    /// endpoints for the rest of its week (see README's 已知边界), so a second call is reachable and
    /// must succeed rather than report that the account is gone.
    /// </para>
    /// </remarks>
    Task<OperationResult<bool>> DeleteAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
