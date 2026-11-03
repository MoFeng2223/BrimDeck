using System.IO;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Calendar;

public sealed class CalendarModule : IModule
{
    public const string Id = "calendar";
    public const string PageId = Id + ":month";
    public const string ActivityId = Id + ":strip";
    public const string SettingsId = Id + ":settings";

    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var workspace = new CalendarWorkspace(context);
        context.Own(workspace);

        IDisposable? page = null, strip = null;
        void RegisterPage() =>
            page = context.Register("month", new ModulePage("日历", "Calendar", view => new CalendarPageView(workspace, view), 150));
        void RegisterStrip() =>
            strip = context.Register<IModuleActivity>("strip", new CalendarActivity(workspace));
        void ShowPage(bool enabled)
        {
            workspace.Options.PageEnabled = enabled;
            workspace.SavePreferences();
            if (enabled) { if (page is null) RegisterPage(); }
            else { page?.Dispose(); page = null; }
        }
        void ShowStrip(bool enabled)
        {
            workspace.Options.ActivityEnabled = enabled;
            workspace.SavePreferences();
            if (enabled) { if (strip is null) RegisterStrip(); }
            else { strip?.Dispose(); strip = null; }
        }

        if (workspace.Options.PageEnabled) RegisterPage();
        if (workspace.Options.ActivityEnabled) RegisterStrip();

        // No ModuleWidget: a settings page that no widget points at is listed under Tools → Modules in the
        // settings sidebar. The two on/off switches live in that page instead of the mini-app component list.
        context.Register<IModuleSettings>("settings", new CalendarSettings(workspace, ShowPage, ShowStrip));
        return ValueTask.CompletedTask;
    }
}

// Holds every piece of state a page or the island strip needs. Views only render it, so navigating
// away and back, or opening the page on a second display, never restarts the calendar.
internal sealed class CalendarWorkspace : IDisposable
{
    private const string PreferencesKey = "preferences";
    private const string EventsKey = "events";
    private const string RemindedKey = "reminded";

    private readonly IModuleContext _context;
    private readonly IModuleNotifications? _notices;
    private readonly Schedule _schedule = new();
    private readonly List<CalendarEvent> _events = [];
    private readonly Dictionary<string, bool> _reminded = [];

    private bool _eventsWritable = true, _disposed;
    private DateOnly _selected, _month, _today;
    private int _revision = 1;

    internal event Action? Changed;

    internal CalendarOptions Options { get; private set; } = new();
    internal DateOnly Selected => _selected;
    internal DateOnly Month => _month;
    internal DateOnly Today => _today;
    internal int Revision => _revision;
    internal bool Writable => _eventsWritable;
    internal int EventCount => _events.Count;
    internal string VivoStatus { get; private set; } = "";
    internal DateTime? VivoSyncedAt { get; private set; }
    private DateTime _vivoAttempt = DateTime.MinValue;
    private ulong _vivoPrint;
    private bool _vivoMerged;

    internal CalendarWorkspace(IModuleContext context)
    {
        _context = context;
        _today = DateOnly.FromDateTime(DateTime.Now);
        _selected = _today;
        _month = MonthGrid.First(_today);
        _notices = TryService<IModuleNotifications>();

        Options = Read<CalendarOptions>(PreferencesKey, false) ?? new();
        Options.Normalize();
        LoadEvents();
        LoadReminders();
        _schedule.Rebuild(_events);

        _context.Subscribe<ModuleSuspending>(_ => SaveAll());
        _context.Every(TimeSpan.FromSeconds(20), Tick);
    }

    internal IReadOnlyList<CalendarEvent> ForDay(DateOnly day) => _schedule.Day(day);
    internal int CountOn(DateOnly day) => _schedule.CountOn(day);
    internal FocusInfo Focus() => _schedule.Focus(DateTime.Now);

    // Mirrors vivo's calendar database into the local list, read-only. Called while the page is open,
    // at most every few seconds; the merge runs only when the imported occurrences actually changed.
    internal void SyncExternal(bool force = false)
    {
        if (_disposed) return;
        if (!Options.VivoEnabled)
        {
            if (_vivoMerged)
            {
                _vivoMerged = false;
                if (_events.RemoveAll(item => item.Source.Length > 0) > 0) AfterChange();
            }
            SetVivoStatus("");
            return;
        }

        var now = DateTime.Now;
        if (!force && (now - _vivoAttempt).TotalSeconds < 4) return;
        _vivoAttempt = now;

        string? path = VivoCalendar.Locate();
        if (path is null) { SetVivoStatus(Loc.T("未找到 vivo 日历库", "vivo calendar not found")); return; }

        var items = VivoCalendar.Read(path, _today.AddDays(-730), _today.AddDays(730), out string? error);
        if (error is not null)
        {
            _context.Report("read vivo calendar", new IOException(error));
            SetVivoStatus(Loc.T("vivo 读取失败", "vivo read failed"));
            return;
        }

        ulong print = VivoCalendar.Print(items);
        bool changed = !_vivoMerged || print != _vivoPrint;
        _vivoPrint = print;
        _vivoMerged = true;
        if (changed)
        {
            MergeExternal(items);
            VivoSyncedAt = DateTime.Now;
        }
        string stamp = VivoSyncedAt is { } at ? $" · {at:HH:mm}" : "";
        SetVivoStatus(Loc.T($"vivo {items.Count} 项", $"vivo {items.Count}") + stamp);
    }

