using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NCUT_Market.Core.Common;
using NCUT_Market.Core.DTOs.Auth;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Enums;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Security;

namespace NCUT_Market.Infrastructure.Services;

internal sealed class AuthService(
    AppDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IOptions<JwtOptions> jwtOptions) : IAuthService
{
    /// <summary>
    /// What every way of failing a reset says, verbatim. Nothing about the account leaks through it.
    /// </summary>
    private const string BadResetCodeMessage = "重置码不对，或者已经过期了。";

    /// <summary>
    /// What a closed account is called from then on, everywhere it is displayed.
    /// </summary>
    private const string DeletedNickname = "已注销用户";

    public async Task<OperationResult<AuthResponse>> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username.Trim();

        // Checked before insert so the caller gets a clean 409 rather than a duplicate-key
        // exception. The unique index is still the real guarantee — two simultaneous registrations
        // of the same name both pass this check, and one of them then fails on insert.
        var taken = await dbContext.Users
            .AnyAsync(x => x.Username == username, cancellationToken);

        if (taken)
        {
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.Conflict,
                $"用户名「{username}」已经被注册了。");
        }

        var studentId = request.StudentId.Trim();

        // Same shape and the same caveat as the check above: a courtesy that turns the common case into
        // a readable 409, with uk_users_student_id as the real guarantee.
        var studentIdTaken = await dbContext.Users
            .AnyAsync(x => x.StudentId == studentId, cancellationToken);

        if (studentIdTaken)
        {
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.Conflict,
                $"学号「{studentId}」已经被注册了。");
        }

        var user = new User
        {
            Username = username,
            Nickname = request.Nickname.Trim(),
            StudentId = studentId,
            PasswordHash = string.Empty
        };

        // The hasher needs the user instance (it salts per user and may fold in a security stamp),
        // so it can only run once the object exists — hence the empty hash above.
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AuthResponse>.Success(IssueToken(user));
    }

    public async Task<OperationResult<AuthResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username.Trim();

        var user = await dbContext.Users
            .FirstOrDefaultAsync(x => x.Username == username, cancellationToken);

        if (user is null)
        {
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.InvalidCredentials,
                "用户名或密码不对。");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            // The same code and message as an unknown username, deliberately. Distinguishing them
            // would turn this endpoint into a way to enumerate who has an account.
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.InvalidCredentials,
                "用户名或密码不对。");
        }

        // A disabled account is checked after the password, not before: answering "this account is
        // disabled" to someone who guessed a username would leak that the account exists.
        if (user.Status != UserStatus.Active)
        {
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.Forbidden,
                "这个账号已经被停用了。");
        }

        // VerifyHashedPassword reports SuccessRehashNeeded when the stored hash was produced with an
        // older iteration count. Rehashing on the way through upgrades the record for free, and this
        // is the only moment the plain password is available to do it with.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return OperationResult<AuthResponse>.Success(IssueToken(user));
    }

    public async Task<OperationResult<AuthResponse>> CompleteResetAsync(
        CompleteResetRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username.Trim();

        var user = await dbContext.Users
            .FirstOrDefaultAsync(x => x.Username == username, cancellationToken);

        var code = ResetCode.Normalize(request.ResetCode);

        if (user is null || !HasUsableResetCode(user, code))
        {
            // One answer for four different problems: no such user, nothing outstanding, the wrong code,
            // and a code past its deadline. Same reasoning as the login path above, one step further —
            // telling them apart would say not only which usernames exist but which of them are
            // mid-recovery, and the second is worse than the first.
            return OperationResult<AuthResponse>.Failure(ErrorCodes.InvalidCredentials, BadResetCodeMessage);
        }

        // After the code, not before, for the same reason LoginAsync checks status after the password:
        // answering "this account is disabled" to someone who has not proved they hold the code would
        // confirm the account exists.
        if (user.Status != UserStatus.Active)
        {
            return OperationResult<AuthResponse>.Failure(
                ErrorCodes.Forbidden,
                "这个账号已经被停用了。");
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);

        // Burned on use, and cleared as a pair: a row holding a digest with no deadline is one that
        // never expires, which is the invariant ResetCode.Matches is written to assume.
        user.PasswordResetCode = null;
        user.PasswordResetExpiresAt = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<AuthResponse>.Success(IssueToken(user));
    }

    public async Task<OperationResult<CurrentUserResponse>> GetByIdAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId && x.Status == UserStatus.Active)
            .Select(x => new CurrentUserResponse(x.Id, x.Username, x.Nickname, x.Role))
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? OperationResult<CurrentUserResponse>.Failure(
                ErrorCodes.NotFound,
                "找不到这个用户。")
            : OperationResult<CurrentUserResponse>.Success(user);
    }

    public async Task<OperationResult<bool>> DeleteAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // By id alone, with no status filter. A token issued before the account was closed keeps
        // working on ordinary member endpoints for the rest of its week, so this method is reachable a
        // second time; filtering on Active would turn that into a 404 on a row that is still there.
        var user = await dbContext.Users
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null)
        {
            return OperationResult<bool>.Failure(ErrorCodes.NotFound, "找不到这个用户。");
        }

        user.Status = UserStatus.Disabled;

        // Nulled rather than scrambled: MySQL treats NULLs as distinct in a unique index, so this is
        // what leaves the number free for the same person to register with again.
        user.StudentId = null;

        user.Username = await ReserveDeletedUsernameAsync(user.Id, cancellationToken);

        // This single line is what anonymizes the rest of the site. Every public surface renders the
        // nickname and never the username — listings, threads, messages, feedback — so they all start
        // reading 已注销用户 at once.
        user.Nickname = DeletedNickname;
        user.AvatarKey = null;

        // Cleared as a pair, and not because a disabled account could redeem one — CompleteResetAsync
        // refuses those. It is because an administrator flipping Status back to Active would otherwise
        // resurrect a code that still works.
        user.PasswordResetCode = null;
        user.PasswordResetExpiresAt = null;

        // A value nobody holds, hashed through the same hasher a real password goes through. This is
        // what makes the deletion final: an administrator can put Status back, but there is no password
        // to sign in with and no code to mint one from, so the account cannot be handed back.
        user.PasswordHash = passwordHasher.HashPassword(user, Guid.NewGuid().ToString("N"));

        // LastSeenAt is deliberately left alone. The heartbeat middleware in Program.cs has already
        // stamped it to now on this very request, and OnlineService counts only Active accounts, so the
        // row has stopped counting towards the online figure regardless.

        await OfflinePublishedAsync(user.Id, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Picks a replacement username no other account holds.
    /// </summary>
    /// <remarks>
    /// The row has to keep a non-null, unique username, so the original cannot simply be blanked out —
    /// but registration validates only the length, which means somebody may already hold the literal
    /// <c>deleted_7</c>. Colliding there would come back as a 409 from the unique index, and an
    /// irreversible action is the worst place to fail, so the name is checked first exactly the way
    /// <see cref="RegisterAsync"/> checks before inserting. The self-exclusion is load-bearing: without
    /// it, a second delete would read the row's own replacement as a collision and append a suffix
    /// again, and again.
    /// </remarks>
    private async Task<string> ReserveDeletedUsernameAsync(long userId, CancellationToken cancellationToken)
    {
        var candidate = $"deleted_{userId}";

        var taken = await dbContext.Users
            .AnyAsync(x => x.Id != userId && x.Username == candidate, cancellationToken);

        return taken ? $"{candidate}_{Random.Shared.Next(0, 1_000_000):D6}" : candidate;
    }

    /// <summary>
    /// Takes the account's live listings down and leaves every other status alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loaded and mutated through the change tracker rather than with a bulk UPDATE. Deletion is not a
    /// hot path, and this way the audit stamp and the concurrency token are applied by the same rule as
    /// every other write in the codebase — which is the whole point of the bulk-update rule that
    /// <c>AppDbContext</c> states and <c>OnlineService</c> exists to satisfy.
    /// </para>
    /// <para>
    /// <see cref="ProductStatus.InTransaction"/> is left alone on purpose. Every phase of the trade
    /// sweep filters on that status and <c>ConfirmOnceAsync</c> refuses anything else, so forcing one to
    /// Offline would strand <c>TransactionBuyerId</c> on a row no sweep will ever look at again and take
    /// the confirm buttons away from both parties. Left as it is, the day-long sweep settles or rolls it
    /// back by itself.
    /// </para>
    /// <para>
    /// This skips the "somebody is negotiating with you" guard in <c>ProductService.OfflineAsync</c>,
    /// deliberately. That guard stops a live seller pulling a listing out from under a negotiation, and
    /// a closed account cannot negotiate. Any proposal left hanging is cleared by
    /// <c>ExpireProposalsAsync</c> a day later.
    /// </para>
    /// <para>
    /// Drafts are left as drafts. There is no cleanup job for them — the scan
    /// <c>idx_products_status_last_activity_at</c> was added to drive was never written — so a closed
    /// account's drafts stay in the table. They are visible to nobody but their former owner, who can no
    /// longer sign in, so leaving them costs nothing today.
    /// </para>
    /// </remarks>
    private async Task OfflinePublishedAsync(long userId, CancellationToken cancellationToken)
    {
        var listings = await dbContext.Products
            .Where(x => x.SellerId == userId && x.Status == ProductStatus.Published)
            .ToListAsync(cancellationToken);

        foreach (var listing in listings)
        {
            listing.Status = ProductStatus.Offline;
            listing.LastActivityAt = AppDbContext.AuditNow;
        }
    }

    /// <summary>
    /// Whether a row carries a reset code that is the one being presented and has not lapsed.
    /// </summary>
    /// <remarks>
    /// The deadline is read from <see cref="AppDbContext.AuditNow"/> rather than
    /// <see cref="DateTime.UtcNow"/> because that is the clock <c>IUserService.IssueResetCodeAsync</c>
    /// wrote it with. Comparing across the two would put every code eight hours out — in the direction
    /// that makes them all still valid, since Beijing time runs ahead.
    /// </remarks>
    private static bool HasUsableResetCode(User user, string code) =>
        user.PasswordResetExpiresAt is DateTime expiresAt
        && expiresAt >= AppDbContext.AuditNow
        && ResetCode.Matches(code, user.PasswordResetCode);

    /// <summary>
    /// Signs a token for a user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token's own expiry is computed from <see cref="DateTime.UtcNow"/>, while the expiry
    /// reported to the client is converted to Beijing wall-clock time. This is not an inconsistency
    /// to tidy up: the <c>exp</c> claim is defined as seconds since the Unix epoch in UTC, and
    /// <see cref="JwtSecurityToken"/> converts an <c>Unspecified</c> DateTime by assuming it is
    /// already UTC. Handing it <c>AuditNow</c> — which carries Beijing wall-clock in an
    /// <c>Unspecified</c> DateTime — would set the claim eight hours later than intended, so every
    /// token would outlive its stated lifetime by eight hours.
    /// </para>
    /// <para>
    /// The reported <see cref="AuthResponse.ExpiresAt"/> is Beijing time because every other
    /// timestamp the API emits is, and the frontend's formatter assumes it.
    /// </para>
    /// </remarks>
    private AuthResponse IssueToken(User user)
    {
        var settings = jwtOptions.Value;

        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddMinutes(settings.ExpireMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: issuedAtUtc,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var expiresAtBeijing = DateTime.SpecifyKind(
            expiresAtUtc.AddHours(8),
            DateTimeKind.Unspecified);

        return new AuthResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAtBeijing,
            new CurrentUserResponse(user.Id, user.Username, user.Nickname, user.Role));
    }
}
