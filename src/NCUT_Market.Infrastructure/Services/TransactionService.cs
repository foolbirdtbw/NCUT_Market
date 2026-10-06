using Microsoft.EntityFrameworkCore;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class TransactionService(AppDbContext dbContext) : ITransactionService
{
    /// <summary>How long a proposal waits for an answer before the sweep drops it.</summary>
    private static readonly TimeSpan ProposalLifetime = TimeSpan.FromDays(1);

    /// <summary>How long a trade has to collect both confirmations before the sweep settles it.</summary>
    private static readonly TimeSpan TransactionLifetime = TimeSpan.FromDays(1);

    /// <summary>How many rows one sweep phase will touch in a single round.</summary>
    /// <remarks>
    /// A bound rather than a page loop. There is nothing here that grows without limit on a campus
    /// marketplace, and the next tick is fifteen minutes away — a round that hits the cap simply
    /// finishes the job then.
    /// </remarks>
    private const int SweepBatchSize = 200;

    private const string StaleMessage = "商品状态刚刚变了，请刷新重试。";

    public async Task<OperationResult<bool>> ProposeAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var (context, failure) = await LoadAsync(conversationId, userId, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var (conversation, product) = context;

        // The listing has to be on sale. This one check is also what stops a second proposal going out
        // once somebody else's offer has been accepted, and what stops one going out on a listing the
        // seller has since taken down.
        if (product.Status != ProductStatus.Published)
        {
            return OperationResult<bool>.Failure(ErrorCodes.InvalidState, "这个商品现在不在售，不能发起交易。");
        }

        if (conversation.TransactionProposedAt is not null)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "这个会话里已经有一个待接受的交易了。");
        }

        conversation.TransactionProposedById = userId;
        conversation.TransactionProposedAt = AppDbContext.AuditNow;

        Notify(
            PeerOf(conversation, userId),
            product.Id,
            NotificationType.TransactionConfirmationNeeded,
            "有人想买你的东西",
            $"「{conversation.ProductTitle}」有人发起了交易，去私信里确认一下吧。");

        return await SaveAsync(cancellationToken);
    }

    public async Task<OperationResult<bool>> AcceptAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var (context, failure) = await LoadAsync(conversationId, userId, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var (conversation, product) = context;

        if (conversation.TransactionProposedAt is not DateTime proposedAt)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "这个会话里没有待接受的交易。");
        }

        if (conversation.TransactionProposedById == userId)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "不能接受自己发起的交易，等对方确认。");
        }

        var now = AppDbContext.AuditNow;

        // The deadline is enforced here as well as by the sweep, which runs every fifteen minutes.
        // Relying on the sweep alone would let a stale proposal be accepted for a quarter of an hour
        // after it should have gone, claiming a listing the sweep is about to free up.
        if (proposedAt < now - ProposalLifetime)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "这个交易提议已经过期了，请重新发起。");
        }

        if (product.Status != ProductStatus.Published)
        {
            return OperationResult<bool>.Failure(ErrorCodes.InvalidState, "这个商品已经不在售了。");
        }

        product.Status = ProductStatus.InTransaction;
        product.TransactionBuyerId = conversation.BuyerId;
        product.TransactionAcceptedAt = now;

        // This thread's proposal has become the trade, so the "pending" marker goes — that keeps
        // `TransactionProposedAt != null` meaning exactly "waiting for an answer" everywhere.
        conversation.TransactionProposedById = null;
        conversation.TransactionProposedAt = null;

        // Everybody else's proposal is dropped. They are not merely invalidated by the listing's new
        // status: a proposal is addressed by thread, and leaving one behind offers a button whose
        // only possible answer is a refusal.
        var superseded = await dbContext.Conversations
            .Where(x => x.ProductId == product.Id
                && x.Id != conversation.Id
                && x.TransactionProposedAt != null)
            .ToListAsync(cancellationToken);

        foreach (var other in superseded)
        {
            other.TransactionProposedById = null;
            other.TransactionProposedAt = null;
        }

        var content = $"「{conversation.ProductTitle}」的交易已经达成，双方各确认一次就完成了。";

        Notify(conversation.BuyerId, product.Id, NotificationType.TransactionAccepted, "交易已开始", content);
        Notify(conversation.SellerId, product.Id, NotificationType.TransactionAccepted, "交易已开始", content);

        return await SaveAsync(cancellationToken);
    }

    public Task<OperationResult<bool>> ConfirmReceiptAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default) =>
        ConfirmAsync(conversationId, userId, buyer: true, cancellationToken);

    public Task<OperationResult<bool>> ConfirmPaymentAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken = default) =>
        ConfirmAsync(conversationId, userId, buyer: false, cancellationToken);

    public async Task<int> SweepAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var changed = await ExpireProposalsAsync(now, cancellationToken);

        changed += await SettleStalledTradesAsync(now, cancellationToken);
        changed += await RollBackAbandonedTradesAsync(now, cancellationToken);

        return changed;
    }

    /// <summary>
    /// One side's confirmation, with a single retry for the case where the other side confirmed at the
    /// same instant.
    /// </summary>
    /// <remarks>
    /// Both confirmations are read-modify-write on the same row, so a genuine race loses one of them
    /// to the concurrency token. The loser's retry re-reads the row, finds the other side's
    /// confirmation already sitting there, and completes the trade itself. Without the retry both
    /// writes would appear to have failed, or — worse, under a scheme with no token at all — both
    /// would land with neither side noticing the other, leaving a fully confirmed trade stuck at
    /// "交易中" forever.
    /// </remarks>
    private async Task<OperationResult<bool>> ConfirmAsync(
        long conversationId,
        long userId,
        bool buyer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConfirmOnceAsync(conversationId, userId, buyer, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Discards the pending notification rows along with everything else. They were not
            // inserted — SaveChanges is one transaction — so keeping them tracked would write them
            // twice on the retry.
            dbContext.ChangeTracker.Clear();
        }

        try
        {
            return await ConfirmOnceAsync(conversationId, userId, buyer, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Twice in a row means something other than a single racing click is moving this listing.
            // Give up rather than spin: every transition here is idempotent, so the caller re-issuing
            // the request is a perfectly good recovery.
            dbContext.ChangeTracker.Clear();

            return OperationResult<bool>.Failure(ErrorCodes.InvalidState, StaleMessage);
        }
    }

    private async Task<OperationResult<bool>> ConfirmOnceAsync(
        long conversationId,
        long userId,
        bool buyer,
        CancellationToken cancellationToken)
    {
        var (context, failure) = await LoadAsync(conversationId, userId, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        var (conversation, product) = context;

        var callerIsBuyer = conversation.BuyerId == userId;

        if (callerIsBuyer != buyer)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.Forbidden,
                buyer ? "只有买方能确认收货。" : "只有卖方能确认收款。");
        }

        // The trade belongs to the listing, but the action arrives addressed by thread. The seller is a
        // participant in every thread about their own listing, so without this they could confirm from
        // any unrelated one — and the columns are read off whichever thread the caller happened to be
        // standing in.
        if (product.TransactionBuyerId != conversation.BuyerId)
        {
            return OperationResult<bool>.Failure(
                ErrorCodes.InvalidState,
                "这笔交易不是从这个会话发起的。");
        }

        // Already finished, most likely because the other side confirmed a moment ago and the retry
        // above is what noticed. Nothing left to do, and reporting success keeps a repeat click and a
        // reload harmless.
        if (product.Status == ProductStatus.Sold)
        {
            return OperationResult<bool>.Success(true);
        }

        if (product.Status != ProductStatus.InTransaction)
        {
            return OperationResult<bool>.Failure(ErrorCodes.InvalidState, "这笔交易已经不在进行中了。");
        }

        if (buyer ? product.BuyerConfirmedAt is not null : product.SellerConfirmedAt is not null)
        {
            return OperationResult<bool>.Success(true);
        }

        var now = AppDbContext.AuditNow;

        if (buyer)
        {
            product.BuyerConfirmedAt = now;
        }
        else
        {
            product.SellerConfirmedAt = now;
        }

        if (product.BuyerConfirmedAt is not null && product.SellerConfirmedAt is not null)
        {
            product.Status = ProductStatus.Sold;
            product.SoldAt = now;

            var done = $"「{conversation.ProductTitle}」的交易已经完成。";

            Notify(conversation.BuyerId, product.Id, NotificationType.ProductSold, "交易已完成", done);
            Notify(conversation.SellerId, product.Id, NotificationType.ProductSold, "交易已完成", done);
        }
        else
        {
            // Only reaches the other side, and only when this side's confirmation is what just
            // happened — which the guard above guarantees on the retry path too, so a retried
            // confirmation cannot send this twice.
            Notify(
                PeerOf(conversation, userId),
                product.Id,
                NotificationType.TransactionConfirmationNeeded,
                "等待你确认",
                buyer
                    ? $"买方已经确认收货了，请你去「{conversation.ProductTitle}」的私信里确认收款。"
                    : $"卖方已经确认收款了，请你去「{conversation.ProductTitle}」的私信里确认收货。");
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>Phase one: proposals nobody answered inside their day.</summary>
    private async Task<int> ExpireProposalsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - ProposalLifetime;

        // Both proposal columns are tested, not just the clock. They are written and cleared as a
        // pair, so the second term never excludes a real row — it makes the invariant a precondition
        // of the query instead of an assertion the loop would have to make on the way past.
        var expired = await dbContext.Conversations
            .Where(x => x.TransactionProposedAt != null
                && x.TransactionProposedById != null
                && x.TransactionProposedAt < cutoff)
            .Take(SweepBatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var conversation in expired)
        {
            Notify(
                conversation.TransactionProposedById!.Value,
                conversation.ProductId,
                NotificationType.TransactionCancelled,
                "交易提议已过期",
                $"「{conversation.ProductTitle}」的交易提议一天内没有回应，已经自动取消了。");

            conversation.TransactionProposedById = null;
            conversation.TransactionProposedAt = null;
        }

        return await SaveSweepBatchAsync(expired.Count, cancellationToken);
    }

    /// <summary>
    /// Phase two: a trade that ran out its day with at least one side having confirmed.
    /// </summary>
    /// <remarks>
    /// The silent side is taken to have agreed, which is what makes the deadline mean anything — one
    /// confirmation plus a day of silence closes the trade rather than freezing it. The
    /// <c>??=</c> pair also covers the both-confirmed case, where the two writes raced and each side
    /// read the other's column as still null, so neither flipped the status.
    /// </remarks>
    private async Task<int> SettleStalledTradesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - TransactionLifetime;

        var stalled = await dbContext.Products
            .Where(x => x.Status == ProductStatus.InTransaction
                && x.TransactionAcceptedAt != null
                && x.TransactionAcceptedAt < cutoff
                && (x.BuyerConfirmedAt != null || x.SellerConfirmedAt != null))
            .Take(SweepBatchSize)
            .ToListAsync(cancellationToken);

        if (stalled.Count == 0)
        {
            return 0;
        }

        foreach (var product in stalled)
        {
            product.BuyerConfirmedAt ??= now;
            product.SellerConfirmedAt ??= now;

            product.Status = ProductStatus.Sold;
            product.SoldAt = now;

            NotifyBoth(
                product,
                product.TransactionBuyerId,
                NotificationType.ProductSold,
                "交易已完成",
                $"「{product.Title}」的交易在一天内没有双方确认，已自动确认完成。");
        }

        return await SaveSweepBatchAsync(stalled.Count, cancellationToken);
    }

    /// <summary>Phase three: a trade nobody confirmed at all, which goes back on sale.</summary>
    private async Task<int> RollBackAbandonedTradesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - TransactionLifetime;

        var abandoned = await dbContext.Products
            .Where(x => x.Status == ProductStatus.InTransaction
                && x.TransactionAcceptedAt != null
                && x.TransactionAcceptedAt < cutoff
                && x.BuyerConfirmedAt == null
                && x.SellerConfirmedAt == null)
            .Take(SweepBatchSize)
            .ToListAsync(cancellationToken);

        if (abandoned.Count == 0)
        {
            return 0;
        }

        foreach (var product in abandoned)
        {
            // Read before the columns below are cleared. The buyer id is the notification's second
            // recipient, and it is one of the columns this phase exists to null out.
            var buyerId = product.TransactionBuyerId;

            // Every transaction column, not just the status. Clearing the status alone would leave a
            // row that is Published with an AcceptedAt still set — invisible to phase two, which
            // filters on InTransaction, and to phase one, which does not look at products at all. A
            // state no transition can leave.
            product.Status = ProductStatus.Published;
            product.TransactionBuyerId = null;
            product.TransactionAcceptedAt = null;
            product.BuyerConfirmedAt = null;
            product.SellerConfirmedAt = null;

            NotifyBoth(
                product,
                buyerId,
                NotificationType.TransactionCancelled,
                "交易已取消",
                $"「{product.Title}」的交易双方都没有确认，已经自动恢复为在售。");
        }

        return await SaveSweepBatchAsync(abandoned.Count, cancellationToken);
    }

    /// <summary>
    /// Commits one sweep phase, absorbing a lost race.
    /// </summary>
    /// <returns>The rows changed, or zero when the batch was abandoned.</returns>
    /// <remarks>
    /// A user acting on one of these listings between the read and the write rejects the whole batch:
    /// the concurrency token fires on that row and EF rolls the statement back. Reporting zero rather
    /// than a partial count is honest, and the next tick re-runs the phase from scratch — every
    /// predicate here is re-evaluated against current data, so nothing is lost by waiting fifteen
    /// minutes on a deadline measured in days.
    /// </remarks>
    private async Task<int> SaveSweepBatchAsync(int changed, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return changed;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();

            return 0;
        }
    }

    /// <summary>
    /// Loads the thread and the listing it is about, with the caller's membership already checked.
    /// </summary>
    /// <returns>
    /// The pair, or the failure to return to the caller. The two-step shape is what lets the four
    /// actions share one membership rule — and, more importantly, one that cannot be forgotten.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Both entities are tracked, not <c>AsNoTracking</c>. Every action either writes one of them or
    /// depends on the listing's concurrency token appearing in the generated <c>WHERE</c>.
    /// </para>
    /// <para>
    /// The context half of the tuple is non-nullable and carries <c>default</c> on the failure paths,
    /// so callers that have already returned on a non-null failure can destructure it directly. A
    /// nullable struct here would only buy three <c>.Value</c> calls.
    /// </para>
    /// </remarks>
    private async Task<(ThreadContext Context, OperationResult<bool>? Failure)> LoadAsync(
        long conversationId,
        long userId,
        CancellationToken cancellationToken)
    {
        // Membership is part of the lookup, so a thread the caller is not in is indistinguishable from
        // one that does not exist. Same rule as ConversationService.
        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(
                x => x.Id == conversationId && (x.BuyerId == userId || x.SellerId == userId),
                cancellationToken);

        if (conversation is null)
        {
            return (default, OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个会话。"));
        }

        // The column is SET NULL when the listing is hard-deleted. The thread survives that, and so
        // does it stay readable — but there is nothing left to trade in it.
        if (conversation.ProductId is not long productId)
        {
            return (default, OperationResult<bool>.Failure(ErrorCodes.NotFound, "这个商品已经被删除了。"));
        }

        var product = await dbContext.Products
            .FirstOrDefaultAsync(x => x.Id == productId, cancellationToken);

        if (product is null)
        {
            return (default, OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个商品。"));
        }

        return (new ThreadContext(conversation, product), null);
    }

    private async Task<OperationResult<bool>> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return OperationResult<bool>.Success(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else moved the listing between the read and the write — the other buyer's
            // acceptance landing first, or the seller taking it down. The notification queued
            // alongside is discarded too, which is the point: a notice about a trade that did not
            // happen would be a lie.
            dbContext.ChangeTracker.Clear();

            return OperationResult<bool>.Failure(ErrorCodes.InvalidState, StaleMessage);
        }
    }

    /// <summary>The other participant of a thread, given one of them.</summary>
    private static long PeerOf(Conversation conversation, long userId) =>
        conversation.BuyerId == userId ? conversation.SellerId : conversation.BuyerId;

    /// <summary>Queues the same notification for both parties of a trade.</summary>
    /// <param name="product">The listing, for the title and the "查看商品" link.</param>
    /// <param name="buyerId">
    /// The trade's buyer, or null if there is none to tell. Passed in rather than read off
    /// <paramref name="product"/> because the sweep's rollback clears <c>TransactionBuyerId</c> and
    /// notifies as one transition — a version that read the column here would look correct at every
    /// call site that happened to write in the right order.
    /// </param>
    private void NotifyBoth(
        Product product,
        long? buyerId,
        NotificationType type,
        string title,
        string content)
    {
        Notify(product.SellerId, product.Id, type, title, content);

        if (buyerId is long id)
        {
            Notify(id, product.Id, type, title, content);
        }
    }

    /// <summary>
    /// Queues a notification on the change tracker.
    /// </summary>
    /// <remarks>
    /// Never saves. Every caller commits it in the same <c>SaveChangesAsync</c> as the state change it
    /// describes, so the two are one transaction — no explicit <c>BeginTransaction</c>, and no window
    /// in which a notification exists for something that did not happen.
    /// <c>CreatedAt</c> is stamped by the context's usual audit pass.
    /// </remarks>
    private void Notify(
        long userId,
        long? productId,
        NotificationType type,
        string title,
        string content)
    {
        dbContext.Notifications.Add(new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Content = content,
            RelatedProductId = productId
        });
    }

    private readonly record struct ThreadContext(Conversation Conversation, Product Product);
}
