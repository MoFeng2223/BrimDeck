using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

// A temporary accent a module asked for (host.control.accent). Requests are a stack: the newest is drawn, releasing one
// falls back to the one behind it, and the user's own theme change clears them all at once — the user always has the last
// word. A stopped module's requests go with its activation (the service owns each lease in its scope).
public sealed class AccentOverrides{
    private readonly object _gate = new();
    // Newest last: the top of the stack is what the panel draws.
    private readonly List<Entry> _entries = [];
    public sealed record Entry(string ModuleId, string Color, AccentOverride? Lease);
    // The color to draw instead of the theme's accent, or null when no module asked for one.
    public string? Current { get { lock (_gate) return _entries.Count > 0 ? _entries[^1].Color : null; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    // Raised after the stack changed through a module (a request or a release); the app repaints the panels.
    public event Action? Changed;

    public AccentOverride Add(string moduleId, string color)
    {
        var lease = new AccentOverride(this, color);
        lock (_gate) _entries.Add(new Entry(moduleId, color, lease));
        Changed?.Invoke();
        return lease;
    }
    internal bool Remove(AccentOverride lease)
    {
        lock (_gate) return _entries.RemoveAll(entry => ReferenceEquals(entry.Lease, lease)) > 0;
    }
    // The user changed the theme: every request ends, and their own colors show.
    public bool Clear()
    {
        AccentOverride[] ended;
        lock (_gate)
        {
            if (_entries.Count == 0) return false;
            ended = [.. _entries.Select(entry => entry.Lease).OfType<AccentOverride>()];
            _entries.Clear();
        }
        foreach (var lease in ended) lease.End();
        Changed?.Invoke();
        return true;
    }
    // A module stopped: its requests end, and the stack behind them shows again.
    public bool ClearModule(string moduleId)
    {
        AccentOverride[] ended;
        lock (_gate)
        {
            ended = [.. _entries.Where(entry => entry.ModuleId == moduleId).Select(entry => entry.Lease).OfType<AccentOverride>()];
            if (ended.Length == 0) return false;
            _entries.RemoveAll(entry => entry.ModuleId == moduleId);
        }
        foreach (var lease in ended) lease.End();
        Changed?.Invoke();
        return true;
    }
    // A hex color the panel can draw; null when the text is not one.
    public static string? Normalize(string? color)
        => ThemeColor.TryParse(color?.Trim(), out var parsed) ? parsed.ToString() : null;

    public sealed class AccentOverride(AccentOverrides owner, string color) : ModuleAccentOverride
    {
        private int _ended;
        public string Color { get; } = color;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            if (owner.Remove(this)) owner.Changed?.Invoke();
        }
        // The end the stack itself brings (a theme change, the module stopping): the entry is already gone from the stack.
        internal void End() => Interlocked.Exchange(ref _ended, 1);
    }
}

// The service a module receives for host.control: bound to one activation, so each action checks that activation's own
// declarations and lifetime, and every accent request is released with it.
internal sealed class HostControlService(App app, ModuleServiceScope scope, ModuleManifest manifest) : IModuleHostControl
{
    public IModulePageControl Pages { get; } = new PageControl(app, scope, manifest);
    public IModulePanelControl Panel { get; } = new PanelControl(app, scope, manifest);
    public IModuleMediaControl Media { get; } = new MediaControl(app, scope, manifest);
    public IModuleAccentControl Accent { get; } = new AccentControl(app, scope, manifest);

