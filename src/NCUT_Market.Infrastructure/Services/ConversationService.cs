using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Messages;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Storage;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class ConversationService(
    AppDbContext dbContext,
    IOptions<StorageOptions> storageOptions) : IConversationService
{
    /// <summary>How much of the most recent message a list row shows.</summary>
    private const int PreviewLength = 60;

    private readonly StorageOptions _storage = storageOptions.Value;

    public async Task<PagedResult<ConversationSummaryResponse>> ListAsync(
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Conversations
            .AsNoTracking()
            .Where(x => x.BuyerId == userId || x.SellerId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        // Order, page and only then project. Ordering the projected rows instead makes the provider
        // push the ORDER BY back through the correlated EXISTS subqueries the projection contains,
        // which it declines to translate. Ordering the entity query first keeps the sort on the
        // indexed columns where it belongs.
        //
        // Id breaks ties. Two threads sharing a millisecond would otherwise be free to swap places
        // between pages, which is how a row gets shown twice and another never.
        var page = query
            .OrderByDescending(x => x.LastMessageAt)
            .ThenByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize);

        var rows = await Summaries(page, userId).ToListAsync(cancellationToken);

        return pagination.ToResult(WithDisplayValues(rows), totalCount);
    }

    public async Task<OperationResult<UnreadCountResponse>> GetUnreadCountAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var count = await UnreadConversations(userId).CountAsync(cancellationToken);

        return OperationResult<UnreadCountResponse>.Success(new UnreadCountResponse(count));
    }

    public async Task<OperationResult<ConversationSummaryResponse>> StartAsync(
        long buyerId,
        StartConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        // The same visibility rule as ProductService.GetByIdAsync: a listing that is not live is
        // invisible to everyone but its seller, so a stranger guessing an id cannot confirm that
        // somebody's draft exists. The seller keeps access here only so that clicking "联系卖家" on
        // one's own listing produces the clear error below rather than a misleading 404.
        var product = await dbContext.Products
            .AsNoTracking()
            .Where(x => x.Id == request.ProductId
                && (x.Status == ProductStatus.Published
                    || x.Status == ProductStatus.Sold
                    || x.Status == ProductStatus.InTransaction
                    || x.SellerId == buyerId))
            .Select(x => new
            {
                x.Id,
                x.SellerId,
                x.Title,
                ThumbnailKey = x.Images
                    .OrderBy(image => image.SortOrder)
                    .Select(image => image.ThumbnailKey)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return OperationResult<ConversationSummaryResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个商品。");
        }

        if (product.SellerId == buyerId)
        {
            return OperationResult<ConversationSummaryResponse>.Failure(
                ErrorCodes.InvalidArgument,
                "这是你自己发布的商品，不用联系自己。");
        }

        var existingId = await dbContext.Conversations
            .AsNoTracking()
            .Where(x => x.ProductId == product.Id && x.BuyerId == buyerId)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingId is { } id)
        {
            // Find-or-create: a second click on "联系卖家" opens the thread that is already there
            // rather than starting a parallel one.
            var found = await Summaries(
                    dbContext.Conversations.AsNoTracking().Where(x => x.Id == id), buyerId)
                .FirstAsync(cancellationToken);

            return OperationResult<ConversationSummaryResponse>.Success(
                WithDisplayValues([found])[0]);
        }

        var now = AppDbContext.AuditNow;

        var conversation = new Conversation
        {
            ProductId = product.Id,
            ProductTitle = product.Title,
            ProductThumbnailKey = product.ThumbnailKey ?? string.Empty,
            BuyerId = buyerId,
            SellerId = product.SellerId,

            // All three read: the thread is empty, so nothing is unread for either side, and the
            // list has something to sort by. LastMessageAt is not nullable and is not stamped by
            // the context, so leaving it unset would fail the insert.
            LastMessageAt = now,
            BuyerLastReadAt = now,
            SellerLastReadAt = now
        };

        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);

        var created = await Summaries(
                dbContext.Conversations.AsNoTracking().Where(x => x.Id == conversation.Id), buyerId)
            .FirstAsync(cancellationToken);

        return OperationResult<ConversationSummaryResponse>.Success(WithDisplayValues([created])[0]);
    }

    public async Task<OperationResult<ConversationDetailResponse>> GetAsync(
        long id,
        long userId,
        PaginationQuery pagination,
        CancellationToken cancellationToken = default)
    {
        // Membership is part of the lookup, so a thread the caller is not in is indistinguishable
        // from one that does not exist.
        var header = await dbContext.Conversations
            .AsNoTracking()
            .Where(x => x.Id == id && (x.BuyerId == userId || x.SellerId == userId))
            .Select(x => new
            {
                x.Id,
                x.ProductId,
                x.ProductTitle,
                x.ProductThumbnailKey,
                x.BuyerId,
                x.TransactionProposedById,
                x.TransactionProposedAt,

                // Read raw and interpreted below rather than filtered here: the "is this thread the
                // trade" test compares two of these columns against each other, which reads better as
                // one line in C# than as a repeated conditional inside a projection.
                ProductStatus = x.Product == null ? (ProductStatus?)null : x.Product.Status,
                TradeBuyerId = x.Product == null ? (long?)null : x.Product.TransactionBuyerId,
                AcceptedAt = x.Product == null ? (DateTime?)null : x.Product.TransactionAcceptedAt,
                BuyerConfirmedAt = x.Product == null ? (DateTime?)null : x.Product.BuyerConfirmedAt,
                SellerConfirmedAt = x.Product == null ? (DateTime?)null : x.Product.SellerConfirmedAt,

                PeerId = x.BuyerId == userId ? x.SellerId : x.BuyerId,
                PeerNickname = x.BuyerId == userId ? x.Seller.Nickname : x.Buyer.Nickname
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (header is null)
        {
            return OperationResult<ConversationDetailResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个会话。");
        }

        var totalCount = await dbContext.Messages
            .CountAsync(x => x.ConversationId == id, cancellationToken);

        // Descending, so the page returned is the newest one; reversed below so the frontend can
        // render it top-to-bottom without reordering. See ConversationDetailResponse.
        var messages = await dbContext.Messages
            .AsNoTracking()
            .Where(x => x.ConversationId == id)
            .OrderByDescending(x => x.Id)
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Select(x => new MessageResponse(x.Id, x.SenderId, x.Sender.Nickname, x.Content, x.CreatedAt))
            .ToListAsync(cancellationToken);

        messages.Reverse();

        ConversationTradeResponse? trade = null;

        // Null once the listing has been hard-deleted. The thread survives that by design, and stays
        // readable, but there is nothing left in it to trade.
        if (header.ProductStatus is ProductStatus status)
        {
            // Only the thread the trade actually came from gets to see the confirmation columns. From
            // any other buyer's thread a taken listing looks the same as one the seller took down,
            // which is the truth from where they are standing: they have no trade, and there is
            // nothing for them to confirm. It is also what stops a stranger's thread leaking who won.
            var isTradeThread = header.TradeBuyerId == header.BuyerId;

            trade = new ConversationTradeResponse(
                status,
                header.BuyerId,
                header.TransactionProposedById,
                header.TransactionProposedAt,
                isTradeThread ? header.AcceptedAt : null,
                isTradeThread ? header.BuyerConfirmedAt : null,
                isTradeThread ? header.SellerConfirmedAt : null);
        }

        return OperationResult<ConversationDetailResponse>.Success(new ConversationDetailResponse(
            header.Id,
            header.ProductId,
            header.ProductTitle,
            ToUrl(header.ProductThumbnailKey),
            header.PeerId,
            header.PeerNickname,
            pagination.ToResult(messages, totalCount),
            trade));
    }

    public async Task<OperationResult<MessageResponse>> SendAsync(
        long id,
        long senderId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var content = request.Content.Trim();

        // The attribute caps the length; a string of spaces passes it and still says nothing.
        if (content.Length == 0)
        {
            return OperationResult<MessageResponse>.Failure(
                ErrorCodes.ValidationError,
                "请写点什么。");
        }

        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(
                x => x.Id == id && (x.BuyerId == senderId || x.SellerId == senderId),
                cancellationToken);

        if (conversation is null)
        {
            return OperationResult<MessageResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个会话。");
        }

        var nickname = await dbContext.Users
            .Where(x => x.Id == senderId)
            .Select(x => x.Nickname)
            .FirstAsync(cancellationToken);

        var now = AppDbContext.AuditNow;

        var message = new Message
        {
            ConversationId = conversation.Id,
            SenderId = senderId,
            Content = content,

            // Set by hand rather than left to the stamping pass, so that it and the conversation's
            // LastMessageAt below are the same instant. Writing a message must not move the thread
            // down its own list.
            CreatedAt = now
        };

        // This is what lifts the thread to the top of the recipient's list.
        conversation.LastMessageAt = now;

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<MessageResponse>.Success(
            new MessageResponse(message.Id, senderId, nickname, content, message.CreatedAt));
    }

    public async Task<OperationResult<bool>> MarkReadAsync(
        long id,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.Conversations
            .FirstOrDefaultAsync(
                x => x.Id == id && (x.BuyerId == userId || x.SellerId == userId),
                cancellationToken);

        if (conversation is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个会话。");
        }

        var now = AppDbContext.AuditNow;

        // Only the caller's own marker moves. Stamping both would mean reading a thread also marks
        // it read for the other side, which is the one thing this column pair exists to prevent.
        if (conversation.BuyerId == userId)
        {
            conversation.BuyerLastReadAt = now;
        }
        else
        {
            conversation.SellerLastReadAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// The threads holding at least one message the caller has not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two flat branches rather than one projection with a conditional inside it. Each arm compares a
    /// plain, non-nullable column, so the whole thing lowers to two correlated <c>EXISTS</c> subqueries
    /// joined by <c>OR</c> — a shape Pomelo translates without difficulty, and one that contains no
    /// <c>COALESCE</c> over a fallback to reason about.
    /// </para>
    /// <para>
    /// The "how far have I read" columns are non-nullable precisely to keep it that way. A nullable
    /// marker would need a fallback to the conversation's creation instant, and since the row and its
    /// first message are stamped from the same clock into a millisecond-resolution column, that
    /// fallback would compare equal and the first message of a thread would never count as unread.
    /// </para>
    /// <para>
    /// Timestamps here come from the database, both sides written by <see cref="AppDbContext.AuditNow"/>,
    /// so the comparison is between two Beijing wall-clock values and never touches the server's own
    /// (UTC) clock.
    /// </para>
    /// </remarks>
    private IQueryable<Conversation> UnreadConversations(long userId) =>
        dbContext.Conversations.Where(x =>
            (x.BuyerId == userId
                && x.Messages.Any(m => m.SenderId != userId && m.CreatedAt > x.BuyerLastReadAt))
            || (x.SellerId == userId
                && x.Messages.Any(m => m.SenderId != userId && m.CreatedAt > x.SellerLastReadAt)));

    /// <summary>
    /// The one list-row projection, shared by the list and by find-or-create so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// A method over <see cref="IQueryable{T}"/> rather than the static
    /// <c>Expression&lt;Func&lt;...&gt;&gt;</c> that <c>ProductService</c> uses, because this row
    /// depends on who is asking — the peer and the unread flag both flip with <paramref name="userId"/>.
    /// EF Core has no two-argument <c>Select</c>, so the caller's id is closed over here instead and
    /// the whole expression is still translated to SQL.
    /// </remarks>
    private IQueryable<ConversationSummaryResponse> Summaries(IQueryable<Conversation> query, long userId) =>
        query.Select(x => new ConversationSummaryResponse(
            x.Id,
            x.ProductId,

            // The frozen copy, not x.Product.Title: the listing may have been hard-deleted, and the
            // row has to keep reading correctly when Product is null.
            x.ProductTitle,
            x.ProductThumbnailKey,
            x.BuyerId == userId ? x.SellerId : x.BuyerId,
            x.BuyerId == userId ? x.Seller.Nickname : x.Buyer.Nickname,
            x.Messages
                .OrderByDescending(message => message.Id)
                .Select(message => message.Content)
                .FirstOrDefault(),
            x.LastMessageAt,
            (x.BuyerId == userId
                && x.Messages.Any(m => m.SenderId != userId && m.CreatedAt > x.BuyerLastReadAt))
            || (x.SellerId == userId
                && x.Messages.Any(m => m.SenderId != userId && m.CreatedAt > x.SellerLastReadAt))));

    /// <summary>
    /// Turns storage keys into URLs and trims the preview.
    /// </summary>
    /// <remarks>
    /// In memory, after the query, for the reason <c>ProductService.SummaryProjection</c> documents:
    /// concatenating the base path in SQL would rely on the provider translating string addition into
    /// <c>CONCAT</c>, and a page is at most a hundred rows, so doing it here costs nothing and cannot
    /// fail to translate.
    /// </remarks>
    private List<ConversationSummaryResponse> WithDisplayValues(
        IEnumerable<ConversationSummaryResponse> items) =>
        items.Select(item => item with
        {
            ProductThumbnailUrl = ToUrl(item.ProductThumbnailUrl),
            LastMessagePreview = Truncate(item.LastMessagePreview)
        }).ToList();

    /// <summary>Prefixes a storage key with the public path, or null when there is no key.</summary>
    private string? ToUrl(string? key) =>
        string.IsNullOrEmpty(key) ? null : _storage.PublicBasePath + "/" + key;

    /// <summary>Shortens a preview to fit a list row, marking that it was cut.</summary>
    private static string? Truncate(string? text) =>
        text is null || text.Length <= PreviewLength ? text : string.Concat(text.AsSpan(0, PreviewLength), "…");
}
