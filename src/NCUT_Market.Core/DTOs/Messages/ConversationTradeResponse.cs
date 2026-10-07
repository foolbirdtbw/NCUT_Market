using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// The raw facts about the trade on the listing this thread is about.
/// </summary>
/// <param name="ProductStatus">
/// The listing's lifecycle state, so the client can tell "you can still propose" from "somebody
/// already accepted" without a second request.
/// </param>
/// <param name="BuyerId">
/// Who opened this thread, and therefore who the buyer would be if this thread's proposal were
/// accepted. Compare it against the signed-in user's id to decide which side of the trade they are
/// on — the same comparison <see cref="MessageResponse.SenderId"/> already gets.
/// </param>
/// <param name="ProposedById">Who proposed in this thread, or null when nothing is pending here.</param>
/// <param name="ProposedAt">When they did. Null exactly when <paramref name="ProposedById"/> is.</param>
/// <param name="AcceptedAt">
/// When a proposal was accepted — but only if it was <em>this</em> thread's. Another buyer's accepted
/// trade leaves this null, which is how a thread that lost reads as "not the live trade".
/// </param>
/// <param name="BuyerConfirmedAt">Null until the buyer confirms receipt.</param>
/// <param name="SellerConfirmedAt">Null until the seller confirms payment.</param>
/// <remarks>
/// <para>
/// Facts, not conclusions. Whether a button should be shown depends on who is asking and on rules
/// that the server enforces anyway, so this carries the columns and lets the frontend derive the
/// answer — the same split <c>ConversationSummaryResponse.HasUnread</c> and <c>PeerId</c> already use.
/// A field called <c>CanConfirmReceipt</c> would be a second copy of the rule, free to disagree with
/// the server's.
/// </para>
/// <para>
/// Null on <see cref="ConversationDetailResponse.Trade"/> when the listing has been hard-deleted:
/// there is nothing left to trade, even though the thread survives.
/// </para>
/// <para>
/// A listing that sold through the platform is the reason this is not simply "the listing is
/// gone". Its seller can clear it away, but <c>ProductService.DeleteAsync</c> only marks the row
/// (<c>Product.DeletedAt</c>) instead of removing it, precisely so this panel and the whole
/// accept / confirm-receipt / confirm-payment history stays readable on both sides. Note that
/// <see cref="ConversationDetailResponse.ProductId"/> goes null in that case while this does not:
/// the listing's page is gone, its trade is not.
/// </para>
/// </remarks>
public sealed record ConversationTradeResponse(
    ProductStatus ProductStatus,
    long BuyerId,
    long? ProposedById,
    DateTime? ProposedAt,
    DateTime? AcceptedAt,
    DateTime? BuyerConfirmedAt,
    DateTime? SellerConfirmedAt);
