using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;

namespace NCUT_Market.Core.Services;

/// <summary>
/// Account registration, login, and lookup. Read-only with respect to everything except the
/// <c>users</c> table.
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
}
