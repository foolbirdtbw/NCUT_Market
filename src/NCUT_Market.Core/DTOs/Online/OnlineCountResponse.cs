namespace NCUT_Market.Core.DTOs.Online;

/// <summary>
/// What the footer's counter asks for.
/// </summary>
/// <remarks>
/// One integer and nothing else. No names, no ids, no breakdown — it is readable without a token,
/// because the footer shows it to signed-out visitors too, so it must not describe anybody.
/// </remarks>
/// <param name="OnlineCount">Active accounts seen inside the window.</param>
public sealed record OnlineCountResponse(int OnlineCount);
