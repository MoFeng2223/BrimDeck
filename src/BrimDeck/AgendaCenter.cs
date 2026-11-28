using System.IO;
using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// The planner's data for the whole application: every panel draws from this one AgendaBook and changes it here. It reads
// agenda.json from the data folder (next to settings.json) in the background at the start, saves a second after the last
// change, and writes what is left at exit. A file that cannot be read for now (in use, no permission) is read again every
// half minute, and nothing is written until it has been: what is changed meanwhile joins what it holds. Runs on the UI thread.
//
// It also gives the events' reminders (AgendaReminders) once the file was read, as notices of BrimDeck's own (Notices). Each check
// gives the reminders since the last one: one due within NoticeClock.MissedLimit shows as it arrives; one missed longer (BrimDeck
// was not running, the PC slept) is listed unread while its time has not ended, and dropped after. The time checked up to is
// kept in agenda.json (AgendaData.RemindedTo) when a reminder was given and at exit, so a later start knows what it missed. A
// reminder put off ("稍后提醒") is kept only while BrimDeck runs.
//
// With AgendaLeftTodos.Move it also moves the unfinished to-dos of earlier days to today (AgendaBook.MoveToToday): once the file
// was read, on the first check of reminders on a new day (they are checked at least once a minute, so no timer of its own runs),
// and when the setting is switched to Move (ApplySettings).
internal sealed class AgendaCenter : IDisposable
{
    // The planner's habits as the settings have them now (DeckSettings.AgendaWeekStart and the rest, the same for every profile);
    // a new DeckSettings's where there is no App.
    internal static DeckSettings Habits => (Application.Current as App)?.Settings ?? NoSettings;
    private static readonly DeckSettings NoSettings = new();

    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1), RetryDelay = TimeSpan.FromSeconds(30);
    // Reminders are checked at least this often, so a clock set forward or a PC that woke up is noticed soon.
    private static readonly TimeSpan RemindRecheck = TimeSpan.FromMinutes(1);
    // Missed reminders are looked for this far back at most.
    private static readonly TimeSpan RemindLookBack = TimeSpan.FromDays(2);
    private readonly AgendaStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _save, _retry;
    private readonly PromptTimer _remind;
    // The time reminders were checked up to; null until the file was read.
    private DateTime? _checkedTo;
    // The day the to-dos left on earlier days were last moved to (AgendaLeftTodos.Move).
    private DateOnly? _movedOn;
    // Reminders put off: the event, the start of its time, and when to remind again.
    private readonly List<(string Id, DateTime Start, DateTime At)> _snoozed = [];
    // The writes in the background, one after another; each one's text was taken when it was queued.
    private Task _writing = Task.CompletedTask;
    // _loaded: the file was read (or there was none, or it was moved aside), so it may be written. _dirty: changes not written yet.
    private bool _loaded, _loading, _merging, _dirty, _disposed;

    public AgendaBook Book { get; } = new();
    // How the last reading went; null until the first one has finished.
    public AgendaReadState? ReadState { get; private set; }
    // The name the unreadable file was given when it was moved aside (ReadState Recovered).
    public string? KeptAs { get; private set; }
    // The last write failed; it is tried again.
    public bool SaveFailed { get; private set; }
    public string FilePath => _store.FilePath;
    // Raised after the data changed or was read, and after the reading or saving state changed.
    public event Action? Changed;

    // "查看" from elsewhere (the notices page): the planner page shows Day, switched to View, with ItemId marked. The request
    // waits in PendingReveal until a page takes it (TakeReveal), so a page opened afterwards still finds it.
    public AgendaReveal? PendingReveal { get; private set; }
    public event Action<AgendaReveal>? RevealRequested;

    // Where reminders go; set once the notices take notices in (App), which looks for the reminders due. Without it (the README
    // renderer) nothing reminds.
    public NoticeCenter? Notices { get => _notices; set { _notices = value; ScheduleReminders(); } }
    private NoticeCenter? _notices;
    // The clock reminders are worked out by; the README renderer may set another.
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public AgendaCenter(string dataFolder, Dispatcher dispatcher)
    {
        _store = new AgendaStore(dataFolder);
        _dispatcher = dispatcher;
        _save = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SaveDelay };
        _save.Tick += (_, _) => { _save.Stop(); _save.Interval = SaveDelay; Save(); };
        _retry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RetryDelay };
        _retry.Tick += (_, _) => { _retry.Stop(); Load(); };
        _remind = new PromptTimer(dispatcher, CheckReminders);
        Book.Changed += BookChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += ClockMoved;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        Load();
    }

    // from: whatever asks, such as the panel whose notices page asked, so a page can tell a request meant for its own panel.
    public void Reveal(DateOnly day, AgendaView view, string? itemId = null, object? from = null)
    {
        var reveal = new AgendaReveal(day, view, itemId, from);
        PendingReveal = reveal;
        RevealRequested?.Invoke(reveal);
    }

    public AgendaReveal? TakeReveal()
    {
        var reveal = PendingReveal;
        PendingReveal = null;
        return reveal;
    }

    private void BookChanged()
    {
        if (!_merging) { _dirty = true; QueueSave(); }
        Changed?.Invoke();
        // An event added, moved or given another reminder: the next reminder may be another one.
        ScheduleReminders();
    }

    // ---- Reminders ----

    private void ClockMoved(object? sender, EventArgs e) => _dispatcher.BeginInvoke(ScheduleReminders);
    private void PowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Resume) ClockMoved(sender, e); }

    // Gives what is due and waits for the next reminder.
    private void ScheduleReminders()
    {
        if (_disposed || !_loaded) return;
        CheckReminders();
    }

    // Gives the reminders since the last check and waits for the next one, at most RemindRecheck at a time.
    private void CheckReminders()
    {
        _remind.Stop();
        if (_disposed || !_loaded) return;
        // A move changes the book, which checks the reminders once more from here (BookChanged); this check then finds them done.
        MoveLeftTodos();
        var now = AgendaTime.Second(Clock());
        if (_checkedTo is null)
        {
            // The first start that reminds: nothing earlier is looked for. Kept at once, so the next start does not miss what is
            // due meanwhile.
            _checkedTo = Book.RemindedTo ?? now;
            if (Book.RemindedTo is null) Keep(now);
        }
        var from = _checkedTo.Value;
        if (from > now) from = now;
        if (from < now - RemindLookBack) from = now - RemindLookBack;
        bool gave = false;
        if (Notices is { } notices)
        {
            if (now > from) foreach (var due in AgendaReminders.Due(Book.Events, from, now)) gave |= Give(notices, due.Event, due.At, now);
            for (int i = _snoozed.Count - 1; i >= 0; i--)
            {
                var (id, start, at) = _snoozed[i];
                if (at > now) continue;
                _snoozed.RemoveAt(i);
                if (AgendaReminders.Find(Book.FindEvent(id), start) is { } time) gave |= Give(notices, time, at, now);
            }
        }
        _checkedTo = now;
        if (gave) Keep(now);
        var next = AgendaReminders.Next(Book.Events, now);
        foreach (var (_, _, at) in _snoozed) if (next is null || at < next) next = at;
        var wait = next is { } then ? then - Clock() : RemindRecheck;
        _remind.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > RemindRecheck ? RemindRecheck : wait;
        _remind.Start();
    }

    // Gives one reminder, unless its time has ended. Whether a notice was posted.
    private static bool Give(NoticeCenter notices, AgendaEvent time, DateTime at, DateTime now)
    {
        bool ended = time.AllDay ? time.LastDay < DateOnly.FromDateTime(now) : time.End <= now;
        if (ended) return false;
        notices.Post(AgendaReminders.Notice(time, now, missed: now - at > NoticeClock.MissedLimit, TimeZoneInfo.Local, Habits.AgendaSnoozeMinutes));
        return true;
    }

    // The reminders up to time were given: written with the next save.
    private void Keep(DateTime time)
    {
        Book.RemindedTo = time;
        _dirty = true; QueueSave();
    }

    // "稍后提醒" on a reminder's notice: it goes, and the reminder comes again after delay (or at at, when given). False when the
    // notice is not a reminder or its event's time is gone.
    public bool Snooze(Notice notice, TimeSpan delay, DateTime? at = null)
    {
        if (Notices is not { } notices || AgendaReminders.Parse(notice.InstanceKey) is not { } key || AgendaReminders.Find(Book.FindEvent(key.Id), key.Start) is null) return false;
        _snoozed.RemoveAll(item => item.Id == key.Id && item.Start == key.Start);
        _snoozed.Add((key.Id, key.Start, at ?? AgendaTime.Second(Clock() + delay)));
        notices.Delete(notice);
        ScheduleReminders();
        return true;
    }

    // The island's "5 分钟后再提醒": the delay as the settings have it when the button is pressed.
    public static TimeSpan SnoozeDelay => TimeSpan.FromMinutes(AgendaReminders.SnoozeMinutes(Habits.AgendaSnoozeMinutes));

    // ---- To-dos left on earlier days ----

    // The settings changed: switched to Move, what is left moves now.
    public void ApplySettings()
    {
        _movedOn = null;
        MoveLeftTodos();
    }

    // With AgendaLeftTodos.Move, once a day: the unfinished to-dos of earlier days without a due day become today's.
    private void MoveLeftTodos()
    {
        if (_disposed || !_loaded || Habits.AgendaLeftTodos != AgendaLeftTodos.Move) return;
        var today = DateOnly.FromDateTime(Clock());
        if (_movedOn == today) return;
        _movedOn = today;
        Book.MoveToToday(today);
    }

    // "查看日程": the day and the event a reminder's notice is about, or null when the event is gone.
    public AgendaReveal? RevealFor(Notice notice, object? from)
    {
        if (AgendaReminders.Parse(notice.InstanceKey) is not { } key || Book.FindEvent(key.Id) is null) return null;
        return new AgendaReveal(DateOnly.FromDateTime(key.Start), AgendaView.Events, key.Id, from);
    }

    private void QueueSave()
    {
        if (!_loaded || _disposed || _save.IsEnabled) return;
        _save.Start();
    }

    private async void Load()
    {
        if (_loading || _loaded || _disposed) return;
        _loading = true;
        AgendaRead read;
        // Anything else going wrong (a path the system refuses) is treated as a file that cannot be reached now.
        try { read = await Task.Run(() => _store.Read(DateTime.Now)); }
        catch (Exception) { read = new(AgendaReadState.Unavailable, new()); }
        finally { _loading = false; }
        if (_disposed) return;
        ReadState = read.State;
        if (read.KeptAs is not null) KeptAs = read.KeptAs;
        if (read.Writable)
        {
            _loaded = true;
            _merging = true;
            try { Book.MergeLoaded(read.Data); }
            finally { _merging = false; }
            // Changed before the file could be read: what was changed is written now, together with what the file held.
            if (_dirty) QueueSave();
            MoveLeftTodos();
            ScheduleReminders();
        }
        else _retry.Start();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (!_loaded || !_dirty || _disposed) return;
        _dirty = false;
        var text = AgendaStore.Serialize(Book.Snapshot());
        _writing = WriteAfter(_writing, text);
    }

    // Runs without the UI thread, so Flush can wait for it there.
    private async Task WriteAfter(Task previous, string text)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { }
        try
        {
            await Task.Run(() => _store.Write(text)).ConfigureAwait(false);
            _ = _dispatcher.BeginInvoke(() => { if (SaveFailed) { SaveFailed = false; Changed?.Invoke(); } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                // Written again, with whatever changed since, after a while.
                _dirty = true; SaveFailed = true;
                _save.Stop(); _save.Interval = RetryDelay; _save.Start();
                Changed?.Invoke();
            });
        }
    }

    // On exit: the writes under way finish, and what is not written yet is written now.
    public void Flush()
    {
        if (!_loaded) return;
        _save.Stop();
        // The reminders checked while running count as given: a later start looks for those missed from here on.
        if (_checkedTo is { } checkedTo && !(Book.RemindedTo >= checkedTo)) { Book.RemindedTo = checkedTo; _dirty = true; }
        try { _writing.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        if (!_dirty) return;
        _dirty = false;
        try { _store.Write(Book.Snapshot()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _dirty = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _save.Stop(); _retry.Stop(); _remind.Stop();
        Book.Changed -= BookChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= ClockMoved;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
    }
}
