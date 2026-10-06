namespace NCUT_Market.Infrastructure.Services;

/// <summary>
/// Builds the pattern argument for <c>EF.Functions.Like</c> out of a term a user typed.
/// </summary>
/// <remarks>
/// Shared rather than written per service because the escaping is the part that has to be right, and a
/// second copy is a second place for it to quietly stop being right. Case-insensitivity is not handled
/// here at all: it comes from the columns' <c>utf8mb4_0900_ai_ci</c> collation.
/// </remarks>
internal static class LikePattern
{
    /// <summary>
    /// Character that makes the next character literal inside a <c>LIKE</c> pattern.
    /// </summary>
    /// <remarks>
    /// Backslash, because it is already MySQL's default <c>LIKE</c> escape and so behaves the same
    /// whether or not the <c>ESCAPE</c> clause survives translation.
    /// </remarks>
    public const string Escape = "\\";

    /// <summary>
    /// A <c>%term%</c> pattern matching any value containing the term.
    /// </summary>
    /// <param name="term">What the user typed. Leading and trailing space should already be trimmed.</param>
    public static string Contains(string term) => "%" + EscapeTerm(term) + "%";

    /// <summary>
    /// Makes <c>%</c>, <c>_</c> and the escape character itself literal in a <c>LIKE</c> pattern.
    /// </summary>
    /// <remarks>
    /// The backslash is replaced first. Doing it after would escape the backslashes this method had
    /// just inserted, so a search for <c>100%</c> would become <c>%100\\\%%</c> and match
    /// everything — a wrong answer that looks like a working search.
    /// </remarks>
    private static string EscapeTerm(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
