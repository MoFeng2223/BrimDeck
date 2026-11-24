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
    // One command line run by cmd /C (Codex, Antigravity).
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
    // A plugin file of BrimDeck's own, which the tool loads from its plugin folder (OpenCode): the whole file is BrimDeck's.
    PluginFile
}

// One hook BrimDeck adds: the event, its matcher (null: every one), and whether the tool waits for it (an approval, a question)
// or runs it in the background (a state report).
public sealed record HookEntry(string Event, string? Matcher, bool Waits);

// A coding agent ("harness") whose hooks BrimDeck can install: where its settings are, how they hold hooks, and which events
// it reports. Tools that share one settings file are one harness (the Claude desktop app and the Claude Code CLI; the
// Antigravity app, IDE and CLI). A tool found but without hooks BrimDeck can write has Unsupported, which says why.
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
    string? AfterInstall = null,
    Func<string>? Unsupported = null,
    // Whether the tool reads "async" (runs a hook in the background); one that does not gets none.
    bool Async = true);

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
public enum HookState { Missing, Installed, Outdated, Unreadable, Unsupported }

public static class AgentHarnesses
{
    // Long enough for an approval or a question to be answered on the island: BrimDeck's own wait ends first (AgentWait, at
    // most NoticeProtocol.MaxTimeoutMs), so the tool never cancels the hook before BrimDeck lets it go.
    public const int WaitingTimeoutSeconds = (int)(NoticeProtocol.MaxTimeoutMs / 1000) + 60;
    // The name of BrimDeck's hook in a settings file of named hooks (HookFileShape.Named).
    public const string HookName = "brimdeck";

