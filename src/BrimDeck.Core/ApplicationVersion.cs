using System.Reflection;

namespace BrimDeck.Core;

public static class ApplicationVersion
{
    // Version of the running application: the entry assembly's informational version without build metadata
    // ("0.2.0+abc123" -> "0.2.0"). Release builds receive it from the tag through -p:Version.
    public static string Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // Major.minor.patch; a prerelease suffix ("0.2.0-beta") compares as its release. Unreadable text counts as 0.0.0.
    public static Version Parse(string? text)
    {
        var core = (text ?? "").Split('+', '-')[0].Trim();
        return Version.TryParse(core, out var version) && version.Build >= 0 ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 0, 0);
    }

    private static string Normalize(string? text) => Parse(text).ToString(3);
}
