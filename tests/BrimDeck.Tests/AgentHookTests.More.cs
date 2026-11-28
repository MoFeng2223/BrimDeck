using System.Text;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// The coding agents' hooks, continued: which tools hold their own prompt back while a hook waits and what the island then
// does, the mark of a tool call, and what a session's transcript tells that no hook does.
internal static partial class AgentHookTests
{
    private static void Asks(Action<string, bool> check)
    {
        static (bool Approvals, bool Questions) Holds(string id) { var harness = AgentHarnesses.Find(id)!; return (harness.HoldsApprovals, harness.HoldsQuestions); }
        check("Asks: the tools that show their own prompt beside the hook, and those that only tell, have no switch",
            new[] { "claude", "opencode", "mimo", "kimi", "trae", "trae-cn", "cursor", "antigravity" }.All(id => Holds(id) == (false, false)));
        check("Asks: the tools that wait for the hook first have a switch for what they hold back",
            Holds("codex") == (true, false) && Holds("workbuddy") == (true, false) && Holds("commandcode") == (true, true)
            && Holds("codeagent") == (false, true) && Holds("qoder") == (true, true) && Holds("qoder-cn") == (true, true) && Holds("zcode") == (false, true));
        const string runs = """{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"rm x"}}""";
        var codex = AgentHook.Plan(AgentHookFormats.Codex, "codex", "Codex", Input(runs), chinese: false).Ask!;
        check("Asks: an approval says whether its tool holds its own prompt back",
            codex.Holds == true && Tool("commandcode", runs).Ask is { Holds: true } && Claude(runs).Ask is { Holds: null } && Tool("zcode", runs).Ask is { Holds: null }
            && AgentHook.Plan(AgentHookFormats.CodeBuddy, "workbuddy", "WorkBuddy", Input(runs), true).Ask is { Holds: true }
            && AgentHook.Plan(AgentHookFormats.OpenCode, "opencode", "OpenCode", Input(runs), true).Ask is { Holds: null });
        var settings = new DeckSettings();
        check("Asks: held back and not chosen in the settings, the island only tells; a prompt that shows beside the hook is always answered here",
            AgentAsks.OnlyTold(settings, codex) && !AgentAsks.OnlyTold(settings, Claude(runs).Ask!));
        settings.AgentApprovalsHeld.Add("codex");
        settings.AgentApprovalsHeld.Add("qoder");
        var asked = Tool("qoder", Asking("PreToolUse")).Ask!;
        check("Asks: chosen, an approval waits on the island; approvals and questions are chosen apart",
            !AgentAsks.OnlyTold(settings, codex) && AgentAsks.OnlyTold(settings, asked));
        settings.AgentQuestionsHeld.Add("qoder");
        var unknown = Tool("my-tool", Asking("PreToolUse")).Ask!;
        check("Asks: a question chosen waits; a tool BrimDeck does not know has no switch and is waited for as it asked",
            !AgentAsks.OnlyTold(settings, asked) && unknown.Holds == true && !AgentAsks.OnlyTold(new DeckSettings(), unknown));
        AgentAsks.Tell(codex);
        AgentAsks.Tell(asked);
        check("Asks: told only, the notice has no buttons or options, stays until the session moves on, and nobody waits for it",
            codex is { Wait: null, Holds: null, Actions: null, Questions: null, TimeoutMs: null, Level: "persistent", Title: "Wants to run a command", Code: "rm x", ReplaceId: "agent:codex:s" }
            && codex.Body!.Contains("Codex") && asked is { Wait: null, Questions: null, Actions: null, Body: "Q?" }
            && NoticeIntake.FromPipe(codex) is { Awaited: false } input && input.Questions.Count == 0);

        var approval = Claude("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"npm test","description":"Run"}}""");
        var ran = Claude("""{"hook_event_name":"PostToolUse","session_id":"s","tool_name":"Bash","tool_input":{"description":"Run","command":"npm test"},"tool_response":{"exit":0}}""");
        var other = Claude("""{"hook_event_name":"PostToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"ls"}}""");
        check("Calls: an approval and the result of its call carry one mark, however the tool orders the arguments; another call another",
            approval.Report?.Call is { Length: 16 } mark && ran.Report?.Call == mark && other.Report?.Call is { } differs && differs != mark
            && approval.Answered is { State: "working" } answered && answered.Call == mark
            && Claude("""{"hook_event_name":"PostToolUse","session_id":"s","tool_name":"Read","tool_input":{"command":"npm test","description":"Run"}}""").Report?.Call != mark);
        check("Calls: ZCode's camelCase fields are read alike, and a report that names no tool has no mark",
            Tool("zcode", """{"hookEventName":"PostToolUseFailure","sessionId":"s","toolName":"Bash","toolInput":{"command":"npm test","description":"Run"}}""").Report?.Call == approval.Report!.Call
            && Claude("""{"hook_event_name":"PostToolUse","session_id":"s"}""").Report is { State: "working", Call: null });
        check("Calls: a question is marked by what it asks, so its result with the answers added is the same call",
            Tool("zcode", Asking("PreToolUse")).Report?.Call is { } question
            && Tool("zcode", Asking("PostToolUse").Replace("\"tool_input\":{", "\"tool_input\":{\"answers\":{\"Q?\":\"A\"},")).Report?.Call == question
            && AgentHook.Stamp(null, default) is null && AgentHook.Stamp("Bash", default) is { Length: 16 });
    }

