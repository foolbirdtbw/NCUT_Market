namespace NCUT_Market.Core.DTOs.DormitoryAreas;

/// <summary>
/// A dormitory area as the buyer-facing catalogue returns it.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Name">Display name, e.g. 1号楼.</param>
/// <param name="SortOrder">Hand-maintained ordering key. The list is already sorted by it.</param>
/// <param name="CreatedAt">Beijing time, filled by the audit stamp. No timezone suffix on the wire.</param>
/// <param name="UpdatedAt">Beijing time, filled by the audit stamp. No timezone suffix on the wire.</param>
/// <remarks>
/// No <c>status</c> member, for the same reason as <see cref="Categories.CategoryResponse"/>: the read
/// path only ever returns active rows.
/// </remarks>
public sealed record DormitoryAreaResponse(
    long Id,
    string Name,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);
