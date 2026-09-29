namespace NCUT_Market.Api.Configuration;

/// <summary>
/// Locates the frontend directory and reports which candidate won.
/// </summary>
/// <remarks>
/// <para>
/// The frontend lives outside this project, at <c>web/</c> in the repository root, so its path
/// cannot be assumed. Under <c>dotnet run</c> the content root is the project directory and the
/// repository root is <c>../../</c>; after <c>dotnet publish</c> the content root is the publish
/// output and <c>../../</c> points somewhere unrelated. A hard-coded relative path would therefore
/// work all through development and 404 silently in production — the class of bug this project
/// keeps running into, where the dev machine's layout makes a wrong assumption look correct.
/// </para>
/// <para>
/// So candidates are tried in order and the winner is logged at startup. A wrong resolution should
/// be noisy, not silent.
/// </para>
/// </remarks>
internal static class StaticWebRoot
{
    /// <summary>
    /// Configuration key that overrides the search — <c>StaticFiles__WebRoot</c> in .env. An
    /// operator pointing at a container mount or a CDN mirror wins over both conventions.
    /// </summary>
    internal const string ConfigurationKey = "StaticFiles:WebRoot";

    /// <summary>
    /// Returns the first candidate directory that exists, or <see langword="null"/> when none does.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing is deliberate: a missing frontend must not stop the API
    /// from booting. Every <c>/api</c> route still works, and the reason the UI 404s is one warning
    /// line in the log instead of a startup crash.
    /// </remarks>
    public static string? Resolve(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger)
    {
        var candidates = new List<string>();

        // 1. Explicit override.
        var configured = configuration[ConfigurationKey];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured)));
        }

        // 2. Development layout: <repo>/src/NCUT_Market.Api -> <repo>/web
        candidates.Add(Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "web")));

        // 3. Published layout: the csproj copies web/** into the output as web/.
        candidates.Add(Path.GetFullPath(Path.Combine(environment.ContentRootPath, "web")));

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                logger.LogInformation("Serving frontend static files from {WebRoot}", candidate);

                return candidate;
            }
        }

        logger.LogWarning(
            "No frontend directory found; tried {Candidates}. Static files will not be served.",
            string.Join(", ", candidates));

        return null;
    }
}