    private static void Require(ModuleManifest manifest, string capability)
    {
        if (!ModulePolicy.Declares(manifest, capability))
            throw new ModuleApiException("capability-not-declared", "Declare capability " + capability);
    }
    private sealed class PageControl(App app, ModuleServiceScope scope, ModuleManifest manifest) : IModulePageControl
    {
        public Task<ModuleHostResult> OpenOwnPageAsync(string pageId, CancellationToken cancellation = default)
        {
            scope.RequireLive(); Require(manifest, ModuleHostControlActions.Page);
            ArgumentNullException.ThrowIfNull(pageId);
            if (!scope.Ready) return Task.FromResult(ModuleHostResult.Rejected("module-not-ready"));
            // The page has to be one this module registered; the host never navigates elsewhere on a module's behalf.
            bool owns = app.Modules.Registry.List<ModulePage>().Any(entry => entry.Id == pageId && entry.ModuleId == scope.ModuleId);
            if (!owns) return Task.FromResult(ModuleHostResult.Rejected("page-not-own"));
            return Task.FromResult(app.OpenOwnModulePage(scope.ModuleId, pageId, userInitiated: true)
                ? ModuleHostResult.Completed : ModuleHostResult.Rejected("panel-unavailable"));
        }
    }
    private sealed class PanelControl(App app, ModuleServiceScope scope, ModuleManifest manifest) : IModulePanelControl
    {
        public Task<ModuleHostResult> RequestExpandAsync(ModuleCommandOrigin origin, CancellationToken cancellation = default)
        {
            scope.RequireLive(); Require(manifest, ModuleHostControlActions.Expand);
            if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
            // Only a user action may take the screen. A timer, an event or a state callback is refused, however often it asks.
            if (origin != ModuleCommandOrigin.User) return Task.FromResult(ModuleHostResult.Rejected("user-initiated-only"));
            if (!scope.Ready) return Task.FromResult(ModuleHostResult.Rejected("module-not-ready"));
            return Task.FromResult(app.RequestModuleExpand(scope.ModuleId) ? ModuleHostResult.Completed : ModuleHostResult.Rejected("panel-unavailable"));
        }
    }
    private sealed class MediaControl(App app, ModuleServiceScope scope, ModuleManifest manifest) : IModuleMediaControl
    {
        public async Task<ModuleHostResult> ToggleMediaPlayPauseAsync(CancellationToken cancellation = default)
        {
            scope.RequireLive(); Require(manifest, ModuleHostControlActions.Media);
            if (!scope.Ready) return ModuleHostResult.Rejected("module-not-ready");
            return await app.ToggleModuleMediaPlayPauseAsync(scope.Lifetime, cancellation);
        }
    }
    private sealed class AccentControl(App app, ModuleServiceScope scope, ModuleManifest manifest) : IModuleAccentControl
    {
        public ModuleAccentResult RequestAccentOverride(string color)
        {
            scope.RequireLive(); Require(manifest, ModuleHostControlActions.Accent);
            if (AccentOverrides.Normalize(color) is not { } normalized) return new(ModuleHostResultKind.Rejected, null, "invalid-color");
            if (!scope.Ready) return new(ModuleHostResultKind.Rejected, null, "module-not-ready");
            var lease = app.RequestModuleAccent(scope.ModuleId, normalized);
            // The request ends with the activation even if the module never disposes it.
            scope.OnDispose(() => lease.Dispose());
            return new(ModuleHostResultKind.Completed, lease);
        }
    }
}

public partial class App
{
    // The panel's temporary accent, as modules asked for it (host.control.accent). The stack is the order of precedence; the
    // panel draws only the top one, and every request ends with its module's activation.
    internal AccentOverrides ModuleAccents { get; } = new();

    internal ModuleAccentOverride RequestModuleAccent(string moduleId, string color)
    {
        if (Exiting || Dispatcher.HasShutdownStarted) throw new ModuleApiException("panel-unavailable", "The panel is not running.");
        var lease = ModuleAccents.Add(moduleId, color);
        ApplyModuleAccent();
        return lease;
    }
    // A user choosing a theme (or the system switching one) ends every module's request: the user's own colors always win.
    internal void ClearModuleAccents()
    {
        if (!ModuleAccents.Clear()) return;
        ApplyPanelTheme(reload: false);
    }
    // Draws the accent the stack currently asks for, or the theme's own when it is empty. Called after a request or a release.
    internal void ApplyModuleAccent()
    {
        if (Exiting || Dispatcher.HasShutdownStarted) return;
        void Apply()
        {
            if (Exiting) return;
            PanelTheme.AccentOverride = ModuleAccents.Current;
            // Use() resolves a fresh palette when the accent differs, and a repaint follows; with nothing to change it does nothing.
            ApplyPanelTheme(drawAccent: true);
        }
        if (Dispatcher.CheckAccess()) Apply(); else Dispatcher.BeginInvoke(Apply);
    }
}
