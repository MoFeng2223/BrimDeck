using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using BrimDeck.Core;
using BrimDeck.Modules;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private bool _moduleControls, _moduleClosing, _moduleKnownActive, _moduleAdjustmentHeld;
    private ModuleViewHost? _modulePage, _moduleExpandedView, _moduleCompactView;
    private Contribution<ModulePage>? _modulePageEntry;
    private Contribution<IModuleActivity>? _moduleActivity;
    private readonly List<(IModuleActivity Activity, string Owner, Action Handler)> _moduleWatches = [];
    private string? _preferredModuleActivityId;
    private bool _moduleOpening;
    private ContextMenu? _modulePageMenu;
    private int _moduleCompletion, _modulePresentationVersion;
    private readonly Grid _moduleLeaving = new() { IsHitTestVisible = false };
    private PageHandover? _moduleHandover;
    private readonly BlurEffect _moduleBlur = new() { Radius = 0, RenderingBias = RenderingBias.Performance };
    private readonly ScaleTransform _moduleScale = new(1, 1);
    private PromptTimer? _modulePresentationEnd, _moduleCompletionTimer;
    private ActivityState ActivityState => _moduleActivity is { } item
        ? _app.Modules.Invoke(item.ModuleId, "activity state", () => NormalizeActivity(item.Value.State)) ?? new(false, 0, false, UI.Accent)
        : new(false, 0, false, UI.Accent);
    private static ActivityState NormalizeActivity(ActivityState state)
    { _ = UI.Brush(state.Accent); return state with { Progress = double.IsFinite(state.Progress) ? Math.Clamp(state.Progress, 0, 1) : 0 }; }
    internal bool ModuleActivityPresented => !_preview && Settings.Modules.Enabled && ActivityState.Active;
    // Whether the presented activity takes the whole collapsed row (replacing the built-in readings), as opposed to a
    // passive activity that offers itself as one part beside them. Everything that decides whether the built-in row
    // content is suppressed asks this, not ModuleActivityPresented: a row part stands beside the readings, it does not
    // replace them.
    internal bool ModuleActivityTakesRow => ModuleActivityPresented && !ActivityState.Passive;
    private bool ModuleControlsVisible => _transient is null && (_moduleClosing || _moduleControls && ModuleActivityPresented);
    private ModuleViewContext ModuleContext => new(this, OpenModulePage, _app.OpenModuleSettings, ShowModuleActivity);
    private void ShowModuleActivity(string id)
    {
        if (!_app.Modules.Registry.List<IModuleActivity>().Any(e => e.Id == id && _app.Modules.Invoke(e.ModuleId, "activity state", () => e.Value.State.Active))) return;
        _preferredModuleActivityId = id; SelectModuleActivity(); ShowModuleControls();
    }

    private void InitializeModuleViews()
    {
        IsVisibleChanged += (_, _) => RefreshModulePresentation();
        StateChanged += (_, _) => RefreshModulePresentation();
        ModuleControlHost.Effect = _moduleBlur; ModuleControlHost.RenderTransform = _moduleScale; ModuleControlHost.RenderTransformOrigin = new(.5, .5);
        ExpandedContent.Children.Add(_moduleLeaving); _moduleHandover = new(_moduleLeaving);
        RefreshModuleContributions();
        Closed += (_, _) =>
        {
            ClearModuleWatches(); ReleaseModulePage(); ReleaseModuleActivityViews();
            _modulePresentationEnd?.Stop(); _moduleCompletionTimer?.Stop(); _moduleHandover?.Drop();
        };
    }
    private void RefreshModulePresentation()
    {
        bool shown = IsVisible && WindowState != WindowState.Minimized && !IsSuppressed && !_preview && !_poweringOff;
        bool controls = ModuleControlsVisible && ModuleActivityPresented;
        _modulePage?.SetPresented(shown && _expanded && IsMiniAppsPage && !controls && _transient is null && ContentCanvas.Visibility == Visibility.Visible);
        _moduleExpandedView?.SetPresented(shown && _expanded && controls && _transient is null);
        _moduleCompactView?.SetPresented(shown && !_expanded && ModuleActivityPresented && EffectiveStyle != CompactStyle.Line
            && _alert is null && _transientNotice is null && _transient is null && ModuleCompact.Visibility == Visibility.Visible);
    }
    private void ClearModuleWatches()
    {
        foreach (var (activity, owner, handler) in _moduleWatches)
            _app.Modules.Invoke(owner, "unsubscribe activity", () => { activity.Changed -= handler; return true; });
        _moduleWatches.Clear();
    }
    internal void RefreshModuleContributions()
    {
        var entries = _app.Modules.Registry.List<IModuleActivity>();
        if (!entries.Select(e => e.Value).SequenceEqual(_moduleWatches.Select(w => w.Activity)))
        {
            ClearModuleWatches();
            foreach (var entry in entries)
            {
                Action handler = () => { if (Dispatcher.CheckAccess()) ModuleActivityChanged(); else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ModuleActivityChanged); };
                _app.Modules.Invoke(entry.ModuleId, "subscribe activity", () => { entry.Value.Changed += handler; return true; });
                _moduleWatches.Add((entry.Value, entry.ModuleId, handler));
            }
        }
        SelectModuleActivity(); ModuleActivityChanged();
        if (_modulePageEntry is not null && !_app.Modules.Registry.List<ModulePage>().Any(e => ReferenceEquals(e, _modulePageEntry))) ReleaseModulePage();
        if (IsMiniAppsPage) RenderMiniApps();
        if (IsLoaded) ApplySettings();
    }
    private void SelectModuleActivity()
    {
        var entries = _app.Modules.Registry.List<IModuleActivity>();
        bool Active(Contribution<IModuleActivity> entry) => _app.Modules.Invoke(entry.ModuleId, "activity state", () => entry.Value.State.Active);
        var selected = entries.FirstOrDefault(e => e.Id == _preferredModuleActivityId && Active(e)) ?? entries.FirstOrDefault(Active) ?? entries.FirstOrDefault();
        if (ReferenceEquals(selected, _moduleActivity)) return;
        ReleaseModuleActivityViews(); _moduleActivity = selected; _moduleCompletion = ActivityState.Completion;
    }
    private void ReleaseModuleActivityViews()
    {
        _moduleExpandedView?.Dispose(); _moduleCompactView?.Dispose(); _moduleExpandedView = _moduleCompactView = null;
        ModuleControlHost.Children.Clear(); ModuleCompact.Children.Clear();
        ReleaseModuleParts();
    }
    // The passive activities' readings as parts of the collapsed row: every passive activity whose compact view
    // declares a width (IModuleCompactWidth) stands beside the built-in readings, in the user's module order and under
    // the module's own switch. The hosts are kept for the session — built once per activity generation — and the row
    // only detaches them from the old row's grid before adopting them: releasing on every rebuild would dispose and
    // re-mount the views, which showed as the readings blinking out while the surface was dragged or resized.
    private sealed class ModulePart(Contribution<IModuleActivity> owner, ModuleViewHost host)
    {
        internal Contribution<IModuleActivity> Owner { get; } = owner;
        internal ModuleViewHost Host { get; } = host;
        internal double Width { get; set; }
        internal bool Placed { get; set; }
        internal bool Dropped { get; set; }
    }
    private readonly List<ModulePart> _moduleParts = [];
    private (IReadOnlyList<ModulePart> Parts, double Face) ModuleCompactParts(double scale)
    {
        // A row part is not "the selected activity": every passive activity that offers a width stands in the row,
        // whether or not it is the one the control bar would show. Selecting one activity for the bar is a separate
        // question (SelectModuleActivity), and picking a running task there must not hide the readings beside it.
        var sources = _app.Modules.Registry.List<IModuleActivity>()
            .Where(e => _app.Modules.Invoke(e.ModuleId, "activity state", () => e.Value.State is { Active: true, Passive: true }))
            .ToList();
        if (sources.Count == 0) { ReleaseModuleParts(); return ([], 0); }
        // The modules that can offer a part, in the user's saved order then registration order: a module the file has
        // never seen joins at the end, so installing one is enough for it to appear.
        var order = Settings.ModulePartOrder(EffectiveStyle, sources.Select(e => e.ModuleId)).ToList();
        sources = [.. sources.OrderBy(e => order.IndexOf(e.ModuleId))];
        // Generations that went away (a module disabled, its activity stopped) lose their host with their state.
        foreach (var stale in _moduleParts.Where(part => !sources.Any(e => ReferenceEquals(e, part.Owner))).ToList())
        { CompactModuleLayer.Children.Remove(stale.Host); stale.Host.Dispose(); _moduleParts.Remove(stale); }
        double face = 0;
        var parts = new List<ModulePart>(sources.Count);
        foreach (var source in sources)
        {
            var part = _moduleParts.FirstOrDefault(item => ReferenceEquals(item.Owner, source));
            if (part is null)
            {
                var module = source.Value;
                var host = new ModuleViewHost(_app.Modules, source.ModuleId, ModuleContext, context => module.CreateView(context, ActivityViewKind.Compact));
                // The host lives in CompactModuleLayer, which never leaves the tree, so its view keeps reporting itself
                // on show across row rebuilds; the row only moves and sizes it.
                CompactModuleLayer.Children.Add(host);
                // The view arrives after the host is built, and the row is laid out before that: rebuild the collapsed
                // row once it does, so its declared width takes part.
                host.Mounted += () => { if (!_expanded && IsLoaded) RenderCompact(); };
                part = new ModulePart(source, host);
                _moduleParts.Add(part);
            }
            part.Width = 0; part.Placed = false; part.Dropped = false;
            // A view that has not mounted yet (or that does not offer a width) takes no part; the host stays for the
            // next rebuild rather than being thrown away.
            if (part.Host.View is not IModuleCompactWidth declared || declared.PreferredWidth is not { } wanted) continue;
            part.Width = wanted;
            face += wanted;
            parts.Add(part);
        }
        // A part the module's own switch turns off is not placed and takes no room, but its host stays mounted so the
        // switch brings it straight back.
        _modulePartFace = face;
        return (parts, face);
    }
    // The row is about to be rebuilt: the parts are not taken out of the tree with it (they live in CompactModuleLayer),
    // so nothing to detach — the new row places and sizes them again through PlaceModulePart. The layer's own size goes
    // back to nothing so this build's parts start their own span.
    private void ClearModulePartForRow()
    {
        CompactModuleLayer.Visibility = Visibility.Collapsed;
        CompactModuleLayer.Width = 0; CompactModuleLayer.Height = 0;
        foreach (var part in _moduleParts) { part.Placed = false; part.Host.Visibility = Visibility.Collapsed; part.Host.SetPresented(false); }
    }
    // Places one module's part at the room the arrangement gave it. A part the row had no room for stays hidden and says
    // so in the settings.
    private void PlaceModulePart(string moduleId, double left, double width, double height)
    {
        var part = _moduleParts.FirstOrDefault(item => item.Owner.ModuleId == moduleId);
        if (part is null || width <= 0) return;
        // The layer is hidden before each rebuild (ClearModulePartForRow); the first part placed shows it again.
        CompactModuleLayer.Visibility = Visibility.Visible;
        double layerWidth = double.IsFinite(CompactModuleLayer.Width) ? CompactModuleLayer.Width : 0;
        double layerHeight = double.IsFinite(CompactModuleLayer.Height) ? CompactModuleLayer.Height : 0;
        CompactModuleLayer.Width = Math.Max(layerWidth, left + width);
        CompactModuleLayer.Height = Math.Max(layerHeight, height);
        part.Host.Visibility = Visibility.Visible;
        part.Host.Width = width; part.Host.Height = height;
        part.Host.HorizontalAlignment = HorizontalAlignment.Left; part.Host.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(part.Host, Math.Max(0, left)); Canvas.SetTop(part.Host, 0);
        part.Host.SetViewport(width, 24, Math.Clamp(_modulePartScale, .75, 2));
        part.Host.SetPresented(true);
    }
    private double _modulePartScale = 1, _modulePartFace;
    // Whether any module reading stood in the row at its last build (the settings page says which ones did not, and why).
    private bool _compactModulesShown;
    // Which modules offer a part, and whether the row had room: the settings page lists them, with the reason when one
    // is not shown (the #78 plan's rule for the collapsed row).
    internal bool ModulePartWanted => _moduleParts.Count > 0;
    internal double ModulePartFace => _modulePartFace;
    internal IReadOnlyList<string> ModulePartIds => [.. _moduleParts.Select(part => part.Owner.ModuleId)];
    internal bool ModulePartShown(string moduleId) => _moduleParts.FirstOrDefault(part => part.Owner.ModuleId == moduleId) is { Placed: true };
    internal string ModulePartName(string moduleId) => _app.Modules.Modules.FirstOrDefault(m => m.Id == moduleId)?.Name ?? moduleId;
    private void ReleaseModuleParts()
    {
        foreach (var part in _moduleParts) { CompactModuleLayer.Children.Remove(part.Host); part.Host.Dispose(); }
        _moduleParts.Clear();
        CompactModuleLayer.Visibility = Visibility.Collapsed;
        _modulePartFace = 0;
    }
    private void ReleaseModulePage()
    { if (_modulePageMenu is not null) _modulePageMenu.IsOpen = false; _modulePageMenu = null; _modulePage?.Dispose(); _modulePage = null; _modulePageEntry = null; MiniAppsContent.Children.Clear(); }
    private void DismissExpandedFromEscape()
    {
        if (_moveArmed) { CancelMove(); return; }
        if (IsMiniAppsPage && !ModuleControlsVisible && _modulePage?.CancelAdjustment() == true) return;
        SetExpanded(false, keyboard: true);
    }
    private bool HoldModuleAdjustment()
    {
        if (IsMiniAppsPage && !ModuleControlsVisible && _modulePage?.IsAdjusting == true)
        { _moduleAdjustmentHeld = true; _leave.Interval = TimeSpan.FromMilliseconds(80); return true; }
        if (!_moduleAdjustmentHeld) return false;
        _moduleAdjustmentHeld = false; _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.CloseDelay)); return true;
    }
    private void MiniAppsPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.MiniApps);
    internal void RefreshMiniAppsPreferences() => _modulePage?.Refresh();
    private void RenderMiniApps()
    {
        UsageContent.Visibility = MusicContent.Visibility = MonitorContent.Visibility = Visibility.Collapsed;
        MiniAppsContent.Visibility = Visibility.Visible; ModelToolbar.Visibility = ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        ApplyHeader(PanelHeader.For(ToolbarFor(ContentCanvas.Height), 20));
        double bottom = ContentCanvas.Height < 180 ? 8 : 10;
        MiniAppsContent.Margin = new Thickness(_header.Side, 0, _header.Side, bottom);
        var selected = _app.Modules.Registry.List<ModulePage>().FirstOrDefault(e => e.Id == CurrentPageId);
        if (!ReferenceEquals(selected, _modulePageEntry) || _modulePage is null)
        {
            ReleaseModulePage(); _modulePageEntry = selected;
            if (selected is not null)
            {
                // One page per stable navigation entry; language and theme updates keep this mount.
                _modulePage = new(_app.Modules, selected.ModuleId, ModuleContext, context => selected.Value.CreateView(context), selected.Value.CreateViewAsync);
                MiniAppsContent.Children.Add(_modulePage);
            }
            else MiniAppsContent.Children.Add(UI.Centered(Loc.T("没有已启用的小程序，请在模组设置中启用。", "Enable a mini app in module settings."), 13, UI.Secondary));
        }
        _modulePage?.SetViewport(new ModuleViewport(ContentCanvas.Width - 2 * _header.Side, ContentCanvas.Height - _header.Row - bottom) { DeviceScale = _pixelsPerDip, ContentScale = _scale });
        RefreshModulePresentation(); _modulePage?.Refresh(); RefreshStatus();
    }
    internal void OpenModulePageFromSettings(string id) => OpenModulePage(id);
    private bool AllowModuleOpening<T>(T opening) where T : ModuleOpeningEvent
    {
        if (_moduleOpening) return false;
        _moduleOpening = true;
        try { _app.Modules.Publish(opening); return !opening.Cancelled; }
        finally { _moduleOpening = false; }
    }
    private void OpenModulePage(string id)
    {
        if (_transient is not null || _app.Modules.Registry.List<ModulePage>().FirstOrDefault(e => e.Id == id) is not { } requested) return;
        if (!AllowModuleOpening(new ModulePageOpening(id)) || !_app.Modules.Registry.List<ModulePage>().Any(e => ReferenceEquals(e, requested))) return;
        if (PageDirectory.Find(id) is not { } definition || !PageDirectory.Shown(definition)) return;
        _moduleHandover?.Drop();
        var leaving = PageHandover.Capture(ModuleControlHost);
        _moduleControls = false; _moduleClosing = false;
        SelectPage(id); RenderMiniApps(); SetExpanded(true);
        if (leaving is not null) _handover.Send(leaving, new Thickness(0), MiniAppsContent, Handover.Morph);
    }
    internal void ShowModuleControls()
    {
        if (!ModuleActivityPresented || IsSuppressed || _transient is not null) return;
        if (_moduleActivity is not { } requested) return;
        if (!AllowModuleOpening(new ModuleActivityOpening(requested.Id)) || !ModuleActivityPresented || !ReferenceEquals(requested, _moduleActivity)
            || !_app.Modules.Registry.List<IModuleActivity>().Any(e => ReferenceEquals(e, requested))) return;
        _modulePresentationEnd?.Stop(); ++_modulePresentationVersion; _moduleClosing = false; _moduleHandover?.Drop();
        var leaving = _expanded ? PageHandover.Capture(ExpandedContent) : null;
        _moduleControls = true; _openCentre = null; BuildModuleControls();
        _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _pageSwitchHold.Start(); SetExpanded(true); _hotkeyPinned = true;
        if (leaving is not null) _moduleHandover?.Send(leaving, new Thickness(0), ModuleControlHost, Handover.Morph);
        ModuleActivityArrival();
    }
    private void ModuleActivityChanged()
    {
        SelectModuleActivity(); bool active = ModuleActivityPresented;
        if (_moduleKnownActive != active)
        {
            bool wasControls = ModuleControlsVisible || _moduleControls; _moduleKnownActive = active;
            _modulePresentationEnd?.Stop(); ++_modulePresentationVersion; _moduleClosing = false;
            if (!active)
            {
                MusicIndicator.Background = UI.Brush(PanelTheme.Hex("indicatorProgress"));
                if (wasControls && _expanded)
                {
                    _moduleClosing = true; _moduleControls = false; RenderCompact(); SetExpanded(false);
                    int version = _modulePresentationVersion;
                    _modulePresentationEnd = new(Dispatcher, () => { _modulePresentationEnd?.Stop(); if (version != _modulePresentationVersion) return; _moduleClosing = false; ApplyModuleControlsVisibility(); }) { Interval = TimeSpan.FromMilliseconds(Soft.Enabled ? Settings.AnimationDuration + 20 : 1) };
                    _modulePresentationEnd.Start();
                }
                else _moduleControls = false;
            }
            UpdateCollapsedGeometry(); RenderCompact(); if (!_expanded && !_moduleClosing) SetExpanded(false);
        }
        if (active && _moduleExpandedView is null) BuildModuleControls();
        // A part's first mount lands after the row was built without it: the module's next Changed (which is what
        // brought us here) rebuilds the row so the readings join it. Once mounted, later updates go through the view's
        // own redraw, so no rebuild is needed for every reading.
        if (active && !_expanded && _moduleParts.Any(part => part.Host.View is null)) RenderCompact();
        var state = ActivityState;
        if (_moduleCompletion != state.Completion)
        {
            _moduleCompletion = state.Completion;
            if (ModuleControlsVisible) ModuleActivityArrival();
            else if (!_expanded && _transient is null && _transientNotice is null) PageHandover.Appear(ModuleCompact);
            _moduleCompletionTimer ??= new(Dispatcher, () => { _moduleCompletionTimer?.Stop(); UpdateModuleDisplays(); }) { Interval = TimeSpan.FromMilliseconds(920) }; _moduleCompletionTimer.Start();
        }
        UpdateModuleDisplays();
    }
    private void BuildModuleControls()
    {
        if (_moduleExpandedView is not null || _moduleActivity is not { } entry) return;
        _moduleExpandedView = new(_app.Modules, entry.ModuleId, ModuleContext, context => entry.Value.CreateView(context, ActivityViewKind.Expanded),
            entry.Value is IModuleAsyncActivity asyncActivity ? context => asyncActivity.CreateViewAsync(context, ActivityViewKind.Expanded) : null);
        ModuleControlHost.Children.Add(_moduleExpandedView); UpdateModuleDisplays();
    }
    private void ApplyModuleControlsVisibility()
    {
        bool controls = ModuleControlsVisible; if (controls) BuildModuleControls();
        ModuleControlHost.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;
        _moduleExpandedView?.SetViewport(new ModuleViewport(320, 76) { DeviceScale = _pixelsPerDip, HorizontalInset = Settings.Style == CompactStyle.Notch ? 30 : 18, CornerRadius = Settings.Style == CompactStyle.Notch ? 18 : 38 });
        if (_transient is null) { ContentCanvas.Visibility = controls ? Visibility.Hidden : Visibility.Visible; Toolbar.Visibility = controls ? Visibility.Hidden : Visibility.Visible; }
        RefreshModulePresentation();
    }
    private void UpdateModuleDisplays()
    { RefreshModulePresentation(); _moduleExpandedView?.Refresh(); _moduleCompactView?.Refresh(); _modulePage?.Refresh(); RenderModuleIndicator(); }
    private bool RenderModuleIndicator()
    {
        if (!ModuleActivityPresented || _expanded || EffectiveStyle != CompactStyle.Line || _alert is not null || _transientNotice is not null || _transient is not null) return false;
        var state = ActivityState; MusicIndicator.Visibility = Visibility.Visible; MusicIndicator.Background = UI.Brush(state.Accent);
        MusicIndicator.Width = Collapsed.Surface.Width * (state.Indeterminate ? 1 : Math.Clamp(state.Progress, 0, 1)); return true;
    }
    private void RenderModuleCompact()
    {
        CompactRings.Visibility = CompactAlert.Visibility = Visibility.Hidden;
        _quotaCarousel?.SetRunning(false, false); _stripCarousel?.SetRunning(false, false); _compactClock = null; _compactStrip = null;
        ModuleCompact.Visibility = Visibility.Visible;
        if (_moduleCompactView is null && _moduleActivity is { } entry)
        { _moduleCompactView = new(_app.Modules, entry.ModuleId, ModuleContext, context => entry.Value.CreateView(context, ActivityViewKind.Compact),
            entry.Value is IModuleAsyncActivity asyncActivity ? context => asyncActivity.CreateViewAsync(context, ActivityViewKind.Compact) : null); ModuleCompact.Children.Add(_moduleCompactView); }
        _moduleCompactView?.SetViewport(Math.Max(1, CompactContentWidth(Collapsed)), 24, Math.Clamp(Collapsed.Scale, .75, 2));
        UpdateModuleDisplays(); UpdateMonitorDemand();
    }
    private void ModuleActivityArrival()
    {
        if (!Soft.Enabled) return; int duration = Settings.AnimationDuration;
        var reveal = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(duration), FillBehavior = FillBehavior.Stop };
        reveal.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        reveal.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * .15))));
        reveal.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * .8)), Soft.Out));
        ModuleControlHost.BeginAnimation(OpacityProperty, reveal);
        _moduleBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(duration * .8)) { EasingFunction = Soft.Out });
        _moduleScale.BeginAnimation(ScaleTransform.ScaleXProperty, Soft.Fade(.92, 1, duration)); _moduleScale.BeginAnimation(ScaleTransform.ScaleYProperty, Soft.Fade(.92, 1, duration));
    }
    private void AnimateModuleContents(bool expanded, int duration, Track content, Track compact)
    {
        if (!ModuleActivityPresented || !ModuleControlsVisible) return;
        if (duration == 0) SetImmediate(ModuleControlHost, OpacityProperty, 1d);
        Animate(_moduleBlur, BlurEffect.RadiusProperty, expanded ? 0 : 6, duration, content, Soft.Out);
        Animate(_moduleScale, ScaleTransform.ScaleXProperty, expanded ? 1 : .92, duration, content, Soft.Out);
        Animate(_moduleScale, ScaleTransform.ScaleYProperty, expanded ? 1 : .92, duration, content, Soft.Out);
    }
}