    private void MergeExternal(IReadOnlyList<VivoOccurrence> items)
    {
        _events.RemoveAll(item => item.Source.Length > 0);
        foreach (var item in items)
            _events.Add(new CalendarEvent
            {
                Id = item.Key,
                Date = CalendarEvent.Key(item.Day),
                Title = string.IsNullOrWhiteSpace(item.Title) ? Loc.T("未命名", "Untitled") : item.Title.Trim(),
                AllDay = item.AllDay,
                Start = item.Start,
                Duration = item.Duration,
                Category = item.Category,
                Source = "vivo"
            });
        AfterChange();
    }

    private void SetVivoStatus(string text)
    {
        if (VivoStatus == text) return;
        VivoStatus = text;
        Raise();
    }

    internal void Select(DateOnly day)
    {
        if (day == _selected) return;
        _selected = day;
        if (day.Year != _month.Year || day.Month != _month.Month) _month = MonthGrid.First(day);
        Raise();
    }

    internal void Move(int months)
    {
        if (months == 0) return;
        _month = MonthGrid.First(_month.AddMonths(months));
        Raise();
    }

    internal void GoToday()
    {
        _today = DateOnly.FromDateTime(DateTime.Now);
        _selected = _today;
        _month = MonthGrid.First(_today);
        Raise();
    }

    internal void Upsert(CalendarEvent item)
    {
        if (!_eventsWritable) return;
        if (string.IsNullOrEmpty(item.Id)) item.Id = NewId();
        int index = _events.FindIndex(existing => existing.Id == item.Id);
        if (index >= 0) _events[index] = item; else _events.Add(item);
        AfterChange();
    }

    internal void Remove(string? id)
    {
        if (!_eventsWritable || string.IsNullOrEmpty(id)) return;
        if (_events.RemoveAll(item => item.Id == id) == 0) return;
        AfterChange();
    }

    internal void SavePreferences()
    {
        Options.Normalize();
        Write(PreferencesKey, Options.Copy(), false);
        Raise();
    }

    internal void Apply(CalendarOptions options)
    {
        Options = options.Copy();
        SavePreferences();
    }

    private void AfterChange()
    {
        _schedule.Rebuild(_events);
        _revision = _revision == int.MaxValue ? 1 : _revision + 1;
        Write(EventsKey, new EventFile { Events = [.. _events] }, true);
        Raise();
    }

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (today != _today) { _today = today; _revision = _revision == int.MaxValue ? 1 : _revision + 1; }
        EvaluateReminder(now);
        Raise();
    }

    private void EvaluateReminder(DateTime now)
    {
        int advance = Options.ReminderMinutes;
        if (advance < 0 || _notices is null) return;
        var today = DateOnly.FromDateTime(now);
        bool changed = false;
        foreach (var item in _schedule.Day(today))
        {
            if (item.AllDay || item.Done) continue;
            var start = item.StartAt(today);
            if (now < start.AddMinutes(-advance) || now >= start.AddMinutes(1)) continue;
            string key = item.Id + "@" + start.ToString("yyyyMMddHHmm");
            if (_reminded.ContainsKey(key)) continue;
            var result = _notices.Show(new(key, Loc.T("日程提醒", "Event reminder"), $"{item.Clock}  {item.Title}")
            {
                Tone = ModuleNoticeTone.Info, DurationMilliseconds = 4000, ProgressPercent = null
            });
            // Only a received notice uses up this trigger; a rate-limited one may retry on the next tick.
            if (result is ModuleNoticeResult.Shown or ModuleNoticeResult.Queued) { _reminded[key] = true; changed = true; }
        }
        if (changed) SaveReminders();
    }

    private void LoadEvents()
    {
        var file = Read<EventFile>(EventsKey, true);
        if (file?.Events is not { } items) return;
        foreach (var item in items)
        {
            if (item is null || !item.Normalize()) continue;
            if (string.IsNullOrEmpty(item.Id)) item.Id = NewId();
            _events.Add(item);
        }
        // A mirror left behind by a disabled source is not shown, and not saved back on the next write.
        if (!Options.VivoEnabled) _events.RemoveAll(item => item.Source.Length > 0);
    }

    private void LoadReminders()
    {
        var file = Read<ReminderFile>(RemindedKey, false);
        if (file?.Marks is not { } marks) return;
        foreach (var mark in marks) if (!string.IsNullOrEmpty(mark)) _reminded[mark] = true;
        PruneReminders();
    }

    private void SaveReminders()
    {
        PruneReminders();
        Write(RemindedKey, new ReminderFile { Marks = [.. _reminded.Keys] }, false);
    }

    private void PruneReminders()
    {
        while (_reminded.Count > 200) _reminded.Remove(_reminded.Keys.First());
    }

    private void SaveAll()
    {
        if (_disposed) return;
        Write(PreferencesKey, Options.Copy(), false);
        if (_eventsWritable) Write(EventsKey, new EventFile { Events = [.. _events] }, true);
        SaveReminders();
    }

    private T? Read<T>(string key, bool critical) where T : class
    {
        var result = _context.Storage.ReadResult<T>(key);
        if (result.Status == ModuleReadStatus.Corrupt)
        {
            if (critical) _eventsWritable = false;
            _context.Report("read " + key, new InvalidDataException(result.Error ?? "Corrupt module data."));
            return null;
        }
        return result.Status == ModuleReadStatus.Value ? result.Value : null;
    }

    private void Write<T>(string key, T value, bool critical)
    {
        if (_disposed) return;
        if (critical && !_eventsWritable) return;
        try { _context.Storage.Write(key, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (critical) _eventsWritable = false;
            _context.Report("write " + key, error);
        }
    }

    private T? TryService<T>() where T : class
    {
        try { return _context.GetService<T>(); }
        catch (Exception error) when (error is ModuleApiException or InvalidOperationException)
        {
            _context.Report("service " + typeof(T).Name, error);
            return null;
        }
    }

    private void Raise()
    {
        if (_disposed) return;
        foreach (var listener in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)listener)(); }
            catch (Exception error) { _context.Report("calendar listener", error); }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public void Dispose()
    {
        if (_disposed) return;
        SaveAll();
        _disposed = true;
        Changed = null;
        _events.Clear();
        _reminded.Clear();
    }
}