    // What the tools offer beyond allowing once, and what the island does with each tool's own ways (5.24, 5.25 of the live
    // tests): the suggestions as they came from the Claude desktop app and the Qoder app, 2026-11-28.
    private static void Offers(Action<string, bool> check)
    {
        const string suggested = """
            {"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"echo OK > D:/t/opt.txt"},"permission_suggestions":[
            {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo OK *"}],"behavior":"allow","destination":"localSettings"},
            {"type":"addDirectories","directories":["D:\\t"],"destination":"session"},
            {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm *"}],"behavior":"deny","destination":"localSettings"},
            {"type":"setMode","mode":"acceptEdits","destination":"session"}]}
            """;
        var plan = Tool("qoder", suggested);
        var actions = plan.Ask!.Actions!;
        check("Offers: each suggestion is a button of its own with what it allows and where it is kept; a rule that denies is not offered",
            actions.Select(a => a.Key).SequenceEqual([AgentHook.Allow, AgentHook.Deny, AgentHook.AlwaysAllow, AgentHook.AlwaysAllow + ":1", AgentHook.AlwaysAllow + ":2", AgentHook.Terminal])
            && actions[2].Label == "总是允许 echo OK *（本项目）" && actions[3].Label == "允许访问 D:\\t（本会话）" && actions[4].Label == "切换到 acceptEdits 模式（本会话）");
        JsonElement Updated(string key) => JsonDocument.Parse(plan.Reply(Chosen(key))!).RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        check("Offers: the button picked gives back its own suggestion alone, as the tool gave it",
            Updated(AgentHook.AlwaysAllow + ":1") is var folder && folder.GetProperty("behavior").GetString() == "allow" && folder.GetProperty("updatedPermissions").GetArrayLength() == 1
            && folder.GetProperty("updatedPermissions")[0].GetProperty("type").GetString() == "addDirectories" && folder.GetProperty("updatedPermissions")[0].GetProperty("destination").GetString() == "session"
            && Updated(AgentHook.AlwaysAllow + ":2").GetProperty("updatedPermissions")[0].GetProperty("mode").GetString() == "acceptEdits"
            && !Updated(AgentHook.Allow).TryGetProperty("updatedPermissions", out _) && plan.Reply(Chosen(AgentHook.AlwaysAllow + ":3")) is null);
        string longRule = suggested.Replace("echo OK *", new string('x', 60));
        check("Offers: a long rule is cut, and its scope still shows",
            Tool("qoder", longRule).Ask!.Actions![2].Label is { } cut && cut.EndsWith("…（本项目）", StringComparison.Ordinal) && cut.Length <= 4 + 40 + 5);
        var open = AgentHook.Plan(AgentHookFormats.OpenCode, "mimo", "MiMo Code", Input("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"bash","tool_input":{"command":"echo hi"},"permission_suggestions":["echo *"]}"""), true);
        check("Offers: OpenCode's always names the patterns it adds",
            open.Ask!.Actions!.Single(a => a.Key == AgentHook.AlwaysAllow).Label == "总是允许 echo *" && open.Reply(Chosen(AgentHook.AlwaysAllow)) == """{"reply":"always"}""");

        // The Qoder IDE: an "extra" object of the signed-in user with every event, and a PermissionRequest with tool_use_id.
        const string extra = "\"extra\":{\"email\":\"a@b.c\"},\"request_set_id\":\"r\",";
        string Ide(string json) => json.Replace("{\"hook_event_name\"", "{" + extra + "\"hook_event_name\"");
        var ideApproval = Tool("qoder", Ide("""{"hook_event_name":"PermissionRequest","session_id":"s","tool_name":"Bash","tool_input":{"command":"echo OK"},"tool_use_id":"t1"}"""));
        var ideOutput = JsonDocument.Parse(ideApproval.Reply(Chosen(AgentHook.Deny))!).RootElement.GetProperty("hookSpecificOutput");
        check("Qoder IDE: its approval holds the IDE back, and the reply is its own permissionDecision",
            ideApproval.Ask is { Holds: true } && Tool("qoder", suggested).Ask is { Holds: null }
            && ideOutput.GetProperty("permissionDecision").GetString() == "deny" && ideOutput.GetProperty("permissionDecisionReason").GetString()!.Length > 0 && !ideOutput.TryGetProperty("decision", out _)
            && JsonDocument.Parse(ideApproval.Reply(Chosen(AgentHook.Allow))!).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString() == "allow");
        var ideQuestion = Tool("qoder-cn", Ide(Asking("PermissionRequest")));
        check("Qoder IDE: its question is told at once, its PreToolUse after the answer is left alone, and its result closes it",
            ideQuestion is { Ask: null, Notice: { Level: "persistent", Body: "Q?" }, Report: { State: "question", Call: { } asked } }
            && Tool("qoder-cn", Ide(Asking("PreToolUse"))) is { Ask: null, Report: null, Notice: null }
            && Tool("qoder-cn", Ide(Asking("PostToolUse"))).Report is { State: "working" } done && done.Call == asked
            && Tool("qoder-cn", Asking("PermissionRequest")).Ask is { Holds: null, Questions.Count: 1 });
        check("Qoder IDE: the session it ends after every turn stays, idle; the Qoder app's end removes it",
            Tool("qoder", Ide("""{"hook_event_name":"SessionEnd","session_id":"s","reason":"success"}""")).Report?.State == "idle"
            && Tool("qoder", """{"hook_event_name":"SessionEnd","session_id":"s","reason":"other"}""").Report?.State == "ended");

        AgentHookPlan Codex(string json) => AgentHook.Plan(AgentHookFormats.Codex, "codex", "Codex", Input(json), chinese: true);
        check("Codex: the app's own requests after a turn are none of the user's sessions; a prompt of the CLI without a transcript is",
            Codex("""{"hook_event_name":"SessionStart","session_id":"f","cwd":"D:\\p","source":"fork","transcript_path":null}""") is { Report: null, Notice: null }
            && Codex("""{"hook_event_name":"UserPromptSubmit","session_id":"f","cwd":"D:\\p","transcript_path":null,"prompt":"This is an app-generated request for a suggestion"}""").Report is null
            && Codex("""{"hook_event_name":"UserPromptSubmit","session_id":"e","cwd":"D:\\p","transcript_path":null,"prompt":"Reply OK"}""").Report?.State == "working"
            && Codex("""{"hook_event_name":"SessionStart","session_id":"m","cwd":"D:\\p","source":"startup"}""").Report?.State == "started");
        check("Codex: a stopped turn is idle", Codex("""{"hook_event_name":"Interrupt","session_id":"m","turn_id":"t"}""").Report?.State == "idle");
        const string codexAsks = """{"hook_event_name":"PreToolUse","session_id":"m","tool_name":"request_user_input","tool_use_id":"c1","tool_input":{"questions":[{"header":"颜色","id":"color","question":"Red 还是 Blue？","options":[{"label":"Red"},{"label":"Blue"}]}]}}""";
        var codexQuestion = Codex(codexAsks);
        check("Codex: its plan-mode question is told without waiting, and its result closes it",
            codexQuestion is { Ask: null, Notice: { Title: "颜色", Body: "Red 还是 Blue？" }, Report: { State: "question", Call: { } call } }
            && Codex(codexAsks.Replace("PreToolUse", "PostToolUse")).Report is { State: "working" } result && result.Call == call
            && Codex("""{"hook_event_name":"PreToolUse","session_id":"m","tool_name":"Bash","tool_input":{"command":"ls"}}""").Report is null);
    }

