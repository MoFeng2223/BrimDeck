using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Protocol;

// The ids of the coding agents' hook formats ("brim hook <format>"). Several tools share one: a tool whose hooks take Claude
// Code's input and output (hook_event_name, PermissionRequest, PreToolUse on AskUserQuestion) uses "claude" with its own --name.
public static class AgentHookFormats
{
    public const string Claude = "claude", Codex = "codex", Cursor = "cursor", Antigravity = "antigravity";
    // OpenCode's BrimDeck plugin hands its events over in Claude Code's words and posts back what the hook prints, which is in
    // OpenCode's own: {"reply":"once|always|reject"} for an approval, {"answers":[["label", ...], ...]} for questions.
    public const string OpenCode = "opencode";
    // CodeBuddy Code (inside WorkBuddy): Claude Code's input and its approval decision, but a hook can neither add an "always
    // allow" rule nor answer a question (the question's hook runs only after the user answered in the app).
    public const string CodeBuddy = "codebuddy";
    // Kimi Code: Claude Code's field names, but its hooks decide nothing. An approval and a question show in Kimi Code at once,
    // so the hook only tells BrimDeck, and PermissionResult says when the approval was answered there.
    public const string Kimi = "kimi";
    // TRAE: Claude Code's field names; a Notification says when TRAE waits for an approval or an answer, which no hook can give.
    public const string Trae = "trae";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Claude, Codex, Cursor, Antigravity, OpenCode, CodeBuddy, Kimi, Trae };

    // The name a format's sessions show by when --name gives none.
    public static string DefaultName(string format) => format switch
    {
        Codex => "Codex", Cursor => "Cursor", Antigravity => "Antigravity", OpenCode => "OpenCode", CodeBuddy => "WorkBuddy", Kimi => "Kimi Code", Trae => "TRAE", _ => "Claude Code"
    };
}

// What one hook call does: a report of the session's state, a notice (a finished turn, or an approval or a question the tool
// asks in its own window only), a question or an approval the hook waits on and the reply the tool reads from its output, and a
// report after the answer. Any of them may be missing. A notice that tells of an approval or a question carries the session's
// key (AgentKeysWire); BrimDeck closes it once the session reports that it moved on.
public sealed class AgentHookPlan
{
    public NoticeMessage? Report { get; init; }
    public NoticeMessage? Notice { get; init; }
    public NoticeMessage? Ask { get; init; }
    // The hook's output for how Ask ended (null: the user did not answer here); null output leaves the tool to its own flow.
    public Func<NoticeMessage?, string?> Reply { get; init; } = _ => null;
    // The state once Ask was answered here. It carries the call's mark, as every report that a call was answered does: the
    // tool may already ask for its next approval when the report arrives, which is then no answer to that one.
    public NoticeMessage? Answered { get; init; }
    // What the hook prints when it has no answer to give: nothing for most tools; Antigravity reads a JSON object from every hook.
    public string? Quiet { get; init; }
    // While Ask waits: where the tool itself writes how the call ended (answered or interrupted in its own window).
    public AgentCallWatch? Watch { get; init; }
}

// The call an approval waits on, as the session's transcript names it (AgentTranscripts), and the reports for how it ended in
// the tool itself: Moved once the call has a result there, Interrupted once the user stopped the turn.
public sealed record AgentCallWatch(string File, string Tool, string Call, NoticeMessage Moved, NoticeMessage Interrupted);

// Reads a coding agent's hook input (the JSON on standard input) and plans what "brim hook" sends BrimDeck and prints back.
// Nothing here blocks the tool for long without the user: only approvals and questions wait, and every way they end without an
// answer here prints nothing, which leaves the tool to its own dialog.
public static class AgentHook
{
    // The keys of an approval's buttons, and of the button that hands it back to the terminal.
    public const string Allow = "allow", AlwaysAllow = "always", Deny = "deny", Terminal = "terminal";
    // The tool that asks the user a question, as every tool with Claude Code's hooks names it.
    private const string QuestionTool = "AskUserQuestion";
    // Words of a last message kept in a notice.
    private const int MessageLimit = 600;

