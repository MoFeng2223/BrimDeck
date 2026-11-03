using System.IO;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Timers;

// A normal, separately loaded module. The only host entry point is the public SDK context.
public sealed class TimerModule : IModule
{
    public const string Id = "timers";
    public const string PageId = Id + ":timers", ActivityId = Id + ":session";
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var platform = context.Platform();
        var workspace = new TimerWorkspace(context);
        context.Own(workspace);
        platform.Registries.Register("timers", new ModulePage("计时器", "Timers", view => new TimerPageView(workspace, view.Owner,
            () => view.ShowActivity(ActivityId), () => view.OpenSettings(Id + ":general")), 100));
        platform.Registries.Register<IModuleActivity>("session", new TimerActivity(workspace));
        platform.Registries.Register<IModuleSettings>("general", new TimerGeneralSettings(workspace));
        platform.Events.On<ModuleActivityOpening>(e => { if (e.ContributionId == ActivityId && !workspace.Timer.Active) e.Cancel(); });
        foreach (var mode in Enum.GetValues<TimerMode>())
        {
            string key = mode.ToString().ToLowerInvariant();
            var settings = new TimerSettings(workspace, mode);
            platform.Registries.Register<IModuleSettings>(key, settings);
            platform.Registries.Register(key, new ModuleWidget(settings.Name, settings.NameEn, Id + ":" + key,
                () => workspace.Options.WidgetEnabled(mode), enabled => workspace.SetEnabled(mode, enabled), (int)mode)
            {
                ReadOrder = () => workspace.Options.Widgets.ToList().IndexOf(mode),
                MoveTo = index => workspace.MoveTo(mode, index),
                IconPath = mode switch
                {
                    TimerMode.Pomodoro => "M 8,4 C 4,1 1,5 2,10 C 3,15 7,16 8,14 C 9,16 13,15 14,10 C 15,5 12,2 8,4 Z M 8,4 C 7,2 6,1 4,2 M 8,4 C 9,1 11,1 12,2 M 8,4 V 1",
                    TimerMode.Countdown => "M 3,1 H 13 M 3,15 H 13 M 4,1 V 4 L 12,12 V 15 M 12,1 V 4 L 4,12 V 15",
                    _ => "M 5,1 H 11 M 8,1 V 4 M 12,3 L 14,5 M 8,7 V 10 M 14,10 A 6,6 0 1 1 2,10 A 6,6 0 1 1 14,10"
                }
            });
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class TimerWorkspace : IDisposable
{
    private readonly IModuleContext _context;
    private readonly IModulePresentation _presentation;
    private IDisposable? _pulse;
    private bool _disposed;
    private bool _sessionCorrupt;
    internal TimerSession Timer { get; } = new();
    internal TimerOptions Options { get; private set; }
    internal string SaveWarning { get; private set; } = "";
    internal int Completion { get; private set; }
    internal long CompletedUntil { get; private set; }
    internal event Action? Changed;

    internal TimerWorkspace(IModuleContext context)
    {
        _context = context;
        var platform = context.Platform();
        _presentation = platform.Services.Get<IModulePresentation>();
        Options = Read<TimerOptions>("preferences") ?? new(); Options.Normalize(); Options.ApplyDefaultMode();
        var saved = Read<TimerSnapshot>("session");
        if (saved is not null && Options.Allows(saved.Mode)) Timer.Restore(saved);
        Timer.Changed += OnChanged; Timer.PhaseCompleted += OnCompleted;
        platform.Events.On<ModuleSuspending>(_ => Timer.Pause(), ModuleEventPriority.High);
        platform.Events.On<ModuleUiChanged>(_ => Raise());
    }
    private T? Read<T>(string key) where T : class
    {
        var result = _context.Storage.ReadResult<T>(key);
        if (result.Status == ModuleReadStatus.Corrupt)
        {
            if (key == "session") _sessionCorrupt = true;
            SaveWarning = Loc.T("计时数据未能读取，可在模组设置中查看详情。", "Timer data could not be read. See module details.");
            _context.Report("read " + key, new IOException(result.Error));
        }
        return result.Status == ModuleReadStatus.Value ? result.Value : null;
    }
    internal void SaveOptions()
    {
        if (_disposed) return;
        Options.Normalize();
        if (Timer.Active && !Options.Allows(Timer.Mode)) Timer.Stop();
        Write("preferences", Options.Copy()); Raise(); _presentation.Invalidate();
    }
    internal void SetEnabled(TimerMode mode, bool enabled)
    {
        switch (mode) { case TimerMode.Pomodoro: Options.PomodoroEnabled = enabled; break; case TimerMode.Countdown: Options.CountdownEnabled = enabled; break; default: Options.StopwatchEnabled = enabled; break; }
        SaveOptions();
    }
    internal void MoveTo(TimerMode mode, int index)
    {
        var order = Options.Widgets.ToList(); order.Remove(mode); order.Insert(Math.Clamp(index, 0, order.Count), mode);
        Options.Order = order; SaveOptions();
    }
    private void OnChanged()
    {
        _sessionCorrupt = false; // A deliberate timer command can replace unusable session data.
        Write("session", Timer.Snapshot());
        if (Timer.Running && _pulse is null) _pulse = _context.Every(TimeSpan.FromMilliseconds(250), () => { Timer.Tick(); Raise(); });
        else if (!Timer.Running) { _pulse?.Dispose(); _pulse = null; }
        Raise();
    }
    private void OnCompleted(TimerPhase phase)
    { Completion++; CompletedUntil = Environment.TickCount64 + 900; Raise(); }
    private void Write<T>(string key, T value)
    {
        try { _context.Storage.Write(key, value); SaveWarning = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SaveWarning = Loc.T("计时数据未能保存，请检查数据目录权限。", "Timer data could not be saved. Check data folder permissions."); _context.Report("save " + key, ex); }
    }
    private void Raise()
    {
        if (_disposed) return;
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); } catch (Exception ex) { _context.Report("timer view", ex); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Timer.Pause(); if (!_sessionCorrupt) Write("session", Timer.Snapshot()); _disposed = true;
        _pulse?.Dispose(); _pulse = null;
        Timer.Changed -= OnChanged; Timer.PhaseCompleted -= OnCompleted; Changed = null;
    }
}

internal sealed class TimerGeneralSettings(TimerWorkspace workspace) : IModuleSettings
{
    public string Name => "计时器";
    public string NameEn => "Timers";
    public bool Visible => true;
    public IReadOnlyList<ModuleSetting> Fields => [new("default", "默认打开", "Default widget", ModuleSettingKind.Choice)
    {
        Options = new[] { new ModuleSettingOption(-1, "上次使用", "Last used") }.Concat(workspace.Options.Modes.Select(mode =>
            new ModuleSettingOption((int)mode, mode switch { TimerMode.Pomodoro => "番茄钟", TimerMode.Countdown => "倒计时", _ => "秒表" }, mode.ToString()))).ToArray()
    }];
    public object Read(string key) => key == "default" ? workspace.Options.DefaultMode is { } mode ? (int)mode : -1 : throw new ArgumentException(key);
    public void Write(string key, object value)
    {
        if (key != "default") throw new ArgumentException(key);
        int selected = Convert.ToInt32(value);
        if (selected != -1 && (!Enum.IsDefined((TimerMode)selected) || !workspace.Options.WidgetEnabled((TimerMode)selected))) throw new ArgumentOutOfRangeException(nameof(value));
        workspace.Options.DefaultMode = selected == -1 ? null : (TimerMode)selected;
        workspace.SaveOptions();
    }
    public void Reset() { workspace.Options.DefaultMode = null; workspace.SaveOptions(); }
}
