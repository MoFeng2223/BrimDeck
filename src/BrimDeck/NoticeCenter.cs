using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using BrimDeck.Protocol;

namespace BrimDeck;

// Dynamic notices for the whole application: it runs the sources the settings switch on (the Windows notification list,
// brim-notify's pipe, the WSL agents), keeps the recent notices (NoticeHub, saved by NoticeHistory), and decides with the
// AttentionCoordinator which panel briefly shows what. Quota alerts and track changes go through the same coordinator, so
// the three never cover each other. Everything here runs on the UI thread; the sources hand over to it.
internal sealed partial class NoticeCenter : IDisposable
{
    public const string TestAppKey = "brimdeck:test";
    private readonly App _app;
    private readonly Dispatcher _dispatcher;
    private readonly PromptTimer _attentionTimer;
    private readonly DispatcherTimer _saveTimer = new() { Interval = SaveDelay };
    // A change waits this long before it is saved; a task's progress or notices marked viewed wait longer (perf-7).
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2), MinorSaveDelay = TimeSpan.FromSeconds(12);
    private readonly DispatcherTimer _maintenance = new() { Interval = TimeSpan.FromMinutes(10) };
    // While notices are held back, checks every 15 seconds whether presentation mode or the pause has ended (neither raises an event).
    private readonly DispatcherTimer _quietCheck = new() { Interval = TimeSpan.FromSeconds(15) };
    // Ending a quiet spell waits for the Windows source to read its list once more (at most this long), so notices that
    // arrived meanwhile but were not read yet join the summary instead of popping up one by one afterwards.
    private readonly DispatcherTimer _resumeLimit = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _resuming;
    private int _resumeReading;
    private readonly WindowsNoticeSource _windows = new();
    private readonly WslNoticeBridge _wsl = new();
    private readonly NoticeQuiet _quiet;
    private NoticePipeServer? _pipe;
    private TaskbarFlashWatch? _flash;
    // Why the taskbar flash source could not start, for the settings page; null while it runs or is off.
    public string? ChatFlashError { get; private set; }
    private volatile NoticeHistory? _history;
    // Whether notices are kept on disk, for a save finishing on a worker thread (see NoticeHistory.Save).
    private volatile bool _keepHistory;
    // _historyLoaded (HistoryLoaded): the folder's file has been read into the list (or there was none); only then is the list
    // written over it (core-M4).
    private bool _historyLoaded, _loadingHistory, _saving, _savePending, _enabled, _disposed;
    // What NoticeStorage.Prepare made of the folder; null until it has been checked, which happens in the background with the
    // first reading after the folder changed, not on every settings change (C-M2).
    private NoticeStorage.FolderTrust? _folderTrust;
    // The folder the notices moved away from; its file goes once the list has been written in the new one.
    private NoticeHistory? _retired;
    // A folder whose file, never read, moves into the new folder once that one has been checked (NoticeHistoryStep.MoveOld).
    private string? _moveFrom;
    // Changes of the recent notices reach the views at most every ChangeSpacing, and an update that only moves what shows
    // (a task's progress) reaches the shown notice at most as often: a burst of progress rebuilds nothing on every step (perf-7).
    private static readonly TimeSpan ChangeSpacing = TimeSpan.FromMilliseconds(250);
    private readonly PromptTimer _changeTimer, _refreshTimer;
    private bool _changeWaiting;
    private readonly HashSet<string> _refreshWaiting = new(StringComparer.Ordinal);
    private DateTimeOffset? _attentionAt;
    // Reminders dropped beyond NoticeHub.MaxPending and not logged yet, and when the log last heard of some (L3).
    private int _droppedPending;
    private DateTimeOffset _droppedLogged;
    // Old files removed in the background (L6), one after another; a reading waits for them, so it never reads a file that
    // is about to go.
    private Task _deleting = Task.CompletedTask;
    // Messages each link (the pipe, each distribution) may deliver per window; more are refused until the window moves on.
    private const int IntakeLimit = 200;
    private static readonly TimeSpan IntakeWindow = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _intake = new(StringComparer.Ordinal);
    // Time-kind notices (NoticeTime.cs). _timeDue waits for the next reminder BrimDeck keeps time for, at most a minute at a
    // time so that sleep and clock changes are noticed; it is off while nothing is due. _liveTick is the one shared second
    // tick for live words ("24:59 left"); it runs only while something that shows such words is on screen.
    private readonly PromptTimer _timeDue, _liveTick;
    private DateTimeOffset? _dueFor;
    private Action? _live;
    private static readonly TimeSpan DueRecheck = TimeSpan.FromMinutes(1);

    public NoticeHub Hub { get; }
    public AttentionCoordinator Attention { get; } = new();
    // The notices' sounds (NoticeSounds): a notice of level 2 plays its sound once as an island starts to show it, one of level 3
    // loops for as long as an island shows it. The settings try a chosen sound through it.
    public NoticeSoundPlayer Sounds { get; }
    private readonly NoticeSoundPace _soundPace = new();
    // Where the imported sounds are: "sounds" in the data folder.
    public string SoundFolder => Path.Combine(_app.Store.DirectoryPath, NoticeSounds.Folder);
    // Windows' own banner switch of each application (NoticeBanners), read and written by the "应用" settings page.
    internal IBannerRegistry BannerRegistry { get; } = new NoticeBannerRegistry();
    public NoticeIcons Icons { get; }
    public NoticeHistoryState HistoryState { get; private set; } = NoticeHistoryState.Empty;
    public NoticeSourceState WindowsState => _enabled ? _windows.State : NoticeSourceState.Off;
    // Why the Windows source failed (the exception's message, never a notice's words); empty while it does not fail.
    public string WindowsFailure => WindowsState == NoticeSourceState.Failed ? _windows.Detail : "";
    public string? PipeError => _pipe?.Error;
    public IReadOnlyDictionary<string, (WslAgentState State, string Detail, bool Conflict)> WslStates => _wsl.States;
    public bool Enabled => _enabled;
    public bool Quiet { get; private set; }
    public bool FocusSupported => _quiet.FocusSupported;
    // Counts the changes after which every view words its notices again, although no notice changed (the time zone changed).
    public int Epoch { get; private set; }
    // Raised after the recent notices or a source's state changed; the panels' notices pages and the settings page follow it.
    public event Action? Changed;
    internal DeckSettings Settings => _app.Settings;

    public NoticeCenter(App app)
    {
        _app = app; _dispatcher = app.Dispatcher;
        Hub = new NoticeHub(key => _app.Settings.NoticeRule(key), key => _app.Settings.NoticeReceives(key));
        Sounds = new NoticeSoundPlayer(() => SoundFolder, () => _app.Settings.NoticeSoundVolume, Log);
        // Banners an earlier version turned off and could not give back before it stopped (NoticeBanners); the ledger lives in the
        // data folder, next to settings.json, and is gone once everything in it is restored.
        string dataFolder = app.Store.DirectoryPath;
        _ = Task.Run(() =>
        {
            try { NoticeBannerRegistry.RestoreAll(dataFolder); }
            catch (Exception ex) { Log(ex); }
        });
        Icons = new NoticeIcons(_dispatcher, _windows.LogoAsync, distro => _wsl.States.TryGetValue(distro, out var state) && state.State is WslAgentState.Server or WslAgentState.Monitor or WslAgentState.MonitorDenied);
        _quiet = new NoticeQuiet(_dispatcher);
        _quiet.Changed += UpdateQuiet;
        _attentionTimer = new PromptTimer(_dispatcher, () => { _attentionTimer!.Stop(); _attentionAt = null; Apply(Attention.Tick(DateTimeOffset.UtcNow)); });
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = SaveAsync(); };
        // A file that could not be reached at start-up is read once it can be (core-M4).
        _maintenance.Tick += (_, _) => { Hub.Prune(DateTimeOffset.UtcNow); if (_droppedPending > 0) LogDropped(); UpdateQuiet(); if (HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Denied) LoadHistory(); };
        _quietCheck.Tick += (_, _) => UpdateQuiet();
        _resumeLimit.Tick += (_, _) => FinishResume(int.MaxValue);
        _timeDue = new PromptTimer(_dispatcher, FireDue);
        _liveTick = new PromptTimer(_dispatcher, LiveStep);
        _changeTimer = new PromptTimer(_dispatcher, ChangeStep);
        _refreshTimer = new PromptTimer(_dispatcher, RefreshStep);
        Microsoft.Win32.SystemEvents.TimeChanged += ClockMoved;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        Attention.CanShow = CanShow;
        // A notice gone from the recent list (deleted, cleared, pruned by age or count) no longer waits to be shown.
        Hub.Changed += HubChanged;
        Hub.Persist += QueueSave;
        // Until the saved file is read, what is deleted or cleared must not come back with it (C-L2).
        Hub.TrackRemovals = true;
        // Counted here and written at most once a minute, in the background (L3): a script looping past the limit drops one with
        // every message.
        Hub.PendingDropped += count => { _droppedPending += count; if (DateTimeOffset.UtcNow - _droppedLogged >= TimeSpan.FromMinutes(1)) LogDropped(); };
        // A reading still under way when notices or the source were switched off arrives late; it is dropped.
        // Notifications of Windows' Clock, Outlook, the calendars and Teams are sorted into time kinds here (NoticeTimeSources).
        _windows.Received += input => _dispatcher.BeginInvoke(() =>
        {
            if (!_enabled || _disposed) return;
            Receive(NoticeTimeSources.Classify(input, TimeZoneInfo.Local));
        });
        _windows.Missed += input => _dispatcher.BeginInvoke(() => OnMissed(input));
        _windows.Removed += key => _dispatcher.BeginInvoke(() => { if (!_disposed) Hub.SourceRemoved(key); });
        _windows.StateChanged += () => _dispatcher.BeginInvoke(() => Changed?.Invoke());
        // Raised after the notices of a reading were handed over, so they are queued on the dispatcher before this.
        _windows.Synced += covers => _dispatcher.BeginInvoke(() => FinishResume(covers));
        _wsl.Received += (distro, message) => _dispatcher.BeginInvoke(() => FromAgent(distro, message));
        _wsl.StateChanged += () => _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    // Starts and stops sources to match the settings. Called at start-up and after every settings change.
    public void ApplySettings()
    {
        if (_disposed) return;
        var settings = Settings;
        _enabled = settings.NoticesEnabled;
        // BrimDeck's command line reaches it whenever it runs; with notices off only its notices are refused (HandlePipeAsync).
        if (_pipe is null) { _pipe = new NoticePipeServer(HandlePipeAsync) { Failed = Log }; _pipe.Start(); }
        Hub.Retention = TimeSpan.FromHours(settings.NoticeHistoryHours);
        Hub.MaxCount = NoticeHub.DefaultMaxCount;
        UseHistoryFolder(settings);
        if (!_enabled)
        {
            _windows.Stop(); _wsl.Apply([]);
            EndWaits();
            StopChatFlash(); ApplyWeChat(false);
            _maintenance.Stop(); _quietCheck.Stop();
            ScheduleDue();
            // Whatever shows ends and nothing waiting shows later; quota alerts and track changes keep working through the
            // coordinator.
            EndResume();
            if (Attention.Suppressed) Apply(Attention.SetSuppressed(false, DateTimeOffset.UtcNow, out _));
            Apply(Attention.Remove(item => item.IsNotice, DateTimeOffset.UtcNow));
            Quiet = false;
            Changed?.Invoke();
            return;
        }
        // Off for a panel's profile: what shows there ends and nothing waiting shows later; the notices still reach the list.
        var silent = _app.Decks.Where(deck => !deck.ShowsNewNotices).Select(deck => deck.NoticeLane).ToHashSet(StringComparer.Ordinal);
        if (silent.Count > 0) Apply(Attention.Remove(item => item.IsNotice && silent.Contains(item.Lane), DateTimeOffset.UtcNow));
        LoadHistory();
        _windows.Start();
        // The taskbar flash source runs while a chat it watches is listed and received (WeChat is, for a new user).
        if (ChatFlashApps.All.Any(app => app.Enabled && settings.NoticeRule("flash:" + app.Id) is { Enabled: true })) StartChatFlash(); else StopChatFlash();
        ApplyWeChat(settings.NoticeRule(WeChatRows.AppKey) is { Enabled: true, ReadContent: true });
        _wsl.Apply(settings.NoticeWslDistros);
        _maintenance.Start();
        Hub.Prune(DateTimeOffset.UtcNow);
        // A level chosen for an application may have ended what stayed on the island until handled.
        DropMissing();
        UpdateQuiet();
        ScheduleDue();
        SyncLoop();
        Changed?.Invoke();
    }

    // The taskbar flash source (ChatFlash): a hook window while a chat it watches is listed, nothing at all otherwise.
    private void StartChatFlash()
    {
        if (_flash is not null) return;
        try
        {
            _flash = new TaskbarFlashWatch(OnFlash);
            ChatFlashError = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EntryPointNotFoundException)
        {
            ChatFlashError = ex.Message; Log(ex);
        }
        Changed?.Invoke();
    }

    private void StopChatFlash()
    {
        bool had = _flash is not null || ChatFlashError is not null;
        _flash?.Dispose(); _flash = null; ChatFlashError = null;
        if (had) Changed?.Invoke();
    }

    // Asks Windows for access to its notifications (from the settings page) and reads the list once access is granted.
    public async Task<Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus> RequestWindowsAccessAsync()
    {
        var status = await WindowsNoticeSource.RequestAccessAsync();
        _windows.Refresh(baseline: true);
        Changed?.Invoke();
        return status;
    }

    // ----- Intake -----

    public NoticeOutcome Receive(NoticeInput input)
    {
        // Nothing is taken in once the last save on exit has been made (core-L3).
        if (_disposed) return new(NoticeReceipt.Filtered, null, false, "shutting-down");
        var now = DateTimeOffset.UtcNow;
        var outcome = Hub.Receive(input, now);
        if (outcome.Notice is not { } notice) return outcome;
        // Closed by its sender (brim-notify close): it leaves the island too, whatever its level.
        if (input.StopTiming && !notice.SourcePresent) { EndWait(notice.Id, NoticeWaits.Dismissed); Withdraw(notice.Id); }
        if (outcome.Present) Present(notice, refresh: outcome.Receipt == NoticeReceipt.Updated);
        // A task's progress moves the notice that shows; nothing new appears.
        else if (outcome.Receipt == NoticeReceipt.Updated) QueueRefresh(notice);
        return outcome;
    }

    // Windows notifications that arrived while the PC slept (WindowsNoticeSource.Missed, core-M5): recorded unviewed, not
    // shown.
    private void OnMissed(NoticeInput input)
    {
        if (_enabled && !_disposed) Receive(NoticeTimeSources.Classify(input, TimeZoneInfo.Local) with { Missed = true });
    }

    // The first update shows at once; those that follow within ChangeSpacing show together at its end, the latest of each.
    private void QueueRefresh(Notice notice)
    {
        if (_refreshTimer.IsEnabled) { _refreshWaiting.Add(notice.Id); return; }
        RefreshShown(notice);
        _refreshTimer.Interval = ChangeSpacing; _refreshTimer.Start();
    }

    private void RefreshStep()
    {
        if (_refreshWaiting.Count == 0) { _refreshTimer.Stop(); return; }
        var waiting = _refreshWaiting.ToList();
        _refreshWaiting.Clear();
        foreach (var id in waiting) if (Hub.Find(id) is { } notice) RefreshShown(notice);
    }

    // Whatever the recent notices lost no longer waits to be shown at once; the views hear of the change at most every
    // ChangeSpacing, the first change at once.
    private void HubChanged()
    {
        // A notice deleted or cleared away while its sender waits is not answered any more.
        if (_waits.Count > 0) foreach (var id in _waits.Keys.Where(id => Hub.Find(id) is null).ToList()) EndWait(id, NoticeWaits.Dismissed);
        DropMissing(); ScheduleDue();
        if (_changeTimer.IsEnabled) { _changeWaiting = true; return; }
        Changed?.Invoke();
        _changeTimer.Interval = ChangeSpacing; _changeTimer.Start();
    }

    private void ChangeStep()
    {
        if (!_changeWaiting) { _changeTimer.Stop(); return; }
        _changeWaiting = false;
        Changed?.Invoke();
    }

    // The notice changed in place; where it shows, it shows the change.
    private void RefreshShown(Notice notice)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var deck in _app.Decks)
            if (Attention.Current(deck.NoticeLane) is { Kind: AttentionKind.Notice, Payload: Notice shown } && shown.Id == notice.Id)
                Apply(Attention.Submit(Item(deck.NoticeLane, notice, refresh: true), now));
    }

    private AttentionItem Item(string lane, Notice notice, bool refresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        var level = Level(notice, now);
        var style = NoticeLevelStyles.Of(level, Settings.NoticeRule(notice.AppKey), Settings.NoticeStyles, notice.Call);
        return new()
        {
            // One conversation's messages (the application and the title) merge into one notice.
            Lane = lane, Kind = AttentionKind.Notice, Key = notice.Id, Group = AttentionCoordinator.GroupOf(notice.AppKey, notice.Title),
            Priority = notice.Priority, Payload = notice, Refresh = refresh, Level = level, Style = style, Limit = StayLimit(notice),
            // A ringing call is as urgent as an alarm; the user can still make its application low priority (NoticeHub).
            Urgent = NoticeClock.Alerting(notice.Timing, now) || notice.Call && notice.Priority != NoticePriority.Low,
            Duration = TimeSpan.FromSeconds(style.Seconds)
        };
    }

    // How strongly the notice shows on the island now (NoticeLevels.Resolve), by the user's rule for its application, what it
    // is and what its sender asked.
    internal NoticeLevel Level(Notice notice, DateTimeOffset now) => NoticeLevels.Resolve(notice, Settings.NoticeRule(notice.AppKey), now);

    // How long a level-3 notice stays on the island at most: the settings' time, or the sender's (Notice.Timeout) when shorter.
    // The sender's wait for an answer runs its own time (BeginWait); the notice stays on the notices page.
    private TimeSpan StayLimit(Notice notice)
    {
        var most = TimeSpan.FromSeconds(Settings.NoticeStaySeconds);
        return notice.Timeout is { } asked && asked < most ? asked : most;
    }

    // Shows the notice on every panel whose profile shows notices. Each panel has its own item with the notice's key, so that
    // handling it on one screen ends it on all (Withdraw).
    private void Present(Notice notice, bool refresh)
    {
        var now = DateTimeOffset.UtcNow;
        UpdateQuiet();
        if (Level(notice, now) == NoticeLevel.Silent) return;
        if (Attention.Suppressed) { Attention.Submit(Item("", notice), now); return; }
        var lanes = Lanes();
        foreach (var lane in lanes) Apply(Attention.Submit(Item(lane, notice, refresh), now));
        // A panel on any screen is open and staying open: the notice shows on no island, only on the notices page, and its sound
        // plays once, a loop too (nothing ends it on the page).
        if (!refresh && lanes.Count > 0 && Attention.AnyOpen) PlayOnce(Item(lanes[0], notice));
    }

    // The panels that show an arriving notice: each one whose profile shows notices (NoticeIsland) and that can show one now. A
    // panel hidden at the moment (by a full-screen rule, or by the user) does not show it later; the notice stays in the list.
    private IReadOnlyList<string> Lanes() =>
        _app.Decks.Where(deck => deck.ShowsNewNotices && deck.CanShowNotices).Select(deck => deck.NoticeLane).Distinct(StringComparer.Ordinal).ToList();

    private async Task<NoticePipeServer.Answer> HandlePipeAsync(NoticeMessage message)
    {
        if (NoticeProtocol.Validate(message) is { } problem) return new(NoticeProtocol.Reply(NoticeResults.Rejected, problem, message.EventId));
        // A removal that takes a notice out of Windows' notification center too is answered once Windows has done it.
        if (message.Type == NoticeTypes.Remove)
        {
            var (reply, windows) = await _dispatcher.InvokeAsync(() =>
                _disposed ? (NoticeProtocol.Reply(NoticeResults.Rejected, "shutting-down", message.EventId), null)
                : !_enabled ? (NoticeProtocol.Reply(NoticeResults.Rejected, "notices-off", message.EventId), null)
                : !Admit("pipe") ? (NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", message.EventId), null)
                : Remove(NoticeIntake.FromPipe(message), message.EventId));
            if (windows is not null && !await windows) reply.Detail = WindowsKept;
            return new(reply);
        }
        return await _dispatcher.InvokeAsync(() =>
        {
            NoticePipeServer.Answer Refuse(string why) => new(NoticeProtocol.Reply(NoticeResults.Rejected, why, message.EventId));
            if (_disposed) return Refuse("shutting-down");
            // An agent's session state shows on the collapsed row whether or not live notices are on.
            if (message.Type == NoticeTypes.Agent)
                return AgentReport is { } report ? (Admit("agent") ? new(report(message)) : Refuse("rate-limited")) : Refuse("unknown-type");
            if (!_enabled) return Refuse("notices-off");
            if (message.Type == NoticeTypes.Status) return new(NoticeProtocol.Reply(NoticeResults.Accepted, StatusLine(), message.EventId));
            if (message.Type == NoticeTypes.List) return Admit("pipe") ? new(ListReply(message)) : Refuse("rate-limited");
            if (message.Type != NoticeTypes.Notify) return Refuse("unknown-type");
            if (!Admit("pipe")) return Refuse("rate-limited");
            // A coding agent's approval or question waits as long as the user chose in the settings (the hook asks no time of its own).
            if (message.Wait == true && BrimDeck.Core.Agents.AgentKeys.Ask(message.ReplaceId) is not null) message.TimeoutMs = Settings.AgentWaitMinutes * 60_000L;
            var input = NoticeIntake.FromPipe(message);
            var outcome = Receive(input);
            var reply = Reply(outcome, message.EventId);
            // The sender waits for how its notice ends (NoticeMessage.Wait): the connection is kept, and gets the result.
            if (!input.Awaited || outcome is not { Notice: { } notice, Receipt: NoticeReceipt.Accepted or NoticeReceipt.Updated }) return new(reply);
            reply.Waiting = true;
            var wait = BeginWait(notice);
            return new NoticePipeServer.Answer(reply, wait.Task, () => _dispatcher.BeginInvoke(() => Abandon(notice.Id, wait)));
        });
    }

    // ----- brim notify list and remove -----

    // The detail of a removal whose notice left BrimDeck but stayed in Windows' notification center.
    private const string WindowsKept = "windows-kept";

    // One page of the notices a sender may reach (NoticeEntries): only the command line's and programs', unless the user allows
    // every source.
    private NoticeMessage ListReply(NoticeMessage message)
    {
        bool all = Settings.NoticeCommandAccess;
        var entries = NoticeEntries.Of(Hub.Notices, all, message.AppName, Settings.NoticeCodes, Shown, Awaits);
        return NoticeProtocol.ListPage(entries, message.Cursor, all ? NoticeScopes.All : NoticeScopes.Cli, message.EventId);
    }

    // Whether the notice is on an island now.
    private bool Shown(Notice notice) => _app.Decks.Any(deck => Attention.Current(deck.NoticeLane) is { IsNotice: true } item && item.Key == notice.Id);

    // Removes the notice the sender names, as deleting it on the notices page does; a sender that waits for it hears it was
    // dismissed. A Windows notice, which a sender reaches only with the user's leave, is taken out of the notification center
    // too: Windows is that task, true once the notification is gone there. A notice the sender may not reach is answered as one
    // that is not there.
    private (NoticeMessage Reply, Task<bool>? Windows) Remove(NoticeInput input, string? eventId)
    {
        if (Hub.Target(input) is not { } notice || !NoticeEntries.Reachable(notice, Settings.NoticeCommandAccess))
            return (NoticeProtocol.Reply(NoticeResults.Filtered, "not-found", eventId), null);
        EndWait(notice.Id, NoticeWaits.Dismissed);
        Delete(notice);
        var windows = notice.Origin == NoticeOrigin.Windows && notice.InstanceKey.StartsWith("windows:", StringComparison.Ordinal) ? _windows.RemoveAsync(notice.InstanceKey) : null;
        return (NoticeProtocol.Reply(NoticeResults.Accepted, null, eventId), windows);
    }

    // ----- Notices whose sender waits for an answer -----

    // The senders that wait, by their notice's id: each is told once how the notice ended (NoticeWaits) and then waits no more.
    private sealed record Waiter(TaskCompletionSource<NoticeMessage> Result, DispatcherTimer Timer);
    private readonly Dictionary<string, Waiter> _waits = new(StringComparer.Ordinal);
    // The waits of senders inside WSL distributions, by distribution and message id, for the agent's word that one went away.
    private readonly Dictionary<string, (string Notice, TaskCompletionSource<NoticeMessage> Wait)> _agentWaits = new(StringComparer.Ordinal);

    private TaskCompletionSource<NoticeMessage> BeginWait(Notice notice)
    {
        // The same notice sent again to be answered: the sender that waited before hears that another took its place.
        if (_waits.Remove(notice.Id, out var before)) Tell(before, NoticeWaits.Replaced, null);
        var result = new TaskCompletionSource<NoticeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The notice's own time (--timeout), else as long as it may stay on the island; it runs whether or not the notice shows
        // there (the panel may be open, or the island hidden), so a sender never waits without end.
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = notice.Timeout ?? AttentionCoordinator.PersistentLimit };
        string id = notice.Id;
        timer.Tick += (_, _) => EndWait(id, NoticeWaits.Expired);
        timer.Start();
        _waits[id] = new Waiter(result, timer);
        return result;
    }

    private static void Tell(Waiter waiter, string result, string? key, List<NoticeAnswer>? answers = null)
    {
        waiter.Timer.Stop();
        waiter.Result.TrySetResult(new NoticeMessage { Type = NoticeTypes.Result, Result = result, ActionKey = key, Answers = answers });
    }

    // The key of the "action" a sender receives with the answers to its questions.
    public const string AnswerKey = "answer";

    // The user answered the notice's questions: the sender hears the answers (one per question) and the notice leaves the island.
    // False when nobody waits for them any more.
    public bool Answer(Notice notice, List<NoticeAnswer> answers)
    {
        Hub.MarkViewed(notice.Id);
        if (!_waits.Remove(notice.Id, out var waiter)) return false;
        Tell(waiter, NoticeWaits.Action, AnswerKey, answers);
        Hub.Answered(notice.Id);
        Withdraw(notice.Id);
        Answered?.Invoke(notice);
        return true;
    }

    // Takes a coding agent's report of its session (AgentCenter) and gives the reply; set once at start.
    public Func<NoticeMessage, NoticeMessage>? AgentReport { get; set; }

    // A notice its sender waited for was answered here (a button or the questions): the agents' sessions work again.
    public event Action<Notice>? Answered;

    // Whether a sender still waits for the notice's answer.
    public bool Awaits(Notice notice) => _waits.ContainsKey(notice.Id);

    // The wait ends: the sender hears how, the notice loses the buttons that answered it and leaves the island.
    private void EndWait(string id, string result, string? key = null)
    {
        if (!_waits.Remove(id, out var waiter)) return;
        Tell(waiter, result, key);
        Hub.Answered(id);
        Withdraw(id);
    }

    // The sender closed its connection before an answer (it ended, Ctrl+C): nothing is left to answer.
    private void Abandon(string id, TaskCompletionSource<NoticeMessage> wait)
    {
        if (_disposed || !_waits.TryGetValue(id, out var waiter) || waiter.Result != wait) return;
        EndWait(id, NoticeWaits.Dismissed);
    }

    private void EndWaits()
    {
        foreach (var id in _waits.Keys.ToList()) EndWait(id, NoticeWaits.Dismissed);
    }

    // ----- A notice's buttons -----

    // The buttons its sender gave that can still do something: one that answers the sender only while the sender waits (or
    // the WSL agent can pass it on), one that copies or opens always. A planner reminder's buttons are BrimDeck's own (AgendaReminders).
    public IReadOnlyList<NoticeAction> Buttons(Notice notice)
        => [.. notice.Actions.Where(action => action.Key != "default" && (action.Kind != NoticeActionKind.Reply || (notice.Capabilities & NoticeCapabilities.Actions) != 0
            || AgendaReminders.Owns(notice)))];

    // Does what the button says. False when it could not (the clipboard was held, nothing opened the target); the notice then stays.
    public async Task<bool> RunAsync(Notice notice, NoticeAction action)
    {
        Hub.MarkViewed(notice.Id);
        bool done;
        switch (action.Kind)
        {
            case NoticeActionKind.Copy: done = Copy(action.Value ?? ""); break;
            case NoticeActionKind.Open: done = await OpenTargetAsync(action.Value ?? ""); break;
            default:
                if (_waits.ContainsKey(notice.Id)) { EndWait(notice.Id, NoticeWaits.Action, action.Key); Answered?.Invoke(notice); return true; }
                // A planner reminder's "查看日程" or "5 分钟后再提醒", on the main panel (the island and each page pass their own).
                if (AgendaReminders.Owns(notice)) return _app.Deck?.AgendaReminderAction(notice, action.Key) == true;
                return Invoke(notice, action.Key);
        }
        if (!done) return false;
        // Handled: a sender that waits hears which button, and the notice leaves the island either way.
        if (_waits.ContainsKey(notice.Id)) EndWait(notice.Id, NoticeWaits.Action, action.Key); else Withdraw(notice.Id);
        return true;
    }

    // True when the text is on the clipboard; another application may hold the clipboard for a moment.
    internal static bool Copy(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { System.Windows.Clipboard.SetDataObject(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text), true); return true; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }

    // Opens what an open button names (NoticeIntake.OpenTarget made sure of its form): an application by its id, a web address
    // in the browser, a folder in Explorer, a file with the program for its type. A file that would run (a program, a script,
    // a shortcut: NoticeIntake.Runs) is shown selected in its folder instead: BrimDeck starts nothing a sender names.
    private async Task<bool> OpenTargetAsync(string target)
    {
        if (target.StartsWith(NoticeIntake.AppTarget, StringComparison.OrdinalIgnoreCase)) return await _windows.LaunchAsync(target[NoticeIntake.AppTarget.Length..], null);
        try
        {
            bool web = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            System.Diagnostics.ProcessStartInfo start;
            if (web || Directory.Exists(target)) start = new(target) { UseShellExecute = true };
            else if (!File.Exists(target)) return false;
            else if (NoticeIntake.Runs(target)) start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + target + "\"") { UseShellExecute = false };
            else start = new(target) { UseShellExecute = true };
            using var process = System.Diagnostics.Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException) { Log(ex); return false; }
    }

    // A notice of one of BrimDeck's own features (a track change, a quota alert): the same input and the same way in as every
    // other notice, handed over inside the process. Its key is BrimDeck's own (NoticeKeys.Internal), which no sender can take.
    public NoticeOutcome Post(NoticeInput input)
    {
        if (input.Origin != NoticeOrigin.Internal || !input.AppKey.StartsWith(NoticeKeys.InternalPrefix, StringComparison.Ordinal)) throw new ArgumentException("Not a notice of BrimDeck's own.", nameof(input));
        return _enabled ? Receive(input) : new(NoticeReceipt.Filtered, null, false, "notices-off");
    }

    private static NoticeMessage Reply(NoticeOutcome outcome, string? eventId) => NoticeProtocol.Reply(outcome.Receipt switch
    {
        NoticeReceipt.Accepted => NoticeResults.Accepted, NoticeReceipt.Updated => NoticeResults.Updated,
        NoticeReceipt.Duplicate => NoticeResults.Duplicate, _ => NoticeResults.Filtered
    }, outcome.Reason, eventId);

    // A failing Windows source says why (the exception's message; never a notice's words), so "brim-notify status" tells it.
    private string StatusLine() => $"Windows: {WindowsState}{(WindowsFailure.Length > 0 ? " (" + WindowsFailure + ")" : "")}; WSL: {string.Join(", ", WslStates.Select(pair => pair.Key + " " + pair.Value.State))}" + (Quiet ? "; held back" : "");

    private void FromAgent(string distro, NoticeMessage message)
    {
        if (_disposed) return;
        string? refusal = !_enabled ? "notices-off" : !Settings.NoticeWslDistros.Contains(distro, StringComparer.OrdinalIgnoreCase) ? "distro-off" : NoticeProtocol.Validate(message);
        if (refusal is not null)
        {
            // brim-notify inside the distribution waits for a reply through the agent: it hears why at once (core-M2).
            if (message.Type is NoticeTypes.Notify or NoticeTypes.List or NoticeTypes.Remove && message.Origin == "cli" && message.EventId is { } id)
                _wsl.Send(distro, NoticeProtocol.Reply(NoticeResults.Rejected, refusal, id));
            return;
        }
        bool server = _wsl.States.TryGetValue(distro, out var state) && state.State == WslAgentState.Server;
        switch (message.Type)
        {
            case NoticeTypes.Notify:
            {
                if (!Admit("wsl:" + distro))
                {
                    if (message.Origin == "cli" && message.EventId is { } refused) _wsl.Send(distro, NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", refused));
                    break;
                }
                var input = NoticeIntake.FromAgent(message, distro, server);
                var outcome = Receive(input);
                // brim-notify inside the distribution waits for this reply through the agent.
                if (message.Origin == "cli" && message.EventId is { } id)
                {
                    var reply = Reply(outcome, id);
                    // It also waits for how the notice ends (NoticeMessage.Wait): the result follows through the agent, named by
                    // the message's id.
                    if (input.Awaited && outcome is { Notice: { } notice, Receipt: NoticeReceipt.Accepted or NoticeReceipt.Updated })
                    {
                        reply.Waiting = true;
                        var wait = BeginWait(notice);
                        string key = distro + "\n" + id;
                        _agentWaits[key] = (notice.Id, wait);
                        _ = wait.Task.ContinueWith(done => _dispatcher.BeginInvoke(() =>
                        {
                            if (_agentWaits.TryGetValue(key, out var kept) && kept.Wait == wait) _agentWaits.Remove(key);
                            if (_disposed || !done.IsCompletedSuccessfully) return;
                            var result = done.Result;
                            result.EventId = id;
                            _wsl.Send(distro, result);
                        }), TaskScheduler.Default);
                    }
                    _wsl.Send(distro, reply);
                }
                break;
            }
            // brim notify list and remove inside the distribution, answered through the agent by the message's id.
            case NoticeTypes.List when message.EventId is { } listed:
                _wsl.Send(distro, Admit("wsl:" + distro) ? ListReply(message) : NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", listed));
                break;
            case NoticeTypes.Remove when message.EventId is { } removing:
            {
                if (!Admit("wsl:" + distro)) { _wsl.Send(distro, NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", removing)); break; }
                var (reply, windows) = Remove(NoticeIntake.FromAgent(message, distro, server), removing);
                if (windows is null) { _wsl.Send(distro, reply); break; }
                _ = windows.ContinueWith(done => _dispatcher.BeginInvoke(() =>
                {
                    if (!done.IsCompletedSuccessfully || !done.Result) reply.Detail = WindowsKept;
                    if (!_disposed) _wsl.Send(distro, reply);
                }), TaskScheduler.Default);
                break;
            }
            // The sender inside the distribution went away before its notice was answered (the agent says so by the message's id).
            case NoticeTypes.Close when message.Origin == "cli" && message.EventId is { } gone:
                if (_agentWaits.TryGetValue(distro + "\n" + gone, out var waiting)) Abandon(waiting.Notice, waiting.Wait);
                break;
            case NoticeTypes.Close when message.NativeId is { } native:
                Hub.SourceRemoved($"wsl:{distro}:{message.Session ?? ""}:{native}");
                break;
        }
    }

    private bool Admit(string link)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_intake.TryGetValue(link, out var times)) _intake[link] = times = new Queue<DateTimeOffset>();
        while (times.Count > 0 && now - times.Peek() >= IntakeWindow) times.Dequeue();
        if (times.Count >= IntakeLimit) return false;
        times.Enqueue(now);
        return true;
    }

    // ----- Presentation -----

    private void Apply(IReadOnlyList<AttentionChange> changes)
    {
        foreach (var change in changes)
        {
            var deck = _app.Decks.FirstOrDefault(d => d.NoticeLane == change.Lane);
            if (deck is null) continue;
            if (change.Ended is { } ended) deck.AttentionEnded(ended);
            // Shown is not seen: a notice counts as viewed only once its details open, the pointer rests on it or its page
            // lists it (the user may be away while it shows).
            if (change.Started is { } started) { deck.AttentionStarted(started); if (!started.Persistent) PlayOnce(started); }
            if (change.Updated is { } updated) deck.AttentionUpdated(updated);
        }
        SyncLoop();
        Schedule();
    }

    // The sound of a notice that starts to show, played once: at most one a second, once for a notice whatever the number of
    // islands that show it, and never over a loop (NoticeSoundPace).
    private void PlayOnce(AttentionItem item)
    {
        if (NoticeSounds.For(item) is not { Length: > 0 } sound) return;
        if (_soundPace.Admit(item.Key + ":" + item.Level, Sounds.Looping, DateTimeOffset.UtcNow)) Sounds.Once(sound);
    }

    // The loop follows what the islands show, not the steps that led there: it plays while an island shows a notice that stays
    // until handled and has a sound (the first panel's, when two show different ones), and stops when none does, however that
    // notice ended.
    private void SyncLoop()
    {
        string? loop = null;
        if (!_disposed)
            foreach (var deck in _app.Decks)
                if (Attention.Current(deck.NoticeLane) is { Persistent: true } item && NoticeSounds.For(item) is { Length: > 0 } sound) { loop = sound; break; }
        Sounds.Loop(loop);
    }

    private void Schedule()
    {
        var now = DateTimeOffset.UtcNow;
        var next = Attention.NextDeadline(now);
        if (next == _attentionAt && _attentionTimer.IsEnabled) return;
        _attentionTimer.Stop(); _attentionAt = next;
        if (next is not { } at) return;
        var wait = at - now;
        _attentionTimer.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _attentionTimer.Start();
    }

    // A quota alert from a panel. It shows when nothing more important does; while the panel is open it waits.
    public void SubmitQuota(MainWindow deck, object alert, string key)
        => Apply(Attention.Submit(new AttentionItem { Lane = deck.NoticeLane, Kind = AttentionKind.Quota, Key = key, Payload = alert, Duration = MainWindow.AlertShown }, DateTimeOffset.UtcNow));
    // A track change shows only if nothing else does right now. True when it shows.
    public bool TryTrack(MainWindow deck, TimeSpan duration)
    {
        var changes = Attention.Submit(new AttentionItem { Lane = deck.NoticeLane, Kind = AttentionKind.Track, Key = "track", Duration = duration, Level = NoticeLevel.Plain }, DateTimeOffset.UtcNow);
        Apply(changes);
        return Attention.Current(deck.NoticeLane) is { Kind: AttentionKind.Track };
    }
    public void Withdraw(string key) => Apply(Attention.Withdraw(key, DateTimeOffset.UtcNow));

    // The panel's island plays the motion of the notice it shows for this long: a waiting notice takes over only after it.
    public void Playing(MainWindow deck, TimeSpan length)
    {
        var now = DateTimeOffset.UtcNow;
        Attention.Playing(deck.NoticeLane, now + length, now);
        Schedule();
    }
    // The pointer is over the shown item, its details are open, or the keyboard works it: its time stands still.
    public void Hold(MainWindow deck, bool held) => Apply(Attention.Hold(deck.NoticeLane, held, DateTimeOffset.UtcNow));
    // The panel opened or closed. Opened on any screen, the user is looking at BrimDeck: the notices on every screen's island end,
    // including those waiting there, whatever each screen's profile shows (AttentionCoordinator.SetBusy).
    public void DeckExpanded(MainWindow deck, bool expanded) => Apply(Attention.SetBusy(deck.NoticeLane, expanded, DateTimeOffset.UtcNow));
    // The pointer left the open panel, or came back to it (AttentionCoordinator.SetLeaving).
    public void DeckLeaving(MainWindow deck, bool leaving) => Attention.SetLeaving(deck.NoticeLane, leaving);
    public void DeckRetired(MainWindow deck) { Attention.RemoveLane(deck.NoticeLane); SyncLoop(); Schedule(); }
    // A panel stopped being able to show anything (hidden by the user or a full-screen rule): what it shows ends, and its
    // waiting notices are not shown later (they stay in the recent list; CanShow refuses them while the panel is hidden).
    public void DeckHidden(MainWindow deck)
    {
        string lane = deck.NoticeLane;
        Apply(Attention.Remove(item => item.Lane == lane && item.IsNotice, DateTimeOffset.UtcNow));
        if (Attention.Current(lane) is not null) Apply(Attention.Dismiss(lane, DateTimeOffset.UtcNow));
    }

    // The coordinator's last check before anything starts (see AttentionCoordinator.CanShow).
    private bool CanShow(AttentionItem item)
    {
        if (!item.IsNotice) return true;
        if (!_enabled) return false;
        if (item.Payload is Notice notice && Hub.Find(notice.Id) is null) return false;
        return _app.Decks.FirstOrDefault(d => d.NoticeLane == item.Lane) is { CanShowNotices: true, ShowsNewNotices: true };
    }

    // What the recent notices lost no longer shows or waits. Neither does a notice that stayed on the island until handled
    // once there is nothing left to handle: Windows' notification center no longer has it, its alarm or countdown stopped
    // ringing, its sender closed it or sent it again as an ordinary notice, or the user chose a level for its application.
    private void DropMissing()
    {
        var now = DateTimeOffset.UtcNow;
        bool Missing(AttentionItem item) => item.Payload is Notice notice && (Hub.Find(notice.Id) is null || item.Persistent && Level(notice, now) != NoticeLevel.Persistent);
        foreach (var deck in _app.Decks)
            if (Attention.Current(deck.NoticeLane) is { } current && Missing(current) || Attention.Queued(deck.NoticeLane).Any(Missing))
            {
                Apply(Attention.Remove(Missing, DateTimeOffset.UtcNow));
                return;
            }
    }

    // The user closed what the panel shows (a click that opened its application, a close button, Escape); the next waiting
    // notice may follow. A notice shown on several screens ends on all of them. Only BrimDeck's own presentation closes;
    // Windows' notification center keeps the notice. Closing it by hand is the one explicit act that also closes it at a WSL
    // source whose agent is the notification service (as closing a desktop notification bubble does).
    public void Dismiss(MainWindow deck, bool reportToSource)
    {
        var now = DateTimeOffset.UtcNow;
        var current = Attention.Current(deck.NoticeLane);
        if (current is { IsNotice: true }) Apply(Attention.Withdraw(current.Key, now)); else Apply(Attention.Dismiss(deck.NoticeLane, now));
        if (reportToSource && current?.Payload is Notice notice) { ReportDismissed(notice); EndWait(notice.Id, NoticeWaits.Dismissed); }
    }

    // The panel cannot show the item the coordinator gave it (hidden meanwhile, still opening): it ends there only, and the
    // same notice on another screen stays.
    public void Skip(MainWindow deck) => Apply(Attention.Dismiss(deck.NoticeLane, DateTimeOffset.UtcNow));

    private void ReportDismissed(Notice notice)
    {
        if ((notice.Capabilities & NoticeCapabilities.ReportDismissal) == 0 || notice.Distro is not { } distro) return;
        if (NoticeProtocol.ToAgent(NoticeTypes.Dismissed, notice.InstanceKey) is { } message) _wsl.Send(distro, message);
    }

    public bool CanOpen(Notice notice) => notice.AppKey == WeChatRows.AppKey || notice.Origin == NoticeOrigin.Windows && (notice.Capabilities & NoticeCapabilities.OpenApp) != 0
        || notice.Actions.Any(action => action.Key == "default") && (notice.Capabilities & NoticeCapabilities.Actions) != 0;

    // Opens the application the notice came from. False when that did not work; the caller keeps the details open and says so.
    public async Task<bool> OpenAsync(Notice notice)
    {
        Hub.MarkViewed(notice.Id);
        if (notice.AppKey == WeChatRows.AppKey) return await OpenWeChatAsync(notice);
        if (notice.Origin == NoticeOrigin.Windows)
        {
            if (notice.AppId.Length == 0) return false;
            // Opening leads to the notice's own conversation: the toast is read now, for this one notice, and its launch string is
            // used once and not kept. Where it cannot be read or is not supported, the application alone is opened (LaunchAsync).
            WindowsNoticeSource.Replay? replay = ToastActivation.NotificationId(notice.InstanceKey, notice.AppId) is { } id ? new WindowsNoticeSource.Replay(id) : null;
            return await _windows.LaunchAsync(notice.AppId, replay);
        }
        if (notice.Actions.Any(action => action.Key == "default")) return Invoke(notice, "default");
        return false;
    }

    // Passes a chosen action back to the WSL application (only offered while the agent is its notification service).
    public bool Invoke(Notice notice, string key)
    {
        if ((notice.Capabilities & NoticeCapabilities.Actions) == 0 || notice.Distro is not { } distro
            || NoticeProtocol.ToAgent(NoticeTypes.Action, notice.InstanceKey, key) is not { } message) return false;
        _wsl.Send(distro, message);
        Hub.SourceRemoved(notice.InstanceKey);
        Withdraw(notice.Id);
        return true;
    }

    // Deleting and clearing touch BrimDeck's own records only: no source is told, so no application stops reminding because
    // its notice left BrimDeck's list (Windows' notification center and WSL applications keep theirs).
    public void Delete(Notice notice) { Withdraw(notice.Id); Hub.Delete(notice.Id); }
    // Clears the recent notices together with everything shown or waiting, so nothing deleted shows afterwards.
    public void Clear()
    {
        Apply(Attention.Remove(item => item.IsNotice, DateTimeOffset.UtcNow));
        Hub.Clear();
    }

    public enum WindowsSyncStatus { Done, Off, NoAccess, Failed }

    // The sync button of the notices page: the notices read from Windows' notification center are made to match what it holds
    // now (NoticeHub.SyncWindows). Notices of every other source (taskbar flashes, brim-notify, WSL) are not touched.
    public async Task<(WindowsSyncStatus Status, int Removed, int Added)> SyncWindowsAsync()
    {
        bool On() => _enabled && !_disposed;
        if (!On()) return (WindowsSyncStatus.Off, 0, 0);
        IReadOnlyList<NoticeInput>? present;
        try { present = await _windows.ReadCenterAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log(ex);
            return (WindowsSyncStatus.Failed, 0, 0);
        }
        if (present is null) return (WindowsSyncStatus.NoAccess, 0, 0);
        if (!On()) return (WindowsSyncStatus.Off, 0, 0);
        var sorted = present.Select(input => NoticeTimeSources.Classify(input, TimeZoneInfo.Local)).ToList();
        var (removed, added) = Hub.SyncWindows(sorted, DateTimeOffset.UtcNow);
        // A deleted notice that shows or waits to show goes too, as with Delete.
        foreach (var id in removed) Withdraw(id);
        return (WindowsSyncStatus.Done, removed.Count, added);
    }

    // A notice of BrimDeck's own, to try a level's look or an alignment (null takes the settings'). It is never sent to Windows,
    // so it cannot come back through the notification list. BrimDeck's own features show their notices the same way: Receive
    // with a NoticeInput that names its Level and Align.
    public void SendTest(NoticeLevel level, NoticeAlign? align = null)
    {
        Receive(new NoticeInput
        {
            Origin = NoticeOrigin.Test, AppKey = TestAppKey, AppName = "BrimDeck", InstanceKey = "test:" + Guid.NewGuid().ToString("N"),
            Title = Loc.T("灵动提示测试", "Live notice test"),
            Body = Loc.T("通知会在岛上显示。鼠标移上去即可打开通知页。", "Notices show on the island. Rest the pointer on it to open the notices page."),
            Category = NoticeCategory.Info, Level = level, Align = align
        });
    }

    // ----- Time-kind notices -----

    // Waits for the next reminder, at most DueRecheck at a time. Nothing runs while nothing is due.
    private void ScheduleDue()
    {
        var next = _enabled && !_disposed ? Hub.NextDue() : null;
        if (next is not { } at) { _timeDue.Stop(); _dueFor = null; return; }
        if (_timeDue.IsEnabled && _dueFor == at) return;
        _dueFor = at;
        var wait = at - DateTimeOffset.UtcNow;
        _timeDue.Interval = wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait > DueRecheck ? DueRecheck : wait;
        _timeDue.Start();
    }

    private void FireDue()
    {
        _timeDue.Stop(); _dueFor = null;
        if (!_enabled) return;
        // A reminder that fired changes the hub, which schedules the next one; after a recheck with nothing due it is scheduled here.
        foreach (var notice in Hub.FireDue(DateTimeOffset.UtcNow)) Present(notice, refresh: false);
        ScheduleDue();
    }

    // The clock was set or the PC woke: what is due is worked out again from the clock, not from the timer's wait. The time
    // zone may have changed too, which .NET does not notice by itself: times are read and shown in the new one (core-M6).
    private void ClockMoved(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        TimeZoneInfo.ClearCachedData();
        _dueFor = null; _timeDue.Stop(); ScheduleDue();
        Epoch++;
        Changed?.Invoke();
    });
    private void PowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Resume) ClockMoved(sender, e); }

    // The shared second tick for live words. Views subscribe while they show such words and unsubscribe when they stop; the
    // tick runs only while one is subscribed, on the whole second so that every view changes together.
    public event Action LiveTick
    {
        add { _live += value; UpdateLive(); }
        remove { _live -= value; UpdateLive(); }
    }

    private void UpdateLive()
    {
        if (_live is null || _disposed) { _liveTick.Stop(); return; }
        if (_liveTick.IsEnabled) return;
        _liveTick.Interval = ToNextSecond();
        _liveTick.Start();
    }

    private void LiveStep()
    {
        if (_live is null) { _liveTick.Stop(); return; }
        _liveTick.Interval = ToNextSecond();
        _live.Invoke();
    }

    private static TimeSpan ToNextSecond() => TimeSpan.FromMilliseconds(1000 - DateTimeOffset.UtcNow.Millisecond + 5);

    // What BrimDeck itself can do with a notice it keeps time for (stop, cancel, remind again).
    public IReadOnlyList<NoticeAction> TimeActions(Notice notice) => NoticeClock.Actions(notice.Timing, DateTimeOffset.UtcNow);

    public void TimeAction(Notice notice, string key)
    {
        var now = DateTimeOffset.UtcNow;
        Hub.MarkViewed(notice.Id);
        switch (key)
        {
            case NoticeClock.StopAction or NoticeClock.CancelAction:
                if (Hub.StopTimer(notice.Id, now)) RefreshShown(notice);
                break;
            case NoticeClock.SnoozeAction:
                if (Hub.Snooze(notice.Id, NoticeClock.Snooze, now)) Withdraw(notice.Id);
                break;
        }
    }

    // ----- Quiet and pause -----

    public bool Paused => Settings.NoticesPausedUntil is { } until && until > DateTimeOffset.UtcNow;

    public void Pause(TimeSpan? duration)
    {
        _app.Settings.NoticesPausedUntil = duration is { } span ? DateTimeOffset.UtcNow + span : null;
        _app.QueueSave();
        UpdateQuiet();
        Changed?.Invoke();
    }

    // Recomputes whether notices are held back. Ending a quiet spell shows one summary of what arrived meanwhile.
    public void UpdateQuiet()
    {
        if (!_enabled) return;
        if (Settings.NoticesPausedUntil is { } until && until <= DateTimeOffset.UtcNow) { _app.Settings.NoticesPausedUntil = null; _app.QueueSave(); }
        _quiet.Recheck();
        bool quiet = _quiet.Locked || Paused || Settings.NoticeRespectFocus && (_quiet.FocusActive || NoticeQuiet.PresentationOrQuietTime());
        _windows.SetQuiet(_quiet.Locked || Paused);
        if (quiet) { if (!_quietCheck.IsEnabled) _quietCheck.Start(); } else _quietCheck.Stop();
        if (quiet == Quiet) return;
        Quiet = quiet;
        if (quiet) { EndResume(); Apply(Attention.SetSuppressed(true, DateTimeOffset.UtcNow, out _)); }
        else if (_windows.State is NoticeSourceState.Polling or NoticeSourceState.EventLog)
        {
            // Still held back until the Windows list has been read again (FinishResume).
            _resuming = true;
            _resumeLimit.Stop(); _resumeLimit.Start();
            _resumeReading = _windows.Refresh();
        }
        else Resume();
        Changed?.Invoke();
    }

    // Called with the refresh a finished reading covers; one that started before the resume asked does not count.
    private void FinishResume(int covers)
    {
        if (!_resuming || covers < _resumeReading) return;
        EndResume();
        if (_enabled && !Quiet) Resume();
    }

    private void EndResume() { _resuming = false; _resumeLimit.Stop(); }

    // Shows again, starting with one summary of what arrived while held back.
    private void Resume()
    {
        var now = DateTimeOffset.UtcNow;
        Apply(Attention.SetSuppressed(false, now, out int arrived));
        if (arrived <= 0) return;
        foreach (var lane in Lanes())
            Apply(Attention.Submit(new AttentionItem
            {
                Lane = lane, Kind = AttentionKind.Summary, Key = "summary:" + now.Ticks, Payload = arrived, Level = NoticeLevel.Glow,
                Style = Settings.NoticeStyles.Flash, Duration = TimeSpan.FromSeconds(Settings.NoticeStyles.Flash.Seconds)
            }, now));
    }

    // ----- Diagnostics -----

    // Failures go to notices.log in the data folder: the exception's type, code and stack, never a message, a title or a body.
    // The log keeps about the last 256 KB.
    public static void Log(Exception error) => Write($"{error.GetType().FullName} 0x{error.HResult:X8}\n{error.StackTrace}\n\n");

    // A line of BrimDeck's own words (never a sender's text).
    private static void Log(string line) => Write(line + "\n");

    private void LogDropped()
    {
        int count = _droppedPending;
        _droppedPending = 0; _droppedLogged = DateTimeOffset.UtcNow;
        _ = Task.Run(() => Log($"Dropped {count} reminders or stopwatches beyond {NoticeHub.MaxPending} (C-M3)."));
    }

    private static void Write(string entry)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "notices.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current} {entry}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ----- History -----

    // The folder the notices are kept in (NoticeStorage).
    public static string HistoryFolder(DeckSettings settings) => NoticeStorage.Folder;

    // Follows the settings: where the notices are kept and whether (see NoticeHistoryPlan for what that does with the file).
    private void UseHistoryFolder(DeckSettings settings)
    {
        string folder = HistoryFolder(settings);
        bool keep = settings.NoticeHistory, kept = _keepHistory;
        // A folder not checked yet (notices were not kept) is left as it was before: only BrimDeck's own file names go, and
        // never through a link (NoticeHistory.Delete). A refused one is left alone.
        bool oldTrusted = _folderTrust is null or NoticeStorage.FolderTrust.Trusted;
        _keepHistory = keep;
        var old = _history;
        bool? loaded = _historyLoaded ? true : !_loadingHistory && HistoryState == NoticeHistoryState.Empty ? false : null;
        var steps = NoticeHistoryPlan.For(old?.DirectoryPath, folder, kept, keep, loaded);
        if (old is null || !NoticeHistoryPlan.SameFolder(old.DirectoryPath, folder))
        {
            _history = new NoticeHistory(folder, new NoticeProtection());
            SetLoaded(false); HistoryState = NoticeHistoryState.Empty;
            _folderTrust = null; _moveFrom = null;
        }
        // Switched on again: the folder is checked and its file read (the file was removed while off, so the list in memory
        // is written there next) before anything is saved, as at start-up.
        else if (keep && !kept && _folderTrust is null) SetLoaded(false);
        UseIconFolder();
        foreach (var step in steps)
            switch (step)
            {
                // After a save, the old file goes once the new one is written (SaveAsync); a refused folder is left alone.
                case NoticeHistoryStep.DeleteOld when steps.Contains(NoticeHistoryStep.Save): _retired = old; break;
                case NoticeHistoryStep.DeleteOld when oldTrusted: DeleteFiles(old!); break;
                // Moved in the background, once the new folder has been checked (LoadHistoryAsync).
                case NoticeHistoryStep.MoveOld when oldTrusted: _moveFrom = old!.DirectoryPath; break;
                // After a Load, the save follows the reading (LoadHistoryAsync).
                case NoticeHistoryStep.Save when !steps.Contains(NoticeHistoryStep.Load): QueueSave(); break;
            }
        // Not kept: no copy stays behind anywhere.
        if (!keep && _retired is { } retired) { _retired = null; DeleteFiles(retired); }
    }

    // Application icons are kept beside the saved notices only while notices are kept (memory only leaves nothing on disk),
    // and only once the folder was checked and found Trusted (N1): a refused folder is neither written to nor read from, and
    // one not created yet is created by Prepare, private, never by an icon's write with the parent's rights.
    private void UseIconFolder()
        => Icons.Folder = _keepHistory && _folderTrust == NoticeStorage.FolderTrust.Trusted && _history is { } history ? history.DirectoryPath : "";

    private void DeleteFiles(NoticeHistory history) => Remove(() => { history.Delete(); NoticeIcons.DeleteStored(history.DirectoryPath); });

    // Disk work that removes old files, off the UI thread (L6): a folder on a network share that does not answer must not
    // freeze it. In order, after what was queued before; on exit it is done at once, before the process ends.
    private void Remove(Action work)
    {
        if (_disposed) { work(); return; }
        _deleting = _deleting.ContinueWith(_ => { try { work(); } catch (Exception ex) { Log(ex); } }, TaskScheduler.Default);
    }

    // Whether the folder's file has been read; until then the hub remembers what was deleted or cleared (C-L2).
    private void SetLoaded(bool loaded) { _historyLoaded = loaded; Hub.TrackRemovals = !loaded; }

    private void LoadHistory()
    {
        if (_historyLoaded || _loadingHistory || _history is not { } history) return;
        if (!Settings.NoticeHistory) { Remove(history.Delete); SetLoaded(true); HistoryState = NoticeHistoryState.Empty; return; }
        // A refused folder stays refused until another is chosen (or the user closes it to other accounts).
        if (_folderTrust is { } trust && trust is not (NoticeStorage.FolderTrust.Trusted or NoticeStorage.FolderTrust.Unreachable)) { HistoryState = NoticeHistoryState.Refused; return; }
        _loadingHistory = true;
        var moveFrom = _moveFrom;
        _ = LoadHistoryAsync(history, true, moveFrom, check: _folderTrust != NoticeStorage.FolderTrust.Trusted);
    }

    // Read in the background (perf-9). Notices arriving meanwhile join the list, and nothing is written until the file has
    // been read: a file that cannot be reached now is read again later instead of being written over (core-M4). The folder
    // is checked (and the default one created) here too, once per folder, before anything is moved into, read from or
    // written to it (sec-M1, C-M2): a network folder that does not answer holds up the reading, not the UI.
    private async Task LoadHistoryAsync(NoticeHistory history, bool moveLegacy, string? moveFrom, bool check)
    {
        (NoticeStorage.FolderTrust Trust, NoticeHistoryState State, List<Notice> Notices, List<NoticeKnownApp> Apps) read;
        bool closable = false;
        try
        {
            // Old files still being removed go first (Remove): the folder may be the one they are removed from.
            await _deleting;
            read = await Task.Run(() =>
            {
                var trust = check ? NoticeStorage.Prepare(history.DirectoryPath) : NoticeStorage.FolderTrust.Trusted;
                closable = trust == NoticeStorage.FolderTrust.Shared && NoticeStorage.Closable(history.DirectoryPath);
                if (trust != NoticeStorage.FolderTrust.Trusted) return (trust, NoticeHistoryState.Refused, new List<Notice>(), new List<NoticeKnownApp>());
                // A file never read moves along with the folder (life-L1).
                if (moveFrom is not null) NoticeStorage.Move(moveFrom, history.DirectoryPath);
                // The first time, notices an earlier version kept in the install folder move to the data folder.
                if (moveLegacy) NoticeStorage.MoveLegacy(history.DirectoryPath);
                var state = history.Load(out var notices, out var apps);
                return (trust, state, notices, apps);
            });
        }
        finally { _loadingHistory = false; }
        if (_disposed) return;
        // The folder changed meanwhile: the new one is read instead.
        if (_history != history) { if (_enabled) LoadHistory(); return; }
        // A folder that could not be checked now is checked again with the next reading.
        _folderTrust = read.Trust == NoticeStorage.FolderTrust.Unreachable ? null : read.Trust;
        HistoryClosable = closable;
        UseIconFolder();
        if (read.Trust == NoticeStorage.FolderTrust.Trusted && _moveFrom == moveFrom) _moveFrom = null;
        HistoryState = read.Trust == NoticeStorage.FolderTrust.Unreachable ? NoticeHistoryState.Unavailable : read.State;
        if (read.State is NoticeHistoryState.Loaded or NoticeHistoryState.Empty)
        {
            // What arrived while the file was read, or the list that moved here from another folder, is written now.
            bool arrived = Hub.Notices.Count > 0;
            if (read.State == NoticeHistoryState.Loaded) Hub.Load(read.Notices, read.Apps, DateTimeOffset.UtcNow);
            SetLoaded(true);
            if (arrived || _retired is not null) QueueSave();
        }
        Changed?.Invoke();
    }

    // Reads a file that could not be reached again now (the settings page), instead of at the next maintenance.
    public void RetryHistory() { if (_enabled && HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Denied) LoadHistory(); }

    // What made BrimDeck refuse the folder, or null while it is used (or not checked yet).
    public NoticeStorage.FolderTrust? HistoryRefusal => _folderTrust is { } trust && trust != NoticeStorage.FolderTrust.Trusted ? trust : null;

    // Whether a Shared folder can be made the user's own from the settings page (NoticeStorage.Closable, M3).
    public bool HistoryClosable { get; private set; }

    // Closes a folder of the user's own that other accounts may write in (the settings page) and reads it again.
    public async Task CloseHistoryFolderAsync()
    {
        if (_history is not { } history || _folderTrust != NoticeStorage.FolderTrust.Shared || !HistoryClosable) return;
        await Task.Run(() => NoticeStorage.Close(history.DirectoryPath));
        if (_disposed || _history != history) return;
        _folderTrust = null; HistoryState = NoticeHistoryState.Empty;
        UseIconFolder();
        if (_enabled) LoadHistory();
        Changed?.Invoke();
    }

    // Replaces a file that could not be read (damaged, not readable by this account, or a link in a folder BrimDeck uses)
    // with the current (empty or new) list.
    public void ResetHistory()
    {
        if (_folderTrust != NoticeStorage.FolderTrust.Trusted || _loadingHistory) return;
        // Off the UI thread like other deletions (Remove); the save that follows waits for it.
        if (_history is { } history) Remove(history.Delete);
        HistoryState = NoticeHistoryState.Empty;
        SetLoaded(true);
        QueueSave();
        Changed?.Invoke();
    }

    private bool CanSave => Settings.NoticeHistory && _history is not null && _historyLoaded && !_loadingHistory && _folderTrust == NoticeStorage.FolderTrust.Trusted;

    // A sooner save replaces a later one; a minor change never puts off one already due.
    private void QueueSave(bool minor = false)
    {
        if (_disposed || !CanSave) return;
        var wait = minor ? MinorSaveDelay : SaveDelay;
        if (_saveTimer.IsEnabled && _saveTimer.Interval <= wait) return;
        _saveTimer.Interval = wait;
        _saveTimer.Start();
    }

    // The list is copied here, on the UI thread; encrypting and writing happen in the background, one save at a time.
    private async Task SaveAsync()
    {
        if (_history is not { } history || !CanSave) return;
        if (_saving) { _savePending = true; return; }
        _saving = true;
        bool codes = Settings.NoticeCodes;
        var notices = Hub.Notices.Select(n => NoticeCodes.ForHistory(n, codes).Copy()).ToList();
        var apps = Hub.KnownApps.ToList();
        long order = history.NextOrder();
        bool written = false;
        try { await _deleting; written = await Task.Run(() => history.Save(notices, apps, () => _keepHistory && _history == history, order)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        finally { _saving = false; }
        if (written) Retire(history);
        if (_savePending) { _savePending = false; QueueSave(); }
    }

    // The list is written in its new folder: the file it moved away from goes.
    private void Retire(NoticeHistory written)
    {
        if (_retired is not { } retired || _history != written) return;
        _retired = null;
        if (!NoticeHistoryPlan.SameFolder(retired.DirectoryPath, written.DirectoryPath)) DeleteFiles(retired);
    }

    // On exit: what is not saved yet is written now, and kept on the disk itself. A background save still under way may reach
    // the file before or after this one; its list was copied earlier, so NoticeHistory.Save skips it if it comes last, and the
    // newest list is the one left on disk.
    public void Flush()
    {
        if (!_saveTimer.IsEnabled && !_savePending && !_saving) return;
        _saveTimer.Stop(); _savePending = false;
        if (_history is not { } history || !CanSave) return;
        try { if (history.Save(Hub.Notices.Select(n => NoticeCodes.ForHistory(n, Settings.NoticeCodes)).ToList(), Hub.KnownApps)) Retire(history); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // The sources stop first, so nothing arrives after the last save (core-L3).
        _pipe?.Dispose(); _pipe = null;
        _flash?.Dispose(); _flash = null;
        _wechat?.Dispose(); _wechat = null;
        _windows.Dispose(); _wsl.Dispose();
        _disposed = true;
        // BrimDeck exits: the senders that wait hear nothing more (their connections end with the pipe), and no timer of
        // theirs fires afterwards.
        foreach (var waiter in _waits.Values) { waiter.Timer.Stop(); waiter.Result.TrySetCanceled(); }
        _waits.Clear(); _agentWaits.Clear();
        Flush();
        _attentionTimer.Stop(); _saveTimer.Stop(); _maintenance.Stop(); _quietCheck.Stop(); _resumeLimit.Stop(); _timeDue.Stop(); _liveTick.Stop();
        _changeTimer.Stop(); _refreshTimer.Stop();
        Sounds.Dispose();
        Microsoft.Win32.SystemEvents.TimeChanged -= ClockMoved;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        _quiet.Dispose();
    }
}
