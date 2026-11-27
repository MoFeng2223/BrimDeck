using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Protocol;
using JValue = System.Text.Json.Nodes.JsonValue;

namespace BrimDeck.Core.Agents;

// How a tool runs a hook's command, which decides how the command is written into its settings.
public enum HookCommandStyle
{
    // The program and its arguments apart, run without a shell (Claude Code's "command" with "args").
    Exec,
    // One command line run by cmd /C (Antigravity, Kimi Code), or by PowerShell or cmd as the tool finds them (Codex): a path
    // without spaces reads as a program in both, so AgentHookFiles gives a path with spaces in its short form.
    Cmd,
    // One command line run by PowerShell (Cursor).
    PowerShell,
    // One command line run by Git Bash, or by PowerShell where Git Bash is missing (the tools built on Claude Code that may not
    // know "args"): the path is written with forward slashes and without quotes, which both read as a program (AgentHookFiles
    // gives a path with spaces in its short form).
    Shell,
    // A program and its arguments run without a shell, written as ZCode's "process" hook (command, args, timeoutMs).
    Process
}

// How the hooks sit in a tool's settings file.
public enum HookFileShape
{
    // {"hooks": {"Event": [{"matcher": "...", "hooks": [{"type": "command", ...}]}]}} (Claude Code, Codex and the tools that copy them).
    Nested,
    // {"version": 1, "hooks": {"event": [{"command": "..."}]}} (Cursor).
    Flat,
    // {"<hook name>": {"Event": [{"type": "command", ...}], "ToolEvent": [{"matcher": "...", "hooks": [...]}]}} (Antigravity): BrimDeck's
    // hooks are the hook named "brimdeck".
    Named,
    // {"hooks": {"enabled": true, "events": {"Event": [{"matcher": "...", "hooks": [...]}]}}} (ZCode's own agent): its file hooks run
    // only with "enabled", and the file is read strictly, so only the events it knows may be listed.
    Events,
    // A plugin file of BrimDeck's own, which the tool loads from its plugin folder (OpenCode, Command Code): the whole file is
    // BrimDeck's (AgentPlugin).
    PluginFile,
    // [[hooks]] tables in a TOML settings file, each with event, matcher, command and timeout and nothing else (Kimi Code).
    Toml
}

// A plugin file BrimDeck writes whole: its text for a brim.exe, and the first line BrimDeck knows its own file by.
public sealed record AgentPlugin(string Marker, Func<string, string> Text)
{
    public bool Ours(string? text) => text is not null && text.StartsWith(Marker, StringComparison.Ordinal);
}

// One hook BrimDeck adds: the event, its matcher (null: every one), and whether the tool waits for it (an approval, a question)
// or runs it in the background (a state report).
public sealed record HookEntry(string Event, string? Matcher, bool Waits);

// A coding agent ("harness") whose hooks BrimDeck can install: where its settings are, how they hold hooks, and which events
// it reports. Tools that share one settings file are one harness (the Claude desktop app and the Claude Code CLI; the
// Antigravity app, IDE and CLI). Only tools whose sessions BrimDeck can connect are listed: one it cannot (DeepSeek Harness and
// QwenWork have no hooks a settings file could hold, WorkBuddy AI reads none from its own) is not looked for, and so never shown.
public sealed record AgentHarness(
    string Id, string Name, string Format, Func<string> Components,
    // The settings file, from the user's folder and the environment.
    Func<AgentPaths, string> SettingsFile,
    // Whether the tool is installed: any of these files or folders exists.
    Func<AgentPaths, IEnumerable<string>> Evidence,
    HookFileShape Shape, HookCommandStyle Style, IReadOnlyList<HookEntry> Hooks,
    // What the island can do for its sessions: answer approvals, answer questions.
    bool Approvals, bool Questions,
    // What the user must still do after installing, shown beside the row (Codex reviews new hooks before it runs them).
    Func<string>? AfterInstall = null,
    // Whether the tool reads "async" (runs a hook in the background); one that does not gets none.
    bool Async = true,
    // The plugin file of a tool without command hooks (HookFileShape.PluginFile).
    AgentPlugin? Plugin = null,
    // Whether the hooks file carries "version": 1 (Cursor, TRAE).
    bool Versioned = false,
    // Approvals and questions are answered in the tool only; the island tells of them with a notice until they are.
    bool Tells = false)
{
    // The tool shows no approval prompt, or no question, of its own while its hook waits on the island (AgentHook says which
    // tools, and how that is known). Such a one is answered on the island only when the user chose so (AgentAsks); otherwise
    // the island tells of it and the tool asks as it always does.
    public bool HoldsApprovals => Approvals && AgentHook.ApprovalHolds(Format, Id);
    public bool HoldsQuestions => Questions && AgentHook.QuestionHolds(Format, Id);
}

