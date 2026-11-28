using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

internal static class NoticeCoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public static void Run(string root, Action<string, bool> check)
    {
        Hub(check);
        LateAndRestart(root, check);
        Coordinator(check);
        History(root, check);
        Switches(check);
        Intake(check);
        Pages(check);
        SettingsValues(root, check);
        AuditCoreTests.Run(root, check);
        AuditCore2Tests.Run(root, check);
        AuditR3Tests.Run(root, check);
    }

    private static NoticeInput Input(string instance, string app = "windows:Chat", string title = "Hi", string body = "Body", string? replace = null,
        string? eventId = null, double? progress = null, NoticeCategory category = NoticeCategory.Message, NoticePriority hint = NoticePriority.Normal) => new()
    {
        Origin = NoticeOrigin.Windows, AppKey = app, AppName = app.Split(':')[^1], InstanceKey = instance, Title = title, Body = body,
        ReplaceKey = replace, EventId = eventId, Progress = progress, Category = category, PriorityHint = hint
    };

    private static void Hub(Action<string, bool> check)
    {
        var rules = new Dictionary<string, NoticeAppRule>();
        var hub = new NoticeHub(key => rules.GetValueOrDefault(key));
        int changes = 0, saves = 0; hub.Changed += () => changes++; hub.Persist += _ => saves++;
        var first = hub.Receive(Input("a"), T0);
        check("A new notice is accepted and shown", first.Receipt == NoticeReceipt.Accepted && first.Present && hub.Notices.Count == 1 && hub.UnviewedCount == 1 && changes == 1 && saves == 1);
        hub.Receive(Input("b", eventId: "e1"), T0);
        var retry = hub.Receive(Input("c", eventId: "e1"), T0.AddSeconds(1));
        check("A retry with the same event id is a duplicate", retry.Receipt == NoticeReceipt.Duplicate && hub.Notices.Count == 2);
        check("The same event id from another application is not a duplicate", hub.Receive(Input("d", app: "cli:x", eventId: "e1"), T0).Receipt == NoticeReceipt.Accepted);
        check("A kept notice still recognizes its event id after ten minutes", hub.Receive(Input("e", eventId: "e1"), T0.AddMinutes(11)).Receipt == NoticeReceipt.Duplicate);
        hub.Delete(hub.FindInstance("b")!.Id);
        check("Event ids are forgotten after ten minutes once no kept notice has them", hub.Receive(Input("e", eventId: "e1"), T0.AddMinutes(11)).Receipt == NoticeReceipt.Accepted);

        var task = hub.Receive(Input("t1", app: "cli:make", title: "Build", replace: "build", progress: .1, category: NoticeCategory.Task), T0);
        var progress = hub.Receive(Input("t2", app: "cli:make", title: "Build", replace: "build", progress: .6, category: NoticeCategory.Task), T0.AddSeconds(2));
        check("A progress update changes the notice without showing it again", progress.Receipt == NoticeReceipt.Updated && !progress.Present && progress.Notice == task.Notice
            && task.Notice!.Progress == .6 && task.Notice.Revision == 1);
        var done = hub.Receive(Input("t3", app: "cli:make", title: "Build", replace: "build", category: NoticeCategory.Success), T0.AddSeconds(4));
        check("Finishing the task shows it again", done.Receipt == NoticeReceipt.Updated && done.Present && task.Notice!.Progress is null && task.Notice.Category == NoticeCategory.Success && hub.Notices[0] == task.Notice);
        check("The same replace key from another application is a different notice", hub.Receive(Input("t4", app: "cli:other", replace: "build"), T0).Receipt == NoticeReceipt.Accepted);
        check("The same title and body are not taken for duplicates", hub.Receive(Input("x1"), T0).Receipt == NoticeReceipt.Accepted && hub.Receive(Input("x2"), T0).Receipt == NoticeReceipt.Accepted);

        rules["windows:Muted"] = new NoticeAppRule { Key = "windows:Muted", Level = NoticeLevel.Silent };
        rules["windows:Off"] = new NoticeAppRule { Key = "windows:Off", Enabled = false };
        var muted = hub.Receive(Input("m", app: "windows:Muted"), T0);
        check("An application set to list only: its notice is kept but not shown", muted.Receipt == NoticeReceipt.Accepted && !muted.Present);
        int before = hub.Notices.Count;
        check("A disabled application's notice is filtered out entirely", hub.Receive(Input("o", app: "windows:Off"), T0).Receipt == NoticeReceipt.Filtered && hub.Notices.Count == before);
        check("A sender cannot raise its own notice's priority", hub.Receive(Input("h", hint: NoticePriority.High), T0).Notice!.Priority == NoticePriority.Normal);
        var low = hub.Receive(Input("l", hint: NoticePriority.Low), T0);
        check("A low priority hint keeps the notice quiet", low.Notice!.Priority == NoticePriority.Low && !low.Present);
        check("An alert is high priority unless its sender lowered it", NoticeHub.AlertPriority(NoticePriority.Normal) == NoticePriority.High && NoticeHub.AlertPriority(NoticePriority.Low) == NoticePriority.Low);
        var listed = new NoticeHub(key => null, key => key != "windows:Unlisted");
        check("A source that is not received is filtered and still remembered, for the settings to offer it",
            listed.Receive(Input("u", app: "windows:Unlisted"), T0).Receipt == NoticeReceipt.Filtered && listed.Notices.Count == 0 && listed.KnownApps.Any(app => app.Key == "windows:Unlisted"));

        hub.SourceRemoved("a");
        var gone = hub.FindInstance("a")!;
        check("A notice removed at its source is kept, marked as gone", !gone.SourcePresent && hub.Notices.Contains(gone));
        hub.MarkViewed(gone.Id);
        int unviewed = hub.UnviewedCount;
        check("Marking viewed lowers the count", gone.Viewed && unviewed == hub.Notices.Count(n => !n.Viewed));
        hub.MarkAllViewed();
        check("Marking all viewed clears the count", hub.UnviewedCount == 0);
        int persisted = 0;
        hub.Persist += _ => persisted++;
        hub.DismissAgenda(gone.Id);
        hub.DismissAgenda(gone.Id);
        check("Closing a notice's planner strip marks it once and saves it", gone.AgendaDismissed && persisted == 1);
        check("Deleting removes one notice", hub.Delete(gone.Id) && hub.Find(gone.Id) is null && !hub.Delete("missing"));
        check("Known applications are remembered", hub.KnownApps.Any(app => app.Key == "cli:make"));

        var small = new NoticeHub { MaxCount = 3, Retention = TimeSpan.FromHours(1) };
        for (int i = 0; i < 5; i++) small.Receive(Input("n" + i), T0.AddMinutes(i));
        check("The list keeps at most MaxCount notices, newest first", small.Notices.Count == 3 && small.Notices[0].InstanceKey == "n4");
        check("Pruning drops notices past the retention", small.Prune(T0.AddMinutes(63)) && small.Notices.Count == 2);
        small.Clear();
        check("Clearing empties the list", small.Notices.Count == 0 && small.UnviewedCount == 0);

        var loaded = new NoticeHub();
        var saved = new Notice { InstanceKey = "k", AppKey = "windows:A", Title = "t", ReceivedAt = T0, Capabilities = NoticeCapabilities.Actions | NoticeCapabilities.OpenApp };
        loaded.Load([saved, saved, new Notice { Id = "", ReceivedAt = T0 }, new Notice { InstanceKey = "old", ReceivedAt = T0.AddDays(-3) }], [], T0.AddMinutes(1));
        check("Loaded notices are deduplicated, pruned and no longer present at their source", loaded.Notices.Count == 1 && !loaded.Notices[0].SourcePresent
            && loaded.Notices[0].Capabilities == NoticeCapabilities.OpenApp);

        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        var run = NoticeHub.Words(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "r", ExitCode = 1, DurationMs = 65_000, Command = "make" });
        check("A run result is worded in Chinese", run.Title == "make 失败" && run.Body == "退出码 1 · 用时 1 分 5 秒");
        Loc.Use(Loc.English);
        var ok = NoticeHub.Words(new NoticeInput { Origin = NoticeOrigin.Cli, AppKey = "cli:x", AppName = "x", InstanceKey = "r", ExitCode = 0, DurationMs = 1500 });
        check("A run result is worded in English", ok.Title == "Command finished" && ok.Body == "Exit code 0 · took 1.5 s");
        var bodyOnly = NoticeHub.Words(Input("b", title: "", body: "First line\nSecond"));
        check("A body without title gives its first line as the title", bodyOnly.Title == "First line" && bodyOnly.Body == "Second");
        Loc.Use(language);
    }

    // A Claude Code hook in WSL: brim-notify with one replace key per session, through the distribution's agent.
    private static NoticeMessage Hook(string eventId, string body, DateTimeOffset created, long? ageMs, string category = "success") => new()
    {
        Origin = "cli", AppName = "Claude Code", Title = "Claude Code · demo", Body = body, Category = category, ReplaceId = "claude-code:s1",
        EventId = eventId, CreatedAt = created, AgeMs = ageMs
    };

    // BrimDeck restarted: the saved notices come back, then the WSL hook keeps sending, early or late.
    private static void LateAndRestart(string root, Action<string, bool> check)
    {
        var before = new NoticeHub();
        var first = before.Receive(NoticeIntake.FromAgent(Hook("ev1", "done", T0, 0), "Ubuntu", false), T0);
        check("A fresh hook notice is shown and keeps its event id", first.Present && first.Notice!.EventId == "ev1");
        var history = new NoticeHistory(Path.Combine(root, "notices-restart"), new NoticeHistory.PlainProtector());
        history.Save(before.Notices, before.KnownApps);
        history.Load(out var saved, out var apps);
        check("A notice's event id is saved with it", saved.Single().EventId == "ev1");
        history.Delete();

        var now = T0.AddMinutes(38);
        var hub = new NoticeHub();
        hub.Load(saved, apps, now);
        hub.MarkAllViewed();
        var again = hub.Receive(NoticeIntake.FromAgent(Hook("ev1", "done", T0, 0), "Ubuntu", false), now);
        check("A message recorded before the restart is a duplicate after it", again.Receipt == NoticeReceipt.Duplicate && !again.Present
            && hub.Notices.Count == 1 && hub.UnviewedCount == 0);

        var late = hub.Receive(NoticeIntake.FromAgent(Hook("ev2", "waiting", T0, 38 * 60_000L, "info"), "Ubuntu", false), now);
        check("A message that waited 38 minutes is recorded, unviewed, but not shown", late.Receipt == NoticeReceipt.Updated && !late.Present && late.Reason == "late"
            && hub.Notices.Single().Body == "waiting" && hub.UnviewedCount == 1);

        // The distribution's clock is 10 minutes behind Windows': its own age says the message is fresh.
        now = now.AddMinutes(1);
        var fresh = hub.Receive(NoticeIntake.FromAgent(Hook("ev3", "done again", now.AddMinutes(-10), 0), "Ubuntu", false), now);
        check("A fresh message is shown even when the distribution's clock is off", fresh.Present && fresh.Receipt == NoticeReceipt.Updated);
        check("An update shown now reads as just arrived, not as old as the notice it replaced", fresh.Notice!.ReceivedAt == now
            && fresh.Notice.EventId == "ev3" && NoticeText.Relative(fresh.Notice.ReceivedAt, now) == Loc.T("刚刚", "Just now"));
        var oldAgent = hub.Receive(NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", Title = "x", EventId = "ev4", CreatedAt = now.AddHours(-2) }, "Ubuntu", false), now);
        check("A distribution's creation time alone is not trusted to hold a notice back", oldAgent.Present);

        var windowsCli = hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { Title = "late", EventId = "w1", CreatedAt = now.AddMinutes(-38) }), now);
        check("A late message from a sender on Windows' clock is judged by its creation time", windowsCli.Receipt == NoticeReceipt.Accepted && !windowsCli.Present
            && hub.Receive(NoticeIntake.FromPipe(new NoticeMessage { Title = "now", EventId = "w2", CreatedAt = now.AddSeconds(-2) }), now).Present);

        var input = NoticeIntake.FromAgent(Hook("ev5", "x", now, 2 * 60_000L), "Ubuntu", false);
        check("A high priority message may be later than a normal one", NoticeHub.Late(input, NoticePriority.Normal, now) && !NoticeHub.Late(input, NoticePriority.High, now)
            && NoticeHub.Late(input with { Age = TimeSpan.FromMinutes(6) }, NoticePriority.High, now));
        check("Time-kind notices and Windows' notices keep their own rules", !NoticeHub.Late(input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Countdown } }, NoticePriority.Normal, now)
            && NoticeHub.Late(input with { Timing = new NoticeTiming { Kind = NoticeTimeKind.Mail } }, NoticePriority.Normal, now)
            && !NoticeHub.Late(Input("w") with { CreatedAt = now.AddHours(-1) }, NoticePriority.Normal, now));

        var message = new NoticeMessage { Title = "t", CreatedAt = now.AddSeconds(-90) };
        NoticeProtocol.Stamp(message, now);
        var stamped = new NoticeMessage { Title = "t" };
        NoticeProtocol.Stamp(stamped, now);
        check("A sender stamps a message's age by its own clock", message.AgeMs == 90_000 && stamped.CreatedAt == now && stamped.AgeMs == 0
            && NoticeProtocol.Deserialize(NoticeProtocol.Serialize(message))!.AgeMs == 90_000);
        check("A negative age is refused", NoticeProtocol.Validate(new NoticeMessage { Title = "t", AgeMs = -1 }) == "bad-age");
    }

    private static AttentionItem Item(string lane, AttentionKind kind, string key, string group = "", NoticePriority priority = NoticePriority.Normal, double seconds = 6, bool refresh = false)
        => new() { Lane = lane, Kind = kind, Key = key, Group = group, Priority = priority, Duration = TimeSpan.FromSeconds(seconds), Refresh = refresh };

    private static void Coordinator(Action<string, bool> check)
    {
        var c = new AttentionCoordinator();
        var started = c.Submit(Item("main", AttentionKind.Notice, "n1", "A"), T0);
        check("An item on a free lane starts at once", started is [{ Started.Key: "n1" }] && c.Current("main")?.Key == "n1" && c.NextDeadline(T0) == T0.AddSeconds(6));
        var merged = c.Submit(Item("main", AttentionKind.Notice, "n2", "A"), T0.AddMilliseconds(500));
        check("A notice of the conversation that shows merges with a count", merged is [{ Updated.Key: "n2" }] && c.Current("main")!.Count == 2);
        c.Submit(Item("main", AttentionKind.Notice, "n3", "A"), T0.AddSeconds(4));
        check("It merges however long after the one before, without waiting", c.Current("main") is { Key: "n3", Count: 3 } && c.Queued("main").Count == 0
            && c.NextDeadline(T0.AddSeconds(4)) == T0.AddSeconds(6));
        c.Submit(Item("main", AttentionKind.Notice, "n4", "A"), T0.AddSeconds(5));
        check("A merge near the end keeps the notice long enough to be read", c.Current("main") is { Key: "n4", Count: 4 } && c.NextDeadline(T0.AddSeconds(5)) == T0.AddSeconds(6.5));
        c.Submit(Item("main", AttentionKind.Quota, "q"), T0.AddSeconds(5));
        var trackDropped = c.Submit(Item("main", AttentionKind.Track, "t", seconds: 4), T0.AddSeconds(5));
        check("A track change that cannot show at once is dropped", trackDropped.Count == 0 && c.Queued("main").All(i => i.Kind != AttentionKind.Track));
        c.Hold("main", true, T0.AddSeconds(5.2));
        check("While held, the only wake-up is for waiting items going stale", c.NextDeadline(T0.AddSeconds(5.2)) == T0.AddSeconds(35));
        c.Submit(Item("main", AttentionKind.Notice, "hp", "B", NoticePriority.High), T0.AddSeconds(5.5));
        check("Nothing replaces a held item", c.Current("main")?.Key == "n4" && c.Queued("main").Select(i => i.Key).SequenceEqual(["hp", "q"]));
        c.Tick(T0.AddSeconds(60));
        check("A held item does not end on time", c.Current("main")?.Key == "n4");
        c.Hold("main", false, T0.AddSeconds(60));
        check("Released, it stays a moment before it ends or gives way", c.NextDeadline(T0.AddSeconds(60)) == T0.AddSeconds(61.5) && c.Tick(T0.AddSeconds(61)).Count == 0);
        var ended = c.Tick(T0.AddSeconds(61.5));
        check("Stale alerts are not shown after 30 s; high priority notices still are", ended.Any(ch => ch.Ended?.Key == "n4") && c.Current("main")?.Key == "hp"
            && c.Queued("main").Count == 0);

        // Another conversation's notice shows at once: it takes the island over as soon as the one that shows has been up 1.5 s.
        var turn = new AttentionCoordinator();
        turn.Submit(Item("t", AttentionKind.Notice, "a", "A"), T0);
        var waits = turn.Submit(Item("t", AttentionKind.Notice, "b", "B"), T0.AddSeconds(.5));
        check("Another conversation's notice waits only until the one that shows has been up 1.5 s", waits.Count == 0 && turn.Queued("t").Single().Key == "b"
            && turn.NextDeadline(T0.AddSeconds(.5)) == T0 + AttentionCoordinator.MinimumShown);
        turn.Submit(Item("t", AttentionKind.Notice, "b2", "B"), T0.AddSeconds(.8));
        turn.Submit(Item("t", AttentionKind.Notice, "c", "C"), T0.AddSeconds(1));
        check("Waiting conversations each keep their latest notice, with a count", turn.Queued("t").Select(i => (i.Key, i.Count)).SequenceEqual([("b2", 2), ("c", 1)]));
        var took = turn.Tick(T0.AddSeconds(1.5));
        check("They take over in the order they came; the notice replaced is not shown again", took is [{ Ended.Key: "a" }, { Started.Key: "b2" }]
            && turn.Queued("t").Single().Key == "c" && turn.NextDeadline(T0.AddSeconds(1.5)) == T0.AddSeconds(3));
        turn.Tick(T0.AddSeconds(3));
        // The island plays the motion of the notice it shows (the backlight, the liquid) for longer than 1.5 s: the waiting notice
        // takes over once that has played, not in the middle of it. An urgent one does not wait, and a presenter cannot hold the
        // island for longer than MotionLimit.
        var plays = new AttentionCoordinator();
        plays.Submit(Item("t", AttentionKind.Notice, "a", "A"), T0);
        plays.Playing("t", T0.AddSeconds(2.2), T0.AddSeconds(.2));
        plays.Submit(Item("t", AttentionKind.Notice, "b", "B"), T0.AddSeconds(.5));
        check("A waiting notice takes over only once the shown notice's motion has played", plays.NextDeadline(T0.AddSeconds(.5)) == T0.AddSeconds(2.2)
            && plays.Tick(T0.AddSeconds(1.5)).Count == 0 && plays.Current("t")?.Key == "a" && plays.Tick(T0.AddSeconds(2.2)) is [{ Ended.Key: "a" }, { Started.Key: "b" }]);
        plays.Playing("t", T0.AddSeconds(4.4), T0.AddSeconds(2.4));
        plays.Submit(Item("t", AttentionKind.Notice, "a2", "A"), T0.AddSeconds(2.5));
        plays.Submit(Item("t", AttentionKind.Notice, "b2", "B"), T0.AddSeconds(2.6));
        check("Meanwhile a message of the conversation that shows merges into it and one of another waits", plays.Current("t") is { Key: "b2", Count: 2 }
            && plays.Queued("t").Single().Key == "a2" && plays.Tick(T0.AddSeconds(3.7)).Count == 0);
        var alarm = new AttentionItem { Lane = "t", Kind = AttentionKind.Notice, Key = "alarm", Group = "Clock", Duration = TimeSpan.FromSeconds(6), Urgent = true };
        check("An urgent notice takes over in the middle of a motion", plays.Submit(alarm, T0.AddSeconds(3.8)) is [{ Ended.Key: "b2" }, { Started.Key: "alarm" }]);
        plays.Playing("t", T0.AddSeconds(60), T0.AddSeconds(4));
        check("A motion holds the island no longer than the limit", plays.NextDeadline(T0.AddSeconds(4)) == T0.AddSeconds(4) + AttentionCoordinator.MotionLimit);
        plays.Playing("other", T0.AddSeconds(9), T0.AddSeconds(4));
        check("A lane that shows nothing has no motion to wait for", plays.Current("other") is null && plays.Queued("other").Count == 0);
        var atOnce = turn.Submit(Item("t", AttentionKind.Notice, "d", "D"), T0.AddSeconds(6));
        check("A notice that has been up long enough gives way at once", turn.Current("t")?.Key == "d" && atOnce is [{ Ended.Key: "c" }, { Started.Key: "d" }]
            && turn.Queued("t").Count == 0 && turn.NextDeadline(T0.AddSeconds(6)) == T0.AddSeconds(12));
        var rank = new AttentionCoordinator();
        rank.Submit(Item("p", AttentionKind.Notice, "hp", "H", NoticePriority.High, 8), T0);
        rank.Submit(Item("p", AttentionKind.Notice, "n", "N"), T0.AddSeconds(3));
        check("A less important notice waits for a high priority one to end", rank.Current("p")?.Key == "hp" && rank.Queued("p").Single().Key == "n"
            && rank.NextDeadline(T0.AddSeconds(3)) == T0.AddSeconds(8));
        rank.Submit(Item("p", AttentionKind.Notice, "hp2", "H2", NoticePriority.High), T0.AddSeconds(4));
        check("Another high priority notice takes its place", rank.Current("p")?.Key == "hp2" && rank.Queued("p").Single().Key == "n");
        rank.Tick(T0.AddSeconds(10));
        check("The waiting notice shows once the island is free", rank.Current("p")?.Key == "n");
        check("A conversation is an application and a title; without a title, the application",
            AttentionCoordinator.GroupOf("windows:QQ", "群A") != AttentionCoordinator.GroupOf("windows:QQ", "群B") && AttentionCoordinator.GroupOf("windows:QQ", "") == "windows:QQ");

        // The song changes while its title still shows: the new title gets its whole time, counted from the change.
        var songs = new AttentionCoordinator();
        songs.Submit(Item("m", AttentionKind.Track, "track", seconds: 4), T0);
        var next = songs.Submit(Item("m", AttentionKind.Track, "track", seconds: 4), T0.AddSeconds(3));
        check("A title shown again in place runs its whole time from the change", next is [{ Updated.Key: "track" }] && songs.NextDeadline(T0.AddSeconds(3)) == T0.AddSeconds(7));

        var rate = new AttentionCoordinator();
        for (int i = 0; i < 3; i++) { rate.Submit(Item("r", AttentionKind.Notice, "x" + i, "G" + i, seconds: 1), T0.AddSeconds(i * 2)); rate.Tick(T0.AddSeconds(i * 2 + 1)); }
        var fourth = rate.Submit(Item("r", AttentionKind.Notice, "x3", "G3", seconds: 1), T0.AddSeconds(6));
        check("Beyond three notices in ten seconds a notice still shows at once, but subdued", fourth is [{ Started: { Key: "x3", Subdued: true } }]
            && rate.Queued("r").Count == 0 && rate.NextDeadline(T0.AddSeconds(6)) == T0.AddSeconds(7));
        rate.Tick(T0.AddSeconds(7));
        var fifth = rate.Submit(Item("r", AttentionKind.Notice, "x4", "G4", seconds: 1), T0.AddSeconds(10.5));
        check("Subdued notices do not count: once the window moves on, the next shows with its full effect", fifth is [{ Started: { Key: "x4", Subdued: false } }]);

        // The same notice on several screens: one item per lane with its key. It counts once, and ends everywhere at once.
        var screens = new AttentionCoordinator();
        var both = new List<AttentionChange>();
        for (int i = 0; i < 3; i++)
            foreach (var lane in new[] { "s1", "s2" }) both.AddRange(screens.Submit(Item(lane + i, AttentionKind.Notice, "k" + i, "G" + i), T0));
        check("A notice shown on several lanes counts once toward the rate limit", both.Count == 6 && both.All(change => change.Started is { Subdued: false })
            && screens.Submit(Item("s3", AttentionKind.Notice, "k3", "G3"), T0) is [{ Started.Subdued: true }]);
        var everywhere = screens.Withdraw("k0", T0.AddSeconds(1));
        check("Withdrawn, it ends on every lane", everywhere.Count == 2 && everywhere.All(change => change.Ended?.Key == "k0") && screens.Current("s10") is null && screens.Current("s20") is null
            && screens.Current("s11")?.Key == "k1");
        screens.Dismiss("s11", T0.AddSeconds(1));
        check("Dismissed on one lane (its panel cannot show it), it stays on the other", screens.Current("s11") is null && screens.Current("s21")?.Key == "k1");
        var rings = new AttentionCoordinator();
        int rang = 0;
        for (int i = 0; i < AttentionCoordinator.UrgentLimit; i++)
            foreach (var lane in new[] { "a", "b" })
                rang += rings.Submit(new AttentionItem { Lane = lane + i, Kind = AttentionKind.Notice, Key = "alarm" + i, Group = "windows:Clock", Urgent = true, Priority = NoticePriority.High, Duration = TimeSpan.FromSeconds(8) }, T0).Count(change => change.Started is not null);
        check("An alarm ringing on several lanes counts once toward the urgent limit", rang == 2 * AttentionCoordinator.UrgentLimit);

        // A notice that stays until it is handled.
        static AttentionItem Stay(string key, string group, bool urgent = false) => new()
        {
            Lane = "p", Kind = AttentionKind.Notice, Key = key, Group = group, Level = NoticeLevel.Persistent, Urgent = urgent, Duration = TimeSpan.FromSeconds(6)
        };
        var stay = new AttentionCoordinator();
        stay.Submit(Stay("call", "A"), T0);
        check("A persistent notice does not run out with its duration", stay.Tick(T0.AddSeconds(60)).Count == 0 && stay.Current("p") is { Key: "call", Persistent: true }
            && stay.NextDeadline(T0.AddSeconds(60)) == T0 + AttentionCoordinator.PersistentLimit);
        stay.Submit(Item("p", AttentionKind.Notice, "m1", "A"), T0.AddSeconds(61));
        stay.Submit(Item("p", AttentionKind.Notice, "hp", "B", NoticePriority.High), T0.AddSeconds(61));
        stay.Submit(Stay("alarm", "C", urgent: true), T0.AddSeconds(62));
        check("Nothing takes its place or merges into it; others wait behind it, also urgent ones", stay.Current("p") is { Key: "call", Count: 1 }
            && stay.Queued("p").Select(i => i.Key).SequenceEqual(["hp", "m1", "alarm"]));
        stay.Submit(Stay("call", "A"), T0.AddSeconds(63));
        stay.Tick(T0.AddSeconds(100));
        check("A waiting ordinary notice goes stale as before; a waiting persistent one does not", stay.Queued("p").Select(i => i.Key).SequenceEqual(["hp", "alarm"]));
        var handled = stay.Withdraw("call", T0.AddSeconds(101));
        check("Withdrawn (handled, or gone from its source), it ends and the next shows", handled is [{ Ended.Key: "call" }, { Started.Key: "hp" }]);
        stay.Tick(T0.AddSeconds(107));
        check("The next persistent notice then shows", stay.Current("p")?.Key == "alarm");
        var capped = stay.Tick(T0.AddSeconds(107) + AttentionCoordinator.PersistentLimit);
        check("Left unhandled, it ends at the safety limit", capped is [{ Ended.Key: "alarm" }] && stay.Current("p") is null);
        stay.Submit(Stay("job", "J"), T0.AddSeconds(400));
        stay.Hold("p", true, T0.AddSeconds(401));
        stay.Tick(T0.AddSeconds(400) + AttentionCoordinator.PersistentLimit + TimeSpan.FromMinutes(1));
        stay.Hold("p", false, T0.AddSeconds(700));
        check("Held by the user, it does not end at the limit either", stay.Current("p")?.Key == "job");
        var plainAgain = stay.Submit(Item("p", AttentionKind.Notice, "job", "J"), T0.AddSeconds(701));
        check("Sent again as an ordinary notice, it runs out as one", plainAgain is [{ Updated: { Key: "job", Persistent: false } }] && stay.NextDeadline(T0.AddSeconds(701)) == T0.AddSeconds(707));
        stay.Submit(Stay("job", "J"), T0.AddSeconds(702));
        check("Sent again as persistent, it stays again", stay.NextDeadline(T0.AddSeconds(702)) == T0.AddSeconds(702) + AttentionCoordinator.PersistentLimit);
        stay.SetBusy("p", true, T0.AddSeconds(703));
        check("Opening the panel ends a persistent notice", stay.Current("p") is null);
        var unhurried = new AttentionCoordinator();
        for (int i = 0; i < AttentionCoordinator.RateLimit; i++) unhurried.Submit(Item("u" + i, AttentionKind.Notice, "n" + i, "G" + i), T0);
        var beyond = unhurried.Submit(new AttentionItem { Lane = "p", Kind = AttentionKind.Notice, Key = "stay", Level = NoticeLevel.Persistent, Duration = TimeSpan.FromSeconds(6) }, T0);
        check("A persistent notice is never subdued by the rate limit", beyond is [{ Started: { Persistent: true, Subdued: false } }]);

        // Several screens: opening the panel on one ends the notices on all of them, including those waiting, even where the panel
        // opened showed none (its profile shows no notices); another screen's quota alert stays.
        static AttentionItem On(string lane, string key) => new()
        {
            Lane = lane, Kind = AttentionKind.Notice, Key = key, Group = key, Level = NoticeLevel.Persistent, Duration = TimeSpan.FromSeconds(6)
        };
        var opening = new AttentionCoordinator();
        foreach (var lane in new[] { "a", "b" }) { opening.Submit(On(lane, "call"), T0); opening.Submit(On(lane, "alarm"), T0); }
        opening.Submit(On("c", "alarm"), T0);
        opening.Submit(Item("d", AttentionKind.Quota, "quota", seconds: 30), T0);
        var opened = opening.SetBusy("a", true, T0.AddSeconds(1));
        check("Opening the panel on one screen ends the notices on every screen and starts none of those waiting",
            new[] { "a", "b", "c" }.All(lane => opening.Current(lane) is null && opening.Queued(lane).Count == 0) && opened.All(change => change.Started is null));
        check("Another screen's quota alert stays", opening.Current("d")?.Key == "quota");
        var other = new AttentionCoordinator();
        other.Submit(On("b", "call"), T0);
        other.SetBusy("a", true, T0.AddSeconds(1));
        check("Opening a panel that showed no notice still ends the one on another screen", other.Current("b") is null);
        var arriving = other.Submit(On("b", "late"), T0.AddSeconds(2));
        check("While a panel is open, a notice that arrives shows on no other screen", arriving.Count == 0 && other.Current("b") is null && other.Queued("b").Count == 0 && other.AnyOpen);
        other.Submit(Item("b", AttentionKind.Quota, "quota", seconds: 30), T0.AddSeconds(2));
        check("Another screen's quota alert still shows while a panel is open", other.Current("b")?.Key == "quota");
        other.Dismiss("b", T0.AddSeconds(3));
        other.SetLeaving("a", true);
        other.Submit(On("b", "after"), T0.AddSeconds(4));
        check("Once the pointer has left the open panel, other screens show arriving notices again", other.Current("b")?.Key == "after" && !other.AnyOpen);

        var busy = new AttentionCoordinator();
        busy.Submit(Item("b", AttentionKind.Quota, "q1", seconds: 5), T0);
        busy.SetBusy("b", true, T0.AddSeconds(1));
        check("Opening the panel ends the item; a quota alert waits", busy.Current("b") is null && busy.Queued("b").Single().Key == "q1");
        busy.Submit(Item("b", AttentionKind.Notice, "n", "A"), T0.AddSeconds(2));
        check("Notices are not queued while the panel is open", busy.Queued("b").Count == 1);
        var resumed = busy.SetBusy("b", false, T0.AddSeconds(3));
        check("Closing the panel resumes the alert with its remaining time", resumed.Single().Started?.Key == "q1" && busy.NextDeadline(T0.AddSeconds(3)) == T0.AddSeconds(7));
        busy.Submit(Item("b", AttentionKind.Notice, "n2", "B"), T0.AddSeconds(4));
        check("A notice takes over a quota alert", busy.Current("b")?.Key == "n2");
        busy.Dismiss("b", T0.AddSeconds(5));
        check("Dismissing shows what waits next", busy.Current("b")?.Key == "q1");
        busy.Withdraw("q1", T0.AddSeconds(5));
        check("A withdrawn item ends", busy.Current("b") is null);

        // N50-4: a notice that arrives once the pointer has left the open panel shows from the island after it has closed.
        var leaving = new AttentionCoordinator();
        leaving.SetLeaving("l", true);
        leaving.SetBusy("l", true, T0);
        leaving.Submit(Item("l", AttentionKind.Notice, "open", "A"), T0.AddSeconds(1));
        check("Leaving a closed panel changes nothing; an open one keeps its notices to its page", leaving.Queued("l").Count == 0);
        leaving.SetLeaving("l", true);
        leaving.Submit(Item("l", AttentionKind.Notice, "left", "B"), T0.AddSeconds(2));
        check("A notice that arrives once the pointer has left the open panel waits for it to close", leaving.Queued("l").Single().Key == "left");
        var closed = leaving.SetBusy("l", false, T0.AddSeconds(2.3));
        check("Once the panel has closed, that notice shows from the island", closed.Single().Started?.Key == "left");
        leaving.SetBusy("l", true, T0.AddSeconds(20));
        leaving.SetLeaving("l", true);
        leaving.Submit(Item("l", AttentionKind.Notice, "back", "C"), T0.AddSeconds(21));
        leaving.Submit(Item("l", AttentionKind.Quota, "quota", seconds: 5), T0.AddSeconds(21));
        leaving.SetLeaving("l", false);
        check("Coming back to the panel, the waiting notices are on its page and no longer wait; a quota alert still does",
            leaving.Queued("l").Single().Key == "quota");
        leaving.SetLeaving("l", true);
        leaving.SetBusy("l", false, T0.AddSeconds(22));
        leaving.SetBusy("l", true, T0.AddSeconds(23));
        leaving.Submit(Item("l", AttentionKind.Notice, "late", "D"), T0.AddSeconds(24));
        check("A panel opened anew keeps its notices to its page until the pointer leaves it again", leaving.Queued("l").Single().Key == "quota");

        var quiet = new AttentionCoordinator();
        quiet.Submit(Item("m", AttentionKind.Notice, "a", "A"), T0);
        quiet.Submit(Item("m", AttentionKind.Notice, "b", "B"), T0);
        var silenced = quiet.SetSuppressed(true, T0.AddSeconds(1), out _);
        quiet.Submit(Item("m", AttentionKind.Notice, "c", "C"), T0.AddSeconds(2));
        quiet.Submit(Item("m", AttentionKind.Quota, "q"), T0.AddSeconds(2));
        quiet.SetSuppressed(false, T0.AddSeconds(3), out int arrived);
        check("Suppression ends what shows and counts waiting and arriving notices for one summary", silenced.Single().Ended?.Key == "a" && arrived == 2 && quiet.Current("m") is null && quiet.Queued("m").Count == 0);

        // A source sending every half second for 20 seconds: one merged notice may not hold the island all that time.
        var chatty = new AttentionCoordinator();
        int starts = 0;
        DateTimeOffset? firstEnded = null;
        for (int i = 0; i <= 40; i++)
        {
            var at = T0.AddMilliseconds(500 * i);
            foreach (var change in chatty.Tick(at).Concat(chatty.Submit(Item("c", AttentionKind.Notice, "m" + i, "Chat", seconds: 6), at)))
            {
                if (change.Started is not null) starts++;
                if (change.Ended is not null) firstEnded ??= at;
            }
        }
        check("A source that keeps sending gives up the island within twice the duration plus the minimum", starts >= 2 && firstEnded <= T0.AddSeconds(13.5));
        var update = new AttentionCoordinator();
        update.Submit(Item("u", AttentionKind.Notice, "task", "T"), T0);
        var refreshed = update.Submit(Item("u", AttentionKind.Notice, "task", "T", refresh: true), T0.AddSeconds(5.5));
        check("An update to the shown notice refreshes it in place and keeps it a moment longer", refreshed.Single().Updated?.Key == "task" && update.NextDeadline(T0.AddSeconds(5.5)) == T0.AddSeconds(7));
        // Switching notices off or clearing the list cancels what shows and everything waiting; nothing waiting starts.
        var cancel = new AttentionCoordinator();
        cancel.Submit(Item("c", AttentionKind.Notice, "n1", "a"), T0);
        cancel.Submit(Item("c", AttentionKind.Notice, "n2", "b", NoticePriority.High), T0.AddSeconds(1));
        cancel.Submit(Item("c", AttentionKind.Notice, "n3", "c"), T0.AddSeconds(2));
        cancel.Submit(Item("c", AttentionKind.Quota, "q"), T0.AddSeconds(2));
        var cancelled = cancel.Remove(item => item.IsNotice, T0.AddSeconds(3));
        check("Removing notices cancels the shown one and all waiting ones at once", cancelled.All(c => c.Started?.IsNotice != true)
            && cancel.Queued("c").All(i => !i.IsNotice) && cancel.Current("c")?.Kind is null or AttentionKind.Quota);
        // The last check refuses items that may no longer show; the next allowed one starts instead.
        var gated = new AttentionCoordinator();
        var deleted = new HashSet<string> { "g2" };
        gated.CanShow = item => !deleted.Contains(item.Key);
        gated.Submit(Item("g", AttentionKind.Notice, "g1", "a", seconds: 2), T0);
        gated.Submit(Item("g", AttentionKind.Notice, "g2", "b", seconds: 2), T0.AddSeconds(1));
        gated.Submit(Item("g", AttentionKind.Notice, "g3", "c", seconds: 2), T0.AddSeconds(1.5));
        gated.Tick(T0.AddSeconds(2.5));
        check("An item refused by the last check is skipped", gated.Current("g")?.Key == "g3" && gated.Queued("g").Count == 0);
        // Queues are bounded for every priority, and high priority notices stop waiting after a while.
        var flood = new AttentionCoordinator();
        flood.Submit(Item("f", AttentionKind.Notice, "first", "x", NoticePriority.High, seconds: 3600), T0);
        for (int i = 0; i < 2000; i++) flood.Submit(Item("f", AttentionKind.Notice, "h" + i, "g" + i, NoticePriority.High), T0.AddMilliseconds(i));
        check("The waiting queue never grows past its limit", flood.Queued("f").Count == AttentionCoordinator.QueueLimit);
        flood.Dismiss("f", T0.AddHours(8));
        check("High priority notices that waited for hours are no longer shown", flood.Current("f") is null && flood.Queued("f").Count == 0);
        update.RemoveLane("u");
        check("A removed lane is forgotten", update.Current("u") is null && update.NextDeadline(T0) is null);
    }

    private static void History(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "notices");
        var history = new NoticeHistory(folder, new NoticeHistory.PlainProtector());
        check("A missing history file is empty", history.Load(out var none, out _) == NoticeHistoryState.Empty && none.Count == 0);
        var notice = new Notice { InstanceKey = "k", AppKey = "wsl:Ubuntu:make", AppName = "make", Title = "构建", Body = "完成", ReceivedAt = T0, Category = NoticeCategory.Success, Progress = .5,
            Actions = [new("open", "Open")], Viewed = true, AgendaDismissed = true };
        history.Save([notice], [new NoticeKnownApp { Key = "wsl:Ubuntu:make", Name = "make", LastSeen = T0 }]);
        var raw = File.ReadAllBytes(history.FilePath);
        check("The history file is written through the protector", raw.AsSpan(0, 4).SequenceEqual("BDN1"u8) && raw.AsSpan(4, 5).SequenceEqual("PLAIN"u8) && !File.Exists(history.FilePath + ".tmp"));
        var state = history.Load(out var notices, out var apps);
        check("Saved notices load back", state == NoticeHistoryState.Loaded && notices.Single().Title == "构建" && notices[0].Category == NoticeCategory.Success
            && notices[0].Actions.Single().Key == "open" && notices[0].Viewed && notices[0].AgendaDismissed && apps.Single().Name == "make");
        File.WriteAllBytes(history.FilePath, [.. "BDN1"u8, 1, 2, 3]);
        check("A file that cannot be decrypted is reported, not thrown", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        File.WriteAllBytes(history.FilePath, "garbage"u8.ToArray());
        check("A file of another format is reported", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        File.WriteAllBytes(history.FilePath, [.. "BDN1"u8, .. "PLAIN"u8, .. "{not json"u8]);
        check("A file with broken contents is reported", history.Load(out _, out _) == NoticeHistoryState.Unreadable);
        history.Delete();
        check("Deleting removes the file", !File.Exists(history.FilePath));
        history.Save([notice], [], wanted: () => false);
        check("A save no longer wanted when it writes leaves no file", !File.Exists(history.FilePath) && !File.Exists(history.FilePath + ".tmp"));
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() => history.Save([notice], []))).ToArray();
        bool together = true;
        try { Task.WaitAll(writers); } catch (AggregateException) { together = false; }
        check("Saves at the same time take turns instead of failing", together && history.Load(out var last, out _) == NoticeHistoryState.Loaded && last.Count == 1);
        // A background save whose list was copied before the exit save, but which reaches the file after it.
        var stale = notice.Copy(); stale.Title = "旧";
        var fresh = notice.Copy(); fresh.Title = "新";
        long copiedFirst = history.NextOrder();
        history.Save([fresh], []);
        history.Save([stale], [], order: copiedFirst);
        check("A list copied earlier never replaces one copied later", history.Load(out var kept, out _) == NoticeHistoryState.Loaded && kept.Single().Title == "新");
        history.Save([stale], []);
        check("A list copied after that is written again", history.Load(out kept, out _) == NoticeHistoryState.Loaded && kept.Single().Title == "旧");
        history.Delete();
    }

    // The two switches of each profile (DeckSettings.NoticeIsland, NoticesPage) and the files saved while one switch every profile
    // shared received notices (SettingsMigrations.NoticeSwitchesPerProfile).
    private static void Switches(Action<string, bool> check)
    {
        var fresh = new DeckSettings();
        check("Switches: both are on for a new user, so notices are received", fresh.NoticeIsland && fresh.NoticesPage && fresh.NoticesEnabled);
        check("Switches: either one alone receives notices", new DeckSettings { NoticesPage = false }.NoticesEnabled && new DeckSettings { NoticeIsland = false }.NoticesEnabled
            && !new DeckSettings { NoticeIsland = false, NoticesPage = false }.NoticesEnabled);
        var current = Guid.NewGuid();
        var shared = new DeckSettings { NoticeIsland = false, NoticesPage = false, CurrentProfile = current,
            Profiles = [new() { Id = current, Name = "A" }, new() { Name = "B", Values = new JsonObject { ["NoticeIsland"] = true, ["NoticesPage"] = false } }] };
        check("Switches: a profile not in use that shows notices keeps them received", shared.NoticesEnabled && !shared.NoticeIsland);
        shared.Profiles[1].Values = new JsonObject { ["NoticeIsland"] = false, ["NoticesPage"] = false };
        check("Switches: with every switch of every profile off, none is received", !shared.NoticesEnabled);
        check("Switches: both belong to each profile, and whether notices are received is not saved", Profiles.IsProfileKey(nameof(DeckSettings.NoticeIsland))
            && Profiles.IsProfileKey(nameof(DeckSettings.NoticesPage)) && !JsonSerializer.SerializeToNode(new DeckSettings { NoticeIsland = true })!.AsObject().ContainsKey("NoticesEnabled"));

        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        string Old(bool received, bool island) => $$$"""
            {"AppVersion":"1.3.3","NoticesEnabled":{{{(received ? "true" : "false")}}},"NoticeIsland":{{{(island ? "true" : "false")}}},"NoticeScreen":2,"NoticesPage":true,
             "CurrentProfile":"{{{a}}}","Profiles":[{"Id":"{{{a}}}","Name":"A","Values":{"NoticesPage":true}},{"Id":"{{{b}}}","Name":"B","Values":{"NoticesPage":false}},
             {"Id":"{{{c}}}","Name":"C","Values":{}}]}
            """;
        DeckSettings Read(string json) => SettingsMigrations.Read(json, new Version(1, 3, 3), out _);
        var on = Read(Old(true, true));
        check("Migration: with notices on, every profile shows them on the island and keeps its page", on.NoticeIsland && on.NoticesPage && on.NoticesEnabled
            && Profiles.For(on, b) is { NoticeIsland: true, NoticesPage: false } && Profiles.For(on, c) is { NoticeIsland: true, NoticesPage: true });
        var pageOnly = Read(Old(true, false));
        check("Migration: the shared island switch off is off in every profile", !pageOnly.NoticeIsland && pageOnly.NoticesPage && Profiles.For(pageOnly, b) is { NoticeIsland: false, NoticesPage: false });
        var never = Read(Old(false, true));
        check("Migration: with notices off, both switches of every profile are off, as they read", !never.NoticeIsland && !never.NoticesPage && !never.NoticesEnabled
            && Profiles.For(never, b) is { NoticeIsland: false, NoticesPage: false } && Profiles.For(never, c) is { NoticeIsland: false, NoticesPage: false });
        var later = Read("""{"AppVersion":"1.3.3","NoticeIsland":false,"NoticesPage":true}""");
        check("Migration: a file written without the shared switch is left as it is", later.NoticesPage && !later.NoticeIsland && later.NoticesEnabled);
    }

    private static void Intake(Action<string, bool> check)
    {
        var pipe = NoticeIntake.FromPipe(new NoticeMessage { Title = "x", AppName = " make ", Icon = "https://example.com/a.png", EventId = "e", Category = "error", Priority = "high", Distro = "Ubuntu" });
        check("A pipe message is a command-line notice; web icons are ignored", pipe.Origin == NoticeOrigin.Cli && pipe.AppKey == "cli:Ubuntu:make" && pipe.IconRef is null
            && pipe.Category == NoticeCategory.Error && pipe.PriorityHint == NoticePriority.High && pipe.Capabilities == NoticeCapabilities.None && pipe.EventId == "e");
        var dbus = new NoticeMessage { Origin = "dbus", AppName = "Thunderbird", NativeId = "4", Session = "server", Title = "Mail", Actions = [new() { Key = "open", Label = "Open" }], Icon = "/usr/share/icons/a.png", Distro = "Spoofed" };
        var server = NoticeIntake.FromAgent(dbus, "Ubuntu", agentIsServer: true);
        check("A desktop notice from a serving agent can be acted on and dismissed", server.Origin == NoticeOrigin.Wsl && server.AppKey == "wsl:Ubuntu:Thunderbird" && server.Distro == "Ubuntu"
            && server.InstanceKey == "wsl:Ubuntu:server:4" && server.Capabilities == (NoticeCapabilities.Actions | NoticeCapabilities.ReportDismissal) && server.Actions.Count == 1
            && server.IconRef == "wsl:Ubuntu:/usr/share/icons/a.png");
        var watched = NoticeIntake.FromAgent(dbus, "Ubuntu", agentIsServer: false);
        check("A watched desktop notice offers nothing that cannot be done", watched.Capabilities == NoticeCapabilities.None && watched.Actions.Count == 0);
        var cli = NoticeIntake.FromAgent(new NoticeMessage { Origin = "cli", Title = "t" }, "Debian", true);
        check("brim-notify through the agent is keyed by the agent's distribution", cli.Origin == NoticeOrigin.Cli && cli.AppKey == "cli:Debian:brim-notify" && cli.Distro == "Debian");
        check("Distribution names are WSL's own characters only", NoticeIntake.IsDistroName("Ubuntu-24.04") && NoticeIntake.IsDistroName("my_distro")
            && !NoticeIntake.IsDistroName("..") && !NoticeIntake.IsDistroName(@"..\server") && !NoticeIntake.IsDistroName("a/b") && !NoticeIntake.IsDistroName("Ubuntu 24")
            && !NoticeIntake.IsDistroName("") && !NoticeIntake.IsDistroName(new string('a', 65)));
        check("File URLs become paths", NoticeIntake.IconRef("file:///C:/icons/a.png", NoticeOrigin.Cli, null) == @"file:C:\icons\a.png");
    }

    private static void Pages(Action<string, bool> check)
    {
        check("The notices page shows by default", DeckPages.Shown(new DeckSettings(), DeckPage.Notices));
        var settings = new DeckSettings { NoticesPage = false };
        check("The notices page is hidden while its switch is off", !DeckPages.Shown(settings, DeckPage.Notices) && DeckPages.Resolve(settings, DeckPage.Notices) == DeckPage.Usage);
        settings.NoticesPage = true;
        check("The notices page shows once its switch is on, last in the switch", DeckPages.Visible(settings).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Clipboard, DeckPage.Devices, DeckPage.Notices, DeckPage.Agenda]));
        settings.UsagePage = false; settings.MusicPage = false; settings.MonitorPage = false; settings.ClipboardPage = false; settings.DevicesPage = false; settings.AgendaPage = false;
        check("With only the notices page it is the one shown", DeckPages.Resolve(settings, DeckPage.Usage) == DeckPage.Notices && DeckPages.Largest(settings) == (settings.NoticesWidth, settings.NoticesHeight));
        check("The notices page opens by choice", DeckPages.Opening(DefaultDeckPage.Notices) == DeckPage.Notices && DeckPages.Opening(DefaultDeckPage.Last) is null);
        check("The notices page is saved as number 5, after the devices page; as the opening page, 7, after the part under the pointer", (int)DeckPage.Devices == 4 && (int)DeckPage.Notices == 5 && (int)DefaultDeckPage.Notices == 7);
        DeckPages.SetSize(settings, DeckPage.Notices, 100, 9999);
        check("Dragging the notices page keeps it within the page limits", settings.NoticesWidth == DeckPages.MinWidth && settings.NoticesHeight == DeckPages.MaxHeight);
        var strip = new DeckSettings { NotchNotices = false, NoticesPage = false };
        check("The notices' count is on by default, shows the apps' icons and needs the notices page", new DeckSettings().NotchNotices && new DeckSettings().CapsuleNotices
            && new DeckSettings().NoticeStripIcons && !strip.ShowsNotices(CompactStyle.Capsule));
        strip.NoticesPage = true;
        check("The notices' count shows on the style it is on for, never on the indicator", strip.ShowsNotices(CompactStyle.Capsule) && !strip.ShowsNotices(CompactStyle.Notch) && !strip.ShowsNotices(CompactStyle.Line));
        strip.NoticesPage = false;
        check("The notices' count hides with the notices page", !strip.ShowsNotices(CompactStyle.Capsule));
        check("The notices are part 7, added at the end of an order saved without them; the coding agents' part 8 is dropped, they show within part 7",
            (int)CompactPart.Notices == 7 && (int)CompactPart.Agents == 8 && CompactParts.Normalize([CompactPart.Rings, CompactPart.Agents]) is var order
            && order[^1] == CompactPart.Notices && !order.Contains(CompactPart.Agents));
        strip.NotchNotices = true; strip.NoticeStripAgents = true;
        bool agentsOn = strip.ShowsAgents(CompactStyle.Notch) && !strip.ShowsAgents(CompactStyle.Line);
        strip.NoticeStripAgents = false;
        check("The coding agents show where the notices are on, and not with NoticeStripAgents off", agentsOn && !strip.ShowsAgents(CompactStyle.Notch));
    }

    private static void SettingsValues(string root, Action<string, bool> check)
    {
        var defaults = new DeckSettings();
        defaults.Normalize();
        check("Notices are on by default, with no WSL distribution, flashing, and QQ, WeChat, DingTalk and Feishu listed", defaults.NoticesEnabled && defaults.NoticeWslDistros.Count == 0
            && defaults.NoticeHistory && defaults.NoticeHistoryHours == 24 && defaults.NoticeHotkey == ""
            && defaults.NoticeStyles.Text.Seconds == 3 && defaults.NoticeStyles.Flash.Seconds == 3 && !defaults.NoticeStyles.Flash.Liquid && defaults.NoticeStyles.Stay.Liquid && defaults.NoticeStyles.Stay.Color == ""
            && defaults.NoticeApps.Select(rule => rule.Key).SequenceEqual(["windows:QQ", "flash:wechat", "flash:dingtalk", "flash:feishu"]) && defaults.NoticeAppsListed);
        check("Every Windows application and source is received unless switched off; a taskbar-flash chat only once listed", defaults.NoticeReceives("windows:QQ")
            && defaults.NoticeReceives("windows:Other") && defaults.NoticeReceives("cli:make") && defaults.NoticeReceives("wsl:Ubuntu:app") && !defaults.NoticeReceives("flash:wecom"));
        var odd = new DeckSettings
        {
            NoticeHistoryHours = 100_000, NoticesWidth = double.PositiveInfinity, NoticeAppsListed = true,
            NoticeStyles = new() { Flash = new() { Color = "nonsense", Seconds = 0 }, Stay = new() { Color = "ff0000", Seconds = 30 } },
            NoticeWslDistros = ["Ubuntu", " ubuntu ", "", "Debian", @"..\..\server\share", "a b"],
            NoticeApps = [new() { Key = "a", AccentColor = "ff0000", Level = NoticeLevel.Lively }, new() { Key = "a" }, new() { Key = " " }, new() { Key = "windows:Off", Enabled = false, Level = (NoticeLevel)9 }]
        };
        odd.Normalize();
        check("Notice settings are normalized", odd.NoticeStyles.Flash.Seconds == .5 && odd.NoticeStyles.Stay.Seconds == 10
            && odd.NoticeStyles.Flash.Color == "" && odd.NoticeStyles.Stay.Color == "#FF0000" && odd.NoticeHistoryHours == 720 && odd.NoticesWidth == 460
            && odd.NoticeWslDistros.SequenceEqual(["Ubuntu", "Debian"]) && odd.NoticeApps.Count == 2 && odd.NoticeApps[0].AccentColor == "#FF0000" && odd.NoticeApps[0].Level == NoticeLevel.Auto
            && !odd.NoticeApps[1].Enabled && odd.NoticeApps[1].Level == NoticeLevel.Auto && !odd.NoticeReceives("windows:Off"));
        var older = new DeckSettings { NoticeApps = [new() { Key = "windows:QQ", Level = NoticeLevel.Silent }, new() { Key = "windows:Mail" }] };
        older.Normalize();
        check("A list saved before the default applications existed gains them once, keeping its own rules", older.NoticeApps.Count == 5 && older.NoticeRule("windows:QQ")!.Level == NoticeLevel.Silent && older.NoticeAppsListed);
        older.NoticeApps.RemoveAll(rule => rule.Key == "windows:QQ");
        older.Normalize();
        check("A default application the user removed stays removed", older.NoticeRule("windows:QQ") is null);
        const string clock = "windows:Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";
        var listedClock = SettingsMigrations.Read($$"""{"AppVersion":"1.3.3","NoticeAppsListed":true,"NoticeApps":[{"Key":"windows:QQ","Name":"QQ"},{"Key":"{{clock}}","Name":"时钟","Enabled":true,"Level":0,"Styles":null,"AccentColor":""}]}""");
        var changedClock = SettingsMigrations.Read($$"""{"AppVersion":"1.3.3","NoticeAppsListed":true,"NoticeApps":[{"Key":"{{clock}}","Name":"时钟","Enabled":false}]}""");
        check("The Clock row an earlier version listed goes, unless the user changed it", listedClock.NoticeRule(clock) is null && listedClock.NoticeRule("windows:QQ") is not null
            && listedClock.NoticeClockUnlisted && changedClock.NoticeRule(clock) is { Enabled: false } && defaults.NoticeClockUnlisted);
        listedClock.NoticeApps.Add(new() { Key = clock, Name = "时钟" });
        listedClock.Normalize();
        check("A Clock row the user adds again stays", listedClock.NoticeRule(clock) is not null);
        var copy = odd.Copy();
        copy.NoticeApps[0].Styles = new(); copy.NoticeStyles.Flash.Icon = false; copy.NoticeWslDistros.Add("Arch");
        check("Copies do not share notice rules, looks or distributions", odd.NoticeApps[0].Styles is null && odd.NoticeStyles.Flash.Icon && odd.NoticeWslDistros.Count == 2);
        var store = new SettingsStore(Path.Combine(root, "notice-settings"), "1.2.0");
        var saved = new DeckSettings
        {
            NoticeIsland = true, LastPage = DeckPage.Notices, NoticesPausedUntil = T0,
            NoticeApps = [new() { Key = "windows:X", Name = "X", Level = NoticeLevel.Silent, Styles = new() { Flash = new() { Seconds = .5 } } }],
            NoticeStyles = new() { Flash = new() { Seconds = 7 } }
        };
        store.Save(saved);
        var json = File.ReadAllText(store.FilePath);
        var back = store.Load();
        check("Notice settings survive saving", back.NoticeIsland && back.LastPage == DeckPage.Notices && back.NoticesPausedUntil == T0
            && back.NoticeRule("windows:X") is { Level: NoticeLevel.Silent, Styles.Flash.Seconds: .5 } && back.NoticeStyles.Flash.Seconds == 7
            && !json.Contains("\"Text\":{", StringComparison.Ordinal) && JsonDocument.Parse(json).RootElement.GetProperty("LastPage").GetInt32() == 5);
    }
}