    // ----- What each tool does while a hook waits (measured in the tools' own sessions; where not, the source is named) -----

    // Whether the tool holds its own approval prompt back until the PermissionRequest hook returned. Codex and CodeBuddy Code
    // (WorkBuddy) by their code, not yet seen in a session; Command Code asks its mod first. Claude Code, CodeAgent, Qoder,
    // ZCode and OpenCode show their prompt beside the hook, and the first answer counts.
    public static bool ApprovalHolds(string format, string id) => format is AgentHookFormats.Codex or AgentHookFormats.CodeBuddy || id == "commandcode";

    // Whether the tool holds its own question back while a hook that could answer it waits: every tool whose question comes
    // with PreToolUse, which it waits for. Claude Code's comes with PermissionRequest beside its own prompt, and OpenCode 1.x
    // shows its own at once.
    public static bool QuestionHolds(string format, string id) => format == AgentHookFormats.Claude && QuestionsAt(format, id).PreTool;

    // Where a tool's question reaches a hook. Claude Code runs PreToolUse and then PermissionRequest for it, and only the second
    // leaves its own prompt usable. The Qoder CLI does the same, while the Qoder desktop app runs PreToolUse alone, and both
    // read one settings file. CodeAgent runs PreToolUse alone; ZCode runs both and waits for each, so only the first is used.
    private static (bool PreTool, bool Permission) QuestionsAt(string format, string id) => format != AgentHookFormats.Claude ? (true, false) : id switch
    {
        "claude" => (false, true), "qoder" or "qoder-cn" => (true, true), _ => (true, false)
    };

    // One session's reports and notices, in the words of the hook's language.
    private sealed record Scope(string Format, string Id, string Name, string Session, string Project, string? Transcript, Func<string, string, string> T)
    {
        public string? Context => Project.Length > 0 ? Project : null;
        public string Key => AgentKeysWire.For(Id, Session);

        public NoticeMessage State(string state, string? title = null, string? body = null, string? call = null) => new()
        {
            Type = NoticeTypes.Agent, AppId = Id, AppName = Name, Session = Session, State = state, Context = Context, Title = title,
            Body = body is null ? null : Cut(body, MessageLimit), Call = call, Transcript = Transcript
        };

        // A finished turn: the report, and the notice that replaces the session's last one.
        public AgentHookPlan Finished(bool failed, string? last, string? quiet = null) => new()
        {
            Report = State(AgentSessionStates.Done, body: last), Quiet = quiet,
            Notice = new NoticeMessage
            {
                AppName = Name, Context = Context, ReplaceId = "agent-done:" + Id + ":" + Session, Category = failed ? "error" : "success",
                Title = failed ? T("出错停止", "Stopped with an error") : T("已完成", "Done"), Body = last is { Length: > 0 } ? Cut(last, MessageLimit) : null
            }
        };

        // An approval or a question the tool asks in its own window only: the session waits, and a notice stays on the island
        // until the session moves on.
        public AgentHookPlan Told(string state, string title, string? body, string? code, string? call = null) => new()
        {
            Report = State(state, title, call: call),
            Notice = new NoticeMessage
            {
                AppName = Name, Context = Context, ReplaceId = Key, Category = state == AgentSessionStates.Approval ? "warning" : "info", Level = "persistent",
                Title = title, Body = body is { Length: > 0 } ? Cut(body, MessageLimit) : T($"请在 {Name} 中处理", $"Answer it in {Name}"),
                Code = code is { Length: > 0 } ? Cut(code, NoticeProtocol.MaxCode) : null
            }
        };
    }

