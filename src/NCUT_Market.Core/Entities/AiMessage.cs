namespace NCUT_Market.Core.Entities;

/// <summary>
/// One answered question, stored so the panel can show earlier conversations again.
/// </summary>
/// <remarks>
/// <para>
/// A row exists only when the assistant actually answered. A question that failed — quota, model
/// outage — leaves nothing behind, which is the same rule the browser follows for its own history.
/// </para>
/// <para>
/// The listing links are kept as written, not re-queried: this is a record of what was said, and a
/// listing that has been sold or removed since should read as gone rather than quietly turn into a
/// different answer.
/// </para>
/// </remarks>
public sealed class AiMessage : IHasCreatedAt
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>What the reader asked, trimmed and capped on the way in.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>What she answered, already cleaned and capped for the panel.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>
    /// The search results shown under the answer, as a JSON array of id, title and price. Empty string
    /// when no search ran. Kept as text rather than a second table: it is a display fragment, never
    /// queried, and only ever read together with this row.
    /// </summary>
    public string ResultsJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
