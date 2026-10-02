using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Products;

/// <summary>
/// A new listing. Always created as a draft — publishing is a separate call.
/// </summary>
/// <param name="Title">Listing title.</param>
/// <param name="Description">Free text. Optional; the column is MySQL <c>text</c>.</param>
/// <param name="Price">In yuan. The column is <c>decimal(10,2)</c>, so the upper bound is the column's.</param>
/// <param name="Condition">
/// 1..4, matching <see cref="Enums.ProductCondition"/>. Carried as a number rather than the enum
/// type so that an out-of-range value is a validation failure here instead of silently binding to
/// an undefined enum member — System.Text.Json accepts numeric values with no matching member.
/// </param>
/// <param name="CategoryId">Must be an existing active category.</param>
/// <param name="DormitoryAreaId">Must be an existing active dormitory area.</param>
/// <remarks>
/// Attributes are targeted at <c>param:</c>, not <c>property:</c> — MVC refuses to validate a
/// positional record whose metadata sits on the generated property, and throws instead of returning
/// a validation error.
/// </remarks>
public sealed record CreateProductRequest(
    [param: Required, StringLength(100, MinimumLength = 1)] string Title,
    [param: StringLength(2000)] string? Description,
    [param: Range(0, 99999999.99)] decimal Price,
    [param: Range(1, 4)] int Condition,
    [param: Range(1, long.MaxValue)] long CategoryId,
    [param: Range(1, long.MaxValue)] long DormitoryAreaId);

/// <summary>
/// An edit to an existing listing. Same shape as <see cref="CreateProductRequest"/> — every field is
/// replaceable, so a partial-update distinction would only invite a client to leave fields out and
/// wonder why they reset.
/// </summary>
/// <param name="Title">Listing title.</param>
/// <param name="Description">Free text. Optional.</param>
/// <param name="Price">In yuan.</param>
/// <param name="Condition">1..4, matching <see cref="Enums.ProductCondition"/>.</param>
/// <param name="CategoryId">Must be an existing active category.</param>
/// <param name="DormitoryAreaId">Must be an existing active dormitory area.</param>
public sealed record UpdateProductRequest(
    [param: Required, StringLength(100, MinimumLength = 1)] string Title,
    [param: StringLength(2000)] string? Description,
    [param: Range(0, 99999999.99)] decimal Price,
    [param: Range(1, 4)] int Condition,
    [param: Range(1, long.MaxValue)] long CategoryId,
    [param: Range(1, long.MaxValue)] long DormitoryAreaId);
