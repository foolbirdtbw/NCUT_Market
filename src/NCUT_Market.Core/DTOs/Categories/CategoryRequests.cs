using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Categories;

/// <summary>
/// A new category. Administrator-only.
/// </summary>
/// <param name="Name">Display name. Must not collide with a sibling under the same parent.</param>
/// <param name="ParentId">Null for a top-level category; otherwise an existing active category.</param>
/// <param name="SortOrder">Hand-maintained ordering key. Any integer; the list sorts by it.</param>
/// <remarks>
/// Attributes are targeted at <c>param:</c>, not <c>property:</c> — MVC refuses to validate a
/// positional record whose metadata sits on the generated property, and throws instead of returning a
/// validation error. See the note on <c>CreateProductRequest</c> for the long version.
/// </remarks>
public sealed record CreateCategoryRequest(
    [param: Required(ErrorMessage = "请填分类名。")]
    [param: StringLength(50, MinimumLength = 1, ErrorMessage = "分类名最多 50 个字符。")]
    string Name,
    long? ParentId,
    int SortOrder);

/// <summary>
/// A full replacement of an existing category. Same shape as
/// <see cref="CreateCategoryRequest"/> — every field is replaceable.
/// </summary>
/// <param name="Name">Display name. Must not collide with a sibling under the same parent.</param>
/// <param name="ParentId">Null to make this a top-level category; otherwise an existing active one.</param>
/// <param name="SortOrder">Hand-maintained ordering key.</param>
public sealed record UpdateCategoryRequest(
    [param: Required(ErrorMessage = "请填分类名。")]
    [param: StringLength(50, MinimumLength = 1, ErrorMessage = "分类名最多 50 个字符。")]
    string Name,
    long? ParentId,
    int SortOrder);
