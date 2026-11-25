using System.Text;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

// brim's command line ("brim <object> <action>"), what it adds to the protocol (buttons, waiting for an answer, a slot, a
// backlight color, notices kept off the list), and what BrimDeck's core makes of them.
internal static class BrimCliTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        Commands(check);
        Options(check);
        Json(check);
        Wire(check);
        Intake(check);
        Hub(check);
        Buttons(check);
        Coordinator(check);
        ListAndRemove(check);
    }

    // brim notify list, remove, and close or remove by the id list gives.
    private static void ListAndRemove(Action<string, bool> check)
    {
        check("List: list takes --json and -s, and nothing else", Brim("notify", "list") is { Kind: NotifyCommandKind.List, JsonOutput: false, Message: { Type: NoticeTypes.List, AppName: null } }
            && Brim("notify", "list", "--json", "-s", "ci") is { Kind: NotifyCommandKind.List, JsonOutput: true, Message.AppName: "ci" }
            && Brim("notify", "list", "--source=ci").Message?.AppName == "ci" && Brim("notify", "list", "x").Kind == NotifyCommandKind.Error && Brim("notify", "list", "-s").Kind == NotifyCommandKind.Error);
        check("List: BRIM_SOURCE names a sender, so it does not narrow what list shows", BrimCommandLine.Parse(["notify", "list"], "Claude Code").Message?.AppName is null);
        check("Remove: remove takes a key and the source, or --notice", Brim("notify", "remove", "deploy", "-s", "ci") is { Kind: NotifyCommandKind.Send, Message: { Type: NoticeTypes.Remove, ReplaceId: "deploy", AppName: "ci", Stop: null } }
            && Brim("notify", "remove", "--notice", "abc") is { Message: { Type: NoticeTypes.Remove, NoticeId: "abc", ReplaceId: null } }
            && Brim("notify", "close", "--notice", "abc") is { Message: { Type: NoticeTypes.Notify, Stop: true, NoticeId: "abc", ReplaceId: null } }
            && Brim("notify", "remove").Kind == NotifyCommandKind.Error && Brim("notify", "remove", "k", "--notice", "abc").Kind == NotifyCommandKind.Error
            && Brim("notify", "send", "x", "--notice", "abc").Kind == NotifyCommandKind.Error && Brim("notify", "pause") is { Kind: NotifyCommandKind.Error, Error: { } verb } && verb.Contains("list"));
        check("Remove: a removal names a notice, or it is refused", NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.Remove }) == "no-target"
            && NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.Remove, NoticeId = "a" }) is null && NoticeProtocol.Validate(new NoticeMessage { Type = NoticeTypes.List }) is null
            && NoticeProtocol.Validate(Brim("notify", "close", "--notice", "abc").Message!) is null && NoticeProtocol.Validate(new NoticeMessage { Stop = true, Kind = NoticeTimes.Stopwatch }) == "no-key");

        var hub = new NoticeHub();
        var mine = hub.Receive(Input("Deploying", "-k", "deploy", "-s", "ci", "--context", "main"), T0).Notice!;
        var other = hub.Receive(Input("Tests passed", "-s", "ci"), T0.AddSeconds(1)).Notice!;
        var app = hub.Receive(new NoticeInput { Origin = NoticeOrigin.Windows, AppKey = "windows:QQ", AppName = "QQ", InstanceKey = "windows:QQ:7:1", Title = "Alice", Body = "Code 482913 is your verification code" }, T0.AddSeconds(2)).Notice!;
        var unlisted = hub.Receive(Input("Switched", "--no-history"), T0.AddSeconds(3)).Notice!;
        var cli = NoticeEntries.Of(hub.Notices, all: false, null, codes: false, _ => false, n => n == other);
        check("List: without the user's leave only the command line's notices, newest first", cli is [{ Title: "Tests passed", Waiting: true, Origin: "cli" }, { Title: "Deploying", Key: "deploy", Context: "main", Source: "ci", Unread: true }]
            && !cli.Any(e => e.Id == unlisted.Id));
        var all = NoticeEntries.Of(hub.Notices, all: true, null, codes: true, n => n == app, _ => false);
        check("List: with it every source, and a verification code stays hidden as the notices page hides it", all.Count == 3 && all[0] is { Origin: "windows", Source: "QQ", Shown: true } first
            && !first.Body.Contains("482913") && NoticeEntries.Of(hub.Notices, true, "qq", false, _ => false, _ => false) is [{ Body: var body }] && body.Contains("482913"));
        check("List: a notice of another source is out of reach without the leave", !NoticeEntries.Reachable(app, false) && NoticeEntries.Reachable(app, true) && NoticeEntries.Reachable(mine, false));

        var big = Enumerable.Range(0, 40).Select(i => new NoticeEntry { Id = "n" + i.ToString("D2"), Title = "t", Body = new string('x', 3000), ReceivedAt = T0.AddSeconds(-i) }).ToList();
        var pages = new List<NoticeMessage>();
        string? cursor = null;
        do { var page = NoticeProtocol.ListPage(big, cursor, NoticeScopes.Cli, "e"); pages.Add(page); cursor = page.Cursor; } while (cursor is not null && pages.Count < 50);
        check("List: pages fit in a frame and give every notice once, in order", pages.Count > 1 && pages.All(p => NoticeProtocol.Fits(p) && p is { EventId: "e", Scope: NoticeScopes.Cli })
            && pages.SelectMany(p => p.Notices!).Select(e => e.Id).SequenceEqual(big.Select(e => e.Id)));
        var firstPage = NoticeProtocol.ListPage(big, null, NoticeScopes.Cli, null);
        big.RemoveAt(1);
        var resumed = NoticeProtocol.ListPage(big, firstPage.Cursor, NoticeScopes.Cli, null);
        check("List: a page starts after the last one shown even when notices were removed meanwhile", resumed.Notices![0].Id == "n" + firstPage.Notices!.Count.ToString("D2")
            && NoticeProtocol.ListPage(big, "garbage", NoticeScopes.Cli, null).Notices![0].Id == "n00");

        var byOtherSource = NoticeIntake.FromPipe(Brim("notify", "remove", "deploy").Message!, T0);
        check("Remove: by key only the sender's own source, by id any notice", hub.Target(byOtherSource) is null
            && hub.Target(NoticeIntake.FromPipe(Brim("notify", "remove", "deploy", "-s", "ci").Message!, T0)) == mine
            && hub.Target(NoticeIntake.FromPipe(Brim("notify", "remove", "--notice", app.Id).Message!, T0)) == app);
        var closeApp = hub.Receive(NoticeIntake.FromPipe(Brim("notify", "close", "--notice", app.Id).Message!, T0), T0.AddSeconds(4));
        var closeMine = hub.Receive(NoticeIntake.FromPipe(Brim("notify", "close", "--notice", mine.Id).Message!, T0), T0.AddSeconds(5));
        check("Close: by id it closes a command-line notice and leaves an application's alone", closeApp is { Receipt: NoticeReceipt.Filtered, Reason: "no-timer" } && app.SourcePresent
            && closeMine is { Receipt: NoticeReceipt.Updated } && closeMine.Notice == mine && !mine.SourcePresent && hub.Notices.Contains(mine));
    }

    private static NotifyCommand Brim(params string[] args) => BrimCommandLine.Parse(args);

    private static void Commands(Action<string, bool> check)
    {
        check("brim: notify send takes a title, a body and the default source", Brim("notify", "send", "Done", "All tests passed") is
            { Kind: NotifyCommandKind.Send, Message: { Title: "Done", Body: "All tests passed", AppName: BrimCommandLine.DefaultSource, Origin: "cli" } });
        check("brim: the first word is a command, never a title", Brim("Done").Kind == NotifyCommandKind.Error && Brim("notify", "Done").Kind == NotifyCommandKind.Error
            && Brim("notify").Kind == NotifyCommandKind.Error && Brim("notify", "send", "status") is { Kind: NotifyCommandKind.Send, Message.Title: "status" }
            && Brim("notify", "send", "close", "run") is { Kind: NotifyCommandKind.Send, Message: { Title: "close", Body: "run" } });
        check("brim: BRIM_SOURCE names the source, and -s comes before it", BrimCommandLine.Parse(["notify", "send", "x"], "Claude Code").Message?.AppName == "Claude Code"
            && BrimCommandLine.Parse(["notify", "send", "x", "-s", "ci"], "Claude Code").Message?.AppName == "ci" && BrimCommandLine.Parse(["notify", "send", "x"], "  ").Message?.AppName == "brim");
        var close = Brim("notify", "close", "deploy", "-s", "ci");
        var old = NotifyCommandLine.Parse(["close", "deploy", "-s", "ci"]);
        check("brim: notify close sends what brim-notify close sends", close is { Kind: NotifyCommandKind.Send, Message: { Stop: true, ReplaceId: "deploy", AppName: "ci" } }
            && Encoding.UTF8.GetString(NoticeProtocol.Serialize(close.Message)) == Encoding.UTF8.GetString(NoticeProtocol.Serialize(old.Message!)));
        check("brim: timer, stopwatch and event have a verb each way", Brim("timer", "start", "25m", "Tea") is { Kind: NotifyCommandKind.Send, Message: { Kind: NoticeTimes.Countdown, Title: "Tea", InMs: 25 * 60_000 } }
            && Brim("timer", "stop", "tea") is { Message: { Stop: true, ReplaceId: "tea" } } && Brim("stopwatch", "start", "Build") is { Message.Kind: NoticeTimes.Stopwatch }
            && Brim("stopwatch", "stop", "b") is { Message.Stop: true } && Brim("event", "add", "Weekly", "--at", "15:00") is { Message.Kind: NoticeTimes.Event }
            && Brim("event", "remove", "weekly") is { Message: { Stop: true, ReplaceId: "weekly" } });
        check("brim: an unknown command or verb is refused, and the message names it", Brim("timer", "pause") is { Kind: NotifyCommandKind.Error, Error: { } verb } && verb.Contains("timer pause")
            && Brim("hello") is { Kind: NotifyCommandKind.Error, Error: { } word } && word.Contains("hello") && Brim("timer") is { Kind: NotifyCommandKind.Error, Error: { } none } && none.Contains("brim timer start"));
        check("brim: run, status, help and version", Brim("run", "--", "make", "test") is { Kind: NotifyCommandKind.Run, Program: ["make", "test"], Message.AppName: null }
            && Brim("status").Kind == NotifyCommandKind.Status && Brim("status", "--json") is { Kind: NotifyCommandKind.Status, JsonOutput: true } && Brim("status", "x").Kind == NotifyCommandKind.Error
            && Brim().Kind == NotifyCommandKind.Help && Brim("--help").Kind == NotifyCommandKind.Help && Brim("help").Kind == NotifyCommandKind.Help && Brim("--version").Kind == NotifyCommandKind.Version);
        check("brim: a stop that names no key says how brim writes it", Brim("timer", "stop") is { Kind: NotifyCommandKind.Error, Error: { } why } && why.Contains("brim timer stop KEY"));
        check("brim: the help names the commands, the buttons and the exit codes", BrimCommandLine.Usage.Contains("brim notify send TITLE") && BrimCommandLine.Usage.Contains("--action KEY=LABEL")
            && BrimCommandLine.Usage.Contains("--slot PLACE") && BrimCommandLine.Usage.Contains("4 dismissed") && !BrimCommandLine.Usage.Contains("brim-notify"));
        check("brim-notify: its own way of writing commands is unchanged", NotifyCommandLine.Parse(["Done"]) is { Kind: NotifyCommandKind.Send, Message: { Title: "Done", AppName: "brim-notify" } }
            && NotifyCommandLine.Parse(["status"]).Kind == NotifyCommandKind.Status && NotifyCommandLine.Parse(["timer", "25m"]) is { Message.Kind: NoticeTimes.Countdown });
    }

    private static void Options(Action<string, bool> check)
    {
        check("Options: the level by name or by number; flash is the level between plain and persistent", Brim("notify", "send", "x", "-l", "flash").Message?.Level == "flash"
            && Brim("notify", "send", "x", "-l", "0").Message?.Level == "silent" && Brim("notify", "send", "x", "-l", "1").Message?.Level == "plain"
            && Brim("notify", "send", "x", "-l", "2").Message?.Level == "flash" && Brim("notify", "send", "x", "-l", "3").Message?.Level == "persistent"
            && Brim("notify", "send", "x", "-l", "4").Kind == NotifyCommandKind.Error && Brim("notify", "send", "x", "-l", "glow").Message?.Level == "glow");
        check("Options: every level name a sender may write is a level BrimDeck knows", NoticeKeys.Level("flash") == NoticeLevel.Glow && NoticeKeys.Level("glow") == NoticeLevel.Glow
            && NoticeKeys.Level("lively") == NoticeLevel.Glow && NoticeKeys.Level("2") == NoticeLevel.Glow && NoticeKeys.Level("0") == NoticeLevel.Silent && NoticeKeys.Level("1") == NoticeLevel.Plain
            && NoticeKeys.Level("3") == NoticeLevel.Persistent && NoticeKeys.Level("persistent") == NoticeLevel.Persistent && NoticeKeys.Level("4") == NoticeLevel.Auto);
        var full = Brim("notify", "send", "x", "-k", "job", "--context", "repo · main", "--slot", "Music", "--glow", "#e5c890", "--image", @"C:\a.png", "--timeout", "5m", "--no-history");
        check("Options: the key, the context, the slot, the backlight, the picture, the time and the list", full.Message is
            { ReplaceId: "job", Context: "repo · main", Slot: "music", Glow: "#E5C890", Picture: @"C:\a.png", TimeoutMs: 300_000, History: false, Wait: null });
        check("Options: a slot, a color or a time BrimDeck cannot take is refused before anything is sent", Brim("notify", "send", "x", "--slot", "left").Kind == NotifyCommandKind.Error
            && Brim("notify", "send", "x", "--glow", "red").Kind == NotifyCommandKind.Error && Brim("notify", "send", "x", "--glow", "#12345").Kind == NotifyCommandKind.Error
            && Brim("notify", "send", "x", "--timeout", "2h").Kind == NotifyCommandKind.Error && Brim("notify", "send", "x", "--timeout", "0s").Kind == NotifyCommandKind.Error);
        check("Options: --icon none shows no icon", Brim("notify", "send", "x", "--icon", "none").Message is { HideIcon: true, Icon: null }
            && Brim("notify", "send", "x", "--icon", "a.png").Message is { HideIcon: null, Icon: "a.png" });
        var buttons = Brim("notify", "send", "Run?", "--action", "allow=Allow", "--action", "deny=Deny it", "--copy", "Copy=a=b", "--open", "Docs=https://example.com/x");
        check("Options: buttons keep their order; a button that answers the sender means waiting, and the first of them is the main one", buttons.Message is { Wait: true, Actions: [
            { Key: "allow", Label: "Allow", Type: null, Style: NoticeActionTypes.Primary }, { Key: "deny", Label: "Deny it", Style: null },
            { Key: "copy-1", Label: "Copy", Type: NoticeActionTypes.Copy, Value: "a=b" }, { Key: "open-2", Label: "Docs", Type: NoticeActionTypes.Open, Value: "https://example.com/x" }] });
        check("Options: a copy or an open button alone does not make the sender wait", Brim("notify", "send", "Code", "--copy", "Copy=482913").Message is { Wait: null, Actions.Count: 1 }
            && Brim("notify", "send", "Code", "--copy", "Copy=482913", "--wait").Message?.Wait == true);
        check("Options: a button needs both of its parts", Brim("notify", "send", "x", "--action", "allow").Kind == NotifyCommandKind.Error && Brim("notify", "send", "x", "--action", "=Allow").Kind == NotifyCommandKind.Error
            && Brim("notify", "send", "x", "--copy", "Copy=").Kind == NotifyCommandKind.Error && Brim("notify", "send", "x", "--open", "Docs").Kind == NotifyCommandKind.Error);
        check("Options: waiting, buttons and a slot are for a plain notice", Brim("timer", "start", "5m", "--wait").Kind == NotifyCommandKind.Error && Brim("run", "--action", "a=b", "--", "make").Kind == NotifyCommandKind.Error
            && Brim("stopwatch", "start", "--slot", "music").Kind == NotifyCommandKind.Error && Brim("event", "add", "x", "--at", "15:00", "--no-history").Kind == NotifyCommandKind.Error);
        check("Options: -r still names the key, as -k does", Brim("notify", "send", "x", "-r", "a").Message?.ReplaceId == "a" && Brim("notify", "send", "x", "--key=b").Message?.ReplaceId == "b"
            && NotifyCommandLine.Parse(["x", "-k", "c"]).Message?.ReplaceId == "c");
    }

    private static void Json(Action<string, bool> check)
    {
        var send = Brim("notify", "send", "--json", "-", "-l", "3");
        check("JSON: --json takes a file or - and needs no words on the command line", send is { Kind: NotifyCommandKind.Send, JsonInput: "-", Message: { Title: null, Level: "persistent" } }
            && Brim("notify", "send", "--json", "hook.json").JsonInput == "hook.json");
        var file = NoticeProtocol.Deserialize("""{"title":"From file","body":"b","appName":"Hook","level":"plain","distro":"Ubuntu","nativeId":"7","stop":true,"actions":[{"key":"ok","label":"OK"}]}"""u8)!;
        var whole = NotifyCommandLine.Combine(file, send.Message!, "brim");
        check("JSON: the options given come over the file's values, and what is not a sender's to say is dropped", whole is
            { Title: "From file", Body: "b", AppName: "Hook", Level: "persistent", Distro: null, NativeId: null, Stop: null, Origin: "cli", Type: NoticeTypes.Notify });
        check("JSON: a button that answers the sender makes the notice wait", whole!.Wait == true);
        var copyOnly = NotifyCommandLine.Combine(NoticeProtocol.Deserialize("""{"title":"t","actions":[{"key":"c","label":"Copy","type":"copy","value":"1"}]}"""u8)!, new NoticeMessage(), "brim");
        check("JSON: one that copies does not, and the source falls back to the default", copyOnly is { Wait: null, AppName: "brim" });
        check("JSON: with no words in the file or the options there is nothing to send", NotifyCommandLine.Combine(NoticeProtocol.Deserialize("""{"level":"plain"}"""u8)!, new NoticeMessage(), "brim") is null
            && NotifyCommandLine.Combine(NoticeProtocol.Deserialize("{}"u8)!, new NoticeMessage { Title = "t" }, "brim") is { Title: "t" });
    }

    private static void Wire(Action<string, bool> check)
    {
        string Text(NoticeMessage message) => Encoding.UTF8.GetString(NoticeProtocol.Serialize(message));
        var plain = Text(new NoticeMessage { Title = "t" });
        check("Wire: the new fields are left out when not given, so an earlier BrimDeck reads the same message as before",
            new[] { "slot", "glow", "context", "picture", "hideIcon", "history", "wait", "timeoutMs", "waiting" }.All(name => !plain.Contains('"' + name + '"')));
        var sent = new NoticeMessage { Title = "t", Slot = "music", Glow = "#E5C890", Context = "c", Wait = true, TimeoutMs = 60_000, History = false, Actions = [new() { Key = "a", Label = "A", Type = "copy", Value = "v", Style = "primary" }] };
        var back = NoticeProtocol.Deserialize(NoticeProtocol.Serialize(sent))!;
        check("Wire: they travel as written", back is { Slot: "music", Glow: "#E5C890", Context: "c", Wait: true, TimeoutMs: 60_000, History: false, Actions: [{ Key: "a", Label: "A", Type: "copy", Value: "v", Style: "primary" }] }
            && NoticeProtocol.Validate(back) is null && back.Actions![0].Value == "v");
        check("Wire: a time outside what a notice may stay is refused", NoticeProtocol.Validate(new NoticeMessage { Title = "t", TimeoutMs = 10 }) == "bad-timeout"
            && NoticeProtocol.Validate(new NoticeMessage { Title = "t", TimeoutMs = NoticeProtocol.MaxTimeoutMs + 1 }) == "bad-timeout" && NoticeProtocol.Validate(new NoticeMessage { Title = "t", TimeoutMs = NoticeProtocol.MaxTimeoutMs }) is null);
        var longValue = new NoticeMessage { Title = "t", Slot = new string('s', 200), Context = new string('c', 900), Actions = [new() { Key = "k", Label = "L", Type = "copy", Value = new string('v', 5000) }] };
        check("Wire: over-long values are cut like the other fields", NoticeProtocol.Validate(longValue) is null && longValue.Slot!.Length == NoticeTimes.MaxText && longValue.Context!.Length == NoticeProtocol.MaxField
            && longValue.Actions![0].Value!.Length == NoticeProtocol.MaxActionValue);
        var result = new NoticeMessage { Type = NoticeTypes.Result, Result = NoticeWaits.Action, ActionKey = "allow", EventId = "e1" };
        check("Wire: a result names how the notice ended and which button", NoticeProtocol.Validate(result) is null && Text(result).Contains("\"type\":\"result\"") && Text(result).Contains("\"actionKey\":\"allow\"")
            && Text(NoticeProtocol.Reply(NoticeResults.Accepted)) is var reply && !reply.Contains("waiting"));
    }

    private static NoticeInput Input(params string[] args) => NoticeIntake.FromPipe(Brim(["notify", "send", .. args]).Message!, T0);

    private static void Intake(Action<string, bool> check)
    {
        var asked = Input("Run?", "--action", "allow=Allow", "--action", "deny=Deny", "--timeout", "5m", "-c", "warning", "--glow", "#e5c890", "--context", " repo\nmain ");
        check("Intake: a notice that is waited for keeps its buttons, its time, its color and its context", asked is
            { Awaited: true, Timeout: { TotalMinutes: 5 }, Glow: "#E5C890", Context: "repo main", Category: NoticeCategory.Warning, Unlisted: false,
              Actions: [{ Key: "allow", Kind: NoticeActionKind.Reply, Primary: true }, { Key: "deny", Kind: NoticeActionKind.Reply, Primary: false }] }
            && (asked.Capabilities & NoticeCapabilities.Actions) != 0 && asked.AppKey == "cli:brim");
        var message = Brim("notify", "send", "Done", "--action", "ok=OK").Message!;
        message.Wait = false;
        check("Intake: a button that answers the sender is dropped when the sender does not wait", NoticeIntake.FromPipe(message, T0) is { Awaited: false, Actions.Count: 0, Capabilities: NoticeCapabilities.None });
        var copies = Input("Code", "482913", "--copy", "Copy=482913", "--open", "Site=https://example.com/a b", "--open", @"Log=C:\logs\run.txt", "--open", "App=app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        check("Intake: buttons that copy and open stay without waiting, each with what it copies or opens", copies is { Awaited: false, Capabilities: NoticeCapabilities.None, Actions: [
            { Kind: NoticeActionKind.Copy, Value: "482913" }, { Kind: NoticeActionKind.Open, Value: "https://example.com/a%20b" }, { Kind: NoticeActionKind.Open, Value: @"C:\logs\run.txt" },
            { Kind: NoticeActionKind.Open, Value: "app:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }] });
        check("Intake: what BrimDeck does not open is no button", NoticeIntake.OpenTarget("file:///C:/x.txt", null) is null && NoticeIntake.OpenTarget("ms-settings:display", null) is null
            && NoticeIntake.OpenTarget(@"\\server\share\x", null) is null && NoticeIntake.OpenTarget(@"..\x.txt", null) is null && NoticeIntake.OpenTarget("cmd /c calc", null) is null
            && NoticeIntake.OpenTarget("app:C:\\Windows\\notepad.exe", null) is null && NoticeIntake.OpenTarget("javascript:alert(1)", null) is null && NoticeIntake.OpenTarget("", null) is null
            && Input("x", "--open", "Bad=ftp://example.com").Actions.Count == 0);
        check("Intake: a sender inside a WSL distribution opens web addresses only", NoticeIntake.OpenTarget("https://example.com", "Ubuntu") == "https://example.com/"
            && NoticeIntake.OpenTarget(@"C:\logs\run.txt", "Ubuntu") is null && NoticeIntake.OpenTarget("app:Calc", "Ubuntu") is null);
        check("Intake: a file that opening would run is known as such, so it is shown in its folder instead", NoticeIntake.Runs(@"C:\tools\setup.exe") && NoticeIntake.Runs(@"C:\x\run.BAT")
            && NoticeIntake.Runs(@"C:\x\link.lnk") && NoticeIntake.Runs(@"C:\x\script.ps1") && NoticeIntake.Runs(@"C:\x\noextension") && NoticeIntake.Runs(@"C:\x\trick.exe. ")
            && !NoticeIntake.Runs(@"C:\logs\run.txt") && !NoticeIntake.Runs(@"C:\x\report.html") && !NoticeIntake.Runs(@"C:\x\photo.png"));
        var many = Brim("notify", "send", "x", "--copy", "A=1", "--copy", "B=2", "--copy", "C=3", "--copy", "D=4", "--copy", "E=5").Message!;
        check("Intake: at most four buttons, and a key only once", NoticeIntake.FromPipe(many, T0).Actions.Count == NoticeIntake.MaxButtons
            && NoticeIntake.FromPipe(new NoticeMessage { Title = "t", Actions = [new() { Key = "a", Label = "A", Type = "copy", Value = "1" }, new() { Key = "a", Label = "B", Type = "copy", Value = "2" }] }, T0).Actions is [{ Label: "A" }]);
        check("Intake: the slot, the hidden icon and the list", Input("x", "--slot", "usage") is { Slot: NoticeSlot.Usage, HideIcon: false, Unlisted: false } && Input("x", "--icon", "none").HideIcon
            && Input("x", "--no-history").Unlisted && Input("x").Slot is null && NoticeKeys.Slot("ISLAND") == NoticeSlot.Island && NoticeKeys.Slot("nowhere") is null);
        check("Intake: a notice that is waited for is always listed, where it can be answered", Input("x", "--no-history", "--action", "a=A") is { Unlisted: false, Awaited: true });
        check("Intake: a slot names the collapsed part whose place the notice takes; the island is none", NoticeKeys.Part(NoticeSlot.Usage) == CompactPart.Rings && NoticeKeys.Part(NoticeSlot.Music) == CompactPart.Music
            && NoticeKeys.Part(NoticeSlot.Monitor) == CompactPart.Monitor && NoticeKeys.Part(NoticeSlot.Clock) == CompactPart.Clock && NoticeKeys.Part(NoticeSlot.Transfer) == CompactPart.Transfer
            && NoticeKeys.Part(NoticeSlot.Devices) == CompactPart.Devices && NoticeKeys.Part(NoticeSlot.Island) is null);
        check("Intake: a color is #RRGGBB or nothing", NoticeKeys.Color("#a1b2c3") == "#A1B2C3" && NoticeKeys.Color("a1b2c3") is null && NoticeKeys.Color("#a1b2c") is null && NoticeKeys.Color("#gggggg") is null && NoticeKeys.Color(null) is null);
        var timer = Brim("timer", "start", "5m").Message!;
        timer.Wait = true;
        check("Intake: only a plain notice is waited for", !NoticeIntake.Waits(timer) && !NoticeIntake.FromPipe(timer, T0).Awaited);
        var agent = NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", Title = "t", AppName = "hook", Wait = true, Actions = [new() { Key = "ok", Label = "OK" }, new() { Key = "o", Label = "Open", Type = "open", Value = @"C:\x.txt" }] }, "Ubuntu", false, T0);
        check("Intake: through a WSL agent a sender waits and answers the same way, and opens no Windows path", agent is { Awaited: true, AppKey: "cli:Ubuntu:hook", Actions: [{ Key: "ok", Kind: NoticeActionKind.Reply }] });
        var desktop = NoticeIntake.FromAgent(new NoticeMessage { Origin = "dbus", Title = "t", AppName = "app", NativeId = "3", Actions = [new() { Key = "default", Label = "Open" }, new() { Key = "r", Label = "Reply", Type = "copy", Value = "x" }] }, "Ubuntu", true, T0);
        check("Intake: a desktop notification's actions all answer its application, whatever type they claim", desktop.Actions is [{ Key: "default", Kind: NoticeActionKind.Reply }, { Key: "r", Kind: NoticeActionKind.Reply, Value: null }]);
    }

    private static void Hub(Action<string, bool> check)
    {
        var hub = new NoticeHub();
        int changes = 0; hub.Changed += () => changes++;
        var shown = hub.Receive(Input("Switched to headphones", "--no-history", "-k", "out"), T0);
        check("Hub: a notice kept off the list shows and is found by its id, but is not listed, counted or announced as a change", shown is { Receipt: NoticeReceipt.Accepted, Present: true, Notice: { Unlisted: true } notice }
            && hub.Find(notice.Id) == notice && hub.Notices.Count == 0 && hub.UnviewedCount == 0 && changes == 0);
        var again = hub.Receive(Input("Switched to speakers", "--no-history", "-k", "out"), T0.AddSeconds(1));
        check("Hub: the same key updates the one that shows", again is { Receipt: NoticeReceipt.Updated, Present: true } && again.Notice == shown.Notice && again.Notice!.Title == "Switched to speakers" && hub.Notices.Count == 0);
        var closed = hub.Receive(NoticeIntake.FromPipe(Brim("notify", "close", "out").Message!, T0), T0.AddSeconds(2));
        check("Hub: closing it by its key takes it away", closed.Receipt == NoticeReceipt.Updated && closed.Notice == shown.Notice && hub.Find(shown.Notice!.Id) is null);
        for (int i = 0; i < NoticeHub.MaxUnlisted + 5; i++) hub.Receive(Input("n" + i, "--no-history"), T0.AddSeconds(3));
        var last = hub.Receive(Input("last", "--no-history"), T0.AddSeconds(4));
        check("Hub: only the latest of them are kept", hub.Find(last.Notice!.Id) is not null && hub.Notices.Count == 0 && hub.Delete(last.Notice.Id) && hub.Find(last.Notice.Id) is null);

        var asked = hub.Receive(Input("Run?", "dotnet test", "-k", "perm", "--action", "allow=Allow", "--action", "deny=Deny", "--copy", "Copy=dotnet test"), T0.AddSeconds(5));
        var waiting = asked.Notice!;
        check("Hub: a notice that is waited for is listed, stays until handled and offers its buttons", asked.Present && hub.Notices.Contains(waiting) && waiting is { Awaited: true, Actions.Count: 3 }
            && NoticeLevels.Resolve(waiting, null, T0) == NoticeLevel.Persistent && (waiting.Capabilities & NoticeCapabilities.Actions) != 0);
        check("Hub: it does so whatever level the sender or the user's rule names, short of keeping it to the notices page",
            NoticeLevels.Resolve(waiting, new NoticeAppRule { Level = NoticeLevel.Plain }, T0) == NoticeLevel.Persistent
            && NoticeLevels.Resolve(waiting, new NoticeAppRule { Level = NoticeLevel.Silent }, T0) == NoticeLevel.Silent);
        hub.Answered(waiting.Id);
        check("Hub: once answered it no longer waits or stays, loses the buttons that answered and keeps the one that copies", waiting is { Awaited: false, SourcePresent: false }
            && (waiting.Capabilities & NoticeCapabilities.Actions) == 0 && NoticeLevels.Resolve(waiting, null, T0) != NoticeLevel.Persistent
            && waiting.Actions.Any(action => action.Kind == NoticeActionKind.Copy) && hub.Notices.Contains(waiting));
        var asks = hub.Receive(Input("Run?", "dotnet test", "-k", "perm", "--action", "allow=Allow"), T0.AddSeconds(9));
        check("Hub: asked again with the same words, it shows and waits again", asks is { Present: true, Receipt: NoticeReceipt.Updated } && asks.Notice == waiting && waiting is { Awaited: true, SourcePresent: true });
        var styled = hub.Receive(Input("Styled", "--slot", "clock", "--glow", "#112233", "--context", "ctx", "--icon", "none", "--timeout", "10m"), T0.AddSeconds(10)).Notice!;
        check("Hub: the slot, the color, the context, the hidden icon and the time are kept with the notice", styled is { Slot: NoticeSlot.Clock, Glow: "#112233", Context: "ctx", HideIcon: true, Timeout.TotalMinutes: 10 });
    }

    private static void Buttons(Action<string, bool> check)
    {
        check("Buttons: the island shows them when the panel opens on a click, or on hover after a delay long enough to reach one",
            NoticeActions.OnIsland(clickToExpand: true, 0, IslandForm.Capsule, false) && NoticeActions.OnIsland(false, NoticeActions.HoverDelayForButtons, IslandForm.Notch, false)
            && !NoticeActions.OnIsland(false, 0, IslandForm.Capsule, false) && !NoticeActions.OnIsland(false, NoticeActions.HoverDelayForButtons - 1, IslandForm.Notch, false));
        check("Buttons: never on the indicator, nor for a notice in one part's place", !NoticeActions.OnIsland(true, 0, IslandForm.Line, false) && !NoticeActions.OnIsland(true, 900, IslandForm.Capsule, true));
        NoticeAction allow = new("allow", "Allow", Primary: true), deny = new("deny", "Deny"), copy = new("c", "Copy", NoticeActionKind.Copy, "x");
        var picked = NoticeActions.Pick([deny, allow, copy], hasCode: true, canOpen: true);
        check("Buttons: the sender's main button first, then its others; at most two on the island", picked is [{ Kind: NoticeActions.ButtonKind.Sender, Action.Key: "allow", Primary: true }, { Action.Key: "deny", Primary: false }]);
        var own = NoticeActions.Pick([], hasCode: true, canOpen: true);
        check("Buttons: without any of the sender's, copying a code comes before opening the application, and the first is drawn as the main one",
            own is [{ Kind: NoticeActions.ButtonKind.CopyCode, Primary: true }, { Kind: NoticeActions.ButtonKind.OpenApp, Primary: false }]);
        check("Buttons: one of the sender's leaves room for one of BrimDeck's; none means none", NoticeActions.Pick([copy], false, true) is [{ Action.Key: "c", Primary: true }, { Kind: NoticeActions.ButtonKind.OpenApp }]
            && NoticeActions.Pick([], false, false).Count == 0 && NoticeActions.Pick([allow, deny, copy], false, false, limit: 4).Count == 3);
        check("Buttons: all show while the words keep their room, else the first alone, else none", NoticeActions.Fit([50, 40], 6, 300, 96) == 2 && NoticeActions.Fit([50, 40], 6, 190, 96) == 1
            && NoticeActions.Fit([50, 40], 6, 140, 96) == 0 && NoticeActions.Fit([], 6, 300, 96) == 0 && NoticeActions.Fit([50, 40], 6, 198, 96) == 2);
        check("Tones: a notice's own color comes first, then the one of a warning, an error or a success; else none",
            NoticeTones.Glow(new Notice { Glow = "#112233", Category = NoticeCategory.Error }) == "#112233" && NoticeTones.Glow(new Notice { Category = NoticeCategory.Warning }) == NoticeTones.Warning
            && NoticeTones.Glow(new Notice { Category = NoticeCategory.Error }) == NoticeTones.Error && NoticeTones.Glow(new Notice { Category = NoticeCategory.Success }) == NoticeTones.Success
            && NoticeTones.Glow(new Notice { Category = NoticeCategory.Message }) is null && NoticeTones.Glow(new Notice { Category = NoticeCategory.Task }) is null);
        check("Tones: they are among the backlights the settings offer", NoticeStyle.Swatches.Contains(NoticeTones.Warning) && NoticeStyle.Swatches.Contains(NoticeTones.Error) && NoticeStyle.Swatches.Contains(NoticeTones.Success));
    }

    private static void Coordinator(Action<string, bool> check)
    {
        var coordinator = new AttentionCoordinator();
        AttentionItem Item(string key, TimeSpan? limit) => new() { Lane = "main", Kind = AttentionKind.Notice, Key = key, Level = NoticeLevel.Persistent, Limit = limit, Duration = TimeSpan.FromSeconds(6) };
        coordinator.Submit(Item("a", TimeSpan.FromMinutes(10)), T0);
        check("Coordinator: a notice that stays until handled stays for the time its sender gave", coordinator.NextDeadline(T0) == T0.AddMinutes(10)
            && coordinator.Tick(T0.AddMinutes(9)).Count == 0 && coordinator.Tick(T0.AddMinutes(10)).Any(change => change.Ended?.Key == "a"));
        var other = new AttentionCoordinator();
        other.Submit(Item("b", null), T0);
        check("Coordinator: without one, for the usual limit", other.NextDeadline(T0) == T0 + AttentionCoordinator.PersistentLimit);
        other.Submit(Item("b", TimeSpan.FromMinutes(1)), T0.AddSeconds(5));
        check("Coordinator: an update brings the notice's time of then", other.Current("main")?.Limit == TimeSpan.FromMinutes(1));
    }
}