    private static readonly HookEntry[] ClaudeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", "*", false), new("Stop", null, false),
        new("Notification", null, false), new("SessionEnd", null, false),
        new("PermissionRequest", "*", true), new("PreToolUse", "AskUserQuestion", true)
    ];

    private static readonly HookEntry[] CodexHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("Stop", null, false),
        new("PermissionRequest", null, true)
    ];

    private static readonly HookEntry[] CursorHooks =
    [
        new("sessionStart", null, false), new("beforeSubmitPrompt", null, false), new("postToolUse", null, false), new("stop", null, false), new("sessionEnd", null, false)
    ];

    // ZCode's own agent knows seven events (no Notification, no SessionEnd); its hooks always run inline.
    private static readonly HookEntry[] ZCodeHooks =
    [
        new("SessionStart", null, false), new("UserPromptSubmit", null, false), new("PostToolUse", null, false), new("Stop", null, false),
        new("PermissionRequest", null, true), new("PreToolUse", "AskUserQuestion", true)
    ];

    // Antigravity's hooks block its loop, and it has no event for an approval or a question: the session's start, each call of the
    // model (which carries the user's last words) and the loop's end.
    private static readonly HookEntry[] AntigravityHooks = [new("SessionStart", null, false), new("PreInvocation", null, false), new("Stop", null, false)];

    // A tool built on Claude Code that reads hooks from its own folder in Claude Code's format.
    private static AgentHarness ClaudeLike(string id, string name, Func<string> components, string folder, string? variable, Func<AgentPaths, IEnumerable<string>> evidence)
        => new(id, name, AgentHookFormats.Claude, components, p => Path.Combine(p.Under(variable, folder), "settings.json"), evidence,
            HookFileShape.Nested, HookCommandStyle.Shell, ClaudeHooks, Approvals: true, Questions: true, Async: false);

    // A tool found whose sessions cannot be connected with a command hook.
    private static AgentHarness Unconnectable(string id, string name, Func<string> components, Func<AgentPaths, IEnumerable<string>> evidence, Func<string> why)
        => new(id, name, "", components, _ => "", evidence, HookFileShape.Nested, HookCommandStyle.Cmd, [], Approvals: false, Questions: false, Unsupported: why);

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
            AfterInstall: "codex-review"),
        new("antigravity", "Antigravity", AgentHookFormats.Antigravity, () => Loc.T("Antigravity 桌面版、Antigravity IDE 与 Antigravity CLI", "Antigravity desktop app, Antigravity IDE and Antigravity CLI"),
            p => Path.Combine(p.Home, ".gemini", "config", "hooks.json"),
            p => [Path.Combine(p.Local, "agy", "bin", "agy.exe"), Path.Combine(p.Local, "Programs", "antigravity", "Antigravity.exe"), Path.Combine(p.Home, ".gemini", "antigravity"), Path.Combine(p.Home, ".gemini", "antigravity-ide"), Path.Combine(p.Home, ".gemini", "antigravity-cli")],
            HookFileShape.Named, HookCommandStyle.Cmd, AntigravityHooks, Approvals: false, Questions: false),
        new("cursor", "Cursor", AgentHookFormats.Cursor, () => Loc.T("Cursor 编辑器与 Cursor CLI", "Cursor editor and Cursor CLI"),
            p => Path.Combine(p.Home, ".cursor", "hooks.json"),
            p => [Path.Combine(p.Local, "Programs", "cursor", "Cursor.exe"), .. p.OnPath("cursor-agent.exe"), .. p.OnPath("cursor.cmd")],
            HookFileShape.Flat, HookCommandStyle.PowerShell, CursorHooks, Approvals: false, Questions: false),
        ClaudeLike("codeagent", "CodeAgent", () => Loc.T("CodeAgent CLI", "CodeAgent CLI"), ".cac", "CODEAGENT3_CONFIG_DIR",
            p => [Path.Combine(p.Under("CODEAGENT3_CONFIG_DIR", ".cac"), "settings.json"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CodeAgentCLI"), .. p.OnPath("codeagent.exe")]),
        ClaudeLike("qoder", "Qoder", () => Loc.T("Qoder CLI 与 Qoder 桌面版", "Qoder CLI and Qoder desktop app"), ".qoder", "QODER_CONFIG_DIR",
            p => [Path.Combine(p.Under("QODER_CONFIG_DIR", ".qoder"), "bin", "qodercli"), Path.Combine(p.Under("QODER_CONFIG_DIR", ".qoder"), "settings.json")]),
        ClaudeLike("qoder-cn", "Qoder CN", () => Loc.T("Qoder CN CLI 与 Qoder CN 桌面版", "Qoder CN CLI and Qoder CN desktop app"), ".qoder-cn", "QODERCN_CONFIG_DIR",
            p => [Path.Combine(p.Under("QODERCN_CONFIG_DIR", ".qoder-cn"), "bin"), Path.Combine(p.Under("QODERCN_CONFIG_DIR", ".qoder-cn"), "settings.json")]),
        ClaudeLike("workbuddy", "WorkBuddy", () => Loc.T("WorkBuddy 桌面版（内置 CodeBuddy Code）", "WorkBuddy desktop app (with CodeBuddy Code)"), ".workbuddy", null,
            p => [Path.Combine(p.Home, ".workbuddy", "settings.json"), Path.Combine(p.Home, ".workbuddy", "projects")]),
        new("opencode", "OpenCode", AgentHookFormats.OpenCode, () => Loc.T("OpenCode CLI 与 OpenCode 桌面版（通过插件）", "OpenCode CLI and OpenCode desktop app (through a plugin)"),
            p => Path.Combine(p.Under("XDG_CONFIG_HOME", ".config", "opencode", "plugins"), OpenCodePlugin.FileName),
            p => [p.Under("XDG_CONFIG_HOME", ".config", "opencode"), Path.Combine(p.Home, ".local", "share", "opencode", "opencode.db"), .. p.OnPath("opencode.cmd"), .. p.OnPath("opencode.exe")],
            HookFileShape.PluginFile, HookCommandStyle.Exec, [], Approvals: true, Questions: true),
        Unconnectable("dsh", "DeepSeek Harness", () => Loc.T("DeepSeek Harness 桌面版与 dsh", "DeepSeek Harness desktop app and dsh"),
            p => [Path.Combine(p.Home, ".dsh")],
            () => Loc.T("DeepSeek Harness 没有命令钩子，只能通过插件接入，暂不支持自动安装", "DeepSeek Harness has no command hooks, only plugins; installing is not supported yet")),
        Unconnectable("qwenwork", "QwenWork", () => Loc.T("QwenWork 桌面版", "QwenWork desktop app"),
            p => [Path.Combine(p.Home, ".qwenwork")],
            () => Loc.T("QwenWork 每次启动生成临时配置，没有固定的钩子文件，暂不支持", "QwenWork builds a new configuration at each start and has no lasting hooks file; not supported yet")),
        Unconnectable("qwenwork-cn", "QwenWork CN", () => Loc.T("千问办公桌面版", "QwenWork CN desktop app"),
            p => [Path.Combine(p.Home, ".qwenworkcn")],
            () => Loc.T("QwenWork CN 每次启动生成临时配置，没有固定的钩子文件，暂不支持", "QwenWork CN builds a new configuration at each start and has no lasting hooks file; not supported yet")),
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
        if (harness.Unsupported is not null) return HookState.Unsupported;
        if (harness.Shape == HookFileShape.PluginFile)
            return text is null ? HookState.Missing : text == OpenCodePlugin.Text(brim) ? HookState.Installed : OpenCodePlugin.Ours(text) ? HookState.Outdated : HookState.Unreadable;
        if (Parse(text, out bool unreadable) is not { } root) return unreadable ? HookState.Unreadable : HookState.Missing;
        var expected = Install(harness, root.DeepClone().AsObject(), brim);
        var found = Ours(harness, root);
        if (found.Count == 0) return HookState.Missing;
        var wanted = Ours(harness, expected);
        return found.Count == wanted.Count && found.Zip(wanted).All(pair => JsonNode.DeepEquals(pair.First, pair.Second)) ? HookState.Installed : HookState.Outdated;
    }

    // The text with BrimDeck's hooks in place (any of its earlier ones replaced), or null when the file cannot be read as JSON.
    public static string? Installed(AgentHarness harness, string? text, string brim)
        => harness.Shape == HookFileShape.PluginFile ? (text is null || OpenCodePlugin.Ours(text) ? OpenCodePlugin.Text(brim) : null) : harness.Unsupported is null && Parse(text, out _) is { } root ? Write(Install(harness, root, brim)) : null;

    // The text without BrimDeck's hooks, or null when the file cannot be read as JSON. Groups and events left empty go too.
    // A plugin file of BrimDeck's own is removed whole: the text is then empty, and the file is deleted.
    public static string? Removed(AgentHarness harness, string? text)
        => harness.Shape == HookFileShape.PluginFile ? (text is null || OpenCodePlugin.Ours(text) ? "" : null) : Parse(text, out _) is { } root ? Write(Remove(harness, root)) : null;

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
        if (harness.Shape == HookFileShape.Flat) root["version"] ??= 1;
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

// Reads and writes the harnesses' settings files. Before BrimDeck first changes a file, a copy is kept beside it
// (<file>.brimdeck-backup); a file that is not JSON is left as it is.
public static class AgentHookFiles
{
    public const string BackupSuffix = ".brimdeck-backup";

    // brim as a tool that runs its hooks through a shell is given it: a path with spaces in its short form where the volume has
    // short names, which no shell needs to quote.
    public static string Program(AgentHarness harness, string brim)
        => harness.Style == HookCommandStyle.Shell && brim.Contains(' ') && Short(brim) is { } shortened && !shortened.Contains(' ') ? shortened : brim;

    public static HookState State(AgentHarness harness, AgentPaths paths, string brim)
    {
        if (harness.Unsupported is not null) return HookState.Unsupported;
        try { return AgentHarnesses.State(harness, Read(harness.SettingsFile(paths)), Program(harness, brim)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HookState.Unreadable; }
    }

    // Null when done; otherwise why not.
    public static string? Install(AgentHarness harness, AgentPaths paths, string brim) => harness.Unsupported is { } why ? why()
        : Change(harness.SettingsFile(paths), text => AgentHarnesses.Installed(harness, text, Program(harness, brim)), plugin: harness.Shape == HookFileShape.PluginFile);

    public static string? Uninstall(AgentHarness harness, AgentPaths paths) => harness.Unsupported is not null ? null
        : Change(harness.SettingsFile(paths), text => text is null ? null : AgentHarnesses.Removed(harness, text), missingIsDone: true, plugin: harness.Shape == HookFileShape.PluginFile);

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
