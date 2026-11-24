using System.Globalization;
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
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Claude, Codex, Cursor, Antigravity, OpenCode };

    // The name a format's sessions show by when --name gives none.
    public static string DefaultName(string format) => format switch { Codex => "Codex", Cursor => "Cursor", Antigravity => "Antigravity", OpenCode => "OpenCode", _ => "Claude Code" };
}

// What one hook call does: a report of the session's state, a notice (a finished turn), a question or an approval the hook
// waits on and the reply the tool reads from its output, and a report after the answer. Any of them may be missing.
public sealed class AgentHookPlan
{
    public NoticeMessage? Report { get; init; }
    public NoticeMessage? Notice { get; init; }
    public NoticeMessage? Ask { get; init; }
    // The hook's output for how Ask ended (null: the user did not answer here); null output leaves the tool to its own flow.
    public Func<NoticeMessage?, string?> Reply { get; init; } = _ => null;
    // The state once Ask was answered here.
    public NoticeMessage? Answered { get; init; }
    // What the hook prints when it has no answer to give: nothing for most tools; Antigravity reads a JSON object from every hook.
    public string? Quiet { get; init; }
}

// Reads a coding agent's hook input (the JSON on standard input) and plans what "brim hook" sends BrimDeck and prints back.
// Nothing here blocks the tool for long without the user: only approvals and questions wait, and every way they end without an
// answer here prints nothing, which leaves the tool to its own dialog in the terminal.
public static class AgentHook
{
    // The keys of an approval's buttons, and of the button that hands it back to the terminal.
    public const string Allow = "allow", AlwaysAllow = "always", Deny = "deny", Terminal = "terminal";
    // Words of a last message kept in a notice.
    private const int MessageLimit = 600;

    // id: the session id's prefix in BrimDeck (the tool, as the settings know it); name: the tool's name. chinese: the words of
    // the notices (the hook runs apart from BrimDeck and words them in the system's language).
    public static AgentHookPlan Plan(string format, string id, string name, JsonElement input, bool chinese)
    {
        string T(string zh, string en) => chinese ? zh : en;
        if (input.ValueKind != JsonValueKind.Object) return new();
        string evt = Normalize(Text(input, "hook_event_name") ?? Text(input, "hookEventName") ?? "");
        string? session = Text(input, "session_id") ?? Text(input, "conversation_id") ?? Text(input, "conversationId") ?? Text(input, "sessionId") ?? Text(input, "thread_id");
        if (session is null || evt.Length == 0) return new() { Quiet = Quiet(format, evt) };
        if (format == AgentHookFormats.Antigravity) return Antigravity(id, name, session, Project(input), evt, input, T);
        string project = Project(input);
        NoticeMessage State(string state, string? title = null, string? body = null) => new()
        {
            Type = NoticeTypes.Agent, AppId = id, AppName = name, Session = session, State = state, Context = project.Length > 0 ? project : null,
            Title = title, Body = body is null ? null : Cut(body, MessageLimit)
        };
        string? tool = Text(input, "tool_name");
        switch (evt)
        {
            case "sessionstart": return new() { Report = State(AgentSessionStates.Started) };
            case "sessionend": return new() { Report = State(AgentSessionStates.Ended) };
            case "userpromptsubmit" or "beforesubmitprompt": return new() { Report = State(AgentSessionStates.Working, OneLine(Text(input, "prompt") ?? "", 200)) };
            case "posttooluse" or "posttoolusefailure" or "afterfileedit" or "aftershellexecution" or "aftermcpexecution":
                return new() { Report = State(AgentSessionStates.Working) };
            case "stop" or "agentstop":
            {
                // Cursor says how the turn ended; an aborted turn is no result to tell.
                string? status = Text(input, "status");
                if (status is "aborted") return new() { Report = State(AgentSessionStates.Idle) };
                string last = Text(input, "last_assistant_message") ?? "";
                bool failed = status is "error";
                return new()
                {
                    Report = State(AgentSessionStates.Done, body: last),
                    Notice = new NoticeMessage
                    {
                        AppName = name, Context = project.Length > 0 ? project : null, ReplaceId = "agent-done:" + id + ":" + session, Category = failed ? "error" : "success",
                        Title = failed ? T("出错停止", "Stopped with an error") : T("已完成", "Done"), Body = last.Length > 0 ? Cut(last, MessageLimit) : null
                    }
                };
            }
            case "notification":
                return Text(input, "notification_type") is "idle_prompt" ? new() { Report = State(AgentSessionStates.Idle) } : new();
            case "permissionrequest" when format != AgentHookFormats.Cursor && tool != "AskUserQuestion":
                return Approval(format, id, name, session, project, input, tool ?? "", State, T);
            case "pretooluse" when format is AgentHookFormats.Claude or AgentHookFormats.OpenCode && tool == "AskUserQuestion":
                return Question(format, id, name, session, project, input, State, T);
            default: return new();
        }
    }

