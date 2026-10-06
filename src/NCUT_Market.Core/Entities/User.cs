using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

public sealed class User : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    /// <summary>Login name. Unique across the site.</summary>
    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public required string Nickname { get; set; }

    /// <summary>Object storage key for the avatar, not a URL. Null until the user uploads one.</summary>
    public string? AvatarKey { get; set; }

    /// <summary>Disabled accounts are kept, never deleted — see <see cref="UserStatus"/>.</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>
    /// What this account may do beyond the ordinary. Read from the row on each administrative
    /// request, never from the token — see <see cref="UserRole"/>.
    /// </summary>
    /// <remarks>
    /// The initialiser is load-bearing. <c>AuthService.RegisterAsync</c> builds a <see cref="User"/>
    /// in memory and hands it straight to the token issuer without reading it back, so without this
    /// a freshly registered account would report role 0 on the wire while the column default wrote 1.
    /// </remarks>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// Base64 SHA-256 of an outstanding password-reset code. Null when none is pending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code itself is never stored and never leaves the server twice: an administrator sees it once,
    /// in the response that minted it, and the row keeps only this digest. That is what makes "the
    /// administrator does not know the user's password" true after the reset rather than merely
    /// intended — and it means a lost code is replaced by minting a new one, not by reading this back.
    /// </para>
    /// <para>
    /// Hashing here is not the same trade-off as the absent email column. The code is a bearer
    /// credential: whoever holds it can take the account over, so a leaked row would be a leaked
    /// account. <see cref="PasswordResetExpiresAt"/> bounds how long that is worth anything.
    /// </para>
    /// </remarks>
    public string? PasswordResetCode { get; set; }

    /// <summary>
    /// When the outstanding reset code stops working, in Beijing wall-clock time like every other
    /// timestamp in this schema. Null whenever <see cref="PasswordResetCode"/> is null.
    /// </summary>
    public DateTime? PasswordResetExpiresAt { get; set; }

    /// <summary>
    /// When this account last made a request carrying its token. Null until it ever has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not an audit column and never stamped by <c>AppDbContext</c> — it is business data like
    /// <c>Product.LastActivityAt</c>, read by the footer's online count and written by exactly one
    /// caller, <c>OnlineService.TouchAsync</c>.
    /// </para>
    /// <para>
    /// That write goes through <c>ExecuteUpdateAsync</c>, which skips the change tracker and with it
    /// the audit rule. That is the point rather than an accident: a heartbeat that loaded the row and
    /// called SaveChanges would drag <c>UpdatedAt</c> along with it and turn an audit column into a
    /// second presence column.
    /// </para>
    /// </remarks>
    public DateTime? LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<Product> Products { get; } = [];

    public ICollection<Notification> Notifications { get; } = [];

    /// <summary>Threads this user opened, from someone else's listing.</summary>
    public ICollection<Conversation> BuyerConversations { get; } = [];

    /// <summary>Threads about this user's own listings.</summary>
    public ICollection<Conversation> SellerConversations { get; } = [];

    public ICollection<Message> Messages { get; } = [];
}