    // id: the session id's prefix in BrimDeck (the tool, as the settings know it); name: the tool's name. chinese: the words of
    // the notices (the hook runs apart from BrimDeck and words them in the system's language).
    public static AgentHookPlan Plan(string format, string id, string name, JsonElement input, bool chinese)
    {
        string T(string zh, string en) => chinese ? zh : en;
        if (input.ValueKind != JsonValueKind.Object) return new();
        string evt = Normalize(Text(input, "hook_event_name") ?? Text(input, "hookEventName") ?? "");
        string? session = Text(input, "session_id") ?? Text(input, "conversation_id") ?? Text(input, "conversationId") ?? Text(input, "sessionId") ?? Text(input, "thread_id");
        if (session is null || evt.Length == 0) return new() { Quiet = Quiet(format, evt) };
        // Only Claude Code's own transcript is read (AgentTranscripts); the tools built on it write theirs the same way.
        var scope = new Scope(format, id, name, session, Project(input), format == AgentHookFormats.Claude ? Text(input, "transcript_path") : null, T);
        if (format == AgentHookFormats.Antigravity) return Antigravity(scope, evt, input);
        // Cursor runs the hooks of Claude Code's settings too (its "third-party hooks"), in Claude Code's words: with Cursor's own
        // hooks installed the session would show twice, so only those report it.
        if (format != AgentHookFormats.Cursor && input.TryGetProperty("cursor_version", out _)) return new();
        // Codex tidies its memories in a session of its own, in its memories folder and without a transcript, which runs the
        // user's hooks too: it is none of the user's sessions.
        if (format == AgentHookFormats.Codex && scope.Project == "memories" && Text(input, "transcript_path") is null) return new();
        if (format is AgentHookFormats.Kimi or AgentHookFormats.Trae) return Watched(scope, evt, input);
        string? tool = Tool(input);
        var args = Arguments(input);
        switch (evt)
        {
            case "sessionstart": return new() { Report = scope.State(AgentSessionStates.Started) };
            case "sessionend": return new() { Report = scope.State(AgentSessionStates.Ended) };
            case "userpromptsubmit" or "beforesubmitprompt": return new() { Report = scope.State(AgentSessionStates.Working, OneLine(Text(input, "prompt") ?? "", 200)) };
            case "posttooluse" or "posttoolusefailure" or "afterfileedit" or "aftershellexecution" or "aftermcpexecution":
                return new() { Report = scope.State(AgentSessionStates.Working, call: Stamp(tool, args)) };
            case "stop" or "agentstop":
            {
                // Cursor says how the turn ended; an aborted turn is no result to tell. WorkBuddy's CodeBuddy Code ends a turn the
                // user stopped with Stop too, and these words in place of its last message.
                string? status = Text(input, "status"), last = Text(input, "last_assistant_message");
                bool stopped = status is "aborted" || format == AgentHookFormats.CodeBuddy && last == "Interrupted by user";
                return stopped ? new() { Report = scope.State(AgentSessionStates.Idle) } : scope.Finished(status is "error", last);
            }
            // Claude Code: the turn ended with an error of the API (no sign-in, a rate limit), in place of Stop.
            case "stopfailure": return scope.Finished(true, Text(input, "last_assistant_message") ?? Text(input, "error_details") ?? Text(input, "error"));
            case "notification":
                return Text(input, "notification_type") switch
                {
                    "idle_prompt" => new() { Report = scope.State(AgentSessionStates.Idle) },
                    // OpenCode 2.x asks a question as a form, which its plugin can tell of but not answer.
                    "ask_user_question" when format == AgentHookFormats.OpenCode
                        => scope.Told(AgentSessionStates.Question, Text(input, "title") ?? T("需要你选择", "Needs your choice"), Text(input, "message"), null),
                    _ => new()
                };
            case "permissionrequest" when format != AgentHookFormats.Cursor:
                if (tool != QuestionTool) return Approval(scope, input, tool ?? "", args);
                return QuestionsAt(format, id).Permission ? Question(scope, args, atPermission: true) : new();
            case "pretooluse" when format is AgentHookFormats.Claude or AgentHookFormats.OpenCode && tool == QuestionTool && QuestionsAt(format, id).PreTool:
                return Question(scope, args, atPermission: false);
            default: return new();
        }
    }

    // What Antigravity reads from a hook that decides nothing: an empty object, and for Stop a decision that lets the loop end.
    // Before a tool runs it reads a decision; with nothing printed it goes on as it would without the hook (seen in its app),
    // so nothing is printed there. What it cannot read as JSON stops the tool's call.
    public static string? Quiet(string format, string evt)
        => format != AgentHookFormats.Antigravity || evt == "pretooluse" ? null : evt == "stop" ? """{"decision":"stop"}""" : "{}";

