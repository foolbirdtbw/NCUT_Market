using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.Entities;

/// <summary>
/// Site-wide bulletin. Distinct from a personal <see cref="Notification"/>, which always targets one user.
/// </summary>
public sealed class Announcement : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public AnnouncementStatus Status { get; set; } = AnnouncementStatus.Draft;

    public DateTime? PublishedAt { get; set; }

    public DateTime? ExpiredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
