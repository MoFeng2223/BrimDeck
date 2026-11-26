namespace BrimDeck.Core.Agents;

// The brim.exe the coding agents' hooks run: the one beside BrimDeck.exe, unless the environment variable BRIMDECK_HOOK_BRIM
// names another brim.exe. The variable is for development only: a developer who switches between builds sets it to a router
// that passes every call on to whichever BrimDeck is running, so the hooks are installed once and every build reads them as
// installed. Where it is not set, as on users' PCs, nothing changes. A value that is not a full path, does not end in
// brim.exe (BrimDeck tells its own hooks by that name) or names no file is passed over.
public static class HookBrim
{
    public const string Variable = "BRIMDECK_HOOK_BRIM";

    public static string Choose(string beside, string? variable, Func<string, bool> exists)
    {
        string named = (variable ?? "").Trim().Trim('"').Trim();
        if (named.Length == 0 || !Path.IsPathFullyQualified(named)) return beside;
        named = Path.GetFullPath(named);
        return Path.GetFileName(named).Equals("brim.exe", StringComparison.OrdinalIgnoreCase) && exists(named) ? named : beside;
    }
}
