using System.ComponentModel.DataAnnotations;
using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Feedbacks;

/// <summary>
/// A new post on the feedback board. It goes up immediately — there is nothing to approve first.
/// </summary>
/// <param name="Content">Body text.</param>
/// <param name="Kind">
/// Bug report or feature request. Arrives as a number and the binder casts any integer to the enum,
/// so <see cref="System.ComponentModel.DataAnnotations"/> cannot reject a 7 here — the service does.
/// </param>
/// <param name="IsAnonymous">Keep the poster's nickname off the board.</param>
/// <remarks>
/// Attributes are targeted at <c>param:</c> rather than <c>property:</c> — see
/// <see cref="Announcements.CreateAnnouncementRequest"/>.
/// </remarks>
public sealed record CreateFeedbackRequest(
    [param: Required(ErrorMessage = "请写点内容。")]
    [param: StringLength(1000, MinimumLength = 1, ErrorMessage = "正文最多 1000 个字符。")]
    string Content,
    FeedbackKind Kind,
    bool IsAnonymous);
