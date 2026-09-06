namespace BrimDeck.Core;

public sealed record DataLocations(string ClaudeHome, string CodexHome, string CursorDatabase, string Roaming, string Local)
{
    public static DataLocations Detect() => Resolve(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetEnvironmentVariable("CODEX_HOME"));

    public static DataLocations Resolve(string home, string roaming, string local, string? claudeOverride, string? codexOverride) => new(
        ConfigDirectory(claudeOverride, Path.Combine(home, ".claude")),
        ConfigDirectory(codexOverride, Path.Combine(home, ".codex")),
        Path.Combine(roaming, "Cursor", "User", "globalStorage", "state.vscdb"), roaming, local);

    private static string ConfigDirectory(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : fallback;

    public IReadOnlyList<string> ClaudeDesktopProfiles()
    {
        var paths = new List<string> { Path.Combine(Roaming, "Claude") };
        var packages = Path.Combine(Local, "Packages");
        try
        {
            if (Directory.Exists(packages))
                paths.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "config.json"))).ToList();
    }
}