    // What Antigravity reads from a hook that decides nothing: an empty object, and for Stop a decision that lets the loop end.
    public static string? Quiet(string format, string evt) => format != AgentHookFormats.Antigravity ? null : evt == "stop" ? """{"decision":"stop"}""" : "{}";

    // Antigravity (its app, IDE and CLI share one hooks file): camelCase fields; the user's last words come with each call of the
    // model (PreInvocation), and Stop says why the loop ended. It has no approval or question to answer.
    private static AgentHookPlan Antigravity(string id, string name, string session, string project, string evt, JsonElement input, Func<string, string, string> T)
    {
        NoticeMessage State(string state, string? title = null) => new()
        {
            Type = NoticeTypes.Agent, AppId = id, AppName = name, Session = session, State = state, Context = project.Length > 0 ? project : null, Title = title
        };
        string? quiet = Quiet(AgentHookFormats.Antigravity, evt);
        switch (evt)
        {
            case "sessionstart": return new() { Report = State(AgentSessionStates.Started), Quiet = quiet };
            case "preinvocation": return new() { Report = State(AgentSessionStates.Working, Text(input, "lastUserInput") is { } words ? OneLine(words, 200) : null), Quiet = quiet };
            case "stop":
            {
                string reason = Text(input, "terminationReason") ?? "";
                if (reason == "USER_CANCELED") return new() { Report = State(AgentSessionStates.Idle), Quiet = quiet };
                bool failed = reason == "ERROR" || Text(input, "error") is { Length: > 0 };
                return new()
                {
                    Report = State(AgentSessionStates.Done), Quiet = quiet,
                    Notice = new NoticeMessage
                    {
                        AppName = name, Context = project.Length > 0 ? project : null, ReplaceId = "agent-done:" + id + ":" + session, Category = failed ? "error" : "success",
                        Title = failed ? T("出错停止", "Stopped with an error") : T("已完成", "Done"), Body = failed ? Text(input, "error") : null
                    }
                };
            }
            default: return new() { Quiet = quiet };
        }
    }

