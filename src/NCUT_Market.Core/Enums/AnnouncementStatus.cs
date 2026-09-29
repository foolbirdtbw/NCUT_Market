namespace NCUT_Market.Core.Enums;

/// <summary>
/// Whether an announcement is visible is decided by <c>published_at</c> / <c>expired_at</c> at query
/// time, not by this column alone — V1 does not run a job to flip Published into Expired.
/// </summary>
public enum AnnouncementStatus : byte
{
    Draft = 1,
    Published = 2,
    Expired = 3
}