    // Antigravity (its app, IDE and CLI share one hooks file): camelCase fields; the user's last words come with each call of the
    // model (PreInvocation), and Stop says why the loop ended. It has no event for an approval. A question is its ask_question
    // tool, whose form shows once the hook before the tool returned, and which no hook can answer: the island tells of it, and
    // the next call of the model says that it was answered.
    private static AgentHookPlan Antigravity(Scope scope, string evt, JsonElement input)
    {
        string? quiet = Quiet(AgentHookFormats.Antigravity, evt);
        switch (evt)
        {
            case "sessionstart": return new() { Report = scope.State(AgentSessionStates.Started), Quiet = quiet };
            case "preinvocation": return new() { Report = scope.State(AgentSessionStates.Working, Text(input, "lastUserInput") is { } words ? OneLine(words, 200) : null), Quiet = quiet };
            case "pretooluse" when input.TryGetProperty("toolCall", out var call) && Text(call, "name") == "ask_question":
                return scope.Told(AgentSessionStates.Question, scope.T("需要你选择", "Needs your choice"), call.TryGetProperty("args", out var asked) ? FirstQuestion(asked) : null, null);
            case "stop":
            {
                // The reason comes as an enum's name ("USER_CANCELED") or in lower case ("error"), by the version.
                string reason = Normalize(Text(input, "terminationReason") ?? "");
                if (reason == "usercanceled") return new() { Report = scope.State(AgentSessionStates.Idle), Quiet = quiet };
                // The loop ended but work goes on: background tasks still run, or the loop is about to be continued.
                if (Flag(input, "fullyIdle") == false || Flag(input, "continuationActive") == true) return new() { Report = scope.State(AgentSessionStates.Working), Quiet = quiet };
                bool failed = reason == "error" || Text(input, "error") is { Length: > 0 };
                return scope.Finished(failed, failed ? Text(input, "error") : null, quiet);
            }
            default: return new() { Quiet = quiet };
        }
    }

    // A tool that asks for approvals and answers in its own window only (Kimi Code, TRAE): the hook tells BrimDeck that the
    // session waits, with a notice that stays on the island until the session moves on, and never holds the tool up.
    private static AgentHookPlan Watched(Scope scope, string evt, JsonElement input)
    {
        var T = scope.T;
        string? tool = Tool(input);
        var args = Arguments(input);
        switch (evt)
        {
            case "sessionstart": return new() { Report = scope.State(AgentSessionStates.Started) };
            case "sessionend": return new() { Report = scope.State(AgentSessionStates.Ended) };
            case "userpromptsubmit": return new() { Report = scope.State(AgentSessionStates.Working, OneLine(Words(input, "prompt"), 200)) };
            // Kimi Code: the approval was answered in its window.
            case "permissionresult": return new() { Report = scope.State(AgentSessionStates.Working) };
            // The result of the call the session waited on is the first word of its answer (TRAE says nothing else).
            case "posttooluse" or "posttoolusefailure": return new() { Report = scope.State(AgentSessionStates.Working, call: Stamp(tool, args)) };
            case "stop": return scope.Finished(false, Text(input, "last_assistant_message"));
            case "stopfailure": return scope.Finished(true, Text(input, "error_message"));
            case "interrupt": return new() { Report = scope.State(AgentSessionStates.Idle) };
            case "permissionrequest" when scope.Format == AgentHookFormats.Kimi:
            {
                var (title, code) = Wanted(tool ?? "", args, T);
                return scope.Told(AgentSessionStates.Approval, title, Text(args, "description"), code, Stamp(tool, args));
            }
            case "pretooluse" when scope.Format == AgentHookFormats.Kimi && tool == QuestionTool:
                return scope.Told(AgentSessionStates.Question, T("需要你选择", "Needs your choice"), FirstQuestion(args), null, Stamp(tool, args));
            case "notification" when scope.Format == AgentHookFormats.Trae:
                return Text(input, "notification_type") switch
                {
                    "idle_prompt" => new() { Report = scope.State(AgentSessionStates.Idle) },
                    "permission_prompt" => scope.Told(AgentSessionStates.Approval, T("等待审批", "Waiting for approval"), Text(input, "message"), null),
                    "ask_user_question" => scope.Told(AgentSessionStates.Question, T("需要你选择", "Needs your choice"), Text(input, "message"), null),
                    "document_review" or "browser_interaction" => scope.Told(AgentSessionStates.Question, T("等待你处理", "Waiting for you"), Text(input, "message"), null),
                    _ => new()
                };
            default: return new();
        }
    }

