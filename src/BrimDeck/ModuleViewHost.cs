using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Modules;
using BrimDeck.Core;

namespace BrimDeck;

// Own the context before invoking plugin code, including while an asynchronous factory is pending.
internal sealed class ModuleViewHost : Grid, IDisposable
{
    private sealed class Claim(ModuleViewHost owner)
    { internal readonly WeakReference<ModuleViewHost> Owner = new(owner); internal bool Disposed; internal Dispatcher? Dispatcher; }
    private static readonly ConditionalWeakTable<IModuleView, Claim> Claims = new();
    private static readonly object ClaimGate = new();
    private readonly ModuleHost _host;
    private readonly string _owner;
    private readonly ModuleViewContext _context;
    private readonly ModuleViewLease? _lease;
    private readonly Grid _content = new(), _leaving = new() { IsHitTestVisible = false };
    private readonly PageHandover _handover;
    private IModuleView? _view;
    private IDisposable? _lifetime;
    private ModuleViewport? _viewport;
    private bool _disposed, _cleaning;
    private bool _presented;
    private readonly bool _preview;
    private PanelPalette? _localPalette;
    private ResourceDictionary? _themeResources;
    private int _themeGeneration = -1;
    internal bool IsLoading { get; private set; }
    internal bool IsPresented => _context.IsVisible;
    private bool Live => !_disposed && !_context.Lifetime.IsCancellationRequested && _lease?.Current == true;

