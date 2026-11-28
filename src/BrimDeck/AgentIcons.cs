using System.IO;
using BrimDeck.Core.Agents;

namespace BrimDeck;

// A coding tool's own icon, wherever it shows: the collapsed row's sessions, an approval on the island, and the tool's notices
// on the notices page. One reference, "agent:<harness>", stands for the tool; NoticeIcons reads it from the first of the tool's
// sources that gives an icon: the Start menu entry of its desktop application ("aumid:<id>", as the Start menu shows it), of its
// IDE, its installed program ("exe:<path>"), and last the icon built into BrimDeck ("brand:agent-<id>", Assets\Agents). The ids
// are those the applications register; each was checked on a machine with the application installed (2026-11-25; Qoder, Qoder
// CN, the Antigravity IDE and CodeAgent 2026-11-28).
//
// A tool that may be installed as a command line alone has its icon built in, since its program carries none of its own (the
// Codex and Antigravity CLIs carry none, OpenCode's and MiMo Code's carry the Bun runtime's, checked 2026-11-28). The Qoder CLIs
// and CodeAgent carry their own and need none. Where the built-in icons come from (2026-11-28): Claude and Antigravity, their
// installed applications; Codex, the icon of openai.com (the mark on its white plate: the Codex app's own is a white mark alone,
// which a light theme's plate would hide); Cursor, the dark icon of its site (icon-192x192.png, as its app shows it; not the
// light one); OpenCode and Command Code, the icons of their official sites; Kimi Code, the
// logo in its official repository (MoonshotAI/kimi-code). MiMo Code publishes no square icon, only the wide "Xiaomi MiMo"
// wordmark, which is not cut down into one: without its desktop application it shows its letter.
internal static class AgentIcons
{
    private const string Prefix = "agent:", BrandPrefix = "agent-";

    private static readonly Dictionary<string, Func<string[]>> s_sources = new(StringComparer.Ordinal)
    {
        ["claude"] = () => [Aumid("Claude_pzs8sxrjxfjjc!Claude"), Brand("claude")],
        ["codex"] = () => [Aumid("OpenAI.Codex_2p2nqsd0c76g0!App"), Brand("codex")],
        ["cursor"] = () => [Aumid("Anysphere.Cursor"), Brand("cursor")],
        ["antigravity"] = () => [Aumid("electron.app.Antigravity"), Aumid("Google.AntigravityIDE"), Brand("antigravity")],
        // CodeAgent has no desktop application; its Start menu entry runs cmd with the program's icon, under an id Windows makes
        // up from the entry's path, so the program itself is read.
        ["codeagent"] = () => [Exe(Environment.SpecialFolder.ProgramFiles, "CodeAgentCLI", "bin", "codeagentcli.exe")],
        // The Qoder IDE reads the Qoder app's settings and reports as the same tool. The CLIs carry the Qoder icon themselves.
        ["qoder"] = () => [Aumid("com.qoder.app"), Aumid("AlibabaCloud.Qoder"), Home("QODER_CONFIG_DIR", ".qoder", "bin", "qodercli", "qodercli.exe")],
        ["qoder-cn"] = () => [Aumid("com.qodercn.app"), Aumid("AlibabaCloud.QoderCN"), Home("QODERCN_CONFIG_DIR", ".qoder-cn", "bin", "qoderclicn", "qoderclicn.exe")],
        ["kimi"] = () => [Aumid("com.kimi.code.desktop"), Brand("kimi")],
        ["trae"] = () => [Aumid("ByteDance.Trae")],
        ["trae-cn"] = () => [Aumid("ByteDance.TraeCN")],
        ["opencode"] = () => [Aumid("ai.opencode.desktop"), Brand("opencode")],
        ["workbuddy"] = () => [Aumid("WorkBuddy.WorkBuddy")],
        ["workbuddy-ai"] = () => [Aumid("WorkBuddy.WorkBuddyAI")],
        ["zcode"] = () => [Aumid("dev.zcode.app")],
        ["mimo"] = () => [Aumid("com.xiaomi.mimo.desktop")],
        ["commandcode"] = () => [Aumid("ai.commandcode.desktop"), Aumid("electron.app.Command Code"), Brand("commandcode")],
    };

    // The tools whose icon is built in (Assets\Agents\<id>.png).
    private static readonly HashSet<string> s_brands = new(StringComparer.Ordinal) { "claude", "codex", "cursor", "antigravity", "kimi", "opencode", "commandcode" };

    private static string Aumid(string id) => "aumid:" + id;

    private static string Exe(Environment.SpecialFolder folder, params string[] parts) => "exe:" + Path.Combine([Environment.GetFolderPath(folder), .. parts]);

    // A program in the tool's folder in the user's home, or in the folder its variable names (AgentPaths.Under).
    private static string Home(string variable, params string[] parts) => "exe:" + AgentPaths.Current().Under(variable, parts);

    private static string Brand(string id) => "brand:" + BrandPrefix + id;

    // Whether "brand:<name>" names a built-in tool icon (AppBrandIcons.Read).
    public static bool IsBrand(string name) => name.StartsWith(BrandPrefix, StringComparison.Ordinal) && s_brands.Contains(name[BrandPrefix.Length..]);

    // The tool's icon reference; null for a tool without a known icon.
    public static string? Ref(string harness) => s_sources.ContainsKey(harness) ? Prefix + harness : null;

    // The icon of a notice an agent's hook sent (AgentKeys.Harness), null for any other notice.
    public static string? ForNotice(string? replaceKey) => AgentKeys.Harness(replaceKey) is { } harness ? Ref(harness) : null;

    // Where an "agent:" reference is read from, in order; null for any other reference.
    public static string[]? Sources(string iconRef)
        => iconRef.StartsWith(Prefix, StringComparison.Ordinal) && s_sources.TryGetValue(iconRef[Prefix.Length..], out var sources) ? sources() : null;
}