    // The tool a hook's input names, and its arguments; ZCode gives both in camelCase as well.
    private static string? Tool(JsonElement input) => Text(input, "tool_name") ?? Text(input, "toolName");

    private static JsonElement Arguments(JsonElement input)
        => input.TryGetProperty("tool_input", out var args) || input.TryGetProperty("toolInput", out args) ? args : default;

    // The mark of one tool call (NoticeMessage.Call): its tool and its arguments, read alike however the tool orders them. A
    // question is marked by what it asks alone, since its result comes back with the answers added. Null without a tool.
    public static string? Stamp(string? tool, JsonElement args)
    {
        if (tool is not { Length: > 0 }) return null;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            json.WriteStringValue(tool);
            Ordered(json, tool == QuestionTool && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("questions", out var questions) ? questions : args);
            json.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16];
    }

    // The value with every object's members in the order of their names.
    private static void Ordered(Utf8JsonWriter json, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined: json.WriteNullValue(); break;
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var member in value.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal)) { json.WritePropertyName(member.Name); Ordered(json, member.Value); }
                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Ordered(json, item);
                json.WriteEndArray();
                break;
            default: value.WriteTo(json); break;
        }
    }

    // The first question a question tool asks, to show under the notice's title.
    private static string? FirstQuestion(JsonElement args)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? Text(list[0], "question") : null;

    // A field that is words, or a list of parts of which some are words ({"type":"text","text":"..."}), as Kimi Code gives a prompt.
    private static string Words(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array) return "";
        return string.Join(' ', value.EnumerateArray().Select(part => part.ValueKind == JsonValueKind.String ? part.GetString() : Text(part, "text")).Where(text => text is { Length: > 0 }));
    }

    // What an approval is for, and the command, file or address it names.
    private static (string Title, string Code) Wanted(string tool, JsonElement args, Func<string, string, string> T)
    {
        string? command = Text(args, "command"), file = Text(args, "file_path") ?? Text(args, "path") ?? Text(args, "notebook_path"), url = Text(args, "url");
        // Codex gives a patch as the "command" of apply_patch.
        string title = tool == "apply_patch" ? T("请求修改文件", "Wants to change a file")
            : command is not null ? T("请求运行命令", "Wants to run a command")
            : file is not null ? T("请求修改文件", "Wants to change a file")
            : url is not null ? T("请求访问网页", "Wants to open a web page")
            : T($"请求使用 {tool}", $"Wants to use {tool}");
        return (title, command ?? file ?? url ?? (args.ValueKind == JsonValueKind.Object ? args.GetRawText() : ""));
    }

    private static AgentHookPlan Approval(Scope scope, JsonElement input, string tool, JsonElement args)
    {
        var T = scope.T;
        string format = scope.Format;
        var (title, code) = Wanted(tool, args, T);
        // Claude Code offers what "always allow" would add; the hook gives one back to apply it (Codex takes none).
        JsonElement? always = format is AgentHookFormats.Claude or AgentHookFormats.OpenCode && input.TryGetProperty("permission_suggestions", out var suggestions)
            && suggestions.ValueKind == JsonValueKind.Array && suggestions.GetArrayLength() > 0 ? suggestions[0].Clone() : null;
        var actions = new List<NoticeActionInfo>
        {
            new() { Key = Allow, Label = T("允许", "Allow"), Style = NoticeActionTypes.Primary },
            new() { Key = Deny, Label = T("拒绝", "Deny") }
        };
        if (always is not null) actions.Add(new() { Key = AlwaysAllow, Label = T("总是允许", "Always allow") });
        actions.Add(new() { Key = Terminal, Label = T("在终端处理", "Use the terminal") });
        var ask = new NoticeMessage
        {
            AppName = scope.Name, Context = scope.Context, ReplaceId = scope.Key, Category = "warning",
            Title = title, Body = Text(args, "description") is { Length: > 0 } why ? Cut(why, MessageLimit) : null, Code = Cut(code, NoticeProtocol.MaxCode),
            Actions = actions, Wait = true, Holds = ApprovalHolds(format, scope.Id) ? true : null
        };
        string? call = Stamp(tool, args);
        return new()
        {
            Report = scope.State(AgentSessionStates.Approval, title, call: call), Ask = ask, Answered = scope.State(AgentSessionStates.Working, call: call),
            Reply = result => result is { Result: NoticeWaits.Action, ActionKey: Allow or AlwaysAllow or Deny } chosen
                ? format == AgentHookFormats.OpenCode ? OpenCodeReply(chosen.ActionKey!) : Decision(chosen.ActionKey!, always, T) : null,
            Watch = scope.Transcript is { } file && call is not null ? new(file, tool, call, scope.State(AgentSessionStates.Working, call: call), scope.State(AgentSessionStates.Idle)) : null
        };
    }

    // OpenCode's reply to a permission: once, always or reject.
    private static string OpenCodeReply(string key) => "{\"reply\":\"" + (key == Deny ? "reject" : key == AlwaysAllow ? "always" : "once") + "\"}";

    // {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}, with the suggestion given back
    // for "always allow" and a reason for a denial.
    private static string Decision(string key, JsonElement? always, Func<string, string, string> T)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("hookSpecificOutput");
            json.WriteString("hookEventName", "PermissionRequest");
            json.WriteStartObject("decision");
            json.WriteString("behavior", key == Deny ? "deny" : "allow");
            if (key == Deny) json.WriteString("message", T("用户在 BrimDeck 中拒绝了这个操作。", "The user denied this in BrimDeck."));
            if (key == AlwaysAllow && always is { } suggestion) { json.WritePropertyName("updatedPermissions"); json.WriteStartArray(); suggestion.WriteTo(json); json.WriteEndArray(); }
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // AskUserQuestion: each question with its options, and words of the user's own (the tool offers "Other" with every question
    // too). Answered here, the hook gives the tool its input back with the answers, keyed by the question's words; options picked
    // are given by their labels, joined with ", " for several. atPermission: the question came with PermissionRequest, where the
    // answers go back in the approval's decision and the tool's own prompt stays usable meanwhile; with PreToolUse the tool waits
    // for the hook before it asks.
    private static AgentHookPlan Question(Scope scope, JsonElement args, bool atPermission)
    {
        var T = scope.T;
        string format = scope.Format;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return new();
        var questions = new List<NoticeQuestion>();
        var words = new List<(string Text, List<string> Labels)>();
        int index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (Text(item, "question") is not { } text) { index++; continue; }
            var labels = new List<string>();
            var options = new List<NoticeOption>();
            if (item.TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (Text(choice, "label") is { } label)
                    {
                        options.Add(new() { Key = labels.Count.ToString(CultureInfo.InvariantCulture), Label = label, Description = Text(choice, "description") });
                        labels.Add(label);
                    }
            questions.Add(new()
            {
                Id = index.ToString(CultureInfo.InvariantCulture), Header = Text(item, "header"), Question = text,
                MultiSelect = item.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True, AllowOther = true, Options = options
            });
            words.Add((text, labels));
            index++;
        }
        if (questions.Count == 0) return new();
        string title = questions[0].Header is { Length: > 0 } header ? header : T("需要你选择", "Needs your choice");
        var ask = new NoticeMessage
        {
            AppName = scope.Name, Context = scope.Context, ReplaceId = scope.Key, Category = "info",
            Title = title, Body = questions.Count == 1 ? questions[0].Question : string.Join("\n", questions.Select(q => "· " + q.Question)),
            Questions = questions, Actions = [new() { Key = Terminal, Label = T("在终端回答", "Answer in the terminal") }], Wait = true,
            Holds = !atPermission && QuestionHolds(format, scope.Id) ? true : null
        };
        var original = args.Clone();
        string? call = Stamp(QuestionTool, args);
        return new()
        {
            Report = scope.State(AgentSessionStates.Question, title, call: call), Ask = ask, Answered = scope.State(AgentSessionStates.Working, call: call),
            Reply = result => result is { Result: NoticeWaits.Action, Answers: { Count: > 0 } answers }
                ? (format == AgentHookFormats.OpenCode ? OpenCodeAnswers(words, questions, answers) : QuestionReply(original, words, questions, answers, atPermission)) : null,
            Watch = scope.Transcript is { } file && call is not null ? new(file, QuestionTool, call, scope.State(AgentSessionStates.Working, call: call), scope.State(AgentSessionStates.Idle)) : null
        };
    }

    // The labels and words given for each question, in its order; null when one has none.
    private static List<List<string>>? Given(List<(string Text, List<string> Labels)> words, List<NoticeQuestion> questions, List<NoticeAnswer> answers)
    {
        var given = new List<List<string>>();
        for (int i = 0; i < questions.Count; i++)
        {
            var answer = answers.FirstOrDefault(a => a.Id == questions[i].Id);
            var labels = (answer?.Keys ?? []).Select(k => int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < words[i].Labels.Count ? words[i].Labels[n] : null)
                .OfType<string>().ToList();
            if (answer?.Text is { } typed && typed.Trim().Length > 0) labels.Add(typed.Trim());
            if (labels.Count == 0) return null;
            given.Add(labels);
        }
        return given;
    }

    // OpenCode's answers: for each question in order, the labels picked and the words typed.
    private static string? OpenCodeAnswers(List<(string Text, List<string> Labels)> words, List<NoticeQuestion> questions, List<NoticeAnswer> answers)
    {
        if (Given(words, questions, answers) is not { } given) return null;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("answers");
            foreach (var labels in given) { json.WriteStartArray(); foreach (var label in labels) json.WriteStringValue(label); json.WriteEndArray(); }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The tool's input with the answers, allowed: as PreToolUse's permissionDecision with updatedInput, or as PermissionRequest's
    // decision with it. Every question needs its answer; one left out would leave the tool asking it anyway, so nothing is given.
    private static string? QuestionReply(JsonElement original, List<(string Text, List<string> Labels)> words, List<NoticeQuestion> questions, List<NoticeAnswer> answers, bool atPermission)
    {
        if (Given(words, questions, answers) is not { } given) return null;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("hookSpecificOutput");
            json.WriteString("hookEventName", atPermission ? "PermissionRequest" : "PreToolUse");
            if (atPermission) { json.WriteStartObject("decision"); json.WriteString("behavior", "allow"); }
            else json.WriteString("permissionDecision", "allow");
            json.WriteStartObject("updatedInput");
            foreach (var property in original.EnumerateObject())
                if (property.Name != "answers") property.WriteTo(json);
            json.WriteStartObject("answers");
            for (int i = 0; i < given.Count; i++) json.WriteString(words[i].Text, string.Join(", ", given[i]));
            json.WriteEndObject();
            json.WriteEndObject();
            if (atPermission) json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // "UserPromptSubmit", "user_prompt_submit" and "beforeSubmitPrompt" alike: lower case without separators.
    public static string Normalize(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    // The last part of the folder the session works in.
    private static string Project(JsonElement input)
    {
        string? folder = Text(input, "cwd");
        // Cursor and TRAE name the workspace's folders workspace_roots, Antigravity workspacePaths.
        if (folder is null && (input.TryGetProperty("workspace_roots", out var roots) || input.TryGetProperty("workspacePaths", out roots)) && roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() > 0)
            folder = roots[0].ValueKind == JsonValueKind.String ? roots[0].GetString() : null;
        if (string.IsNullOrWhiteSpace(folder)) return "";
        string trimmed = folder.Trim().TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool? Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static string OneLine(string text, int limit) => Cut(string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim(), limit);

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..(char.IsHighSurrogate(text[limit - 2]) ? limit - 2 : limit - 1)] + "…";
}

// AgentKeys of BrimDeck.Core, for the hook, which has only the protocol: the key of a session's approval or question.
public static class AgentKeysWire
{
    public static string For(string harness, string session) => "agent:" + harness + ":" + session;
}
