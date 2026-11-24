using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// The coding agents' hooks: what "brim hook" makes of each tool's hook input and prints back, the sessions BrimDeck keeps from
// the reports, the hooks written into and taken out of the tools' settings files, and the questions a notice carries.
internal static class AgentHookTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        Commands(check);
        Plans(check);
        Replies(check);
        Wire(check);
        Sessions(check);
        Settings(check);
        Watched(check);
        MoreTools(check);
    }

    private static AgentHookPlan Kimi(string json) => AgentHook.Plan(AgentHookFormats.Kimi, "kimi", "Kimi Code", Input(json), chinese: true);
    private static AgentHookPlan Trae(string json) => AgentHook.Plan(AgentHookFormats.Trae, "trae", "TRAE", Input(json), chinese: true);

    // Kimi Code and TRAE: approvals and questions are answered in the tool; the hook tells of them and never waits.
    private static void Watched(Action<string, bool> check)
    {
        var approval = Kimi("""{"hook_event_name":"PermissionRequest","session_id":"session_1","cwd":"D:\\w\\app","id":"approval_1","tool_name":"Bash","tool_input":{"command":"npm test"}}""");
        check("Kimi: an approval is told with a notice that stays, and nothing waits for it",
            approval.Ask is null && approval.Report is { State: "approval", Title: "请求运行命令", AppId: "kimi", Context: "app" }
            && approval.Notice is { Level: "persistent", ReplaceId: "agent:kimi:session_1", Code: "npm test", Category: "warning", Wait: null, Actions: null, Body: "请在 Kimi Code 中处理" });
        var result = Kimi("""{"hook_event_name":"PermissionResult","session_id":"session_1","decision":"approved"}""");
        check("Kimi: the approval answered in Kimi Code closes the notice and the session works again",
            result.Report?.State == "working" && result.Close is { ReplaceId: "agent:kimi:session_1", Stop: true, AppName: "Kimi Code" } && result.Notice is null);
        var question = Kimi("""{"hook_event_name":"PreToolUse","session_id":"session_1","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which one?","options":[{"label":"A"}]}]}}""");
        check("Kimi: a question is told, and its answer closes the notice",
            question.Ask is null && question.Report?.State == "question" && question.Notice is { Body: "Which one?", Category: "info", Level: "persistent" }
            && Kimi("""{"hook_event_name":"PostToolUse","session_id":"session_1","tool_name":"AskUserQuestion"}""").Close is not null
            && Kimi("""{"hook_event_name":"PostToolUse","session_id":"session_1","tool_name":"Bash"}""") is { Close: null, Report.State: "working" }
            && Kimi("""{"hook_event_name":"PreToolUse","session_id":"session_1","tool_name":"Bash"}""").Report is null);
        check("Kimi: a prompt given as parts is read, and the turn's ends are told apart",
            Kimi("""{"hook_event_name":"UserPromptSubmit","session_id":"s","prompt":[{"type":"text","text":"fix"},{"type":"image"},{"type":"text","text":"it"}]}""").Report is { State: "working", Title: "fix it" }
            && Kimi("""{"hook_event_name":"Stop","session_id":"s","stop_hook_active":false}""") is { Report.State: "done", Notice: { Category: "success", Body: null }, Close: not null }
            && Kimi("""{"hook_event_name":"StopFailure","session_id":"s","error_message":"quota"}""").Notice is { Category: "error", Body: "quota" }
            && Kimi("""{"hook_event_name":"Interrupt","session_id":"s"}""") is { Report.State: "idle", Notice: null }
            && Kimi("""{"hook_event_name":"SessionEnd","session_id":"s"}""").Report?.State == "ended");
        var waits = Trae("""{"hook_event_name":"Notification","session_id":"t1","workspace_roots":["D:/w/site"],"notification_type":"permission_prompt","message":"Run npm install?"}""");
        check("TRAE: a notification tells of an approval or a question, and the next tool result closes it",
            waits.Ask is null && waits.Report is { State: "approval", Context: "site" } && waits.Notice is { Title: "等待审批", Body: "Run npm install?", Level: "persistent", ReplaceId: "agent:trae:t1" }
            && Trae("""{"hook_event_name":"Notification","session_id":"t1","notification_type":"ask_user_question"}""") is { Report.State: "question", Notice.Body: "请在 TRAE 中处理" }
            && Trae("""{"hook_event_name":"Notification","session_id":"t1","notification_type":"idle_prompt"}""") is { Report.State: "idle", Notice: null }
            && Trae("""{"hook_event_name":"PostToolUse","session_id":"t1","tool_name":"RunCommand"}""") is { Report.State: "working", Close: not null }
            && Trae("""{"hook_event_name":"Stop","session_id":"t1","last_assistant_message":"Done."}""") is { Report: { State: "done", Body: "Done." }, Notice.Body: "Done." });
        var paths = new AgentPaths(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", name => name == "KIMI_CODE_HOME" ? @"D:\kimi" : null);
        var kimi = AgentHarnesses.Find("kimi")!;
        const string brim = @"C:\Program Files\BrimDeck\brim.exe";
        string config = "default_model = \"kimi\"\r\n\r\n[[hooks]]\r\nevent = \"Stop\"\r\ncommand = \"mine.exe\"\r\n\r\n[mcp]\r\nenabled = true\r\n";
        string installed = AgentHarnesses.Installed(kimi, config, brim)!;
        check("Kimi: hooks are [[hooks]] tables added to the end of config.toml, with only the four keys Kimi Code reads",
            kimi.SettingsFile(paths) == @"D:\kimi\config.toml" && installed.StartsWith(config.TrimEnd(), StringComparison.Ordinal) && installed.Contains("\r\n\r\n[[hooks]]\r\nevent = \"SessionStart\"\r\ncommand = \"\\\"C:\\\\Program Files\\\\BrimDeck\\\\brim.exe\\\" hook kimi\"\r\ntimeout = 10\r\n")
            && installed.Contains("event = \"PreToolUse\"\r\nmatcher = \"^AskUserQuestion$\"\r\ncommand = ") && installed.Contains("event = \"PermissionResult\"") && !installed.Contains("async")
            && installed.Split("[[hooks]]").Length - 1 == kimi.Hooks.Count + 1);
        check("Kimi: installed reads as installed, installing again changes nothing, and a moved BrimDeck needs an update",
            AgentHarnesses.State(kimi, installed, brim) == HookState.Installed && AgentHarnesses.Installed(kimi, installed, brim) == installed
            && AgentHarnesses.State(kimi, config, brim) == HookState.Missing && AgentHarnesses.State(kimi, null, brim) == HookState.Missing
            && AgentHarnesses.State(kimi, installed, @"D:\B\brim.exe") == HookState.Outdated
            && AgentHarnesses.Installed(kimi, installed, @"D:\B\brim.exe")!.Split("[[hooks]]").Length - 1 == kimi.Hooks.Count + 1);
        check("Kimi: removing takes only BrimDeck's tables and leaves the user's file as it was",
            AgentHarnesses.Removed(kimi, installed) == config && AgentHarnesses.Removed(kimi, config) == config
            && AgentHarnesses.Removed(kimi, AgentHarnesses.Installed(kimi, null, brim)) == "");
        check("Kimi: hooks kept as one inline array cannot take tables, and the file is left as it is",
            AgentHarnesses.Installed(kimi, "hooks = []\n", brim) is null && AgentHarnesses.Installed(kimi, "[plugin]\nhooks = []\n", brim) is not null);
        var trae = AgentHarnesses.Find("trae")!;
        var traeFile = JsonNode.Parse(AgentHarnesses.Installed(trae, null, brim)!)!;
        check("TRAE: each edition has its own hooks.json, in Claude Code's nesting with a version and a PowerShell command line",
            trae.SettingsFile(paths) == @"C:\Users\me\.trae\hooks.json" && AgentHarnesses.Find("trae-cn")!.SettingsFile(paths) == @"C:\Users\me\.trae-cn\hooks.json"
            && (int?)traeFile["version"] == 1 && (string?)traeFile["hooks"]!["Notification"]![0]!["hooks"]![0]!["command"] == "& 'C:\\Program Files\\BrimDeck\\brim.exe' hook trae"
            && (string?)traeFile["hooks"]!["Stop"]![0]!["hooks"]![0]!["type"] == "command" && traeFile["hooks"]!["Stop"]![0]!["hooks"]![0]!["async"] is null
            && traeFile["hooks"]!["PreToolUse"] is null && traeFile["hooks"]!["SessionEnd"] is null
            && AgentHarnesses.CommandLine(AgentHarnesses.Find("trae-cn")!, @"C:\B\brim.exe") == "C:\\B\\brim.exe hook trae --name \"TRAE CN\" --id trae-cn"
            && AgentHarnesses.State(trae, traeFile.ToJsonString(), brim) == HookState.Installed && trae.Tells && trae.AfterInstall is not null);
    }

    // WorkBuddy AI, Command Code, MiMo Code, and what the tools' own code showed of the earlier ones.
    private static void MoreTools(Action<string, bool> check)
    {
        var paths = new AgentPaths(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", _ => null);
        const string brim = @"C:\B\brim.exe";
        var buddy = AgentHarnesses.Find("workbuddy")!;
        var buddyAi = AgentHarnesses.Find("workbuddy-ai")!;
        var buddyHooks = JsonNode.Parse(AgentHarnesses.Installed(buddyAi, null, brim)!)!["hooks"]!;
        check("WorkBuddy: each edition has its own settings file, and no hook waits for a question",
            buddy.SettingsFile(paths) == @"C:\Users\me\.workbuddy\settings.json" && buddyAi.SettingsFile(paths) == @"C:\Users\me\.workbuddy-ai\settings.json"
            && buddyHooks["PreToolUse"] is null && buddyHooks["PermissionRequest"] is not null && !buddy.Questions && buddy.Approvals
            && (string?)buddyHooks["Stop"]![0]!["hooks"]![0]!["command"] == "C:/B/brim.exe hook codebuddy --name \"WorkBuddy AI\" --id workbuddy-ai"
            && (string?)JsonNode.Parse(AgentHarnesses.Installed(buddy, null, brim)!)!["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"] == "C:/B/brim.exe hook codebuddy --name WorkBuddy --id workbuddy");
        var asked = AgentHook.Plan(AgentHookFormats.CodeBuddy, "workbuddy", "WorkBuddy", Input("""
            {"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":[{"rule":{"toolName":"Bash"},"behavior":"allow"}]}
            """), true);
        check("WorkBuddy: an approval offers no always allow, which its hooks cannot give, and a question is left to WorkBuddy",
            asked.Ask is { Wait: true } a && !a.Actions!.Any(x => x.Key == AgentHook.AlwaysAllow)
            && JsonNode.Parse(asked.Reply(Chosen(AgentHook.Allow))!)!["hookSpecificOutput"]!["decision"]!["behavior"]!.ToString() == "allow"
            && AgentHook.Plan(AgentHookFormats.CodeBuddy, "workbuddy", "WorkBuddy", Input("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Q"}]}}"""), true).Ask is null);
        var command = AgentHarnesses.Find("commandcode")!;
        string mod = AgentHarnesses.Installed(command, null, @"C:\B b\brim.exe")!;
        check("Command Code: BrimDeck's own mod file in the mods folder answers approvals and questions in Claude Code's words",
            command.SettingsFile(paths) == @"C:\Users\me\.commandcode\mods\brimdeck.ts" && mod.StartsWith(CommandCodeMod.Marker, StringComparison.Ordinal)
            && mod.Contains("""const BRIM = "C:\\B b\\brim.exe" """.TrimEnd()) && mod.Contains("""["hook", "claude", "--name", "Command Code", "--id", "commandcode"]""")
            && mod.Contains("onPermissionRequest") && mod.Contains("export default function (cmd)") && !mod.Contains('\r')
            && AgentHarnesses.State(command, mod, @"C:\B b\brim.exe") == HookState.Installed && AgentHarnesses.State(command, mod, brim) == HookState.Outdated
            && AgentHarnesses.State(command, "export default () => {}", brim) == HookState.Unreadable && AgentHarnesses.Removed(command, mod) == "");
        var mimo = AgentHarnesses.Find("mimo")!;
        string mimoPlugin = AgentHarnesses.Installed(mimo, null, brim)!;
        string openPlugin = AgentHarnesses.Installed(AgentHarnesses.Find("opencode")!, null, brim)!;
        check("MiMo Code: the plugin goes to its own folder under its own name, as a named export",
            mimo.SettingsFile(paths) == @"C:\Users\me\.config\mimocode\plugins\brimdeck.js" && mimoPlugin.Contains("""["hook","opencode","--name","MiMo Code","--id","mimo"]""")
            && mimoPlugin.TrimEnd().EndsWith("export const BrimDeck = legacy", StringComparison.Ordinal) && !mimoPlugin.Contains("export default")
            && mimo.SettingsFile(new AgentPaths(@"C:\Users\me", "", "", name => name == "MIMOCODE_HOME" ? @"D:\mimo" : null)) == @"D:\mimo\config\plugins\brimdeck.js");
        check("OpenCode: one default export serves both generations, a plain object with an id, setup and server",
            openPlugin.TrimEnd().EndsWith("export default { id: \"brimdeck\", setup, server: legacy }", StringComparison.Ordinal)
            && openPlugin.Contains("ctx.permission.reply({ sessionID: d.sessionID, requestID: d.id, decision: reply.reply })") && openPlugin.Contains("session.execution.succeeded"));
        check("Cursor: Claude Code's hooks run by Cursor report nothing, so a Cursor session shows once",
            Claude("""{"hook_event_name":"Stop","session_id":"s1","cursor_version":"3.27.22"}""") is { Report: null, Notice: null }
            && AgentHook.Plan(AgentHookFormats.Cursor, "cursor", "Cursor", Input("""{"hook_event_name":"stop","conversation_id":"c1","status":"completed","cursor_version":"3.27.22"}"""), true).Report?.State == "done");
        check("Antigravity: the workspace's folder, a lower-case reason and a loop that goes on are read",
            Antigravity("""{"hookEventName":"SessionStart","conversationId":"c1","workspacePaths":["C:\\w\\site"]}""").Report?.Context == "site"
            && Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"error","error":"quota"}""").Notice?.Category == "error"
            && Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"user_canceled"}""") is { Report.State: "idle", Notice: null }
            && Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"NO_TOOL_CALL","fullyIdle":false}""") is { Report.State: "working", Notice: null, Quiet: """{"decision":"stop"}""" });
        check("Codex: a patch is a file change, though it comes as a command",
            AgentHook.Plan(AgentHookFormats.Codex, "codex", "Codex", Input("""{"hook_event_name":"PermissionRequest","session_id":"t","tool_name":"apply_patch","tool_input":{"command":"*** Begin Patch"}}"""), true).Ask?.Title == "请求修改文件");
        check("ZCode: a tool that failed is reported too", AgentHarnesses.Find("zcode")!.Hooks.Any(h => h.Event == "PostToolUseFailure"));
    }

    private static JsonElement Input(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    private static AgentHookPlan Claude(string json) => AgentHook.Plan(AgentHookFormats.Claude, "claude", "Claude Code", Input(json), chinese: true);

    private static void Commands(Action<string, bool> check)
    {
        check("Hook command: brim hook claude takes the format's own name and id", BrimCommandLine.Parse(["hook", "claude"]) is { Kind: NotifyCommandKind.Hook, Hook: { Format: "claude", Name: "Claude Code", Id: "claude" } });
        check("Hook command: --name and --id name a tool that uses another tool's format",
            BrimCommandLine.Parse(["hook", "claude", "--name", "Qoder CLI", "--id", "qoder"]) is { Hook: { Format: "claude", Name: "Qoder CLI", Id: "qoder" } }
            && BrimCommandLine.Parse(["hook", "claude", "--name", "My Tool"]).Hook?.Id == "my-tool");
        check("Hook command: an unknown format, a missing one and a stray option are refused",
            BrimCommandLine.Parse(["hook", "vim"]).Kind == NotifyCommandKind.Error && BrimCommandLine.Parse(["hook"]).Kind == NotifyCommandKind.Error
            && BrimCommandLine.Parse(["hook", "codex", "--wait"]).Kind == NotifyCommandKind.Error && BrimCommandLine.Parse(["hook", "codex", "--name"]).Kind == NotifyCommandKind.Error);
        check("Agent report: brim agent report builds a state report",
            BrimCommandLine.Parse(["agent", "report", "--tool", "My Tool", "--session", "s1", "--state", "Working", "--project", "app"]) is
                { Kind: NotifyCommandKind.Send, Message: { Type: NoticeTypes.Agent, AppName: "My Tool", AppId: "my-tool", Session: "s1", State: "working", Context: "app" } });
        check("Agent report: a missing field or an unknown state is refused",
            BrimCommandLine.Parse(["agent", "report", "--tool", "x", "--state", "working"]).Kind == NotifyCommandKind.Error
            && BrimCommandLine.Parse(["agent", "report", "--tool", "x", "--session", "s", "--state", "busy"]).Kind == NotifyCommandKind.Error
            && BrimCommandLine.Parse(["agent", "start"]).Kind == NotifyCommandKind.Error);
    }

    private static void Plans(Action<string, bool> check)
    {
        check("Hook plan: a session's start, a prompt and its end are reported",
            Claude("""{"hook_event_name":"SessionStart","session_id":"s1","cwd":"D:\\Projects\\BrimDeck","source":"startup"}""").Report is { Type: NoticeTypes.Agent, State: "started", Session: "s1", AppId: "claude", Context: "BrimDeck" }
            && Claude("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","prompt":"fix\nthe build"}""").Report is { State: "working", Title: "fix the build" }
            && Claude("""{"hook_event_name":"SessionEnd","session_id":"s1","reason":"other"}""").Report?.State == "ended");
        var stop = Claude("""{"hook_event_name":"Stop","session_id":"s1","cwd":"/home/me/app/","last_assistant_message":"All tests pass."}""");
        check("Hook plan: a finished turn is reported and sent as a notice that replaces the session's last one",
            stop.Report is { State: "done", Body: "All tests pass.", Context: "app" } && stop.Notice is { Title: "已完成", Category: "success", Body: "All tests pass.", ReplaceId: "agent-done:claude:s1" } && stop.Ask is null);
        check("Hook plan: without a session or an event nothing is sent", Claude("""{"hook_event_name":"Stop"}""").Report is null && Claude("""{"session_id":"s"}""").Report is null
            && Claude("[]").Report is null);
        check("Hook plan: idle waiting is reported, other notifications are not",
            Claude("""{"hook_event_name":"Notification","session_id":"s","notification_type":"idle_prompt"}""").Report?.State == "idle"
            && Claude("""{"hook_event_name":"Notification","session_id":"s","notification_type":"permission_prompt"}""").Report is null);
        var approval = Claude("""
            {"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\x\\app","tool_name":"Bash","tool_input":{"command":"dotnet test","description":"Run the tests"},
            "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"dotnet test"}],"behavior":"allow","destination":"localSettings"}]}
            """);
        check("Hook plan: an approval waits with allow, deny, always allow and the terminal, and shows the command",
            approval.Report?.State == "approval" && approval.Ask is { Wait: true, Title: "请求运行命令", Code: "dotnet test", Body: "Run the tests", ReplaceId: "agent:claude:s1", Category: "warning" } a
            && a.Actions!.Select(x => x.Key).SequenceEqual([AgentHook.Allow, AgentHook.Deny, AgentHook.AlwaysAllow, AgentHook.Terminal]) && a.Actions![0].Style == NoticeActionTypes.Primary
            && approval.Answered?.State == "working");
        var codex = AgentHook.Plan(AgentHookFormats.Codex, "codex", "Codex", Input("""{"hook_event_name":"PermissionRequest","session_id":"t","tool_name":"Bash","tool_input":{"command":"rm x"},"permission_suggestions":[{}]}"""), chinese: false);
        check("Hook plan: Codex takes no always allow, and English words follow the system's language",
            codex.Ask is { Title: "Wants to run a command" } c && !c.Actions!.Any(x => x.Key == AgentHook.AlwaysAllow) && c.Actions![0].Label == "Allow");
        check("Hook plan: a file change and another tool are named by what they touch",
            Claude("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Write","tool_input":{"file_path":"C:\\a.txt","content":"x"}}""").Ask is { Title: "请求修改文件", Code: "C:\\a.txt" }
            && Claude("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"mcp__x","tool_input":{"q":1}}""").Ask is { Title: "请求使用 mcp__x", Code: "{\"q\":1}" });
        check("Hook plan: AskUserQuestion is not asked twice through its permission",
            Claude("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"AskUserQuestion","tool_input":{}}""").Ask is null);
        check("Hook plan: other tools before they run are left alone", Claude("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"ls"}}""").Report is null);
        var cursor = AgentHook.Plan(AgentHookFormats.Cursor, "cursor", "Cursor", Input("""{"hook_event_name":"stop","conversation_id":"c1","status":"aborted","workspace_roots":["/d/w/site"]}"""), true);
        check("Hook plan: Cursor's events and fields are read, and an aborted turn tells no result",
            cursor.Report is { State: "idle", Session: "c1", Context: "site" } && cursor.Notice is null
            && AgentHook.Plan(AgentHookFormats.Cursor, "cursor", "Cursor", Input("""{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","prompt":"hi"}"""), true).Report?.State == "working"
            && AgentHook.Plan(AgentHookFormats.Cursor, "cursor", "Cursor", Input("""{"hook_event_name":"stop","conversation_id":"c1","status":"error"}"""), true).Notice?.Category == "error"
            && AgentHook.Plan(AgentHookFormats.Cursor, "cursor", "Cursor", Input("""{"hook_event_name":"permissionRequest","conversation_id":"c1","tool_name":"Shell"}"""), true).Ask is null);
        check("Hook plan: event names are compared without case or separators", AgentHook.Normalize("user_prompt_submit") == AgentHook.Normalize("UserPromptSubmit"));
    }

    private static NoticeMessage Chosen(string key) => new() { Type = NoticeTypes.Result, Result = NoticeWaits.Action, ActionKey = key };

    private static void Replies(Action<string, bool> check)
    {
        var approval = Claude("""
            {"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"Bash","tool_input":{"command":"dotnet test"},
            "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"dotnet test"}],"behavior":"allow","destination":"localSettings"}]}
            """);
        var allow = JsonNode.Parse(approval.Reply(Chosen(AgentHook.Allow))!)!;
        var deny = JsonNode.Parse(approval.Reply(Chosen(AgentHook.Deny))!)!;
        var always = JsonNode.Parse(approval.Reply(Chosen(AgentHook.AlwaysAllow))!)!;
        check("Hook reply: allow and deny are the documented decision", (string?)allow["hookSpecificOutput"]!["hookEventName"] == "PermissionRequest"
            && (string?)allow["hookSpecificOutput"]!["decision"]!["behavior"] == "allow" && allow["hookSpecificOutput"]!["decision"]!["message"] is null
            && (string?)deny["hookSpecificOutput"]!["decision"]!["behavior"] == "deny" && ((string?)deny["hookSpecificOutput"]!["decision"]!["message"])?.Length > 0);
        check("Hook reply: always allow gives the first suggestion back", (string?)always["hookSpecificOutput"]!["decision"]!["behavior"] == "allow"
            && (string?)always["hookSpecificOutput"]!["decision"]!["updatedPermissions"]![0]!["rules"]![0]!["ruleContent"] == "dotnet test");
        check("Hook reply: the terminal, a dismissal, a timeout or no result print nothing, so the tool asks as usual",
            approval.Reply(Chosen(AgentHook.Terminal)) is null && approval.Reply(new() { Type = NoticeTypes.Result, Result = NoticeWaits.Dismissed }) is null
            && approval.Reply(new() { Type = NoticeTypes.Result, Result = NoticeWaits.Expired }) is null && approval.Reply(null) is null);

        var question = Claude("""
            {"hook_event_name":"PreToolUse","session_id":"s1","tool_name":"AskUserQuestion","tool_input":{"questions":[
            {"question":"Which framework?","header":"Framework","options":[{"label":"React","description":"Component library"},{"label":"Vue","description":"Progressive"}],"multiSelect":false},
            {"question":"Which checks?","header":"Checks","options":[{"label":"Lint"},{"label":"Tests"},{"label":"Types"}],"multiSelect":true}]}}
            """);
        check("Hook plan: a question waits with each question, its options and room for words of the user's own",
            question.Report?.State == "question" && question.Ask is { Wait: true, Title: "Framework", Questions: { Count: 2 } qs, ReplaceId: "agent:claude:s1" }
            && qs[0] is { Id: "0", Header: "Framework", MultiSelect: false, AllowOther: true, Options: { Count: 2 } o } && o[1] is { Key: "1", Label: "Vue", Description: "Progressive" }
            && qs[1].MultiSelect == true && question.Ask.Actions!.Single().Key == AgentHook.Terminal);
        var answered = new NoticeMessage
        {
            Type = NoticeTypes.Result, Result = NoticeWaits.Action, ActionKey = "answer",
            Answers = [new() { Id = "0", Text = "Svelte" }, new() { Id = "1", Keys = ["0", "2"] }]
        };
        var reply = JsonNode.Parse(question.Reply(answered)!)!["hookSpecificOutput"]!;
        check("Hook reply: answers go back keyed by the question, typed words as they are and several options joined",
            (string?)reply["hookEventName"] == "PreToolUse" && (string?)reply["permissionDecision"] == "allow"
            && (string?)reply["updatedInput"]!["answers"]!["Which framework?"] == "Svelte" && (string?)reply["updatedInput"]!["answers"]!["Which checks?"] == "Lint, Types"
            && reply["updatedInput"]!["questions"]!.AsArray().Count == 2);
        check("Hook reply: an answer left out, or an option that is not there, prints nothing",
            question.Reply(new() { Type = NoticeTypes.Result, Result = NoticeWaits.Action, Answers = [new() { Id = "0", Keys = ["0"] }] }) is null
            && question.Reply(new() { Type = NoticeTypes.Result, Result = NoticeWaits.Action, Answers = [new() { Id = "0", Keys = ["7"] }, new() { Id = "1", Keys = ["0"] }] }) is null);
    }

    private static void Wire(Action<string, bool> check)
    {
        check("Agent wire: a report needs the tool, the session and a known state",
            NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.Agent, AppId = "x", Session = "s", State = "working" }) is null
            && NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.Agent, AppId = "x", Session = "s", State = "busy" }) == "bad-agent"
            && NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.Agent, Session = "s", State = "working" }) == "bad-agent");
        var many = new NoticeMessage
        {
            Title = "q", Wait = true, Code = new string('c', NoticeProtocol.MaxCode + 50),
            Questions = [.. Enumerable.Range(0, 6).Select(i => new NoticeQuestion { Question = "Q" + i, Options = [.. Enumerable.Range(0, 20).Select(j => new NoticeOption { Label = "o" + j })] })],
            Answers = [new() { Id = "", Text = "x" }, new() { Id = "0", Keys = ["", "1"] }]
        };
        NoticeProtocol.ClipTexts(many);
        check("Agent wire: questions, options and code are cut to the limits, blank ids numbered and blank answers dropped",
            many.Questions!.Count == NoticeProtocol.MaxQuestions && many.Questions[0].Options!.Count == NoticeProtocol.MaxOptions && many.Questions[2] is { Id: "2" }
            && many.Questions[0].Options![3].Key == "3" && many.Code!.Length == NoticeProtocol.MaxCode && many.Answers!.Single() is { Id: "0", Keys: ["1"] });
        var input = NoticeIntake.FromPipe(new NoticeMessage { Title = "Pick", Wait = true, Code = "ls", Questions = [new() { Id = "0", Question = "Which?\u202E", Options = [] }] });
        check("Agent wire: a waited notice takes its questions (one without options is answered in words) and its code",
            input.Questions.Single() is { Question: "Which?", AllowOther: true, MultiSelect: false } && input.Code == "ls");
        check("Agent wire: questions of a notice nobody waits for are dropped",
            NoticeIntake.FromPipe(new NoticeMessage { Title = "Pick", Questions = [new() { Question = "Which?" }] }).Questions.Count == 0);
        check("Agent keys: the key of a session's approval names the tool and the session",
            AgentKeys.Ask(AgentKeys.For("claude", "s:1")) is ("claude", "s:1") && AgentKeys.Ask("agent-done:claude:s") is null && AgentKeys.Ask("agent:x") is null && AgentKeys.Ask(null) is null);
    }

    private static NoticeMessage Report(string state, string session = "s1", string? title = null, string? body = null, string harness = "claude")
        => new() { Type = NoticeTypes.Agent, AppId = harness, AppName = "Claude Code", Session = session, State = state, Context = "app", Title = title, Body = body };

    private static void Sessions(Action<string, bool> check)
    {
        var sessions = new AgentSessions();
        check("Sessions: a report adds a session and the same one again changes nothing", sessions.Apply(Report("working", title: "fix"), T0) && !sessions.Apply(Report("working"), T0.AddSeconds(5))
            && sessions.All.Single() is { State: AgentState.Working, Prompt: "fix", Project: "app", StartedAt: var started } && started == T0);
        sessions.Apply(Report("done", body: "Fixed."), T0.AddMinutes(1));
        sessions.Apply(Report("approval", session: "s2"), T0.AddMinutes(1));
        sessions.Apply(Report("idle", session: "s3"), T0.AddMinutes(1));
        check("Sessions: waiting for the user comes first, idle ones are not shown on the collapsed row",
            sessions.Active(T0.AddMinutes(2)).Select(s => s.Id).SequenceEqual(["s2", "s1"]) && sessions.All.Count == 3);
        check("Sessions: answered on the island, a waiting session works again", sessions.Answered("claude", "s2", T0.AddMinutes(2)) && sessions.All[0] is { Id: "s2", State: AgentState.Working }
            && !sessions.Answered("claude", "s1", T0.AddMinutes(2)));
        check("Sessions: a new prompt starts the words over", sessions.Apply(Report("working", title: "next"), T0.AddMinutes(3)) && sessions.All.First(s => s.Id == "s1") is { Prompt: "next", Message: "" });
        sessions.Apply(Report("done", body: "Ok."), T0.AddMinutes(4));
        check("Sessions: a finished turn turns idle after a while, and a silent session goes",
            sessions.Age(T0.AddMinutes(4) + AgentSessions.DoneFor) && sessions.All.First(s => s.Id == "s1").State == AgentState.Idle
            && sessions.Age(T0.AddMinutes(3) + AgentSessions.Silence + TimeSpan.FromMinutes(2)) && sessions.All.Count == 0);
        sessions.Apply(Report("working"), T0);
        check("Sessions: a session that ends goes at once", sessions.Apply(Report("ended"), T0.AddSeconds(1)) && sessions.All.Count == 0);
        for (int i = 0; i < AgentSessions.Limit + 5; i++) sessions.Apply(Report("working", session: "x" + i), T0.AddSeconds(i));
        check("Sessions: at most a limit is kept, the oldest go", sessions.All.Count == AgentSessions.Limit && sessions.All.All(s => s.Id != "x0"));
    }

    private static AgentHookPlan Antigravity(string json) => AgentHook.Plan(AgentHookFormats.Antigravity, "antigravity", "Antigravity", Input(json), chinese: true);

    // Antigravity: camelCase input, the user's words with each call of the model, a JSON object printed from every hook.
    private static void AntigravityPlans(Action<string, bool> check)
    {
        check("Antigravity: brim hook antigravity is the tool's own format", BrimCommandLine.Parse(["hook", "antigravity"]) is { Hook: { Format: "antigravity", Name: "Antigravity", Id: "antigravity" } });
        var start = Antigravity("""{"hookEventName":"SessionStart","conversationId":"c1","workspacePaths":["C:\\w\\site"],"cwd":"C:\\w\\site"}""");
        check("Antigravity: a session's start is reported and the hook prints an empty object", start.Report is { State: "started", Session: "c1", Context: "site", AppId: "antigravity" } && start.Quiet == "{}");
        check("Antigravity: a call of the model reports work with the user's last words",
            Antigravity("""{"hookEventName":"PreInvocation","conversationId":"c1","lastUserInput":"fix\nit","invocationNum":2}""").Report is { State: "working", Title: "fix it" });
        var done = Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"NO_TOOL_CALL"}""");
        check("Antigravity: the loop's end is a finished turn, and the hook lets it stop",
            done.Report?.State == "done" && done.Notice is { Title: "已完成", Category: "success" } && done.Quiet == """{"decision":"stop"}""");
        check("Antigravity: a canceled loop is idle, an error is told",
            Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"USER_CANCELED"}""") is { Report.State: "idle", Notice: null }
            && Antigravity("""{"hookEventName":"Stop","conversationId":"c1","terminationReason":"ERROR","error":"quota"}""").Notice is { Category: "error", Body: "quota" });
        check("Antigravity: other events and unreadable input still print an object; other tools print nothing",
            Antigravity("""{"hookEventName":"PreToolUse","conversationId":"c1"}""") is { Report: null, Ask: null, Quiet: "{}" } && Antigravity("""{"x":1}""").Quiet == "{}"
            && AgentHook.Quiet(AgentHookFormats.Claude, "stop") is null);
    }

    private static void Settings(Action<string, bool> check)
    {
        AntigravityPlans(check);
        var paths0 = new AgentPaths(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", _ => null);
        var agy = AgentHarnesses.Find("antigravity")!;
        var named = JsonNode.Parse(AgentHarnesses.Installed(agy, """{"other":{"Stop":[{"type":"command","command":"x"}]}}""", @"C:\B\brim.exe")!)!;
        check("Hook settings: Antigravity's hooks are BrimDeck's own named hook beside the others, run through cmd",
            agy.SettingsFile(paths0) == @"C:\Users\me\.gemini\config\hooks.json" && named["other"] is not null && (bool?)named["brimdeck"]!["enabled"] == true
            && (string?)named["brimdeck"]!["Stop"]![0]!["command"] == @"C:\B\brim.exe hook antigravity" && named["brimdeck"]!["Stop"]![0]!["async"] is null
            && named["brimdeck"]!["PreInvocation"] is JsonArray && AgentHarnesses.State(agy, named.ToJsonString(), @"C:\B\brim.exe") == HookState.Installed);
        var unnamed = JsonNode.Parse(AgentHarnesses.Removed(agy, named.ToJsonString())!)!.AsObject();
        check("Hook settings: removing Antigravity's takes only BrimDeck's named hook", unnamed.Select(p => p.Key).SequenceEqual(["other"])
            && JsonNode.Parse(AgentHarnesses.Removed(agy, """{"brimdeck":{"Stop":[{"command":"mine.exe"}]}}""")!)!["brimdeck"] is not null);
        var qoder = AgentHarnesses.Find("qoder")!;
        var shell = JsonNode.Parse(AgentHarnesses.Installed(qoder, null, @"C:\B\brim.exe")!)!["hooks"]!;
        check("Hook settings: a tool built on Claude Code gets one command line with forward slashes, its name and id, and no async",
            (string?)shell["Stop"]![0]!["hooks"]![0]!["command"] == "C:/B/brim.exe hook claude --name Qoder --id qoder" && shell["Stop"]![0]!["hooks"]![0]!["async"] is null
            && shell["PreToolUse"]![0]!["matcher"]!.ToString() == "AskUserQuestion" && qoder.SettingsFile(paths0) == @"C:\Users\me\.qoder\settings.json"
            && AgentHarnesses.CommandLine(AgentHarnesses.Find("qoder-cn")!, @"C:\My Apps\brim.exe") == "\"C:/My Apps/brim.exe\" hook claude --name \"Qoder CN\" --id qoder-cn");
        var zcode = AgentHarnesses.Find("zcode")!;
        var zfile = JsonNode.Parse(AgentHarnesses.Installed(zcode, """{"provider":{"a":1},"model":"m"}""", @"D:\Zcode tools\brim.exe")!)!;
        var zhooks = zfile["hooks"]!;
        var permission = zhooks["events"]!["PermissionRequest"]![0]!["hooks"]![0]!;
        check("Hook settings: ZCode's hooks are switched on under hooks.events as process hooks, with its other settings kept",
            zcode.SettingsFile(paths0) == @"C:\Users\me\.zcode\cli\config.json" && (string?)zfile["model"] == "m" && (bool?)zhooks["enabled"] == true
            && zhooks.AsObject().Select(p => p.Key).Order().SequenceEqual(["enabled", "events"])
            && (string?)permission["type"] == "process" && (string?)permission["command"] == @"D:\Zcode tools\brim.exe"
            && permission["args"]!.AsArray().Select(a => (string?)a).SequenceEqual(["hook", "claude", "--name", "ZCode", "--id", "zcode"])
            && (int?)permission["timeoutMs"] == AgentHarnesses.WaitingTimeoutSeconds * 1000 && permission["timeout"] is null && permission["async"] is null
            && (int?)zhooks["events"]!["Stop"]![0]!["hooks"]![0]!["timeoutMs"] == 10_000
            && (string?)zhooks["events"]!["PreToolUse"]![0]!["matcher"] == "AskUserQuestion");
        check("Hook settings: ZCode gets only the seven events it knows", zhooks["events"]!.AsObject().All(p => p.Key is "SessionStart" or "UserPromptSubmit" or "PreToolUse" or "PermissionRequest" or "PostToolUse" or "PostToolUseFailure" or "Stop")
            && AgentHarnesses.State(zcode, zfile.ToJsonString(), @"D:\Zcode tools\brim.exe") == HookState.Installed);
        var switchedOff = zfile.DeepClone(); switchedOff["hooks"]!["enabled"] = false;
        check("Hook settings: ZCode's hooks switched off need an update", AgentHarnesses.State(zcode, switchedOff.ToJsonString(), @"D:\Zcode tools\brim.exe") == HookState.Outdated);
        var zremoved = JsonNode.Parse(AgentHarnesses.Removed(zcode, zfile.ToJsonString())!)!.AsObject();
        var zkept = JsonNode.Parse(AgentHarnesses.Removed(zcode, AgentHarnesses.Installed(zcode, """{"hooks":{"enabled":true,"events":{"Stop":[{"hooks":[{"type":"command","command":"mine"}]}]}}}""", "brim.exe"))!)!;
        check("Hook settings: removing ZCode's takes the block when only the switch is left, and keeps the user's own hooks",
            zremoved.Select(p => p.Key).Order().SequenceEqual(["model", "provider"])
            && (bool?)zkept["hooks"]!["enabled"] == true && zkept["hooks"]!["events"]!.AsObject().Select(p => p.Key).SequenceEqual(["Stop"])
            && (string?)zkept["hooks"]!["events"]!["Stop"]![0]!["hooks"]![0]!["command"] == "mine");
        var opencode = AgentHarnesses.Find("opencode")!;
        var plugin = AgentHarnesses.Installed(opencode, null, @"C:\B b\brim.exe")!;
        check("Hook settings: OpenCode gets BrimDeck's own plugin file, which runs brim by its full path",
            opencode.SettingsFile(paths0) == @"C:\Users\me\.config\opencode\plugins\brimdeck.js" && OpenCodePlugin.Ours(plugin) && plugin.Contains("""const BRIM = "C:\\B b\\brim.exe" """.TrimEnd())
            && plugin.Contains("""const ARGS = ["hook","opencode"]""") && plugin.Contains("/permission/{requestID}/reply") && plugin.Contains("/question/{requestID}/reply")
            && AgentHarnesses.State(opencode, plugin, @"C:\B b\brim.exe") == HookState.Installed && AgentHarnesses.State(opencode, plugin, @"D:\brim.exe") == HookState.Outdated
            && AgentHarnesses.State(opencode, null, "b") == HookState.Missing);
        check("Hook settings: a plugin file of the same name that is not BrimDeck's is never written or removed",
            AgentHarnesses.State(opencode, "export const Mine = 1", "b") == HookState.Unreadable && AgentHarnesses.Installed(opencode, "export const Mine = 1", "b") is null
            && AgentHarnesses.Removed(opencode, "export const Mine = 1") is null && AgentHarnesses.Removed(opencode, plugin) == "");
        var qwen = AgentHarnesses.Find("qwenwork")!;
        check("Hook settings: a tool found without hooks BrimDeck can write says why and is never written",
            qwen.Unsupported is not null && AgentHarnesses.State(qwen, null, "b") == HookState.Unsupported && AgentHarnesses.Installed(qwen, "{}", "b") is null
            && AgentHookFiles.Install(qwen, paths0, "b") is { Length: > 0 } && AgentHookFiles.Uninstall(qwen, paths0) is null);
        check("Hook settings: every tool has its own id, and each one that can be installed has a known hook format",
            AgentHarnesses.All.Select(h => h.Id).Distinct().Count() == AgentHarnesses.All.Count
            && AgentHarnesses.All.Where(h => h.Unsupported is null).All(h => AgentHookFormats.All.Contains(h.Format) && (h.Hooks.Count > 0 || h.Shape == HookFileShape.PluginFile)));
        var openApproval = AgentHook.Plan(AgentHookFormats.OpenCode, "opencode", "OpenCode", Input("""{"hook_event_name":"PermissionRequest","session_id":"ses_1","cwd":"C:/w/app","tool_name":"bash","tool_input":{"command":"git push"},"permission_suggestions":["git push *"]}"""), true);
        check("OpenCode: an approval answers in OpenCode's words: once, always or reject",
            openApproval.Ask is { Title: "请求运行命令", Code: "git push" } oa && oa.Actions!.Any(x => x.Key == AgentHook.AlwaysAllow)
            && openApproval.Reply(Chosen(AgentHook.Allow)) == """{"reply":"once"}""" && openApproval.Reply(Chosen(AgentHook.AlwaysAllow)) == """{"reply":"always"}"""
            && openApproval.Reply(Chosen(AgentHook.Deny)) == """{"reply":"reject"}""" && openApproval.Reply(Chosen(AgentHook.Terminal)) is null);
        var openQuestion = AgentHook.Plan(AgentHookFormats.OpenCode, "opencode", "OpenCode", Input("""
            {"hook_event_name":"PreToolUse","session_id":"ses_1","tool_name":"AskUserQuestion","tool_input":{"questions":[
            {"question":"Which?","header":"Pick","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}],"multiSelect":true},{"question":"Name?","header":"Name","options":[]}]}}
            """), true);
        check("OpenCode: questions answer as arrays of labels and typed words, in order",
            openQuestion.Ask?.Questions?.Count == 2 && openQuestion.Reply(new() { Type = NoticeTypes.Result, Result = NoticeWaits.Action, Answers = [new() { Id = "0", Keys = ["1"], Text = "C" }, new() { Id = "1", Text = "Ada" }] })
                == """{"answers":[["B","C"],["Ada"]]}""");
        var claude = AgentHarnesses.Find("claude")!;
        var cursor = AgentHarnesses.Find("cursor")!;
        var codex = AgentHarnesses.Find("codex")!;
        const string brim = @"C:\Program Files\BrimDeck\brim.exe";
        string existing = """
            {
              "respectGitignore": true,
              "hooks": {
                "PreToolUse": [ { "matcher": "*", "hooks": [ { "type": "command", "command": "C:/x/git-ai.exe checkpoint claude --hook-input stdin" } ] } ]
              }
            }
            """;
        var installed = AgentHarnesses.Installed(claude, existing, brim)!;
        var root = JsonNode.Parse(installed)!;
        var ask = root["hooks"]!["PreToolUse"]!.AsArray().Last()!;
        check("Hook settings: installing keeps the user's settings and other hooks, and adds BrimDeck's",
            (bool?)root["respectGitignore"] == true && root["hooks"]!["PreToolUse"]!.AsArray().Count == 2 && (string?)root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"] == "C:/x/git-ai.exe checkpoint claude --hook-input stdin"
            && (string?)ask["matcher"] == "AskUserQuestion" && (string?)ask["hooks"]![0]!["command"] == brim && ask["hooks"]![0]!["args"]!.AsArray().Select(a => (string?)a).SequenceEqual(["hook", "claude"])
            && (int?)ask["hooks"]![0]!["timeout"] == AgentHarnesses.WaitingTimeoutSeconds && ask["hooks"]![0]!["async"] is null
            && (bool?)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["async"] == true && root["hooks"]!["PermissionRequest"] is not null);
        check("Hook settings: installed reads as installed, installing again changes nothing",
            AgentHarnesses.State(claude, installed, brim) == HookState.Installed && AgentHarnesses.Installed(claude, installed, brim) == installed
            && AgentHarnesses.State(claude, existing, brim) == HookState.Missing && AgentHarnesses.State(claude, null, brim) == HookState.Missing);
        check("Hook settings: BrimDeck moved, the hooks need an update, and updating leaves one set",
            AgentHarnesses.State(claude, installed, @"D:\BrimDeck\brim.exe") == HookState.Outdated
            && JsonNode.Parse(AgentHarnesses.Installed(claude, installed, @"D:\BrimDeck\brim.exe")!)!["hooks"]!["Stop"]!.AsArray().Count == 1);
        var removed = JsonNode.Parse(AgentHarnesses.Removed(claude, installed)!)!;
        check("Hook settings: removing takes out only BrimDeck's hooks and the events it left empty",
            removed["hooks"]!.AsObject().Select(p => p.Key).SequenceEqual(["PreToolUse"]) && removed["hooks"]!["PreToolUse"]!.AsArray().Count == 1 && (bool?)removed["respectGitignore"] == true
            && AgentHarnesses.Removed(claude, AgentHarnesses.Installed(claude, "{}", brim)) is { } empty && JsonNode.Parse(empty)!.AsObject().Count == 0);
        check("Hook settings: a file that is not JSON is never written", AgentHarnesses.Installed(claude, "{ not json", brim) is null && AgentHarnesses.Removed(claude, "[1]") is null
            && AgentHarnesses.State(claude, "{ not json", brim) == HookState.Unreadable);
        var flat = JsonNode.Parse(AgentHarnesses.Installed(cursor, """{"hooks":{"postToolUse":[{"command":"C:\\x\\git-ai.exe checkpoint cursor --hook-input stdin"}]},"version":1}""", brim)!)!;
        check("Hook settings: Cursor's flat list takes a PowerShell command line beside the user's hooks",
            (int?)flat["version"] == 1 && flat["hooks"]!["postToolUse"]!.AsArray().Count == 2 && (string?)flat["hooks"]!["stop"]![0]!["command"] == "& 'C:\\Program Files\\BrimDeck\\brim.exe' hook cursor"
            && flat["hooks"]!["stop"]![0]!["type"] is null);
        check("Hook settings: Codex runs one command line through cmd, with the path quoted",
            (string?)JsonNode.Parse(AgentHarnesses.Installed(codex, null, brim)!)!["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"] == "\"C:\\Program Files\\BrimDeck\\brim.exe\" hook codex");
        check("Hook settings: a path without spaces needs no call operator in PowerShell", AgentHarnesses.CommandLine(cursor, @"C:\B\brim.exe") == @"C:\B\brim.exe hook cursor");
        check("Hook settings: BrimDeck's hooks are told by brim.exe and \"hook\", and nothing else is",
            AgentHarnesses.Ours(JsonNode.Parse("""{"command":"C:\\B\\brim.exe","args":["hook","claude"]}""")) && AgentHarnesses.Ours(JsonNode.Parse("""{"command":"& 'C:\\B b\\brim.exe' hook cursor"}"""))
            && !AgentHarnesses.Ours(JsonNode.Parse("""{"command":"C:\\B\\brim.exe","args":["notify","send"]}""")) && !AgentHarnesses.Ours(JsonNode.Parse("""{"command":"brim-helper.exe hook"}""")));
        var paths = new AgentPaths(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", name => name == "CODEX_HOME" ? @"D:\codex" : null);
        check("Hook settings: the settings files follow the tools' own variables", claude.SettingsFile(paths) == @"C:\Users\me\.claude\settings.json" && codex.SettingsFile(paths) == @"D:\codex\hooks.json"
            && AgentHarnesses.Detected(cursor, paths, p => p == @"C:\Users\me\AppData\Local\Programs\cursor\Cursor.exe") && !AgentHarnesses.Detected(cursor, paths, p => p == @"C:\Users\me\.cursor")
            && !AgentHarnesses.Detected(claude, paths, _ => false));
    }
}