    internal ModuleViewHost(ModuleHost host, string owner, ModuleViewContext context, Func<ModuleViewContext, IModuleView> create,
        Func<ModuleViewContext, ValueTask<IModuleView>>? createAsync = null, PanelPalette? palette = null, bool preview = false)
    {
        _host = host; _owner = owner; _context = context; _lease = host.CaptureView(owner); _preview = preview; _localPalette = palette;
        _handover = new(_leaving); Children.Add(_content); Children.Add(_leaving);
        IsVisibleChanged += (_, _) => UpdatePresentation();
        Loaded += (_, _) => UpdatePresentation();
        Unloaded += (_, _) => _context.SetVisible(false);
        if (_lease is null) { _context.Dispose(); ShowMessage(Loc.T("此模组当前未运行。", "This module is not running.")); return; }
        context.Fault = error => { if (!_disposed || _cleaning) _lease.Report("view resource", error); };
        foreach (var dictionary in context.Owner.Resources.MergedDictionaries) Resources.MergedDictionaries.Add(dictionary);
        foreach (var key in context.Owner.Resources.Keys) Resources[key] = context.Owner.Resources[key];
        SetResourceReference(BackgroundProperty, "PanelSurface");
        TextElement.SetFontFamily(this, UI.PanelFont);
        context.Attach(this, owner); context.BindLifetime(_lease.Lifetime);
        UpdateAppearance();
        context.OnDispose(DisposeOnDispatcher);
        context.RunCallback = callback => { if (Live) _lease.Invoke("view timer", callback); };
        context.RunVisibilityCallback = callback => _lease.Invoke("view visibility", callback);
        _lifetime = _lease.Own(new ModuleAction(DisposeOnDispatcher));
        if (!Live) { Dispose(); return; }
        try
        {
            if (createAsync is null) CompleteMount(_host.Measure(owner, "mount view", () => create(context)), false);
            else
            {
                IsLoading = true; ShowMessage(Loc.T("正在加载…", "Loading…"));
                var pending = _host.Measure(owner, "mount view", () => createAsync(context));
                if (pending.IsCompleted) CompleteMount(pending.GetAwaiter().GetResult(), false);
                else _ = AwaitMountAsync(pending);
            }
        }
        catch (Exception error) { MountFailed(error); }
    }
    private void ShowMessage(string message)
    {
        _content.Children.Clear();
        var text = UI.Centered(message, 12, UI.Secondary); text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
        AutomationProperties.SetName(_content, message); _content.Children.Add(text);
    }
    private async Task AwaitMountAsync(ValueTask<IModuleView> pending)
    {
        IModuleView? view = null;
        try
        {
            view = await pending.ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { DisposeLateView(view); return; }
            await Dispatcher.InvokeAsync(() => CompleteMount(view, true));
        }
        catch (Exception error)
        {
            if (view is not null) DisposeLateView(view);
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) await Dispatcher.InvokeAsync(() => MountFailed(error));
        }
    }
    private bool ClaimView(IModuleView view)
    { lock (ClaimGate) { if (Claims.TryGetValue(view, out _)) return false; Claims.Add(view, new(this)); return true; } }
    private void CompleteMount(IModuleView view, bool animate)
    {
        if (view is null) throw new ModuleApiException("invalid-view", "A view factory returned null.");
        if (!ClaimView(view)) throw new ModuleApiException("view-already-owned", "A view instance cannot be reused across mounts.");
        if (!Live) { DisposeClaimedView(view, false); return; }
        _view = view;
        var element = view.Element ?? throw new ModuleApiException("invalid-view", "A view needs a root element.");
        lock (ClaimGate) Claims.GetValue(view, _ => new(this)).Dispatcher = element.Dispatcher;
        if (element.Dispatcher != Dispatcher) throw new ModuleApiException("view-thread-mismatch", "Create view elements on the context owner's dispatcher.");
        if (VisualTreeHelper.GetParent(element) is not null || LogicalTreeHelper.GetParent(element) is not null)
        {
            // An invalid second wrapper must not dispose another mount's root.
            _view = null; lock (ClaimGate) Claims.GetValue(view, _ => new(this)).Disposed = true;
            throw new ModuleApiException("view-element-already-owned", "The view root is already parented.");
        }
        var leaving = animate && Soft.Enabled ? PageHandover.Capture(_content) : null;
        _content.Children.Clear(); _content.Children.Add(element);
        if (_viewport is { } viewport) ApplyViewport(viewport);
        view.Refresh(); _context.CompleteMount(); IsLoading = false;
        UpdatePresentation();
        AutomationProperties.SetName(_content, "");
        if (animate) { if (leaving is not null) _handover.Send(leaving, new Thickness(0), element, Handover.Morph); else PageHandover.Appear(element); }
    }
    private void MountFailed(Exception error)
    {
        if (!Live) return;
        _lease!.Report("mount view", error);
        ReleaseMount(); ShowMessage(Loc.T("此模组界面暂时不可用，可在模组设置查看原因。", "This module view is unavailable. See module settings for details."));
    }
    private T? Query<T>(string operation, Func<T> run)
    {
        if (!Live || _view is null) return default;
        try { return _host.Measure(_owner, operation, run); } catch (Exception error) { _lease!.Report(operation, error); return default; }
    }
    internal bool IsAdjusting => Query("view input", () => _view!.IsAdjusting);
    internal void SetPresented(bool presented)
    { _presented = presented; UpdatePresentation(); }
    private void UpdatePresentation()
    {
        bool before = _context.IsVisible;
        _context.SetVisible(_presented && IsVisible && Live && !IsLoading && _view is not null);
        if (!before && _context.IsVisible) Refresh();
    }
    internal bool CancelAdjustment() => Query("cancel adjustment", () => _view!.CancelAdjustment());
    internal void Refresh()
    {
        UpdateAppearance(); Query("refresh view", () => { _view!.Refresh(); return true; });
    }
    internal void SetTheme(PanelPalette palette)
    {
        _localPalette = palette; _themeGeneration = -1; Refresh();
    }
    private void UpdateAppearance()
    {
        if (_disposed) return;
        var palette = _localPalette ?? PanelTheme.Current;
        if (_themeGeneration != PanelTheme.Generation || _themeResources is null)
        {
            var next = PanelTheme.CreateResources(palette);
            foreach (var key in next.Keys) Resources.Remove(key);
            int at = _themeResources is null ? -1 : Resources.MergedDictionaries.IndexOf(_themeResources);
            if (at < 0) Resources.MergedDictionaries.Add(next); else Resources.MergedDictionaries[at] = next;
            _themeResources = next; _themeGeneration = PanelTheme.Generation;
        }
        _context.SetAppearance(new(0, palette.IsDark, PanelTheme.HighContrast && !_preview, Loc.Language, _viewport?.DeviceScale ?? 1, _viewport?.ContentScale ?? 1, !Soft.Enabled, _preview));
    }
    internal void SetViewport(double width, double height, double scale = 1) => SetViewport(new ModuleViewport(width, height) { ContentScale = scale });
    internal void SetViewport(ModuleViewport viewport)
    {
        var normalized = viewport.Normalize(); if (_viewport == normalized) return; _viewport = normalized; UpdateAppearance();
        Query("layout view", () => { ApplyViewport(normalized); return true; });
    }
    private void ApplyViewport(ModuleViewport viewport)
    { if (_view is IModuleLayoutView layout) layout.SetViewport(viewport); else _view!.SetViewport(viewport.Width, viewport.Height, viewport.ContentScale); }
    private void DisposeClaimedView(IModuleView view, bool report)
    {
        Dispatcher? target;
        lock (ClaimGate)
        {
            if (!Claims.TryGetValue(view, out var claim) || !claim.Owner.TryGetTarget(out var owner) || !ReferenceEquals(owner, this) || claim.Disposed) return;
            target = claim.Dispatcher;
        }
        // Late results may never reach CompleteMount, which normally records the element dispatcher.
        // Do not call a plugin getter while holding the shared ownership lock.
        if (target is null)
        {
            try { target = view.Element?.Dispatcher; } catch { }
            target ??= Dispatcher;
        }
        lock (ClaimGate)
        {
            if (!Claims.TryGetValue(view, out var claim) || !claim.Owner.TryGetTarget(out var owner) || !ReferenceEquals(owner, this) || claim.Disposed) return;
            claim.Dispatcher ??= target; target = claim.Dispatcher;
            if (!target.CheckAccess())
            { if (!target.HasShutdownStarted && !target.HasShutdownFinished) _ = target.InvokeAsync(() => DisposeClaimedView(view, report)); return; }
            claim.Disposed = true;
        }
        try { view.Dispose(); } catch (Exception error) { if (report) _lease?.Report("dispose view", error); }
    }
    private void DisposeLateView(IModuleView view)
    {
        void Cleanup() { ClaimView(view); DisposeClaimedView(view, false); }
        if (Dispatcher.CheckAccess()) Cleanup();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished) _ = Dispatcher.InvokeAsync(Cleanup);
        else Cleanup(); // A surviving foreign dispatcher can still release its own late result.
    }
    private void ReleaseMount()
    {
        if (_disposed) return; _disposed = true; _cleaning = true; IsLoading = false; _handover.Drop();
        var lifetime = _lifetime; _lifetime = null; lifetime?.Dispose();
        var view = _view; _view = null; _content.Children.Clear();
        try { _context.Dispose(); } finally { if (view is not null) DisposeClaimedView(view, true); _cleaning = false; }
    }
    private void DisposeOnDispatcher()
    { if (_disposed) return; if (Dispatcher.CheckAccess()) Dispose(); else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(Dispose); }
    public void Dispose()
    { if (!Dispatcher.CheckAccess()) { DisposeOnDispatcher(); return; } ReleaseMount(); Children.Clear(); }
}
