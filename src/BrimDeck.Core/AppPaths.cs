using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BrimDeck.Core;

// Where BrimDeck keeps settings, extensions and browser data. A test or a second profile moves the whole folder with
// --data-dir or BRIMDECK_DATA_DIR, so it never reads or writes the copy the user is running. Without an override the
// folder is the usual one under LocalAppData.
public static class AppPaths
{
    public const string OverrideVariable = "BRIMDECK_DATA_DIR";
    private static string? _data;

    public static string Data => _data ??= Resolve();

    // The folder the app uses when nothing overrides it.
    public static string DefaultData
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");

    // A requested folder that could not be used, for the caller to report. Null when the folder resolved cleanly.
    public static string? LastError { get; private set; }

    // True when this process runs on a folder of its own: the instance mutex and the settings event carry this tag, so
    // two profiles (the user's copy and a test run) can be open at once without one instance shutting the other down.
    public static string ProfileTag
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Data.ToLowerInvariant())))[..8].ToLowerInvariant();

    public static bool IsOverridden => !string.Equals(Data, DefaultData, StringComparison.OrdinalIgnoreCase);

    private static string Resolve()
    {
        var requested = FromArguments() ?? Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.IsNullOrWhiteSpace(requested)) return DefaultData;
        try
        {
            var full = Path.GetFullPath(requested.Trim().Trim('"'));
            Directory.CreateDirectory(full);
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // Silently writing into the user's real profile after a bad --data-dir would be worse than falling back
            // loudly: the caller records LastError in the crash log.
            LastError = "Could not use the data folder \"" + requested + "\": " + ex.Message;
            return DefaultData;
        }
    }

    private static string? FromArguments()
    {
        var args = Environment.GetCommandLineArgs();
        for (int at = 0; at < args.Length; at++)
        {
            if (string.Equals(args[at], "--data-dir", StringComparison.OrdinalIgnoreCase) && at + 1 < args.Length)
                return args[at + 1];
            if (args[at].StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                return args[at]["--data-dir=".Length..];
        }
        return null;
    }
}
