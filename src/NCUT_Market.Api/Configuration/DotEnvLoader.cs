namespace NCUT_Market.Api.Configuration;

/// <summary>
/// Minimal .env reader, so the connection string and other secrets stay out of appsettings.json
/// during local development. Keys follow the same convention as EasyERP: a double underscore becomes
/// a configuration separator, so <c>ConnectionStrings__DefaultConnection</c> binds to
/// <c>ConnectionStrings:DefaultConnection</c> and is picked up by GetConnectionString.
/// </summary>
internal static class DotEnvLoader
{
    public static IReadOnlyDictionary<string, string?> LoadFromRepositoryRoot()
    {
        // Walk up from the working directory so a single .env at the repository root works no matter
        // which project the command was run from.
        var envPath = FindEnvFile(Directory.GetCurrentDirectory())
            ?? FindEnvFile(AppContext.BaseDirectory);

        if (envPath is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[NormalizeKey(key)] = value;
        }

        return values;
    }

    private static string NormalizeKey(string key)
    {
        if (key.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
        {
            return "Environment";
        }

        return key.Replace("__", ":", StringComparison.Ordinal);
    }

    private static string? FindEnvFile(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
