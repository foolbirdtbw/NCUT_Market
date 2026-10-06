using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// Registration and sign-in. Unauthenticated except for <see cref="Me"/>, which is the endpoint a
/// client uses to find out whether the token it is holding is still good.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    /// <summary>Creates an account and signs it in.</summary>
    /// <param name="request">The new account's details.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The account was created, with a token ready to use.</response>
    /// <response code="400">A field was missing or out of range.</response>
    /// <response code="409">The username or the student number is already taken.</response>
    /// <remarks>
    /// Returns a token rather than a bare 201. Requiring a new user to immediately POST their
    /// credentials again to the login endpoint would be a second round trip to learn nothing the
    /// server already knew.
    /// </remarks>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.RegisterAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Exchanges credentials for a token.</summary>
    /// <param name="request">The credentials to check.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The credentials were correct.</response>
    /// <response code="400">A field was missing.</response>
    /// <response code="401">The username or password is wrong.</response>
    /// <response code="403">The credentials are correct but the account is disabled.</response>
    /// <remarks>
    /// An unknown username and a wrong password return the same code and the same message. Telling
    /// them apart would make this endpoint a way to find out who has an account.
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Redeems a reset code for a new password, and signs the account in.</summary>
    /// <param name="request">The username, the code an administrator handed over, and the new password.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The password was changed, with a token ready to use.</response>
    /// <response code="400">A field was missing or out of range.</response>
    /// <response code="401">The code is wrong, has been used, has lapsed, or no such username exists.</response>
    /// <response code="403">The code was right but the account is disabled.</response>
    /// <remarks>
    /// <para>
    /// The self-service half of password recovery, and the reason no email or phone number is needed:
    /// an administrator verifies who the user is in person and mints the code, and the user picks a
    /// password here that the administrator never sees.
    /// </para>
    /// <para>
    /// Deliberately under <c>/api/auth</c> rather than <c>/api/users</c>: the caller is the account
    /// holder with no token, not an administrator. It answers with a token for the same reason
    /// <see cref="Register"/> does — the user has just proved who they are, and making them sign in
    /// again would be a second round trip to learn nothing new.
    /// </para>
    /// <para>
    /// Failures are <c>INVALID_CREDENTIALS</c> and not <c>UNAUTHORIZED</c>, which matters on the client:
    /// <c>web/js/api.js</c> treats a 401 carrying any other code as an expired token and signs the user
    /// out, so a mistyped code would evict a user who was signed in.
    /// </para>
    /// </remarks>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuthResponse>> ResetPassword(
        [FromBody] CompleteResetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.CompleteResetAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>The account the request's token belongs to.</summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The account.</response>
    /// <response code="401">No token, or one that is expired or not signed by this API.</response>
    /// <response code="404">The token is valid but its account no longer exists or is disabled.</response>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var result = await authService.GetByIdAsync(User.GetUserId(), cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