// The folders the harnesses' settings are found from; the tests give their own.
public sealed record AgentPaths(string Home, string Roaming, string Local, Func<string, string?> Variable)
{
    public static AgentPaths Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable);

    // A folder in the user's home, or the one the variable names.
    public string Under(string? variable, params string[] parts)
        => variable is not null && Variable(variable) is { Length: > 0 } set ? Path.Combine([set, .. parts.Skip(1)]) : Path.Combine([Home, .. parts]);

    // A program found in a folder of PATH.
    public IEnumerable<string> OnPath(string file) => (Variable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder => folder.Trim('"')).Where(Path.IsPathFullyQualified).Select(folder => Path.Combine(folder, file));
}

// Whether a harness's hooks are in its settings.
public enum HookState { Missing, Installed, Outdated, Unreadable }

public static class AgentHarnesses
{
    // Long enough for an approval or a question to be answered on the island: BrimDeck's own wait ends first (AgentWait, at
    // most NoticeProtocol.MaxTimeoutMs), so the tool never cancels the hook before BrimDeck lets it go.
    public const int WaitingTimeoutSeconds = (int)(NoticeProtocol.MaxTimeoutMs / 1000) + 60;
    // The name of BrimDeck's hook in a settings file of named hooks (HookFileShape.Named).
    public const string HookName = "brimdeck";

