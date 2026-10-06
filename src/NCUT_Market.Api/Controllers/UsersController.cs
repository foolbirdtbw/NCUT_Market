using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCUT_Market.Api.Errors;
using NCUT_Market.Api.Extensions;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Users;
using NCUT_Market.Core.Services;

namespace NCUT_Market.Api.Controllers;

/// <summary>
/// The account directory, and the password-reset codes that get a locked-out user back in.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the dictionary controllers, nothing here is anonymous — not even the reads. A searchable list
/// of who has an account is not something the shop needs, and the one screen that uses it is the
/// administrator's. <c>[Authorize]</c> at the class level therefore rules out the anonymous case only;
/// the administrator check is made inside <see cref="IUserService"/> against the database, so a
/// signed-in ordinary user gets a 403 rather than a page of names.
/// </para>
/// <para>
/// There is no endpoint here that sets a password. An administrator mints a code and hands it over; the
/// user redeems it at <c>POST /api/auth/reset-password</c>. That split is why an administrator never
/// learns a password they could then sign in with.
/// </para>
/// </remarks>
[ApiController]
[Route("api/users")]
[Authorize]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    /// <summary>One page of accounts, optionally narrowed by a keyword.</summary>
    /// <param name="keyword">
    /// Matched against username and nickname as a substring. Omit it to list everyone.
    /// </param>
    /// <param name="pagination">Page and page size. Out-of-range values are clamped, not rejected.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The requested page. May be empty when nothing matches.</response>
    /// <response code="400">A query-string value was not parseable.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    [HttpGet]
    [ProducesResponseType<PagedResult<UserSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<UserSummaryResponse>>> Search(
        [FromQuery] string? keyword,
        [FromQuery] PaginationQuery pagination,
        CancellationToken cancellationToken)
    {
        var result = await userService.SearchAsync(User.GetUserId(), keyword, pagination, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }

    /// <summary>Mints a one-time password-reset code for an account.</summary>
    /// <param name="id">The account the user is locked out of.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The code, and when it stops working.</response>
    /// <response code="401">No token.</response>
    /// <response code="403">Signed in, but not an admin.</response>
    /// <response code="404">No such account.</response>
    /// <remarks>
    /// <para>
    /// Takes no body: the code is generated, not chosen, so there is nothing for the caller to say. The
    /// response is the only time the code is readable — the row keeps a digest — and calling again mints
    /// a new one and invalidates the old, which is how a mislaid code is dealt with.
    /// </para>
    /// <para>
    /// Deliberately permitted for a disabled account as well as an active one. The redemption endpoint
    /// refuses a disabled account anyway, so issuing a code here cannot revive one; letting the
    /// administrator get a code rather than a confusing 403 just means the refusal arrives at the user,
    /// where it is actionable. The list marks disabled accounts, so the administrator can see why it
    /// will not work before handing anything over.
    /// </para>
    /// </remarks>
    [HttpPost("{id:long}/reset-password")]
    [ProducesResponseType<ResetCodeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResetCodeResponse>> IssueResetCode(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await userService.IssueResetCodeAsync(User.GetUserId(), id, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value)
            : ProblemResults.Failure(this, result.ErrorCode, result.ErrorMessage!);
    }
}
