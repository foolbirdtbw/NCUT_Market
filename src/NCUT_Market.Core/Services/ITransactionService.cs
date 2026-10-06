using NCUT_Market.Core.Common;

namespace NCUT_Market.Core.Services;

/// <summary>
/// The two-sided trade agreed inside a message thread: propose, accept, both sides confirm.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="IConversationService"/>, which is already the largest service
/// in the project and answers a different question. The split also matters for the sweep: it works on
/// listings, and folding it into the conversation service would make a background job depend on the
/// thread machinery for no reason.
/// </para>
/// <para>
/// Every action is addressed by <em>thread</em> id, because that is where a proposal lives and where
/// the user is standing when they click. But the trade itself belongs to the listing, and the seller
/// is a participant in every one of their threads — so each action re-derives
/// <c>(conversation.BuyerId, product.TransactionBuyerId)</c> and refuses when the two disagree.
/// Without that check a seller could confirm payment from any unrelated thread about the same item.
/// </para>
/// <para>
/// Failures are reported as <see cref="ErrorCodes.NotFound"/> when the caller is not in the thread
/// (matching <see cref="IConversationService"/>, so a guessed id confirms nothing) and
/// <see cref="ErrorCodes.InvalidState"/> when the thread is theirs but the trade is not in a state
/// that accepts the action.
/// </para>
/// </remarks>
public interface ITransactionService
{
    /// <summary>
    /// Offers to buy, from inside a thread.
    /// </summary>
    /// <param name="conversationId">The thread the offer is made in.</param>
    /// <param name="userId">The signed-in user, on either side of the thread.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidState"/> when the listing is not on sale — including
    /// when another buyer has already had an offer accepted — or when this thread already has one
    /// pending.
    /// </returns>
    /// <remarks>
    /// Leaves the listing untouched and on sale. That is the point of the two-stage design: the
    /// listing must not disappear from under the other people who are still talking about it.
    /// </remarks>
    Task<OperationResult<bool>> ProposeAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes up the offer made in a thread, moving the listing into <c>InTransaction</c>.
    /// </summary>
    /// <param name="conversationId">The thread holding the offer.</param>
    /// <param name="userId">The signed-in user, who must be a participant.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidState"/> when the caller made the offer themselves,
    /// when it has passed its one-day deadline, or when the listing is no longer on sale — the last
    /// of which is also what another buyer winning the race reports.
    /// </returns>
    /// <remarks>
    /// Clears every other pending proposal on the listing. They are not merely invalidated by the
    /// listing's status: a proposal is addressed by thread, and a stale one would keep offering a
    /// button whose only possible answer is a refusal.
    /// </remarks>
    Task<OperationResult<bool>> AcceptAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Records that the buyer has the item. Completes the trade if the seller has confirmed.</summary>
    /// <param name="conversationId">The thread the trade came from.</param>
    /// <param name="userId">The signed-in user, who must be the buyer of <em>this</em> trade.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<OperationResult<bool>> ConfirmReceiptAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Records that the seller has the money. Completes the trade if the buyer has confirmed.</summary>
    /// <param name="conversationId">The thread the trade came from.</param>
    /// <param name="userId">The signed-in user, who must be the seller.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<OperationResult<bool>> ConfirmPaymentAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every deadline that has passed: expired proposals, and trades that have run out their
    /// day without both confirmations.
    /// </summary>
    /// <param name="now">
    /// Beijing wall-clock time — the same clock the timestamp columns hold. Production passes
    /// <c>AppDbContext.AuditNow</c>; tests pass a value of their own choosing, which is what makes the
    /// deadlines testable without waiting a day.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the host is stopping.</param>
    /// <returns>How many rows were changed, for the log line.</returns>
    /// <remarks>
    /// <b>Every time comparison in here uses only this parameter.</b> Reaching for
    /// <c>AuditNow</c> inside would make the method untestable and, worse, would silently ignore the
    /// clock a test passed in — a change that still looks correct because production happens to supply
    /// the same value.
    /// </remarks>
    Task<int> SweepAsync(DateTime now, CancellationToken cancellationToken = default);
}
