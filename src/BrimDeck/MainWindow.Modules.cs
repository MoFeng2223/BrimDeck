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
    private string? _modulePageId;
    private string? _preferredModuleActivityId;
    private bool _moduleOpening;
    private string _modulePageListKey = "";
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
        var entries = _app.Modules.Registry.List<ModulePage>().OrderBy(e => e.Value.Order).ThenBy(e => e.Id).ToArray();
        var selected = entries.FirstOrDefault(e => e.Id == _modulePageId) ?? entries.FirstOrDefault();
        string listKey = Loc.Language + ":" + string.Join(",", entries.Select(e => e.Id));
        if (!ReferenceEquals(selected, _modulePageEntry) || _modulePage is null || listKey != _modulePageListKey)
        {
            ReleaseModulePage(); _modulePageEntry = selected; _modulePageListKey = listKey;
            if (selected is not null)
            {
                _modulePageId ??= selected.Id;
                var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
                if (entries.Length > 1)
                {
                    var caption = new StackPanel { Orientation = Orientation.Horizontal };
                    caption.Children.Add(UI.Text(Loc.T(selected.Value.Title, selected.Value.TitleEn), 12, UI.Secondary));
                    var arrow = UI.Text("⌄", 12, UI.Secondary); arrow.Margin = new Thickness(12, 0, 0, 0); caption.Children.Add(arrow);
                    var picker = new Button { Style = (Style)FindResource("PanelSourceButton"), Content = caption, Width = double.NaN, Height = 26, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
                    AutomationProperties.SetName(picker, Loc.T("切换小程序", "Switch mini app"));
                    var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu"), Width = 220, PlacementTarget = picker, Placement = PlacementMode.Bottom };
                    foreach (var entry in entries)
                    {
                        var item = new MenuItem { Style = (Style)FindResource("ModulePageMenuItem"), Header = Loc.T(entry.Value.Title, entry.Value.TitleEn), IsCheckable = true, IsChecked = entry.Id == selected.Id };
                        item.Click += (_, _) => OpenModulePage(entry.Id); menu.Items.Add(item);
                    }
                    _modulePageMenu = menu; picker.ContextMenu = menu; picker.Click += (_, _) => menu.IsOpen = !menu.IsOpen; root.Children.Add(picker);
                    menu.Opened += (_, _) => _leave.Stop();
                    menu.Closed += (_, _) => { if (ReferenceEquals(_modulePageMenu, menu) && _expanded) PointerChanged(InsideOpenPanel(WindowsHost.Cursor())); };
                }
                _modulePage = new(_app.Modules, selected.ModuleId, ModuleContext, context => selected.Value.CreateView(context), selected.Value.CreateViewAsync);
                Grid.SetRow(_modulePage, 1); root.Children.Add(_modulePage); MiniAppsContent.Children.Add(root);
            }
            else MiniAppsContent.Children.Add(UI.Centered(Loc.T("没有已启用的小程序，请在模组设置中启用。", "Enable a mini app in module settings."), 13, UI.Secondary));
        }
        double selector = entries.Length > 1 ? 34 : 0;
        _modulePage?.SetViewport(ContentCanvas.Width - 2 * _header.Side, ContentCanvas.Height - _header.Row - bottom - selector);
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
        _modulePageId = id; _moduleHandover?.Drop();
        var leaving = PageHandover.Capture(ModuleControlHost);
        _moduleControls = false; _moduleClosing = false;
        SelectPage(DeckPage.MiniApps); RenderMiniApps(); SetExpanded(true);
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
