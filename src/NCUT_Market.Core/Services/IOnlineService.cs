namespace NCUT_Market.Core.Services;

/// <summary>
/// Who is around right now, for the number in the site footer.
/// </summary>
/// <remarks>
/// <para>
/// "Online" means "an account that made a request carrying its token within the last few minutes".
/// It is a presence reading, not a session: this API is stateless, tokens are the only thing that
/// identifies a caller, and nothing connects one anonymous request to the next. A visitor who has not
/// signed in is therefore invisible to this, however many pages they open — which is why the footer
/// labels the number rather than presenting it as "people on the site".
/// </para>
/// <para>
/// The window itself is deliberately not part of this contract. Callers render the number; only the
/// implementation decides how stale a timestamp may be, and the tests pin that boundary by planting
/// rows rather than by reading a constant back out.
/// </para>
/// </remarks>
public interface IOnlineService
{
    /// <summary>
    /// Records that this account just made a request.
    /// </summary>
    /// <param name="userId">The account the token belongs to.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// Called on every authenticated request, so it writes and then discards the result. An account
    /// that no longer exists is not an error: the token outlives the row only in a hand-edited
    /// database, and there is nothing useful to tell the caller about it.
    /// </remarks>
    Task TouchAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many active accounts have been seen inside the window.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <remarks>
    /// Disabled accounts are excluded even though their tokens still validate and they still reach
    /// <see cref="TouchAsync"/>. Counting them would inflate a number whose whole job is to say how
    /// many people are actually using the site.
    /// </remarks>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
