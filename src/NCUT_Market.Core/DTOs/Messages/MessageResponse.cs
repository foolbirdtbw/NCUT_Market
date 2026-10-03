namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// One message in a thread.
/// </summary>
/// <param name="Id">Primary key. Messages are ordered by it — see the note on
/// <see cref="ConversationDetailResponse"/>.</param>
/// <param name="SenderId">Who wrote it. The frontend compares this against the signed-in user's id to
/// decide which side of the thread to render it on.</param>
/// <param name="SenderNickname">Display name, so a message still reads correctly when the thread is
/// between more than two people (it is not today, but the field costs nothing).</param>
/// <param name="Content">The text as written.</param>
/// <param name="CreatedAt">Beijing time, no timezone suffix.</param>
public sealed record MessageResponse(
    long Id,
    long SenderId,
    string SenderNickname,
    string Content,
    DateTime CreatedAt);
