namespace NCUT_Market.Core.DTOs.Categories;

/// <summary>
/// A category as the buyer-facing catalogue returns it — flat, not nested.
/// </summary>
/// <param name="Id">Primary key. Becomes <c>parentId</c> on the rows beneath this one.</param>
/// <param name="Name">Display name, e.g. 床上用品.</param>
/// <param name="ParentId">Null for a top-level category. Clients assemble the tree from this.</param>
/// <param name="SortOrder">Hand-maintained ordering key. The list is already sorted by it.</param>
/// <param name="CreatedAt">Beijing time, filled by the audit stamp. No timezone suffix on the wire.</param>
/// <param name="UpdatedAt">Beijing time, filled by the audit stamp. No timezone suffix on the wire.</param>
/// <remarks>
/// <para>
/// There is deliberately no <c>status</c> member: every read path filters to
/// <see cref="Enums.CategoryStatus.Active"/>, so the field would be a constant and would only invite
/// clients to branch on it. The admin surface, when it exists, gets its own DTO.
/// </para>
/// <para>
/// <c>createdAt</c> / <c>updatedAt</c> are exposed mainly so that a request can independently confirm
/// the audit stamp is working — they are not meant to drive UI.
/// </para>
/// </remarks>
public sealed record CategoryResponse(
    long Id,
    string Name,
    long? ParentId,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt);
