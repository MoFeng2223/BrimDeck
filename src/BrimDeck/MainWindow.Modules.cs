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
            ClearModuleWatches(); ReleaseModulePage(); ReleaseModuleActivityViews(); ReleaseModuleParts();
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
        ActivityState? StateOf(Contribution<IModuleActivity> entry) => _app.Modules.Invoke(entry.ModuleId, "activity state", () => NormalizeActivity(entry.Value.State));
        var active = entries.Select(entry => (Entry: entry, State: StateOf(entry))).Where(item => item.State is { Active: true }).ToList();
        // A determined choice, not incidental registration order (the platform plan asks for it): the user's own pick
        // first; then a running task (an activity that is not Passive — a timer, say) before a presence-only reading,
        // since a control bar is what a task asks for; ties break on the contribution id, so the same set of modules
        // always picks the same one whatever order they loaded in.
        var selected = entries.FirstOrDefault(e => e.Id == _preferredModuleActivityId && active.Any(item => ReferenceEquals(item.Entry, e)))
            ?? active.OrderBy(item => item.State!.Passive ? 1 : 0).ThenBy(item => item.Entry.Id, StringComparer.Ordinal).Select(item => item.Entry).FirstOrDefault()
            ?? entries.OrderBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
        if (ReferenceEquals(selected, _moduleActivity)) return;
        ReleaseModuleActivityViews(); _moduleActivity = selected; _moduleCompletion = ActivityState.Completion;
    }
    private void ReleaseModuleActivityViews()
    {
        _moduleExpandedView?.Dispose(); _moduleCompactView?.Dispose(); _moduleExpandedView = _moduleCompactView = null;
        ModuleControlHost.Children.Clear(); ModuleCompact.Children.Clear();
        // The row's parts are a separate channel: an activity change (a timer starting or ending) must not take them down.
    }
    // The passive activities' readings as parts of the collapsed row: every passive activity whose compact view
    // declares a width (IModuleCompactWidth) stands beside the built-in readings, in the user's module order and under
    // the module's own switch. The hosts are kept for the session — built once per activity generation — and the row
    // only detaches them from the old row's grid before adopting them: releasing on every rebuild would dispose and
    // re-mount the views, which showed as the readings blinking out while the surface was dragged or resized.
    private sealed class ModulePart(string key, string moduleId, ModuleViewHost host)
    {
        // A stable key, not the contribution object: ModuleCompactParts builds fresh Contribution wrappers on every call,
        // so reference identity would treat every part as new on each row rebuild and re-mount its view.
        internal string Key { get; } = key;
        internal string ModuleId { get; } = moduleId;
        internal ModuleViewHost Host { get; } = host;
        internal double Width { get; set; }
        internal double? CompactWidth { get; set; }
        internal bool Placed { get; set; }
        // The missing-width diagnostic is written once per part, not on every rebuild.
        internal bool ReportedNoWidth { get; set; }
        internal bool Dropped { get; set; }
    }
    private readonly List<ModulePart> _moduleParts = [];
    // The collapsed row's parts: the compact-part contributions (IModuleCompactPart) plus the older passive activities
    // whose view declares a width, which are adapted so modules built before this contract keep working. Both stand
    // beside the built-in readings, in the user's module order and under each module's switch.
    private (IReadOnlyList<ModulePart> Parts, double Face) ModuleCompactParts(double scale)
    {
        // A part is a contribution in its own right, not "the activity the control bar would show" — that selection
        // (SelectModuleActivity) belongs to the activity channel and never hides a part.
        // Each source carries a stable key (kind + contribution id) and the module it belongs to; the value is the
        // contribution itself (a compact part, or an adapted passive activity).
        var sources = _app.Modules.Registry.List<IModuleCompactPart>()
            .Select(e => (Key: "part:" + e.Id, e.ModuleId, Value: (object)e.Value)).ToList();
        foreach (var entry in _app.Modules.Registry.List<IModuleActivity>())
        {
            bool legacy = _app.Modules.Invoke(entry.ModuleId, "activity state", () => entry.Value.State is { Active: true, Passive: true });
            if (legacy) sources.Add(("legacy:" + entry.Id, entry.ModuleId, new LegacyActivityPart(entry.Value)));
        }
        if (sources.Count == 0) { ReleaseModuleParts(); return ([], 0); }
        // The modules that can offer a part, in the user's saved order then registration order: a module the file has
        // never seen joins at the end, so installing one is enough for it to appear.
        var order = Settings.ModulePartOrder(EffectiveStyle, sources.Select(e => e.ModuleId)).ToList();
        sources = [.. sources.OrderBy(e => order.IndexOf(e.ModuleId))];
        // A contribution that went away (its module disabled) loses its host with its state.
        foreach (var stale in _moduleParts.Where(part => !sources.Any(e => e.Key == part.Key)).ToList())
        { CompactModuleLayer.Children.Remove(stale.Host); stale.Host.Dispose(); _moduleParts.Remove(stale); }
        double face = 0;
        var found = new List<ModulePart>(sources.Count);
        foreach (var source in sources)
        {
            var part = _moduleParts.FirstOrDefault(item => item.Key == source.Key);
            if (part is null)
            {
                var contribution = source.Value;
                // Async parts and async legacy activities are mounted through their async factory, as the control bar does.
                Func<ModuleViewContext, IModuleView> create = contribution switch
                {
                    IModuleAsyncCompactPart asyncPart => context => throw new ModuleApiException("async-view-required", "Use the async factory."),
                    IModuleCompactPart compact => compact.CreateView,
                    LegacyActivityPart legacy => context => legacy.Activity.CreateView(context, ActivityViewKind.Compact),
                    _ => throw new ModuleApiException("invalid-part", "Unknown compact part contribution.")
                };
                Func<ModuleViewContext, ValueTask<IModuleView>>? createAsync = contribution switch
                {
                    IModuleAsyncCompactPart asyncPart => context => asyncPart.CreateViewAsync(context),
                    LegacyActivityPart { Activity: IModuleAsyncActivity asyncActivity } => context => asyncActivity.CreateViewAsync(context, ActivityViewKind.Compact),
                    _ => null
                };
                var host = new ModuleViewHost(_app.Modules, source.ModuleId, ModuleContext, create, createAsync);
                // The host lives in CompactModuleLayer, which never leaves the tree, so its view keeps reporting itself
                // on show across row rebuilds; the row only moves and sizes it.
                CompactModuleLayer.Children.Add(host);
                // The view arrives after the host is built, and the row is laid out before that: rebuild the collapsed
                // row once it does, so its declared width takes part.
                host.Mounted += () => { if (!_expanded && IsLoaded) RenderCompact(); };
                part = new ModulePart(source.Key, source.ModuleId, host);
                _moduleParts.Add(part);
            }
            part.Width = 0; part.Placed = false; part.Dropped = false;
            // A view that has not mounted yet takes no part; the host stays for the next rebuild rather than being thrown
            // away. One that mounted without declaring a width can never be placed, and says why in the diagnostics.
            if (part.Host.View is not IModuleCompactWidth declared || declared.PreferredWidth is not { } wanted)
            {
                if (part.Host.View is not null && !part.ReportedNoWidth)
                {
                    part.ReportedNoWidth = true;
                    _app.Modules.Report(source.ModuleId, "compact part",
                        new ModuleApiException("compact-part-no-width", "A compact part's view must declare PreferredWidth or PreferredHeight (IModuleCompactWidth) to stand in the collapsed row."));
                }
                continue;
            }
            part.Width = wanted;
            part.CompactWidth = declared.CompactWidth;
            face += wanted;
            found.Add(part);
        }
        // A part the module's own switch turns off is not placed and takes no room, but its host stays mounted so the
        // switch brings it straight back.
        _modulePartFace = face;
        return (found, face);
    }
    // The row is about to be rebuilt: the parts are not taken out of the tree with it (they live in CompactModuleLayer),
    // so nothing to detach — the new row places and sizes them again through PlaceModulePart. The layer's own size goes
    // back to nothing so this build's parts start their own span.
    // A notice or a temporary page takes the row in place of everything else: the parts leave the screen with the built-in
    // readings, and stop refreshing while they are off it.
    private void HideModuleParts()
    {
        CompactModuleLayer.Visibility = Visibility.Collapsed;
        foreach (var part in _moduleParts) part.Host.SetPresented(false);
    }
    private void ClearModulePartForRow()
    {
        CompactModuleLayer.Visibility = Visibility.Collapsed;
        CompactModuleLayer.Width = 0; CompactModuleLayer.Height = 0;
        foreach (var part in _moduleParts) { part.Placed = false; part.Host.Visibility = Visibility.Collapsed; part.Host.SetPresented(false); }
    }
    // Places one module's part in a side column: the column's width, the length the view declared, at the offset the
    // column's stack gives it. The layer spans the whole column so a part anywhere down it shows.
    private void PlaceModuleColumnPart(string moduleId, double width, double height, double top = 0)
    {
        var part = _moduleParts.FirstOrDefault(item => item.ModuleId == moduleId);
        if (part is null || width <= 0 || height <= 0) return;
        CompactModuleLayer.Visibility = Visibility.Visible;
        CompactModuleLayer.Width = Math.Max(CompactModuleLayer.Width, width);
        CompactModuleLayer.Height = Math.Max(CompactModuleLayer.Height, top + height);
        part.Host.Visibility = Visibility.Visible;
        part.Host.Width = width; part.Host.Height = height;
        part.Host.HorizontalAlignment = HorizontalAlignment.Left; part.Host.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(part.Host, 0); Canvas.SetTop(part.Host, Math.Max(0, top));
        // A narrow viewport: the view lays its readings down the column (IModuleCompactWidth's height contract).
        part.Host.SetViewport(width, height, Math.Clamp(_modulePartScale, .75, 2));
        part.Host.SetPresented(true);
        part.Placed = true;
    }
    // Places one module's part at the room the arrangement gave it. A part the row had no room for stays hidden and says
    // so in the settings.
    private void PlaceModulePart(string moduleId, double left, double width, double height)
    {
        var part = _moduleParts.FirstOrDefault(item => item.ModuleId == moduleId);
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
        // Placement is the reason the settings page reports: a part that is on screen says Shown, not "no room".
        part.Placed = true;
    }
    private double _modulePartScale = 1, _modulePartFace;
    // Whether any module reading stood in the row at its last build (the settings page says which ones did not, and why).
    private bool _compactModulesShown;
    // Which modules offer a part, and whether the row had room: the settings page lists them, with the reason when one
    // is not shown (the #78 plan's rule for the collapsed row).
    internal bool ModulePartWanted => _moduleParts.Count > 0;
    internal double ModulePartFace => _modulePartFace;
    internal IReadOnlyList<string> ModulePartIds => [.. _moduleParts.Select(part => part.ModuleId)];
    internal bool ModulePartShown(string moduleId) => ModulePartStateOf(moduleId) == ModulePartState.Shown;
    internal string ModulePartName(string moduleId) => _app.Modules.Modules.FirstOrDefault(m => m.Id == moduleId)?.Name ?? moduleId;
    // Why a module's readings are not in the collapsed row, in the order the reasons take precedence: the user's own
    // switch first (their choice outranks everything), then a rule that hides the row (full-screen, an app rule, the
    // indicator style that has no room for parts), then the module not running, and last the row having no room.
    internal ModulePartState ModulePartStateOf(string moduleId)
    {
        var part = _moduleParts.FirstOrDefault(item => item.ModuleId == moduleId);
        if (part is null) return ModulePartState.Unavailable;
        if (!Settings.ShowsModulePart(EffectiveStyle, moduleId)) return ModulePartState.UserOff;
        if (IsSuppressed || EffectiveStyle == CompactStyle.Line) return ModulePartState.RuleHidden;
        if (_app.Modules.Modules.FirstOrDefault(m => m.Id == moduleId)?.Status is not ModuleStatus.Ready) return ModulePartState.Unavailable;
        return part.Placed ? ModulePartState.Shown : ModulePartState.NoRoom;
    }
    private void ReleaseModuleParts()
    {
        foreach (var part in _moduleParts) { CompactModuleLayer.Children.Remove(part.Host); part.Host.Dispose(); }
        _moduleParts.Clear();
        CompactModuleLayer.Visibility = Visibility.Collapsed;
        _modulePartFace = 0;
    }
    // Adapts an older passive activity (ActivityState.Passive = true) into the row's parts, so a module written before
    // IModuleCompactPart exists keeps working: the activity's compact view is used, and its declared widths are read
    // through IModuleCompactWidth exactly as a new part's are.
    private sealed class LegacyActivityPart(IModuleActivity activity)
    {
        internal IModuleActivity Activity { get; } = activity;
    }
    private void ReleaseModulePage()
    { if (_modulePageMenu is not null) _modulePageMenu.IsOpen = false; _modulePageMenu = null; _modulePage?.Dispose(); _modulePage = null; _modulePageEntry = null; MiniAppsContent.Children.Clear(); }
    private void DismissExpandedFromEscape()
    {
        if (_moveArmed) { CancelMove(); return; }
        if (ShelfEscape()) return;
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
        // This activity takes the whole row, so the built-in readings go — and so do the module readings' parts, which
        // live in a layer of their own above them. A cover page shows the activity alone.
        CompactRings.Visibility = CompactAlert.Visibility = Visibility.Hidden;
        CompactModuleLayer.Visibility = Visibility.Collapsed;
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

// Why a module's readings are, or are not, in the collapsed row. The settings page names the reason rather than only
// saying "hidden" (the plugin platform plan's rule for the collapsed row).
internal enum ModulePartState { Shown, UserOff, NoRoom, RuleHidden, Unavailable }