    // Claude Code asks a question through PermissionRequest as it asks for an approval, beside its own prompt: no PreToolUse
    // hook waits for one, which would hold Claude Code's prompt up. StopFailure ends a turn the API refused, in place of Stop.
    private static readonly HookEntry[] ClaudeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", "*", false), new("Stop", null, false), new("StopFailure", null, false),
        new("Notification", null, false), new("SessionEnd", null, false), new("PermissionRequest", "*", true)
    ];

    // The tools built on Claude Code: a question comes with PreToolUse (CodeAgent, the Qoder desktop app), which the tool waits
    // for before it asks, and from the Qoder CLI with PermissionRequest after it.
    private static readonly HookEntry[] ClaudeLikeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", "*", false), new("Stop", null, false),
        new("Notification", null, false), new("SessionEnd", null, false),
        new("PermissionRequest", "*", true), new("PreToolUse", "AskUserQuestion", true)
    ];

    // Codex ends a session with SessionEnd (and each run of "codex exec" with it).
    private static readonly HookEntry[] CodexHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("Stop", null, false), new("SessionEnd", null, false),
        new("PermissionRequest", null, true)
    ];

    private static readonly HookEntry[] CursorHooks =
    [
        new("sessionStart", null, false), new("beforeSubmitPrompt", null, false), new("postToolUse", null, false), new("stop", null, false), new("sessionEnd", null, false)
    ];

    // ZCode's own agent knows seven events (no Notification, no SessionEnd); its hooks always run inline. A tool that failed
    // says so with its own event, without which an approval's state would stay.
    private static readonly HookEntry[] ZCodeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("PostToolUseFailure", null, false), new("Stop", null, false),
        new("PermissionRequest", null, true), new("PreToolUse", "AskUserQuestion", true)
    ];

    // CodeBuddy Code (inside WorkBuddy) asks its question before the question's PreToolUse hook runs, so no hook waits for one.
    private static readonly HookEntry[] CodeBuddyHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", "*", false), new("Stop", null, false),
        new("Notification", null, false), new("SessionEnd", null, false), new("PermissionRequest", "*", true)
    ];

    // Kimi Code's hooks decide nothing (its approval shows while PermissionRequest runs, and PermissionResult tells the answer);
    // the matcher is a regular expression that may match anywhere in the tool's name.
    private static readonly HookEntry[] KimiHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("PostToolUseFailure", null, false),
        new("PermissionRequest", null, false), new("PermissionResult", null, false), new("PreToolUse", "^AskUserQuestion$", false),
        new("Stop", null, false), new("StopFailure", null, false), new("Interrupt", null, false), new("SessionEnd", null, false)
    ];

    // TRAE has six events; Notification says when it waits for an approval or an answer.
    private static readonly HookEntry[] TraeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("Stop", null, false), new("Notification", null, false)
    ];

    // Antigravity's hooks block its loop, and it has no event for an approval: the session's start, each call of the model (which
    // carries the user's last words), the loop's end, and its question tool before it runs (the matcher is a regular expression).
    private static readonly HookEntry[] AntigravityHooks =
    [
        new("SessionStart", null, false), new("PreInvocation", null, false), new("Stop", null, false), new("PreToolUse", "ask_question", false)
    ];

    // A tool built on Claude Code that reads hooks from its own folder in Claude Code's format.
    private static AgentHarness ClaudeLike(string id, string name, Func<string> components, string folder, string? variable, Func<AgentPaths, IEnumerable<string>> evidence)
        => new(id, name, AgentHookFormats.Claude, components, p => Path.Combine(p.Under(variable, folder), "settings.json"), evidence,
            HookFileShape.Nested, HookCommandStyle.Shell, ClaudeLikeHooks, Approvals: true, Questions: true, Async: false);

    // WorkBuddy: the CodeBuddy Code inside reads its hooks from the app's own folder and runs them through Git Bash, one at a
    // time. (WorkBuddy AI hands its CodeBuddy Code settings of its own making and reads no hooks from its folder.)
    private static AgentHarness WorkBuddy(string id, string name, Func<string> components, string folder)
        => new(id, name, AgentHookFormats.CodeBuddy, components, p => Path.Combine(p.Home, folder, "settings.json"),
            p => [Path.Combine(p.Home, folder, "settings.json"), Path.Combine(p.Home, folder, "projects")],
            HookFileShape.Nested, HookCommandStyle.Shell, CodeBuddyHooks, Approvals: true, Questions: false, Async: false);

    // TRAE and TRAE CN: each reads hooks.json of its own folder, in Claude Code's nesting with a version, and runs a command
    // through PowerShell. Its global hooks run only once the user switched them on in TRAE's settings, and only when hook
    // commands run locally: in its sandbox, the default, they do not run at all and nothing says so.
    private static AgentHarness Trae(string id, string name, Func<string> components, string folder, string data)
        => new(id, name, AgentHookFormats.Trae, components, p => Path.Combine(p.Home, folder, "hooks.json"),
            p => [Path.Combine(p.Roaming, data), Path.Combine(p.Home, folder, "argv.json")],
            HookFileShape.Nested, HookCommandStyle.PowerShell, TraeHooks, Approvals: false, Questions: false, Async: false, Versioned: true, Tells: true,
            AfterInstall: () => Loc.T($"还需在 {name} 的“设置 › Hooks”中打开“已配置的 Hooks”，并把“Hooks 命令运行方式”改为“本地自动运行”",
                $"In {name}'s Settings › Hooks, also turn on the configured global hooks and set hook commands to run locally, not in the sandbox"));

    public static IReadOnlyList<AgentHarness> All { get; } =
    [
        new("claude", "Claude Code", AgentHookFormats.Claude, () => Loc.T("Claude Code CLI、Claude 桌面版与 IDE 扩展", "Claude Code CLI, Claude desktop app and IDE extensions"),
            p => Path.Combine(p.Under("CLAUDE_CONFIG_DIR", ".claude"), "settings.json"),
            p => [Path.Combine(p.Under("CLAUDE_CONFIG_DIR", ".claude"), "settings.json"), Path.Combine(p.Under("CLAUDE_CONFIG_DIR", ".claude"), "projects"), Path.Combine(p.Home, ".local", "bin", "claude.exe"), .. p.OnPath("claude.exe")],
            HookFileShape.Nested, HookCommandStyle.Exec, ClaudeHooks, Approvals: true, Questions: true),
        new("codex", "Codex", AgentHookFormats.Codex, () => Loc.T("Codex CLI 与 Codex 桌面版", "Codex CLI and Codex desktop app"),
            p => Path.Combine(p.Under("CODEX_HOME", ".codex"), "hooks.json"),
            p => [Path.Combine(p.Under("CODEX_HOME", ".codex"), "config.toml"), Path.Combine(p.Under("CODEX_HOME", ".codex"), "sessions")],
            HookFileShape.Nested, HookCommandStyle.Cmd, CodexHooks, Approvals: true, Questions: false,
            AfterInstall: () => Loc.T("在 Codex 中输入 /hooks，审阅并信任新钩子后才会生效", "In Codex, enter /hooks and trust the new hooks before they run")),
        new("antigravity", "Antigravity", AgentHookFormats.Antigravity, () => Loc.T("Antigravity 桌面版、Antigravity IDE 与 Antigravity CLI", "Antigravity desktop app, Antigravity IDE and Antigravity CLI"),
            p => Path.Combine(p.Home, ".gemini", "config", "hooks.json"),
            p => [Path.Combine(p.Local, "agy", "bin", "agy.exe"), Path.Combine(p.Local, "Programs", "antigravity", "Antigravity.exe"), Path.Combine(p.Home, ".gemini", "antigravity"), Path.Combine(p.Home, ".gemini", "antigravity-ide"), Path.Combine(p.Home, ".gemini", "antigravity-cli")],
            HookFileShape.Named, HookCommandStyle.Cmd, AntigravityHooks, Approvals: false, Questions: false),
        new("cursor", "Cursor", AgentHookFormats.Cursor, () => Loc.T("Cursor 编辑器与 Cursor CLI", "Cursor editor and Cursor CLI"),
            p => Path.Combine(p.Home, ".cursor", "hooks.json"),
            p => [Path.Combine(p.Local, "Programs", "cursor", "Cursor.exe"), Path.Combine(p.Home, ".cursor", "cli-config.json"), .. p.OnPath("cursor-agent.cmd"), .. p.OnPath("cursor-agent.exe"), .. p.OnPath("cursor.cmd")],
            HookFileShape.Flat, HookCommandStyle.PowerShell, CursorHooks, Approvals: false, Questions: false, Versioned: true),
        ClaudeLike("codeagent", "CodeAgent", () => Loc.T("CodeAgent CLI", "CodeAgent CLI"), ".cac", "CODEAGENT3_CONFIG_DIR",
            p => [Path.Combine(p.Under("CODEAGENT3_CONFIG_DIR", ".cac"), "settings.json"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CodeAgentCLI"), .. p.OnPath("codeagent.exe")]),
        ClaudeLike("qoder", "Qoder", () => Loc.T("Qoder CLI 与 Qoder 桌面版", "Qoder CLI and Qoder desktop app"), ".qoder", "QODER_CONFIG_DIR",
            p => [Path.Combine(p.Under("QODER_CONFIG_DIR", ".qoder"), "bin", "qodercli"), Path.Combine(p.Under("QODER_CONFIG_DIR", ".qoder"), "settings.json")]),
        ClaudeLike("qoder-cn", "Qoder CN", () => Loc.T("Qoder CN CLI 与 Qoder CN 桌面版", "Qoder CN CLI and Qoder CN desktop app"), ".qoder-cn", "QODERCN_CONFIG_DIR",
            p => [Path.Combine(p.Under("QODERCN_CONFIG_DIR", ".qoder-cn"), "bin"), Path.Combine(p.Under("QODERCN_CONFIG_DIR", ".qoder-cn"), "settings.json")]),
        WorkBuddy("workbuddy", "WorkBuddy", () => Loc.T("WorkBuddy 桌面版（内置 CodeBuddy Code）", "WorkBuddy desktop app (with CodeBuddy Code)"), ".workbuddy"),
        new("opencode", "OpenCode", AgentHookFormats.OpenCode, () => Loc.T("OpenCode CLI 与 OpenCode 桌面版（通过插件）", "OpenCode CLI and OpenCode desktop app (through a plugin)"),
            p => Path.Combine(p.Under("XDG_CONFIG_HOME", ".config", "opencode", "plugins"), OpenCodePlugin.FileName),
            p => [p.Under("XDG_CONFIG_HOME", ".config", "opencode"), Path.Combine(p.Home, ".local", "share", "opencode", "opencode.db"), .. p.OnPath("opencode.cmd"), .. p.OnPath("opencode.exe")],
            HookFileShape.PluginFile, HookCommandStyle.Exec, [], Approvals: true, Questions: true, Plugin: OpenCodePlugin.Plugin),
        new("kimi", "Kimi Code", AgentHookFormats.Kimi, () => Loc.T("Kimi Code 桌面版与 Kimi Code CLI", "Kimi Code desktop app and Kimi Code CLI"),
            p => Path.Combine(p.Under("KIMI_CODE_HOME", ".kimi-code"), "config.toml"),
            p => [Path.Combine(p.Under("KIMI_CODE_HOME", ".kimi-code"), "config.toml"), Path.Combine(p.Under("KIMI_CODE_HOME", ".kimi-code"), "sessions")],
            HookFileShape.Toml, HookCommandStyle.Cmd, KimiHooks, Approvals: false, Questions: false, Async: false, Tells: true,
            AfterInstall: () => Loc.T("重新启动 Kimi Code 后生效", "They run once Kimi Code is restarted")),
        new("commandcode", "Command Code", AgentHookFormats.Claude, () => Loc.T("Command Code 桌面版与 Command Code CLI（通过插件）", "Command Code desktop app and Command Code CLI (through a mod)"),
            p => Path.Combine(p.Home, ".commandcode", "mods", CommandCodeMod.FileName),
            p => [Path.Combine(p.Home, ".commandcode", "auth.json"), Path.Combine(p.Home, ".commandcode", "projects")],
            HookFileShape.PluginFile, HookCommandStyle.Exec, [], Approvals: true, Questions: true, Plugin: CommandCodeMod.Plugin,
            AfterInstall: () => Loc.T("在新的会话中生效", "They run in new sessions")),
        new("mimo", "MiMo Code", AgentHookFormats.OpenCode, () => Loc.T("MiMo Code CLI（通过插件）。Xiaomi MiMo 桌面版不加载用户插件", "MiMo Code CLI (through a plugin). The Xiaomi MiMo desktop app loads no user plugins"),
            p => Path.Combine(p.Variable("MIMOCODE_HOME") is { Length: > 0 } home ? Path.Combine(home, "config") : p.Under("XDG_CONFIG_HOME", ".config", "mimocode"), "plugins", OpenCodePlugin.FileName),
            p => [Path.Combine(p.Home, ".mimocode", "bin", "mimo.exe"), .. p.OnPath("mimo.exe")],
            HookFileShape.PluginFile, HookCommandStyle.Exec, [], Approvals: true, Questions: true, Plugin: OpenCodePlugin.MiMo,
            AfterInstall: () => Loc.T("重新启动 MiMo Code 后生效", "They run once MiMo Code is restarted")),
        Trae("trae", "TRAE",() => Loc.T("TRAE 与 TRAE SOLO", "TRAE and TRAE SOLO"), ".trae", "Trae"),
        Trae("trae-cn", "TRAE CN", () => Loc.T("TRAE CN 与 TRAE SOLO CN", "TRAE CN and TRAE SOLO CN"), ".trae-cn", "Trae CN"),
        new("zcode", "ZCode", AgentHookFormats.Claude, () => Loc.T("ZCode 桌面版自带的代理", "The ZCode desktop app's own agent"),
            p => Path.Combine(p.Home, ".zcode", "cli", "config.json"),
            p => [Path.Combine(p.Home, ".zcode", "cli", "config.json"), Path.Combine(p.Home, ".zcode", "cli")],
            HookFileShape.Events, HookCommandStyle.Process, ZCodeHooks, Approvals: true, Questions: true, Async: false)
    ];

    public static AgentHarness? Find(string id) => All.FirstOrDefault(h => h.Id == id);

    public static bool Detected(AgentHarness harness, AgentPaths paths, Func<string, bool> exists) => harness.Evidence(paths).Any(exists);

    // ----- The command written into the settings -----

    // brim's own arguments for the harness.
    public static string[] Arguments(AgentHarness harness) => harness.Format == harness.Id && AgentHookFormats.DefaultName(harness.Format) == harness.Name
        ? ["hook", harness.Format] : ["hook", harness.Format, "--name", harness.Name, "--id", harness.Id];

    // The command line for a tool that runs it through a shell. A path without spaces is written bare, which every shell reads.
    public static string CommandLine(AgentHarness harness, string brim)
    {
        string args = string.Join(' ', Arguments(harness).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        bool spaces = brim.Contains(' ');
        return harness.Style switch
        {
            // PowerShell takes a quoted path as a string, not a program, without the call operator.
            HookCommandStyle.PowerShell => (spaces ? "& '" + brim.Replace("'", "''") + "'" : brim) + " " + args,
            // Git Bash would read a backslash as an escape.
            HookCommandStyle.Shell => (spaces ? "\"" + brim.Replace('\\', '/') + "\"" : brim.Replace('\\', '/')) + " " + args,
            _ => (spaces ? "\"" + brim + "\"" : brim) + " " + args
        };
    }

    // Whether a hook in a settings file is one BrimDeck added: its program is brim.exe and its first argument "hook".
    public static bool Ours(JsonNode? hook)
    {
        if (hook is not JsonObject handler) return false;
        string command = handler["command"] is JValue value && value.TryGetValue(out string? text) ? text : "";
        if (handler["args"] is JsonArray args)
            return Path.GetFileName(command.Trim().Trim('"').Replace('/', '\\')).Equals("brim.exe", StringComparison.OrdinalIgnoreCase) && args.Count > 0 && args[0] is JValue first && first.TryGetValue(out string? word) && word == "hook";
        return Ours(command);
    }

    // Whether a command line is one of BrimDeck's hooks.
    public static bool Ours(string command)
    {
        int at = command.IndexOf("brim.exe", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && command[(at + "brim.exe".Length)..].TrimStart('"', '\'', ' ').StartsWith("hook ", StringComparison.Ordinal);
    }

    // ----- Reading and changing a settings file's text -----

    // Reads the settings file's text (null: no file). Unreadable JSON is never written over.
    public static JsonObject? Parse(string? text, out bool unreadable)
    {
        unreadable = false;
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? Fail(out unreadable); }
        catch (JsonException) { return Fail(out unreadable); }
    }

    private static JsonObject? Fail(out bool unreadable) { unreadable = true; return null; }

    // Whether the text holds BrimDeck's hooks as they are now: every hook with this brim, and nothing else of BrimDeck's.
    public static HookState State(AgentHarness harness, string? text, string brim)
    {
        if (harness.Plugin is { } plugin)
            return text is null ? HookState.Missing : text == plugin.Text(brim) ? HookState.Installed : plugin.Ours(text) ? HookState.Outdated : HookState.Unreadable;
        if (harness.Shape == HookFileShape.Toml) return TomlHooks.State(harness, text, brim);
        if (Parse(text, out bool unreadable) is not { } root) return unreadable ? HookState.Unreadable : HookState.Missing;
        var expected = Install(harness, root.DeepClone().AsObject(), brim);
        var found = Ours(harness, root);
        if (found.Count == 0) return HookState.Missing;
        var wanted = Ours(harness, expected);
        return found.Count == wanted.Count && found.Zip(wanted).All(pair => JsonNode.DeepEquals(pair.First, pair.Second)) ? HookState.Installed : HookState.Outdated;
    }

    // The text with BrimDeck's hooks in place (any of its earlier ones replaced), or null when the file cannot be read as JSON.
    public static string? Installed(AgentHarness harness, string? text, string brim)
        => harness.Plugin is { } plugin ? (text is null || plugin.Ours(text) ? plugin.Text(brim) : null)
            : harness.Shape == HookFileShape.Toml ? TomlHooks.Installed(harness, text, brim)
            : Parse(text, out _) is { } root ? Write(Install(harness, root, brim)) : null;

    // The text without BrimDeck's hooks, or null when the file cannot be read as JSON. Groups and events left empty go too.
    // A plugin file of BrimDeck's own is removed whole: the text is then empty, and the file is deleted.
    public static string? Removed(AgentHarness harness, string? text)
        => harness.Plugin is { } plugin ? (text is null || plugin.Ours(text) ? "" : null)
            : harness.Shape == HookFileShape.Toml ? TomlHooks.Removed(text ?? "")
            : Parse(text, out _) is { } root ? Write(Remove(harness, root)) : null;

    private static string Write(JsonObject root) => root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine;

    // The object that holds the events: "hooks" of the file, or BrimDeck's own named hook.
    private static JsonObject Events(AgentHarness harness, JsonObject root)
    {
        if (harness.Shape == HookFileShape.Events)
        {
            if (root["hooks"] is not JsonObject block) root["hooks"] = block = new JsonObject();
            block["enabled"] = true;
            if (block["events"] is not JsonObject inner) block["events"] = inner = new JsonObject();
            return inner;
        }
        string key = harness.Shape == HookFileShape.Named ? HookName : "hooks";
        if (root[key] is not JsonObject events) root[key] = events = harness.Shape == HookFileShape.Named ? new JsonObject { ["enabled"] = true } : new JsonObject();
        return events;
    }

    private static JsonObject Install(AgentHarness harness, JsonObject root, string brim)
    {
        Remove(harness, root);
        if (harness.Versioned) root["version"] ??= 1;
        var hooks = Events(harness, root);
        foreach (var entry in harness.Hooks)
        {
            if (hooks[entry.Event] is not JsonArray list) hooks[entry.Event] = list = new JsonArray();
            // Named hooks list a handler directly for an event without a matcher.
            bool direct = harness.Shape == HookFileShape.Flat || harness.Shape == HookFileShape.Named && entry.Matcher is null;
            list.Add(direct ? Handler(harness, entry, brim) : Group(harness, entry, brim));
        }
        return root;
    }

    private static JsonObject Group(AgentHarness harness, HookEntry entry, string brim)
    {
        var group = new JsonObject();
        if (entry.Matcher is not null) group["matcher"] = entry.Matcher;
        group["hooks"] = new JsonArray(Handler(harness, entry, brim));
        return group;
    }

    private static JsonObject Handler(AgentHarness harness, HookEntry entry, string brim)
    {
        var handler = new JsonObject();
        if (harness.Shape != HookFileShape.Flat) handler["type"] = "command";
        if (harness.Style == HookCommandStyle.Process)
        {
            handler["type"] = "process";
            handler["command"] = brim;
            handler["args"] = new JsonArray([.. Arguments(harness).Select(a => (JsonNode)JValue.Create(a)!)]);
            handler["timeoutMs"] = (entry.Waits ? WaitingTimeoutSeconds : 10) * 1000;
            return handler;
        }
        if (harness.Style == HookCommandStyle.Exec)
        {
            handler["command"] = brim;
            handler["args"] = new JsonArray([.. Arguments(harness).Select(a => (JsonNode)JValue.Create(a)!)]);
        }
        else handler["command"] = CommandLine(harness, brim);
        if (entry.Waits) handler["timeout"] = WaitingTimeoutSeconds;
        else if (harness.Shape == HookFileShape.Nested && harness.Async) { handler["async"] = true; handler["timeout"] = 10; }
        else handler["timeout"] = 10;
        if (entry.Matcher is not null && harness.Shape == HookFileShape.Flat) handler["matcher"] = entry.Matcher;
        return handler;
    }

    private static JsonObject Remove(AgentHarness harness, JsonObject root)
    {
        if (harness.Shape == HookFileShape.Named)
        {
            // BrimDeck's named hook goes whole once it holds a hook of BrimDeck's, or nothing but its switch.
            if (root[HookName] is JsonObject own && (Handlers(own).Any(Ours) || own.All(p => p.Key == "enabled"))) root.Remove(HookName);
            return root;
        }
        var block = root["hooks"] as JsonObject;
        var hooks = harness.Shape == HookFileShape.Events ? block?["events"] as JsonObject : block;
        if (hooks is null) return root;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray list) continue;
            foreach (var item in list.ToList())
            {
                if (harness.Shape == HookFileShape.Flat || item is JsonObject && item["hooks"] is null) { if (Ours(item)) list.Remove(item); continue; }
                if (item?["hooks"] is not JsonArray handlers) continue;
                foreach (var handler in handlers.ToList()) if (Ours(handler)) handlers.Remove(handler);
                if (handlers.Count == 0) list.Remove(item);
            }
            if (list.Count == 0) hooks.Remove(name);
        }
        if (harness.Shape == HookFileShape.Events)
        {
            // The block goes once nothing is left in it but the switch BrimDeck turned on.
            if (hooks.Count == 0) block!.Remove("events");
            if (block!.All(p => p.Key == "enabled")) root.Remove("hooks");
            return root;
        }
        if (hooks.Count == 0) root.Remove("hooks");
        return root;
    }

    // Every handler of a named hook, in groups or not.
    private static IEnumerable<JsonNode?> Handlers(JsonObject events)
        => events.Where(p => p.Value is JsonArray).SelectMany(p => ((JsonArray)p.Value!).SelectMany(item => item?["hooks"] is JsonArray inner ? inner.AsEnumerable() : new[] { item }));

    // BrimDeck's hooks in the order they stand, each with its event and matcher, to compare with what installing would give.
    private static List<JsonNode> Ours(AgentHarness harness, JsonObject root)
    {
        var found = new List<JsonNode>();
        var container = root[harness.Shape == HookFileShape.Named ? HookName : "hooks"];
        if ((harness.Shape == HookFileShape.Events ? container?["events"] : container) is not JsonObject hooks) return found;
        foreach (var (name, value) in hooks)
        {
            if (value is not JsonArray list) continue;
            foreach (var item in list)
            {
                if (item is JsonObject && item["hooks"] is null) { if (Ours(item)) found.Add(new JsonObject { ["event"] = name, ["hook"] = item.DeepClone() }); continue; }
                if (item?["hooks"] is not JsonArray handlers) continue;
                foreach (var handler in handlers)
                    if (Ours(handler)) found.Add(new JsonObject { ["event"] = name, ["matcher"] = item["matcher"]?.DeepClone(), ["hook"] = handler!.DeepClone() });
            }
        }
        // ZCode's file hooks run only while switched on: BrimDeck's are in place only with the switch on.
        if (harness.Shape == HookFileShape.Events && found.Count > 0) found.Add(new JsonObject { ["enabled"] = container?["enabled"]?.DeepClone() });
        return [.. found.OrderBy(n => n.ToJsonString(), StringComparer.Ordinal)];
    }
}

// Hooks kept as [[hooks]] tables of a TOML file (Kimi Code's config.toml). The file is changed as text, so that the user's
// comments and layout stay: BrimDeck's tables are added at the end, and found again by their command. Kimi Code reads a table
// strictly (event, matcher, command, timeout and nothing else), so nothing marks them but the command itself.
internal static class TomlHooks
{
    private static readonly string[] Keys = ["event", "matcher", "command", "timeout"];

    public static HookState State(AgentHarness harness, string? text, string brim)
    {
        if (text is null) return HookState.Missing;
        var lines = Lines(text);
        var ours = Ours(lines);
        if (ours.Count == 0) return HookState.Missing;
        var expected = Tables(harness, brim);
        var found = ours.Select(range => lines[range.Start..range.End].Select(line => line.Trim()).ToArray()).ToList();
        return found.Count == expected.Count && found.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)) ? HookState.Installed : HookState.Outdated;
    }

    // Null when the file holds its hooks as one inline array, which tables cannot be added to.
    public static string? Installed(AgentHarness harness, string? text, string brim)
    {
        string kept = Removed(text ?? "");
        if (Lines(kept).TakeWhile(line => !line.TrimStart().StartsWith('[')).Any(line => Key(line) == "hooks")) return null;
        string newline = Newline(text);
        var builder = new System.Text.StringBuilder(kept.TrimEnd());
        foreach (var table in Tables(harness, brim))
        {
            if (builder.Length > 0) builder.Append(newline).Append(newline);
            builder.Append(string.Join(newline, table));
        }
        return builder.Append(newline).ToString();
    }

    public static string Removed(string text)
    {
        var lines = Lines(text);
        var ours = Ours(lines);
        if (ours.Count == 0) return text;
        var keep = new List<string>();
        int at = 0;
        foreach (var (start, end) in ours)
        {
            keep.AddRange(lines[at..start]);
            // The blank lines around a table go with it; one stays between what was before and what comes after.
            while (keep.Count > 0 && keep[^1].Trim().Length == 0) keep.RemoveAt(keep.Count - 1);
            at = end;
            while (at < lines.Length && lines[at].Trim().Length == 0) at++;
            if (at < lines.Length && keep.Count > 0) keep.Add("");
        }
        keep.AddRange(lines[at..]);
        while (keep.Count > 0 && keep[^1].Trim().Length == 0) keep.RemoveAt(keep.Count - 1);
        string newline = Newline(text);
        return keep.Count == 0 ? "" : string.Join(newline, keep) + newline;
    }

    private static string Newline(string? text) => text is { Length: > 0 } ? (text.Contains("\r\n") ? "\r\n" : "\n") : Environment.NewLine;

    private static string[] Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];

    // The tables BrimDeck writes, one for each hook, as their lines.
    private static List<string[]> Tables(AgentHarness harness, string brim) => [.. harness.Hooks.Select(entry => (string[])
    [
        "[[hooks]]", "event = " + Basic(entry.Event), .. entry.Matcher is { } matcher ? new[] { "matcher = " + Basic(matcher) } : [],
        "command = " + Basic(AgentHarnesses.CommandLine(harness, brim)), "timeout = " + (entry.Waits ? Math.Min(600, AgentHarnesses.WaitingTimeoutSeconds) : 10)
    ])];

    // The [[hooks]] tables whose command is BrimDeck's, as ranges of lines: the header and the keys that follow it.
    private static List<(int Start, int End)> Ours(string[] lines)
    {
        var found = new List<(int, int)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "[[hooks]]") continue;
            int end = i + 1;
            bool ours = false;
            for (; end < lines.Length && Key(lines[end]) is { } key && Keys.Contains(key); end++)
                if (key == "command" && AgentHarnesses.Ours(Value(lines[end]))) ours = true;
            if (ours) found.Add((i, end));
            i = end - 1;
        }
        return found;
    }

    // The bare key of a "key = value" line.
    private static string? Key(string line)
    {
        int equals = line.IndexOf('=');
        string key = equals > 0 ? line[..equals].Trim() : "";
        return key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : null;
    }

    // A line's value: a basic string without its escapes, a literal string, or the words as they are.
    private static string Value(string line)
    {
        string value = line[(line.IndexOf('=') + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value.Length < 2 || value[0] != '"') return value;
        var text = new System.Text.StringBuilder();
        for (int i = 1; i < value.Length && value[i] != '"'; i++) text.Append(value[i] == '\\' && i + 1 < value.Length ? value[++i] : value[i]);
        return text.ToString();
    }

    private static string Basic(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

// Reads and writes the harnesses' settings files. Before BrimDeck first changes a file, a copy is kept beside it
// (<file>.brimdeck-backup); a file that is not JSON is left as it is.
public static class AgentHookFiles
{
    public const string BackupSuffix = ".brimdeck-backup";

    // brim as a tool that runs its hooks through Git Bash or cmd is given it: a path with spaces in its short form where the
    // volume has short names, which no shell needs to quote (a tool may hand cmd the quotes escaped, which cmd does not read).
    public static string Program(AgentHarness harness, string brim)
        => harness.Style is HookCommandStyle.Shell or HookCommandStyle.Cmd && brim.Contains(' ') && Short(brim) is { } shortened && !shortened.Contains(' ') ? shortened : brim;

    public static HookState State(AgentHarness harness, AgentPaths paths, string brim)
    {
        try { return AgentHarnesses.State(harness, Read(harness.SettingsFile(paths)), Program(harness, brim)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HookState.Unreadable; }
    }

    // Null when done; otherwise why not.
    public static string? Install(AgentHarness harness, AgentPaths paths, string brim)
        => Change(harness.SettingsFile(paths), text => AgentHarnesses.Installed(harness, text, Program(harness, brim)), plugin: harness.Shape == HookFileShape.PluginFile);

    public static string? Uninstall(AgentHarness harness, AgentPaths paths)
        => Change(harness.SettingsFile(paths), text => text is null ? null : AgentHarnesses.Removed(harness, text), missingIsDone: true, plugin: harness.Shape == HookFileShape.PluginFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint size);

    private static string? Short(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static string? Read(string file) => File.Exists(file) ? File.ReadAllText(file) : null;

    // plugin: the file is a plugin of BrimDeck's own, kept without a copy and deleted when emptied; one of the same name that is
    // not BrimDeck's is left as it is.
    private static string? Change(string file, Func<string?, string?> change, bool missingIsDone = false, bool plugin = false)
    {
        try
        {
            string? text = Read(file);
            if (text is null && missingIsDone) return null;
            if (change(text) is not { } changed)
                return plugin ? Loc.T("同名文件不是 BrimDeck 写入的，未作修改：", "A file of the same name that BrimDeck did not write was left as it is: ") + file
                    : Loc.T("设置文件不是有效的 JSON，未作修改：", "The settings file is not valid JSON and was left as it is: ") + file;
            if (changed == text) return null;
            if (plugin && changed.Length == 0) { File.Delete(file); return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (text is not null && !plugin && !File.Exists(file + BackupSuffix)) File.Copy(file, file + BackupSuffix);
            string temporary = file + ".brimdeck-tmp";
            File.WriteAllText(temporary, changed, new System.Text.UTF8Encoding(false));
            File.Move(temporary, file, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
