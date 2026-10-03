using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Announcements;

/// <summary>
/// A new announcement. Publishing is immediate — there is no draft state via the API.
/// </summary>
/// <param name="Title">Headline.</param>
/// <param name="Content">Body text.</param>
/// <param name="ExpiredAt">
/// When to stop showing it, or null to show it indefinitely. Must be in the future, which
/// <see cref="System.ComponentModel.DataAnnotations"/> cannot express — the service checks it.
/// </param>
/// <remarks>
/// <para>
/// Attributes are targeted at <c>param:</c> rather than <c>property:</c> — see
/// <see cref="Messages.StartConversationRequest"/>.
/// </para>
/// <para>
/// <paramref name="ExpiredAt"/> arrives from <c>&lt;input type="datetime-local"&gt;</c> as a
/// timezone-less string such as <c>2026-10-05T12:00</c>, which is exactly the convention every
/// timestamp column in this database uses (Beijing wall-clock, no offset). No conversion is applied
/// in either direction.
/// </para>
/// </remarks>
public sealed record CreateAnnouncementRequest(
    [param: Required(ErrorMessage = "请填标题。")]
    [param: StringLength(100, MinimumLength = 1, ErrorMessage = "标题最多 100 个字符。")]
    string Title,
    [param: Required(ErrorMessage = "请填正文。")]
    [param: StringLength(5000, MinimumLength = 1, ErrorMessage = "正文最多 5000 个字符。")]
    string Content,
    DateTime? ExpiredAt);