    private static AgentHookPlan Approval(string format, string id, string name, string session, string project, JsonElement input, string tool,
        Func<string, string?, string?, NoticeMessage> state, Func<string, string, string> T)
    {
        input.TryGetProperty("tool_input", out var args);
        string? command = Text(args, "command"), file = Text(args, "file_path") ?? Text(args, "path") ?? Text(args, "notebook_path"), url = Text(args, "url");
        string title = command is not null ? T("请求运行命令", "Wants to run a command")
            : file is not null ? T("请求修改文件", "Wants to change a file")
            : url is not null ? T("请求访问网页", "Wants to open a web page")
            : T($"请求使用 {tool}", $"Wants to use {tool}");
        string code = command ?? file ?? url ?? (args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "");
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
            AppName = name, Context = project.Length > 0 ? project : null, ReplaceId = AgentKeysWire.For(id, session), Category = "warning",
            Title = title, Body = Text(args, "description") is { Length: > 0 } why ? Cut(why, MessageLimit) : null, Code = Cut(code, NoticeProtocol.MaxCode),
            Actions = actions, Wait = true
        };
        return new()
        {
            Report = state(AgentSessionStates.Approval, title, null), Ask = ask, Answered = state(AgentSessionStates.Working, null, null),
            Reply = result => result is { Result: NoticeWaits.Action, ActionKey: Allow or AlwaysAllow or Deny } chosen
                ? format == AgentHookFormats.OpenCode ? OpenCodeReply(chosen.ActionKey!) : Decision(chosen.ActionKey!, always, T) : null
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

    // Claude Code's AskUserQuestion: each question with its options, and words of the user's own (the terminal offers "Other"
    // with every question too). Answered here, the hook gives the tool its input back with the answers, keyed by the question's
    // words; options picked are given by their labels, joined with ", " for several.
    private static AgentHookPlan Question(string format, string id, string name, string session, string project, JsonElement input,
        Func<string, string?, string?, NoticeMessage> state, Func<string, string, string> T)
    {
        if (!input.TryGetProperty("tool_input", out var args) || !args.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
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
            AppName = name, Context = project.Length > 0 ? project : null, ReplaceId = AgentKeysWire.For(id, session), Category = "info",
            Title = title, Body = questions.Count == 1 ? questions[0].Question : string.Join("\n", questions.Select(q => "· " + q.Question)),
            Questions = questions, Actions = [new() { Key = Terminal, Label = T("在终端回答", "Answer in the terminal") }], Wait = true
        };
        var original = args.Clone();
        return new()
        {
            Report = state(AgentSessionStates.Question, title, null), Ask = ask, Answered = state(AgentSessionStates.Working, null, null),
            Reply = result => result is { Result: NoticeWaits.Action, Answers: { Count: > 0 } answers } ? (format == AgentHookFormats.OpenCode ? OpenCodeAnswers(words, questions, answers) : QuestionReply(original, words, questions, answers)) : null
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

    private static string? QuestionReply(JsonElement original, List<(string Text, List<string> Labels)> words, List<NoticeQuestion> questions, List<NoticeAnswer> answers)
    {
        var given = new List<(string Question, string Answer)>();
        for (int i = 0; i < questions.Count; i++)
        {
            var answer = answers.FirstOrDefault(a => a.Id == questions[i].Id);
            if (answer is null) continue;
            var labels = (answer.Keys ?? []).Select(k => int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < words[i].Labels.Count ? words[i].Labels[n] : null)
                .OfType<string>().ToList();
            if (answer.Text is { } typed && typed.Trim().Length > 0) labels.Add(typed.Trim());
            if (labels.Count > 0) given.Add((words[i].Text, string.Join(", ", labels)));
        }
        // Every question needs its answer; one left out would leave the tool asking it anyway.
        if (given.Count < questions.Count) return null;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("hookSpecificOutput");
            json.WriteString("hookEventName", "PreToolUse");
            json.WriteString("permissionDecision", "allow");
            json.WriteStartObject("updatedInput");
            foreach (var property in original.EnumerateObject())
                if (property.Name != "answers") property.WriteTo(json);
            json.WriteStartObject("answers");
            foreach (var (question, answer) in given) json.WriteString(question, answer);
            json.WriteEndObject();
            json.WriteEndObject();
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
        if (folder is null && input.TryGetProperty("workspace_roots", out var roots) && roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() > 0) folder = roots[0].ValueKind == JsonValueKind.String ? roots[0].GetString() : null;
        if (string.IsNullOrWhiteSpace(folder)) return "";
        string trimmed = folder.Trim().TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string OneLine(string text, int limit) => Cut(string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim(), limit);

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..(char.IsHighSurrogate(text[limit - 2]) ? limit - 2 : limit - 1)] + "…";
}

// AgentKeys of BrimDeck.Core, for the hook, which has only the protocol: the key of a session's approval or question.
public static class AgentKeysWire
{
    public static string For(string harness, string session) => "agent:" + harness + ":" + session;
}
