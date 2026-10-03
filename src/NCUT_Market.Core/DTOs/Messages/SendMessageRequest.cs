using System.ComponentModel.DataAnnotations;

namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// A message to post into a thread.
/// </summary>
/// <param name="Content">The text. Trimmed by the service, which also rejects whitespace-only.</param>
/// <remarks>
/// Attributes are targeted at <c>param:</c> rather than <c>property:</c> — see
/// <see cref="StartConversationRequest"/>. The 500 matches the column and
/// <c>Notification.Content</c>'s existing cap.
/// </remarks>
public sealed record SendMessageRequest(
    [param: Required(ErrorMessage = "请写点什么。")]
    [param: StringLength(500, MinimumLength = 1, ErrorMessage = "一条消息最多 500 个字符。")]
    string Content);