    private static void Transcripts(Action<string, bool> check)
    {
        const string use = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"description":"Run","command":"npm test"}}]}}""";
        const string result = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":"User rejected tool use"}]}}""";
        const string stop = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""";
        string call = AgentHook.Stamp("Bash", Input("""{"command":"npm test","description":"Run"}"""))!;
        var ids = new HashSet<string>();
        var none = new HashSet<string>();
        AgentTranscripts.Calls(Input(use), "Bash", call, ids);
        AgentTranscripts.Calls(Input(use), "Bash", AgentHook.Stamp("Bash", Input("""{"command":"ls"}"""))!, none);
        AgentTranscripts.Calls(Input(use), "Write", call, none);
        AgentTranscripts.Calls(Input(result), "Bash", call, none);
        check("Transcript: a call is found by its tool and arguments, and its result by the call's id",
            ids.SetEquals(["toolu_1"]) && none.Count == 0 && AgentTranscripts.Result(Input(result), ids) && !AgentTranscripts.Result(Input(result), none)
            && !AgentTranscripts.Result(Input(result), new HashSet<string> { "toolu_2" }) && !AgentTranscripts.Result(Input(use), ids));
        check("Transcript: a stopped turn is the user's record that says so, as a part or as plain words, and nothing else",
            AgentTranscripts.Interrupted(Input(stop)) && AgentTranscripts.Interrupted(Input("""{"type":"user","message":{"content":"[Request interrupted by user]"}}"""))
            && !AgentTranscripts.Interrupted(Input("""{"type":"assistant","message":{"content":[{"type":"text","text":"[Request interrupted by user]"}]}}"""))
            && !AgentTranscripts.Interrupted(Input("""{"type":"user","message":{"content":[{"type":"text","text":"why did it say [Request interrupted by user]?"}]}}"""))
            && !AgentTranscripts.Interrupted(Input(result)) && !AgentTranscripts.Interrupted(Input("""{"type":"user"}""")) && !AgentTranscripts.Interrupted(Input("[]"))
            && AgentTranscripts.Record("not json") is null && AgentTranscripts.Record("{broken") is null);

        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Tests-transcript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "tail.jsonl"), missing = Path.Combine(folder, "none.jsonl");
            File.WriteAllText(file, use + "\n" + "{\"type\":\"x\"");
            var tail = new TranscriptTail(file, 0);
            var first = tail.Read();
            File.AppendAllText(file, "}\n" + stop + "\r\n");
            var second = tail.Read();
            check("Transcript: only whole lines are read, each once, as they are added",
                first.SequenceEqual([use]) && second.SequenceEqual(["{\"type\":\"x\"}", stop]) && tail.Read().Count == 0 && tail.Position == new FileInfo(file).Length);
            check("Transcript: the lines between two places leave out a line the end cuts",
                AgentTranscripts.Lines(file, 0, use.Length + 5).SequenceEqual([use]) && AgentTranscripts.Lines(file, 0, new FileInfo(file).Length).Count == 3
                && AgentTranscripts.Lines(file, use.Length + 1, new FileInfo(file).Length).Count == 2);
            File.WriteAllText(file, "short\n");
            check("Transcript: a file written anew is read from its end, and a missing one gives nothing",
                tail.Read().Count == 0 && tail.Position == 6 && new TranscriptTail(missing, 0).Read().Count == 0
                && AgentTranscripts.Length(missing) == -1 && AgentTranscripts.Length(file) == 6);

            // A hook that waits on the island while the tool itself ends the call.
            (int Code, List<NoticeMessage> Sent, string Printed) Wait(string added)
            {
                string transcript = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jsonl");
                File.WriteAllText(transcript, use + "\n");
                string hookInput = "{\"hook_event_name\":\"PermissionRequest\",\"session_id\":\"s1\",\"transcript_path\":" + JsonSerializer.Serialize(transcript)
                    + ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\",\"description\":\"Run\"}}";
                var sent = new List<NoticeMessage>();
                var never = new TaskCompletionSource<(NoticeMessage? Reply, NoticeMessage? Result)>();
                var sender = new NotifySender("test", message => { lock (sent) sent.Add(message); return Task.FromResult<NoticeMessage?>(NoticeProtocol.Reply(NoticeResults.Accepted)); }, _ => (null, 1, false), "", "")
                {
                    SendWaiting = message => { lock (sent) sent.Add(message); return never.Task; },
                    Input = () => new MemoryStream(Encoding.UTF8.GetBytes(hookInput))
                };
                var before = Console.Out;
                var printed = new StringWriter();
                Console.SetOut(printed);
                try
                {
                    // Everything up to the wait runs before RunAsync returns, the watch's first look at the file included.
                    var run = NotifyCli.RunAsync(BrimCommandLine.Parse(["hook", "claude"]), sender);
                    File.AppendAllText(transcript, added);
                    int code = run.Wait(TimeSpan.FromSeconds(10)) ? run.Result : -1;
                    lock (sent) return (code, [.. sent], printed.ToString());
                }
                finally { Console.SetOut(before); }
            }
            var refused = Wait(result + "\n");
            check("Hook watch: the call answered in the tool itself ends the wait, tells that the session works again, and prints nothing",
                refused.Code == 0 && refused.Printed.Length == 0 && refused.Sent.Count == 3 && refused.Sent[0] is { Type: NoticeTypes.Agent, State: "approval" }
                && refused.Sent[1] is { Wait: true, ReplaceId: "agent:claude:s1" } && refused.Sent[2] is { Type: NoticeTypes.Agent, State: "working", Session: "s1" }
                && refused.Sent[2].Call == refused.Sent[0].Call);
            var stopped = Wait(result + "\n" + stop + "\n");
            check("Hook watch: a refusal that stops the turn, as in the Claude desktop app, tells that the session is idle",
                stopped.Code == 0 && stopped.Printed.Length == 0 && stopped.Sent.Count == 3 && stopped.Sent[2] is { Type: NoticeTypes.Agent, State: "idle" });
            var alone = Wait(stop + "\n");
            check("Hook watch: a turn stopped while the approval waits tells that the session is idle",
                alone.Code == 0 && alone.Sent.Count == 3 && alone.Sent[2] is { Type: NoticeTypes.Agent, State: "idle" });
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }
}
