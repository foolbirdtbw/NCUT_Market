using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// Opens a thread about a listing — or returns the one that already exists. Idempotent, so a buyer
/// clicking "联系卖家" twice does not end up with two threads.
/// </summary>
/// <param name="ProductId">The listing to talk about. Must be one the caller can see and does not own.</param>
/// <remarks>
/// Attributes are targeted at <c>param:</c> rather than <c>property:</c> — MVC refuses to validate a
/// positional record whose metadata sits on the generated property, and throws instead of returning a
/// validation error. Messages are Chinese because they are shown verbatim under the form.
/// </remarks>
public sealed record StartConversationRequest(
    [param: Range(1, long.MaxValue, ErrorMessage = "请选择一个商品。")] long ProductId);
