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
