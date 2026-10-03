namespace NCUT_Market.Core.DTOs.Announcements;

/// <summary>
/// A site announcement, as published.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Title">Headline, capped at 100 characters.</param>
/// <param name="Content">Body. Plain text; the frontend escapes it.</param>
/// <param name="PublishedAt">
/// Beijing time, no timezone suffix. Never null on this DTO — the list only returns announcements
/// that have already gone live, so the "not yet published" state is not representable here.
/// </param>
/// <param name="ExpiredAt">When it stops being shown, or null when it never does.</param>
public sealed record AnnouncementResponse(
    long Id,
    string Title,
    string Content,
    DateTime PublishedAt,
    DateTime? ExpiredAt);
