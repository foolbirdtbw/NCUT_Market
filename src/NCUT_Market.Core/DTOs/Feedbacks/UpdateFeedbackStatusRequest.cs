using NCUT_Market.Core.Enums;

namespace NCUT_Market.Core.DTOs.Feedbacks;

/// <summary>
/// An administrator moving a post along.
/// </summary>
/// <param name="Status">Where it stands now. Checked against the enum's members by the service.</param>
/// <remarks>
/// This is the only field an administrator may change. The body stays as written — editing somebody
/// else's words to match the reply is how a board stops being worth reading.
/// </remarks>
public sealed record UpdateFeedbackStatusRequest(FeedbackStatus Status);
