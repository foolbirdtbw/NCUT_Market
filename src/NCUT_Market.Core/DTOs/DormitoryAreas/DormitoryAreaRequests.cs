using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.DormitoryAreas;

/// <summary>
/// A new dormitory area. Administrator-only.
/// </summary>
/// <param name="Name">
/// Display name. Carries a unique index in the database, so a duplicate is refused rather than
/// merged.
/// </param>
/// <param name="SortOrder">Hand-maintained ordering key. Any integer; the list sorts by it.</param>
public sealed record CreateDormitoryAreaRequest(
    [param: Required(ErrorMessage = "请填宿舍区名。")]
    [param: StringLength(50, MinimumLength = 1, ErrorMessage = "宿舍区名最多 50 个字符。")]
    string Name,
    int SortOrder);

/// <summary>
/// A full replacement of an existing dormitory area. Same shape as
/// <see cref="CreateDormitoryAreaRequest"/>.
/// </summary>
/// <param name="Name">Display name. Must not collide with another area.</param>
/// <param name="SortOrder">Hand-maintained ordering key.</param>
public sealed record UpdateDormitoryAreaRequest(
    [param: Required(ErrorMessage = "请填宿舍区名。")]
    [param: StringLength(50, MinimumLength = 1, ErrorMessage = "宿舍区名最多 50 个字符。")]
    string Name,
    int SortOrder);