// One settings page for the whole module. No ModuleWidget points at it, so the host lists it under
// Tools → Modules instead of under the mini-app components; the two component switches live here.
internal sealed class CalendarSettings(CalendarWorkspace workspace, Action<bool> showPage, Action<bool> showStrip) : IModuleSettings
{
    public string Name => "日历";
    public string NameEn => "Calendar";
    public bool Visible => true;

    public IReadOnlyList<ModuleSetting> Fields =>
    [
        new("first", "每周起始", "Week starts", ModuleSettingKind.Choice)
        {
            Options = [new(1, "周一", "Monday"), new(0, "周日", "Sunday")]
        },
        new("lunar", "显示农历与节气", "Show lunar dates", ModuleSettingKind.Toggle),
        new("holiday", "显示节日与调休", "Show festivals", ModuleSettingKind.Toggle),
        new("reminder", "提醒提前", "Remind before", ModuleSettingKind.Choice)
        {
            Options =
            [
                new(-1, "关闭", "Off"), new(0, "准时", "On time"), new(5, "5 分钟", "5 min"),
                new(10, "10 分钟", "10 min"), new(30, "30 分钟", "30 min")
            ]
        },
        new("page", "显示日历页面", "Show calendar page", ModuleSettingKind.Toggle),
        new("strip", "显示岛内日程", "Show island agenda", ModuleSettingKind.Toggle),
        new("vivo", "读取 vivo 日历（实验性）", "Read vivo calendar (experimental)", ModuleSettingKind.Toggle)
    ];

    public object Read(string key) => key switch
    {
        "first" => workspace.Options.FirstDay,
        "lunar" => workspace.Options.ShowLunar,
        "holiday" => workspace.Options.ShowHoliday,
        "reminder" => workspace.Options.ReminderMinutes,
        "page" => workspace.Options.PageEnabled,
        "strip" => workspace.Options.ActivityEnabled,
        "vivo" => workspace.Options.VivoEnabled,
        _ => throw new ArgumentException(key)
    };

    public void Write(string key, object value)
    {
        switch (key)
        {
            case "first": workspace.Options.FirstDay = Convert.ToInt32(value); workspace.SavePreferences(); break;
            case "lunar": workspace.Options.ShowLunar = Convert.ToBoolean(value); workspace.SavePreferences(); break;
            case "holiday": workspace.Options.ShowHoliday = Convert.ToBoolean(value); workspace.SavePreferences(); break;
            case "reminder": workspace.Options.ReminderMinutes = Convert.ToInt32(value); workspace.SavePreferences(); break;
            case "page": showPage(Convert.ToBoolean(value)); break;
            case "strip": showStrip(Convert.ToBoolean(value)); break;
            case "vivo":
                workspace.Options.VivoEnabled = Convert.ToBoolean(value);
                workspace.SavePreferences();
                workspace.SyncExternal(true);
                break;
            default: throw new ArgumentException(key);
        }
    }

    // Restoring defaults touches the preferences only; the two component switches keep their state.
    public void Reset()
    {
        var options = workspace.Options;
        options.FirstDay = 1;
        options.ShowLunar = true;
        options.ShowHoliday = true;
        options.ReminderMinutes = 10;
        workspace.SavePreferences();
    }
}
