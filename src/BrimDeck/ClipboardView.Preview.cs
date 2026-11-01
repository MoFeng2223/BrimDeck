// WPF translation of Edge-Drop PreviewFlyout / StackItems and fitPreviewImage (767b6d63), Apache-2.0.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using DropShelf.Models;
using DropShelf.Services;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private Popup? _preview;
    private string? _previewId;
    private bool _hoverPreview;
    private ClipboardCard? _hoverCard;
    private readonly System.Windows.Threading.DispatcherTimer _previewDelay = new();
    private readonly System.Windows.Threading.DispatcherTimer _previewLeave = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private System.Windows.Threading.DispatcherOperation? _previewUpdate;
    private Rect _previewDestination = Rect.Empty;
    private Spring _previewX, _previewY;
    private bool _previewFollowing;
    private double _previewFrameAt;
    internal bool PreviewOpen => _preview?.IsOpen == true;

    private void InitializeHoverPreview()
    {
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            if (_hoverCard is { IsMouseOver: true } card && Mouse.LeftButton == MouseButtonState.Released && Find<ButtonBase>(Mouse.DirectlyOver as DependencyObject) is null) OpenHoverPreview(card);
        };
        _previewLeave.Tick += (_, _) =>
        {
            _previewLeave.Stop();
            if (_hoverPreview && _preview?.Child.IsMouseOver != true && _hoverCard?.IsMouseOver != true) CloseHoverPreview();
        };
        _list.PreviewMouseMove += (_, e) =>
        {
            if (_settings().Mode != ModuleMode.Full) { CloseHoverPreview(); return; }
            var card = Find<ClipboardCard>(e.OriginalSource as DependencyObject);
            bool onAction = Find<ButtonBase>(e.OriginalSource as DependencyObject) is not null;
            _previewLeave.Stop();
            if (card == _hoverCard && !onAction && (_previewDelay.IsEnabled || PreviewOpen && _previewId == card?.Entry?.Id)) return;
            CancelPreviewUpdate(); _previewDelay.Stop(); _hoverCard = card;
            if (card is not { DockMode: true, Entry: not null }) { if (_hoverPreview) _previewLeave.Start(); return; }
            if (onAction && !_hoverPreview || e.LeftButton == MouseButtonState.Pressed || _drag.IsDragging || _editor?.IsOpen == true || _activeMenu?.IsOpen == true || PreviewOpen && !_hoverPreview) return;
            // Only the first expansion dwells. While browsing an open flyout, render the newest card once per frame.
            if (_hoverPreview && PreviewOpen || _settings().PreviewDelayMilliseconds == 0) QueueHoverPreview();
            else { _previewDelay.Interval = TimeSpan.FromMilliseconds(_settings().PreviewDelayMilliseconds); _previewDelay.Start(); }
        };
        _list.MouseLeave += (_, _) => { _hoverCard = null; CancelPreviewUpdate(); _previewDelay.Stop(); if (_hoverPreview) _previewLeave.Start(); };
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.HorizontalChange == 0 && e.VerticalChange == 0) return;
            _previewDelay.Stop(); CloseHoverPreview();
        }));
    }
    private void OpenHoverPreview(ClipboardCard card)
    {
        if (_settings().Mode != ModuleMode.Full || _disposed || !IsVisible || _drag.IsDragging || _activeMenu?.IsOpen == true || _editor?.IsOpen == true
            || PreviewOpen && !_hoverPreview || card is not { IsLoaded: true, DockMode: true, Entry: { } entry }) return;
        if (PreviewOpen && _previewId == entry.Id) return;
        OpenPreview(entry);
    }
    internal void RefreshPreviewSensitivity()
    {
        if (!_previewDelay.IsEnabled) return;
        _previewDelay.Stop();
        int delay = _settings().PreviewDelayMilliseconds;
        if (delay == 0) QueueHoverPreview();
        else { _previewDelay.Interval = TimeSpan.FromMilliseconds(delay); _previewDelay.Start(); }
    }
    private void QueueHoverPreview()
    {
        if (_previewUpdate is not null) return;
        _previewUpdate = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _previewUpdate = null;
            if (_hoverCard is { } latest) OpenHoverPreview(latest);
        });
    }
    private void CancelPreviewUpdate() { _previewUpdate?.Abort(); _previewUpdate = null; }
    private void StopPreviewTimers() { CancelPreviewUpdate(); _previewDelay.Stop(); _previewLeave.Stop(); }
    private void RepositionPreview() { if (_preview is { IsOpen: true } popup) popup.HorizontalOffset = popup.HorizontalOffset == 0 ? .01 : 0; }
    private void PreviewFollowFrame(object? sender, EventArgs args)
    {
        double now = Environment.TickCount64 / 1000d, dt = now - _previewFrameAt; _previewFrameAt = now;
        bool moving = _previewX.Step(dt) | _previewY.Step(dt);
        if (!Soft.For(this) || !moving)
        {
            _previewX = new(_previewDestination.Left); _previewY = new(_previewDestination.Top); StopPreviewFollow();
        }
        RepositionPreview();
    }
    private void StopPreviewFollow()
    {
        if (_previewFollowing) CompositionTarget.Rendering -= PreviewFollowFrame;
        _previewFollowing = false;
    }
    private Point PreviewPosition(Rect destination, Rect work)
    {
        if (_previewDestination != destination)
        {
            bool canFollow = Soft.For(this) && !_previewDestination.IsEmpty;
            _previewDestination = destination;
            if (canFollow)
            {
                // Retarget the existing spring, carrying its velocity through rapid changes instead of queuing motions.
                _previewX.Target = destination.Left; _previewY.Target = destination.Top;
                if (!_previewFollowing) { _previewFollowing = true; _previewFrameAt = Environment.TickCount64 / 1000d; CompositionTarget.Rendering += PreviewFollowFrame; }
            }
            else { StopPreviewFollow(); _previewX = new(destination.Left); _previewY = new(destination.Top); }
        }
        // A following spring may overshoot; it must never leave the destination monitor's usable bounds.
        _previewX.Value = Math.Clamp(_previewX.Value, work.Left, work.Right - destination.Width);
        _previewY.Value = Math.Clamp(_previewY.Value, work.Top, work.Bottom - destination.Height);
        return new Point(_previewX.Value, _previewY.Value);
    }
    private void CloseHoverPreview() { StopPreviewTimers(); if (_hoverPreview && _preview is not null) _preview.IsOpen = false; }
    internal void ShowPreview(ClipboardEntry entry)
    {
        StopPreviewTimers();
        CloseHoverPreview();
        var anchor = PreviewAnchor(entry.Id);
        var card = ScreenRect(anchor); var panel = ScreenRect(this);
        if (_openPreviewWindow is not null) _openPreviewWindow(entry, card, panel, PresentationScale());
        else if (Window.GetWindow(this) is { } owner)
            ClipboardPreviewWindows.ForOwner(owner, _history, _settings, _activate).Show(entry, card, panel, PresentationScale());
    }
    private FrameworkElement PreviewAnchor(string? id)
    {
        var item = _list.Items.OfType<ClipboardEntry>().FirstOrDefault(item => item.Id == id);
        return item is not null && _list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject row && Descendant<ClipboardCard>(row) is { } card ? card : this;
    }
    private static Rect ScreenRect(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    // PreviewFlyout's card-center/clamped-left formula, extended to the current monitor and all four island edges.
    // This boundary uses device pixels throughout, including WPF's custom-placement callback; content uses scaled DIPs.
    internal static Rect PlacePreview(Rect card, Rect panel, Rect work, Size size, double gap)
    {
        double width = Math.Min(size.Width, work.Width), height = Math.Min(size.Height, work.Height);
        double x = Math.Clamp(card.Left + card.Width / 2 - width / 2, work.Left, work.Right - width);
        double y = Math.Clamp(card.Top + card.Height / 2 - height / 2, work.Top, work.Bottom - height);
        Rect[] candidates = [new(x, Math.Max(card.Bottom, panel.Bottom) + gap, width, height),
            new(x, Math.Min(card.Top, panel.Top) - height - gap, width, height),
            new(Math.Max(card.Right, panel.Right) + gap, y, width, height),
            new(Math.Min(card.Left, panel.Left) - width - gap, y, width, height)];
        var best = candidates.FirstOrDefault(work.Contains);
        if (best.IsEmpty || best.Width == 0)
            best = candidates.OrderByDescending(rect => { var visible = Rect.Intersect(rect, work); return visible.IsEmpty ? 0 : visible.Width * visible.Height; }).First();
        return new Rect(Math.Clamp(best.X, work.Left, work.Right - width), Math.Clamp(best.Y, work.Top, work.Bottom - height), width, height);
    }
    private void OpenPreview(ClipboardEntry entry)
    {
        bool reuse = _hoverPreview && PreviewOpen;
        if (!reuse && _preview is not null) _preview.IsOpen = false;
        _previewId = entry.Id; _hoverPreview = true;
        var anchor = PreviewAnchor(entry.Id);
        double scale = PresentationScale(), unit = VisualTreeHelper.GetDpi(this).DpiScaleX * scale;
        var panel = ScreenRect(this); var cardRect = ScreenRect(anchor);
        var work = Native.WindowsHost.WorkAreaAt(new Point(cardRect.Left + cardRect.Width / 2, cardRect.Top + cardRect.Height / 2));
        double inset = Math.Min(12 * unit, Math.Min(work.Width, work.Height) / 8); work.Inflate(-inset, -inset);
        double maxWidth = Math.Min(760, work.Width / unit), width = Math.Min(440, maxWidth);
        double verticalRoom = Math.Max(work.Bottom - panel.Bottom, panel.Top - work.Top) / unit - 6;
        double sideRoom = Math.Max(work.Right - panel.Right, panel.Left - work.Left) / unit - 6;
        double maxHeight = Math.Min(540, Math.Max(120, verticalRoom >= 180 || sideRoom < width ? verticalRoom : work.Height / unit));
        maxHeight = Math.Min(maxHeight, work.Height / unit);
        double bodyHeight = Math.Max(1, maxHeight - 96);
        var root = new DockPanel();
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 10) }; bar.ColumnDefinitions.Add(new()); bar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = ClipboardPreview.Text(ClipboardHistory.DisplayTitle(entry), 12); title.FontWeight = FontWeights.Medium; title.TextWrapping = TextWrapping.NoWrap; title.VerticalAlignment = VerticalAlignment.Center; bar.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); bar.Children.Add(actions);
        actions.Children.Add(IslandContentCard.ActionButton("⧉", Loc.T("复制整条记录", "Copy whole entry"), () => _activate(entry, true)));
        actions.Children.Add(IslandContentCard.ActionButton("↵", Loc.T("回贴整条记录", "Paste whole entry"), () => _activate(entry, false)));
        actions.Children.Add(IslandContentCard.ActionButton("×", Loc.T("关闭预览", "Close preview"), () => { if (_preview is not null) _preview.IsOpen = false; }));
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var metadata = ClipboardPreview.Text(string.Join(" · ", new[] { ClipboardContentTypes.Label(entry.Content.Kind), entry.SourceName, entry.CopiedAt.ToLocalTime().ToString("g"), "×" + entry.CopyCount }.Where(value => value.Length > 0)), 10.5, "PanelTertiary");
        metadata.Margin = new Thickness(0, 10, 0, 0); metadata.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(metadata, "clipboard-preview-metadata"); DockPanel.SetDock(metadata, Dock.Bottom); root.Children.Add(metadata);
        FrameworkElement body;
        if (entry.Content.Kind == ClipboardContentKind.Files)
        {
            bool gallery = entry.Content.Files.All(IslandFilePreview.IsImage);
            width = Math.Min(maxWidth, Math.Max(320, EdgeDropPresentation.StackOpenWidth(entry.Content.Files.Length, gallery)));
            var rows = new StackPanel();
            foreach (string path in entry.Content.Files)
            {
                var one = entry with { Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [path] } };
                rows.Children.Add(IslandFilePreview.Row(path, () => _activate(one, true), () => ExportEntry(one)));
            }
            body = new ScrollViewer { Content = rows, MaxHeight = bodyHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }
        else if (entry.Content.Kind is ClipboardContentKind.Image or ClipboardContentKind.Screenshot && ClipboardPreview.Create(entry.Content, true) is Image image && image.Source is BitmapSource)
        {
            var fit = EdgeDropPresentation.FitImage(entry.Content.PixelWidth, entry.Content.PixelHeight, new(Math.Min(440, maxWidth), maxWidth, bodyHeight, 24, unit));
            width = fit.FlyoutWidth; image.Width = fit.Width; image.Height = fit.Height; image.Stretch = Stretch.Uniform; image.HorizontalAlignment = HorizontalAlignment.Center;
            body = new ScrollViewer { Content = image, MaxHeight = bodyHeight, VerticalScrollBarVisibility = fit.Scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }
        else { body = ClipboardPreview.Create(entry.Content, true); body.MaxHeight = bodyHeight; }
        root.Children.Add(body);
        var chrome = reuse ? (Border)_preview!.Child : new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1) };
        chrome.Width = width; chrome.Child = root; chrome.LayoutTransform = new ScaleTransform(scale, scale);
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR12");
        AutomationProperties.SetName(chrome, Loc.T("本地内容预览", "Local content preview")); AutomationProperties.SetAutomationId(chrome, "clipboard-expanded-preview");
        if (reuse) { _previewDelay.Stop(); RepositionPreview(); return; }
        // Hover previews never capture the mouse or activate the island; the leave grace bridges the gap into the flyout.
        _previewDestination = Rect.Empty;
        var popup = _preview = new Popup { PlacementTarget = this, Placement = PlacementMode.Custom, StaysOpen = true, AllowsTransparency = true, Child = chrome };
        popup.CustomPopupPlacementCallback = (size, _, _) =>
        {
            anchor = PreviewAnchor(_previewId); panel = ScreenRect(this); cardRect = ScreenRect(anchor);
            var monitorWork = Native.WindowsHost.WorkAreaAt(new Point(cardRect.Left + cardRect.Width / 2, cardRect.Top + cardRect.Height / 2)); monitorWork.Inflate(-inset, -inset);
            var at = PlacePreview(cardRect, panel, monitorWork, size, 6 * unit);
            chrome.RenderTransformOrigin = new Point(Math.Clamp((cardRect.Left + cardRect.Width / 2 - at.Left) / at.Width, 0, 1), at.Top >= cardRect.Bottom ? 0 : at.Bottom <= cardRect.Top ? 1 : .5);
            return [new CustomPopupPlacement(PreviewPosition(at, monitorWork) - (Vector)panel.TopLeft, PopupPrimaryAxis.None)];
        };
        var owner = Window.GetWindow(this); Rect lastPanel = panel, lastCard = cardRect;
        void Follow(object? sender, EventArgs args)
        {
            if (!popup.IsOpen || PresentationSource.FromVisual(this) is null) return;
            var nextAnchor = PreviewAnchor(_previewId); var nextPanel = ScreenRect(this); var nextCard = ScreenRect(nextAnchor);
            if (nextPanel == lastPanel && nextCard == lastCard) return;
            lastPanel = nextPanel; lastCard = nextCard;
            popup.HorizontalOffset = popup.HorizontalOffset == 0 ? .01 : 0;
        }
        LayoutUpdated += Follow; if (owner is not null) owner.LocationChanged += Follow;
        // A stale closing snapshot would outlive the card it described during a fast sweep.
        PopupMotion.Attach(popup, exitSnapshot: false); popup.Opened += (_, _) => InteractionChanged?.Invoke();
        popup.Closed += (_, _) =>
        {
            LayoutUpdated -= Follow; if (owner is not null) owner.LocationChanged -= Follow;
            if (ReferenceEquals(_preview, popup)) { _previewId = null; _hoverPreview = false; StopPreviewTimers(); StopPreviewFollow(); _previewDestination = Rect.Empty; }
            InteractionChanged?.Invoke();
        };
        chrome.MouseEnter += (_, _) => _previewLeave.Stop();
        chrome.MouseLeave += (_, _) => { if (_hoverPreview) _previewLeave.Start(); };
        chrome.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.IsOpen = true;
    }
    private void ExportEntry(ClipboardEntry entry)
    {
        if (Window.GetWindow(this) is not { } owner) return;
        _drag.Export(owner, entry);
    }
    internal void RefreshTimes()
    {
        void Visit(DependencyObject node)
        {
            if (node is ClipboardCard card) card.UpdateTime();
            if (node is ClipboardTimelineCard timeline) timeline.RefreshHeading();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Visit(VisualTreeHelper.GetChild(node, i));
        }
        Visit(_list);
    }
    internal sealed record PresentationState(string? SelectedId, double X, double Y, string? PreviewId, bool SearchOpen);
    internal PresentationState CapturePresentation() => new((_list.SelectedItem as ClipboardEntry)?.Id, Descendant<ScrollViewer>(_list)?.HorizontalOffset ?? 0, Descendant<ScrollViewer>(_list)?.VerticalOffset ?? 0, _previewId, _searchOpen);
    internal void RestorePresentation(PresentationState state)
    {
        SetSearchOpen(state.SearchOpen);
        _list.SelectedItem = _history.Items.FirstOrDefault(item => item.Id == state.SelectedId);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_disposed) return;
            if (Descendant<ScrollViewer>(_list) is { } scroll) { scroll.ScrollToHorizontalOffset(state.X); scroll.ScrollToVerticalOffset(state.Y); }
            if (state.PreviewId is { } id && _history.Items.FirstOrDefault(item => item.Id == id) is { } entry) OpenPreview(entry);
        });
    }
}
