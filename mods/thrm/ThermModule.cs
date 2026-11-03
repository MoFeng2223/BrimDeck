using System.IO;
using BrimDeck.Modules;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Thrm;
// The cooler module: one persistent activity (the collapsed row's three readings) and its settings. The readings
// are presence, not a task, so the module registers no page — the collapsed row is the whole surface until the
// platform grows a way to fold richer readings into it. The only host entry point is the public SDK context.
public sealed class ThermModule : IModule
{
    public const string Id = "thrm";
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var workspace = new ThermWorkspace(context);
        context.Own(workspace);
        context.Register<IModuleActivity>("readings", new ThermActivity(workspace));
        context.Register<IModuleSettings>("general", new ThermSettings(workspace));
        // The readings' switch, shown with the module's details; off, the collapsed row returns to the built-in readings.
        context.Register("readings", new ModuleWidget("散热器读数", "Cooler readings", Id + ":general",
            () => workspace.Options.ShowReadings, workspace.SetShowReadings) { IconPath = ThermVisuals.FanGlyph });
        return ValueTask.CompletedTask;
    }
}

public sealed class ThermOptions
{
    public int PollSeconds { get; set; } = 2;
    public bool ShowReadings { get; set; } = true;
    public void Normalize() => PollSeconds = double.IsFinite(PollSeconds) ? Math.Clamp(PollSeconds, 1, 10) : 2;
}

// One poll feeds every surface: the workspace holds the latest sample and raises Changed, and the views redraw
// themselves from it — nothing is recreated while the user reads it. The sample is delivered on the thread the
// module was activated on (the UI thread in the host): the timer's own callbacks and the request's await already
// resume there, and delivery marshals back should either ever not.
internal sealed class ThermWorkspace : IDisposable
{
    private readonly IModuleContext _context;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private IDisposable? _pulse;
    private int _polling;
    private bool _disposed, _storageWritable;
    internal ThermOptions Options { get; private set; } = new();
    internal ThrmSample? Latest { get; private set; }
    internal bool BridgeUp => Latest is not null;
    internal event Action? Changed;

    internal ThermWorkspace(IModuleContext context)
    {
        _context = context;
        var saved = context.Storage.ReadResult<ThermOptions>("preferences");
        _storageWritable = saved.Status is ModuleReadStatus.Missing or ModuleReadStatus.Value;
        if (!_storageWritable) context.Report("read preferences", new InvalidDataException("Saved cooler settings are corrupt or unsupported; they will not be overwritten."));
        Options = saved.Value ?? new ThermOptions();
        Options.Normalize();
        RestartPulse();
        // The first read right away, so the readings are not blank for one interval; skipped when activation gave
        // no UI context, in which case the scheduler's timer supplies both the first read and its thread.
        if (_ui is not null) Poll();
    }

    // The poll callback runs on the UI thread (the host scheduler's timer); the request's await resumes there too,
    // so Latest and Changed always happen on the thread the views are on. One request at a time: a bridge that
    // answers slowly never stacks polls.
    private void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            var sample = await ThermBridge.ReadAsync(_context.Lifetime);
            Deliver(sample);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _context.Report("poll bridge", error); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void Deliver(ThrmSample? sample)
    {
        if (_disposed) return;
        if (ReferenceEquals(SynchronizationContext.Current, _ui)) { Apply(sample); return; }
        _ui?.Post(_ => { if (!_disposed) Apply(sample); }, null);
    }

    private void Apply(ThrmSample? sample)
    {
        // Bridge up-and-down is the norm (the cooler software starts and stops, the machine sleeps): the readings
        // show dashes and the page says so, and none of it goes into the module's diagnostics, which would only pile
        // up one entry per flip.
        Latest = sample;
        Raise();
    }

    internal void SetPollSeconds(int seconds)
    {
        Options.PollSeconds = double.IsFinite(seconds) ? Math.Clamp(seconds, 1, 10) : 2;
        Save();
        RestartPulse();
        Raise();
    }

    internal void SetShowReadings(bool enabled)
    {
        Options.ShowReadings = enabled;
        Save();
        Raise();
    }

    internal void ResetOptions()
    {
        Options = new ThermOptions();
        Save();
        RestartPulse();
        Raise();
    }

    private void RestartPulse()
    {
        _pulse?.Dispose();
        _pulse = _context.Every(TimeSpan.FromSeconds(Options.PollSeconds), Poll);
    }

    private void Save()
    {
        if (_disposed || !_storageWritable) return;
        try { _context.Storage.Write("preferences", Options); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _context.Report("save preferences", error); }
    }

    private void Raise()
    {
        if (_disposed) return;
        foreach (var listener in Changed?.GetInvocationList() ?? [])
            try { ((Action)listener)(); } catch (Exception error) { _context.Report("cooler view", error); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pulse?.Dispose();
        _pulse = null;
        Changed = null;
    }
}

internal sealed class ThermSettings(ThermWorkspace workspace) : IModuleSettings
{
    public string Name => "散热器";
    public string NameEn => "Cooler";
    public bool Visible => true;
    public IReadOnlyList<ModuleSetting> Fields =>
    [
        new("poll", "轮询间隔", "Poll interval", ModuleSettingKind.Number, 1, 10, "秒", "s")
    ];
    public object Read(string key) => key switch
    {
        "poll" => workspace.Options.PollSeconds,
        _ => throw new ArgumentException(key)
    };
    public void Write(string key, object value)
    {
        switch (key)
        {
            case "poll": workspace.SetPollSeconds((int)Math.Round(Convert.ToDouble(value))); break;
            default: throw new ArgumentException(key);
        }
    }
    public void Reset() => workspace.ResetOptions();
}
