namespace BrimDeck.Core;

// PATH as Windows keeps it in the registry: folders separated by ";", some in quotes, some written with %VARIABLES%. The
// installer adds BrimDeck's folder so that "brim" is found in a terminal (installer/BrimDeck.iss, SetCommandPath), and takes
// it out again when BrimDeck is uninstalled. BrimDeck itself only reads PATH to say in the settings whether brim is on it, and
// adds its folder for the current user when asked to (a portable copy, or an installation whose PATH could not be changed).
// Nothing else in PATH is rewritten: the other entries stay as they were written, in their order.
public static class CommandPath
{
    // Whether the folder is one of PATH's entries, however it is written there (quotes, a trailing separator, either slash,
    // letter case, environment variables, which expand reads out).
    public static bool Contains(string? path, string folder, Func<string, string>? expand = null)
    {
        string wanted = Normal(folder, expand);
        return wanted.Length > 0 && Entries(path).Any(entry => string.Equals(Normal(entry, expand), wanted, StringComparison.OrdinalIgnoreCase));
    }

    // PATH with the folder as its last entry; unchanged when it is already there.
    public static string With(string? path, string folder, Func<string, string>? expand = null)
    {
        string current = path ?? "";
        if (Normal(folder, expand).Length == 0 || Contains(current, folder, expand)) return current;
        string kept = current.TrimEnd(';', ' ');
        string entry = folder.Contains(';') ? '"' + folder + '"' : folder;
        return kept.Length == 0 ? entry : kept + ";" + entry;
    }

    // PATH without the folder's entries; every other entry as it was.
    public static string Without(string? path, string folder, Func<string, string>? expand = null)
    {
        string wanted = Normal(folder, expand);
        if (wanted.Length == 0) return path ?? "";
        return string.Join(";", Entries(path).Where(entry => !string.Equals(Normal(entry, expand), wanted, StringComparison.OrdinalIgnoreCase)));
    }

    // PATH's entries as written; a ";" inside quotes belongs to its entry. Empty entries are left out.
    public static IEnumerable<string> Entries(string? path)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        int start = 0;
        bool quoted = false;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] == '"') quoted = !quoted;
            if (i < path.Length && (path[i] != ';' || quoted)) continue;
            string entry = path[start..i].Trim();
            start = i + 1;
            if (entry.Length > 0) yield return entry;
        }
    }

    // A folder as it is compared: without quotes, with its variables read out, backslashes only, and no separator at its end
    // unless it is a drive's root.
    private static string Normal(string entry, Func<string, string>? expand)
    {
        string folder = entry.Trim().Trim('"').Trim();
        if (folder.Length == 0) return "";
        folder = (expand ?? Environment.ExpandEnvironmentVariables)(folder).Replace('/', '\\');
        while (folder.Length > 3 && folder[^1] == '\\') folder = folder[..^1];
        return folder;
    }
}
