using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Ai;

namespace NCUT_Market.Core.Services;

/// <summary>
/// The little character in the corner: what she should say on the page you are on, and what she says
/// when you type a question at her.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two very different halves behind one name.</b> <see cref="GetHintsAsync"/> is rule-based, reads
/// the database and costs nothing — it never fails, and always returns something to say. Only
/// <see cref="AskAsync"/> reaches the model, and only that half is metered.
/// </para>
/// <para>
/// Keeping them together is deliberate: the free half is also what grounds the paid half. The same
/// facts that become hints on the page are what get handed to the model as context, so she cannot
/// answer from anything a reader could not have read off the page themselves.
/// </para>
/// </remarks>
public interface IAgentAssistant
{
    /// <summary>
    /// What to say on this page, computed from real state.
    /// </summary>
    /// <param name="userId">
    /// The caller, or null when nobody is signed in. Null means no personal counts are queried at all
    /// — an anonymous reader gets static advice and nothing that reveals what anybody owns.
    /// </param>
    /// <param name="page">Which page, as the slug the client uses. Unknown values fall back to general advice.</param>
    /// <param name="productId">The listing being read, when the page is a product page.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>At least one hint, so the panel is never blank.</returns>
    Task<AgentHintsResponse> GetHintsAsync(
        long? userId,
        string? page,
        long? productId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers one free-form question, grounded in the same page facts the hints come from.
    /// </summary>
    /// <param name="userId">The caller. Used both for the context and for the daily allowance.</param>
    /// <param name="request">The question and the page it was asked on.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>
    /// Fails with <see cref="ErrorCodes.InvalidArgument"/> when the question is empty or too long,
    /// with <see cref="ErrorCodes.QuotaExceeded"/> when the day's allowance is spent, and with
    /// <see cref="ErrorCodes.ServiceUnavailable"/> when the model could not be reached or answered
    /// with nothing usable — in which case the attempt is handed back.
    /// </returns>
    Task<OperationResult<AgentAnswerResponse>> AskAsync(
        long userId,
        AgentAskRequest request,
        CancellationToken cancellationToken = default);
}
