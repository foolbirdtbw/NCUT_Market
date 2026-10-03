namespace NCUT_Market.Core.DTOs.Messages;

/// <summary>
/// The number behind the header badge.
/// </summary>
/// <param name="Count">
/// The number of <em>conversations</em> holding at least one unread message — not the number of
/// unread messages.
/// </param>
/// <remarks>
/// Deliberately the thread count. The badge answers "how many things are waiting on me", and a thread
/// with eleven new lines is one thing to deal with, not eleven. Changing this to a message count
/// would make the number jump around for no benefit to the reader.
/// </remarks>
public sealed record UnreadCountResponse(int Count);
