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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<Product> Products { get; } = [];

    public ICollection<Notification> Notifications { get; } = [];
}
