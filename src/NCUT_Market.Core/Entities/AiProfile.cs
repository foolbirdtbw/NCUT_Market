namespace NCUT_Market.Core.Entities;

/// <summary>
/// What the assistant remembers about one reader: a few sentences the model wrote from what they asked.
/// </summary>
/// <remarks>
/// <para>
/// This is the one part of a conversation that survives the browser. The chat turns themselves are
/// still never stored — what lands here is a summary the model wrote, capped and free of anything it
/// was told not to infer (gender, college, year, contact details).
/// </para>
/// <para>
/// The category is kept as a name rather than a foreign key. Administrators delete categories, and a
/// row pointing at one would turn that into a 409; a name that no longer resolves is simply treated as
/// no category when a recommendation is looked up.
/// </para>
/// </remarks>
public sealed class AiProfile : IHasCreatedAt, IHasUpdatedAt
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>The model's note. Empty until the first summary runs.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Comma-separated search terms, at most five. What a recommendation searches with.</summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>A category name as it was when written, or the empty string.</summary>
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>
    /// How many questions this reader has asked. Drives "summarise every few questions" without needing
    /// a second table, and survives a restart where an in-memory counter would not.
    /// </summary>
    public int QuestionCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
