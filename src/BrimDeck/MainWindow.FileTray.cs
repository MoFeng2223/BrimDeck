using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Models;
using DropShelf.Services;
using TrayItem = DropShelf.Models.ShelfItem;

namespace BrimDeck;

// The island renders the original DropShelf model in BrimDeck cards; the original shelf window shares the same items.
// Ordinary files keep their source paths; virtual attachments and volatile images use DropShelf's owned cache. A card is the file's thumbnail when it has one, its kind's glyph otherwise.
// Island geometry and controls share the clipboard Dock; the original shelf model and flying-card/OLE drag remain owners.
public partial class MainWindow
{
    private ShelfTrayAdapter Tray => _app.ShelfTray;
    private readonly FileShareService _fileShare;
    private ShelfIslandDropTarget? _fileShareDrop;
    private readonly DispatcherTimer _trayDropHover = new() { Interval = TimeSpan.FromMilliseconds(360) };
    private Grid? _fileShareCue;
    private ContextMenu? _trayMenu;
    internal bool ShelfInteractionBusy => FileShareHoldsOpen;
    private bool FileShareHoldsOpen => IsTrayPage && _clipboardInput || _trayExporting || _trayDragging || _trayActionBusy || _fileShareDrop?.IsDragging == true || _fileShareDrop?.IsReceiving == true || _trayMenu?.IsOpen == true;
    private bool _trayActionBusy;
    private Border? _trayFrame;
    private string _trayKey = "";
    // The item that arrived last: only it plays the arrival, and the grid scrolls to it.
    private string? _trayArrived;
    // Where a press started, so a click on a card is not taken for a drag.
    private Point _trayPressAt;
    private TrayItem? _trayPressed;
    private FrameworkElement? _trayPressCard;
    // A drag of a card: inside the tray it takes a new place, outside it becomes an ordinary file drag.
    private bool _trayDragging, _trayExporting;
    private TrayItem? _trayDragItem;
    private FrameworkElement? _trayDragCard;
    private Grid? _trayGrid;
    private Canvas? _trayGhostLayer;
    private Image? _trayGhost;
    private Point _trayGrab;
    private int _trayColumns = 1;
    private bool _trayWired;
    private ScrollViewer? _trayScroll;
    private Grid? _trayBar;
    private readonly Dictionary<string, ShelfIslandCard> _trayCards = [];
    private double _trayCardWidth, _trayCardHeight;
    private string[]? _trayOriginalOrder;
    private bool _layingOutTray, _trayRefreshQueued;
    private DropShelf.ViewModels.ShelfViewModel? _trayViewportModel;

    private void ReleaseTrayViewport()
    {
        _trayViewportModel?.SetViewportItems(this, []); _trayViewportModel = null;
    }

    private void InitializeFileSharing()
    {
        _trayDropHover.Tick += (_, _) =>
        {
            _trayDropHover.Stop();
            if (!_app.ShelfRunning || _app.ModulesChanging || _fileShareDrop?.IsDragging != true || _trayExporting || _trayDragging || ClipboardHoldsOpen) return;
            SelectPage(DeckPage.FileTray); SetExpanded(true);
            if (_fileShareCue is { } cue) cue.Height = 44;
        };
        ResumeShelfInput();
    }

    private void FileShareHighlight(bool shown)
    {
        if (Island.Child is Panel content)
        {
            if (_fileShareCue is not null) content.Children.Remove(_fileShareCue);
            _fileShareCue = null;
            if (shown)
            {
                _fileShareCue = TrayDropZone(Island.ActualHeight);
                _fileShareCue.Visibility = Visibility.Visible;
                Panel.SetZIndex(_fileShareCue, 30);
                content.Children.Add(_fileShareCue);
            }
        }
        if (shown) { _hover.Stop(); _leave.Stop(); _trayDropHover.Start(); }
        else { _trayDropHover.Stop(); PointerChanged(_pointerInside); }
    }

    private void FileShareProblem(string problem)
    {
        _trayKey = "";
        if (IsTrayPage && _expanded) { RenderFileTray(); ShowTrayNotice(problem); }
        else MessageBox.Show(this, problem, Loc.T("文件操作", "File operations"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RenderFileTray()
    {
        if (!_app.ShelfRunning) return;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; TrayContent.Visibility = Visibility.Visible;
        MonitorContent.Visibility = Visibility.Collapsed; ClipboardContent.Visibility = Visibility.Collapsed;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        SyncButton.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        PrepareClipboardHeader(instant: !_expanded);
        TrayContent.Margin = new Thickness(0);

        // 只更新当前模型的可见卡片；查看页面不会触发清理或保存。
        if (!_trayWired)
        {
            _trayWired = true;
            TrayContent.PreviewMouseMove += TrayPointerMove;
            TrayContent.PreviewMouseLeftButtonUp += TrayPointerUp;
            TrayContent.LostMouseCapture += (_, _) => { if (_trayDragging && !TrayContent.IsMouseCaptured) EndTrayReorder(false); };
        }

        // Existence is swept once, here, rather than asked per item per question further down (FileTray.RefreshExistence).
        Tray.RefreshExistence(DateTimeOffset.Now);
        string key = Tray.Model.Id + "#" + string.Join("|", Tray.Items.Select(item => $"{item.Id}:{item.Pinned}:{item.Exists}:{item.Value}:{item.Detail}:{item.Thumbnail?.GetHashCode()}")) + $"#{width:0.##}x{height:0.##}:{PanelTheme.Generation}:{Loc.Language}:{_trayActionBusy}:{Tray.Model.IsBusy}:{Tray.Model.WindowTitle}:{Settings.Tray.GridView}:{_trayFilter}:{_trayPinnedOnly}:{_trayQuery}";
        if (key == _trayKey && TrayContent.Children.Count > 0) return;
        _trayKey = key;
        BuildFileTrayPage(width, height);
    }

    private void BuildFileTrayPage(double width, double height)
    {
        if (_trayFrame is null || !TrayContent.Children.Contains(_trayFrame))
        {
            TrayContent.Children.Clear(); _trayCards.Clear();
            _trayFrame = new Border { Background = Brushes.Transparent, AllowDrop = true };
            _trayFrame.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(MainWindow).Assembly.GetName().Name};component/ClipboardResources.xaml", UriKind.Relative) });
            var host = new Grid(); var page = new Grid();
            page.RowDefinitions.Add(new() { Height = new GridLength(ClipboardDockLayout.HeaderHeight) }); page.RowDefinitions.Add(new());
            _trayBar = new Grid { Height = ClipboardDockLayout.HeaderHeight, Background = Brushes.Transparent };
            page.Children.Add(_trayBar);
            BuildTrayDockHeader();
            // Fractional equal columns must not each round up independently at 125/150% DPI.
            _trayGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, UseLayoutRounding = false };
            _trayScroll = new ScrollViewer { Content = _trayGrid, Focusable = false, CanContentScroll = false,
                Margin = new Thickness(ClipboardDockLayout.ListInset, ClipboardDockLayout.ListTop, ClipboardDockLayout.ListInset, ClipboardDockLayout.ListBottom) };
            AutomationProperties.SetAutomationId(_trayScroll, "shelf-dock-items");
            _trayScroll.ScrollChanged += (_, args) =>
            {
                if (!_layingOutTray && (args.HorizontalChange != 0 || args.VerticalChange != 0 || args.ViewportWidthChange != 0 || args.ViewportHeightChange != 0))
                {
                    if (args.ViewportWidthChange != 0 && Settings.Tray.GridView) LayoutTrayCards();
                    else UpdateVisibleTrayCards();
                }
            };
            _trayScroll.PreviewMouseWheel += (_, args) =>
            {
                if (args.Delta == 0) return;
                if (Settings.Tray.GridView) { if (args.Delta < 0) _trayScroll.LineDown(); else _trayScroll.LineUp(); }
                else { if (args.Delta < 0) _trayScroll.LineRight(); else _trayScroll.LineLeft(); }
                args.Handled = true;
            };
            Grid.SetRow(_trayScroll, 1); page.Children.Add(_trayScroll); host.Children.Add(page);
            _trayEmpty = ClipboardPreview.Text("", 12, "PanelSecondary"); _trayEmpty.TextAlignment = TextAlignment.Center;
            _trayEmpty.HorizontalAlignment = HorizontalAlignment.Center; _trayEmpty.VerticalAlignment = VerticalAlignment.Center;
            _trayEmpty.Margin = new Thickness(24); Grid.SetRow(_trayEmpty, 1); page.Children.Add(_trayEmpty);
            _trayGhostLayer = new Canvas { IsHitTestVisible = false }; host.Children.Add(_trayGhostLayer);
            BuildTrayNotice(host);
            _trayFrame.Child = host; TrayContent.Children.Add(_trayFrame);
            _trayFrame.SizeChanged += (_, _) => { FitTrayDockHeader(); LayoutTrayCards(); FitTrayNotice(); };
            _trayFrame.IsVisibleChanged += (_, _) =>
            {
                if (!_trayFrame.IsVisible) { HideTrayNotice(); CloseTrayPopups(); EndTrayReorder(false); ReleaseTrayViewport(); }
                else UpdateVisibleTrayCards();
            };
            Closed += (_, _) => ReleaseTrayViewport();
        }
        RefreshTrayDockHeader();
        LayoutTrayCards();
    }

    private void UpdateVisibleTrayCards()
    {
        if (_trayGrid is null || _trayScroll is null) return;
        if (!ReferenceEquals(_trayViewportModel, Tray.Model)) { ReleaseTrayViewport(); _trayViewportModel = Tray.Model; }
        bool grid = Settings.Tray.GridView;
        double pitch = grid ? _trayCardHeight + ClipboardDockLayout.CardGap : _trayCardWidth + ClipboardDockLayout.CardGap;
        double offset = grid ? _trayScroll.VerticalOffset : _trayScroll.HorizontalOffset;
        double viewport = grid ? _trayScroll.ViewportHeight : _trayScroll.ViewportWidth;
        if (viewport <= 0) viewport = grid ? Math.Max(80, TrayContent.ActualHeight - ClipboardDockLayout.HeaderHeight) : Math.Max(240, TrayContent.ActualWidth);
        int first = Math.Max(0, (int)Math.Floor(offset / pitch) - 1) * (grid ? _trayColumns : 1);
        int end = Math.Min(_trayResults.Count, ((int)Math.Ceiling((offset + viewport) / pitch) + 1) * (grid ? _trayColumns : 1));
        var wanted = _trayResults.Skip(first).Take(Math.Max(0, end - first)).Select(item => item.Id).ToHashSet();
        if (_trayDragItem is { } dragged) wanted.Add(dragged.Id);
        foreach (var (id, card) in _trayCards.ToArray())
        {
            if (wanted.Contains(id)) continue;
            _trayGrid.Children.Remove(card); _trayCards.Remove(id);
        }
        for (int i = first; i < end; i++)
        {
            var item = _trayResults[i];
            if (!_trayCards.TryGetValue(item.Id, out var card))
            {
                card = new ShelfIslandCard(ToggleTrayPin, value => Tray.Model.CopyToClipboard([value]), RemoveTrayItem) { DockMode = true, Margin = new Thickness(0, 0, ClipboardDockLayout.CardGap, ClipboardDockLayout.CardGap), AllowDrop = true };
                card.Focusable = true;
                AutomationProperties.SetAutomationId(card, "shelf-card-" + item.Id);
                var captured = card;
                card.PreviewKeyDown += (_, e) =>
                {
                    if (captured.Item is not { } target || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
                    if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Tray.Model.Open(target); e.Handled = true; }
                    else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Tray.Model.CopyToClipboard([target]); e.Handled = true; }
                    else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { RemoveTrayItem(target); e.Handled = true; }
                    else if (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) { OpenTrayMenu(captured, target); e.Handled = true; }
                };
                card.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    if (TrayButtonAncestor(e.OriginalSource as DependencyObject) || captured.Item is not { } pressed) return;
                    if (e.ClickCount == 2) { Tray.Model.Open(pressed); e.Handled = true; return; }
                    _trayPressAt = e.GetPosition(TrayContent); _trayPressed = pressed; _trayPressCard = captured;
                };
                card.MouseRightButtonUp += (_, e) => { if (captured.Item is { } target) OpenTrayMenu(captured, target); e.Handled = true; };
                _trayCards.Add(item.Id, card); _trayGrid.Children.Add(card);
                if (item.Id == _trayArrived) Arrive(card);
            }
            card.Update(item, _trayCardWidth, _trayCardHeight);
            Grid.SetColumn(card, grid ? i % _trayColumns : i); Grid.SetRow(card, grid ? i / _trayColumns : 0);
        }
        _trayViewportModel.SetViewportItems(this, _expanded && IsTrayPage && _trayFrame!.IsVisible ? _trayCards.Values.Select(card => card.Item!).Where(item => item is not null) : []);
        if (_trayEmpty is not null)
        {
            _trayEmpty.Text = Tray.Count == 0 ? Loc.T("拖入文件、图片、文字或链接", "Drop files, images, text or links") : Loc.T("没有匹配的项目", "No matching items");
            _trayEmpty.Visibility = _trayResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private static bool TrayButtonAncestor(DependencyObject? node)
    {
        while (node is not null) { if (node is System.Windows.Controls.Primitives.ButtonBase) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : null; }
        return false;
    }

    private string TrayLabel()
    {
        int count = Tray.Count, pinned = Tray.PinnedCount, missing = Tray.MissingCount;
        if (_trayActionBusy) return Loc.T("正在执行操作…", "Working…");
        if (count == 0) return Loc.T("文件中转", "File tray");
        string text = Loc.T($"{count} 个项目", Loc.Count(count, "item", "items"));
        if (pinned > 0) text += Loc.T($" · {pinned} 个已固定", $" · {pinned} pinned");
        if (missing > 0) text += Loc.T($" · {missing} 个已失效", $" · {missing} missing");
        return text;
    }

    // The item's menu: open it, show where it lives, pin it, take it off the tray.
    private void OpenTrayMenu(FrameworkElement card, TrayItem item)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
        var style = (Style)FindResource("TrayMenuItem");
        var separator = (Style)FindResource("TrayMenuSeparator");
        void Add(string header, string glyph, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Style = style, Header = header, Icon = UI.CenteredIcon(glyph, 11, PanelTheme.Hex("textSecondary")), IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add(item.Kind == ShelfItemKind.Text ? Loc.T("复制文本", "Copy text") : Loc.T("打开", "Open"), "\uE8A7", () => Tray.Model.Open(item), item.Exists);
        Add(Loc.T("打开所在文件夹", "Show in folder"), "\uE8B7", () => ShowTrayItemInFolder(item), item.IsFileSystem && item.Exists);
        Add(Loc.T("打开当前置物架…", "Open current shelf…"), "\uE8A9", _app.OpenShelf);
        if (item.IsFileSystem)
        {
            var share = FileShareDropTarget.SourceMenuItem(item.Path, paths => ShareTrayPaths(paths, Loc.T("分享源文件", "Share original file")));
            share.Style = style; share.Icon = UI.CenteredIcon("\uE72D", 11, PanelTheme.Hex("textSecondary")); menu.Items.Add(share);
        }
        menu.Items.Add(TrayActionsMenu([item]));
        menu.Items.Add(new Separator { Style = separator });
        Add(item.Pinned ? Loc.T("取消固定", "Unpin") : Loc.T("固定", "Pin"), item.Pinned ? "\uE840" : "\uE718", () => ToggleTrayPin(item));
        Add(Loc.T("从托盘移除", "Remove from tray"), "\uE78B", () => RemoveTrayItem(item));
        menu.Items.Add(new Separator { Style = separator });
        Add(Loc.T("清空未固定的项目", "Clear unpinned items"), "\uE74D", ClearTray, Tray.Items.Any(entry => !entry.Pinned));
        card.ContextMenu = menu; _trayMenu = menu;
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
        menu.Closed += (_, _) => { card.ContextMenu = null; if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; PointerChanged(_pointerInside); _app.Shelves?.Shelves.NotifyActivityChanged(); };
        _leave.Stop(); menu.PlacementTarget = card; menu.IsOpen = true;
    }

    // Windows' own way of showing a file: Explorer selects it. The folder's path is opened when the item is a folder.
    private static void ShowTrayItemInFolder(TrayItem item)
    {
        try
        {
            if (item.Kind == ShelfItemKind.Folder) System.Diagnostics.Process.Start("explorer.exe", "\"" + item.Path + "\"");
            else System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    private void ShareTrayPaths(IEnumerable<string> paths, string title)
    {
        if (!_fileShare.TryShare(paths, title, out var problem)) { FileShareProblem(problem); return; }
        _trayKey = "";
        if (IsTrayPage && _expanded) RenderFileTray();
    }

    private void RemoveTrayItem(TrayItem item)
    {
        Tray.Remove(item.Id);
        _trayKey = ""; RenderFileTray();
    }

    // The tray changed: rebuild the page, unless it is not on screen (then the next switch builds it).
    internal void TrayChanged()
    {
        _trayKey = "";
        if (_trayRefreshQueued || !IsTrayPage || !_expanded) return;
        _trayRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _trayRefreshQueued = false;
            if (_trayDragging) EndTrayReorder(false);
            if (IsTrayPage && _expanded) RenderFileTray();
        });
    }

    private void ClearTray()
    {
        if (Tray.Clear(keepPinned: true) > 0) { _trayKey = ""; RenderFileTray(); }
    }

    private void ToggleTrayPin(TrayItem item)
    {
        Tray.TogglePin(item.Id);
        _trayKey = ""; RenderFileTray();
    }

    private void StartTrayDrag(TrayItem item, MouseEventArgs e)
    {
        _trayPressed = null; _trayPressCard = null;
        if (!item.Exists || !_app.ShelfRunning || _app.ModulesChanging) return;
        var shelf = _app.Shelves!.Shelves.ActiveWindow;
        var model = shelf.ViewModel;
        if (model.IsBusy) return;
        using var operation = model.Operations.Enter();
        _trayExporting = true; e.Handled = true;
        try
        {
            var mode = _app.Shelves!.Settings.DragOutMode;
            var result = ShelfDragService.DoDragDrop(this, [item], model.Id, out var dragged, mode);
            if (result == DragOutResult.Cancelled) return;
            if (dragged.Any(entry => entry.IsTemporaryAttachment)) model.KeepTemporaryFilesForAWhile();
            bool closeWhenEmpty = _app.Shelves!.Settings.CloseWhenEmpty;
            model.ApplyDragOut(dragged, result, closeWhenEmpty);
            if (model.IsEmpty && closeWhenEmpty) shelf.CloseAnimated();
        }
        finally { _trayExporting = false; PointerChanged(_pointerInside); _app.Shelves?.Shelves.NotifyActivityChanged(); }
    }

    // The drop well: the shape the whole page takes while a drag is over it. A dashed outline in the accent, one glyph and one
    // short line, so the target is obvious while the pointer is still moving.
    private Grid TrayDropZone(double height)
    {
        // Panel.SplitDropZone: receiving is a distinct target layer; existing content stays legible behind it.
        var zone = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 4, 12, 10), Height = height < 90 ? 26 : 44 };
        var chrome = new Border { BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 4, 12, 4) };
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelAccent"); chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR12");
        var label = ClipboardPreview.Text(Loc.T("松手放入：", "Drop on: ") + Tray.Model.WindowTitle, height < 90 ? 10.5 : 12, "PanelAccent");
        label.TextWrapping = TextWrapping.NoWrap; label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
        chrome.Child = label; zone.Children.Add(chrome); Soft.Enter(zone); return zone;
    }

    // The card that arrived rises the last few DIPs into place as it fades in, the same give the panel's content has.
    private static void Arrive(FrameworkElement card)
    {
        if (!Soft.For(card)) return;
        // What lands in the tray arrives the way the panel's own content does: a fade, a small rise, and a scale that starts
        // just under its size, so it is never seen coming from nothing.
        card.Opacity = 1;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        var scale = new ScaleTransform(.92, .92);
        var shift = new TranslateTransform(0, 6);
        card.RenderTransformOrigin = new Point(.5, .5);
        card.RenderTransform = new TransformGroup { Children = { scale, shift } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
    }

    // Keep the base transform at its final slot; FillBehavior.Stop must not restore the starting offset.
    private static void SlideFrom(FrameworkElement card, double dx, double dy)
    {
        if (!Soft.For(card)) return;
        var shift = new TranslateTransform();
        card.RenderTransform = shift;
        var time = TimeSpan.FromMilliseconds(Soft.Move);
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, time) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, time) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }

    // Hover feedback on the monitor's curve, as Feedback does for buttons (Soft.Hover on Soft.Follow).
    private static void Fade(UIElement layer, double target)
    {
        double from = layer.Opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = target;
        if (!Soft.For(layer)) return;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Soft.Hover)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }

    private void OpenTrayMore(FrameworkElement anchor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu"), PlacementTarget = anchor };
        void Add(string label, Action action)
        {
            var option = new MenuItem { Header = label, Style = (Style)FindResource("TrayMenuItem") };
            option.Click += (_, _) => action(); menu.Items.Add(option);
        }
        Add(Loc.T("选择当前置物架…", "Choose current shelf…"), () => { menu.IsOpen = false; OpenShelfChooser(anchor); });
        Add(Loc.T("新建置物架", "New shelf"), _app.NewShelf);
        Add(Loc.T("打开当前浮动置物架", "Open current floating shelf"), _app.OpenShelf);
        Add(Tray.Items.All(item => item.Pinned) ? Loc.T("全部取消固定", "Unpin everything") : Loc.T("全部固定", "Pin everything"), PinAllTray);
        Add(Settings.Tray.GridView ? Loc.T("横向列表", "Horizontal strip") : Loc.T("网格视图", "Grid view"), ToggleTrayLayout);
        menu.Items.Add(TrayActionsMenu(Tray.Items.ToArray()));
        Add(Loc.T("清空未固定的项目", "Clear unpinned items"), ClearTray);
        Add(Loc.T("置物架设置与最近关闭…", "Shelf settings and recent shelves…"), _app.OpenShelfSettings);
        _trayMenu = menu; menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; PointerChanged(_pointerInside); _app.Shelves?.Shelves.NotifyActivityChanged(); };
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement); menu.IsOpen = true;
    }

    private void OpenShelfChooser(FrameworkElement anchor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu"), PlacementTarget = anchor };
        var manager = _app.Shelves!.Shelves;
        foreach (var shelf in manager.OpenShelves.Where(shelf => !shelf.IsClosing).ToArray())
        {
            var option = new MenuItem { Style = (Style)FindResource("TrayMenuItem"), Header = shelf.ViewModel.WindowTitle,
                IsCheckable = true, IsChecked = ReferenceEquals(shelf.ViewModel, Tray.Model) };
            option.Click += (_, _) => manager.SelectShelf(shelf, activate: true);
            menu.Items.Add(option);
        }
        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuSeparator") });
        var create = new MenuItem { Style = (Style)FindResource("TrayMenuItem"), Header = Loc.T("新建置物架", "New shelf") };
        create.Click += (_, _) => _app.NewShelf(); menu.Items.Add(create);
        _trayMenu = menu; menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; PointerChanged(_pointerInside); _app.Shelves?.Shelves.NotifyActivityChanged(); };
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
        menu.IsOpen = true;
    }

    private MenuItem TrayActionsMenu(IReadOnlyList<TrayItem> targets)
    {
        var group = new MenuItem { Style = (Style)FindResource("TrayMenuItem"), Header = DropShelf.Localization.Strings.QuickActionsTitle };
        var settings = _app.Shelves!.Settings;
        group.Loaded += (_, _) =>
        {
            if (group.Template?.FindName("PART_Popup", group) is System.Windows.Controls.Primitives.Popup popup) PopupMotion.Attach(popup);
        };
        // 直接复用原版操作目录与执行模型；岛内模式不需要借道不可见的浮动窗口。
        foreach (var action in QuickActions.Visible(settings).Concat(QuickActions.Available(settings.CustomActions)).DistinctBy(action => action.Id))
        {
            var option = new MenuItem { Style = (Style)FindResource("TrayMenuItem"), Header = action.Title,
                Icon = UI.CenteredIcon(action.Glyph, 11, PanelTheme.Hex("textSecondary")),
                IsEnabled = !_trayActionBusy && !Tray.Model.IsBusy && targets.Any(item => item.IsFileSystem && item.Exists) };
            option.Click += async (_, _) => await RunTrayActionAsync(action, targets);
            group.Items.Add(option);
        }
        return group;
    }

    private void OpenTrayActions(FrameworkElement anchor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu"), PlacementTarget = anchor };
        var group = TrayActionsMenu(Tray.Items.ToArray());
        var options = group.Items.Cast<MenuItem>().ToArray(); group.Items.Clear();
        foreach (var option in options) menu.Items.Add(option);
        _trayMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; PointerChanged(_pointerInside); _app.Shelves?.Shelves.NotifyActivityChanged(); };
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
        menu.IsOpen = true;
    }

    private async Task RunTrayActionAsync(QuickActionInfo action, IReadOnlyList<TrayItem> targets)
    {
        var model = Tray.Model;
        if (_trayActionBusy || model.IsBusy || !_app.ShelfRunning || _app.ModulesChanging) return;
        using var operation = model.Operations.Enter();
        // 原版选择目标目录时尚未置 IsBusy，也必须保持岛内视图与展示位置不变。
        _trayActionBusy = true; _trayKey = "";
        if (IsTrayPage && _expanded) RenderFileTray();
        try
        {
            var result = await model.RunActionAsync(action, targets, new WindowInteropHelper(this).Handle, () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = DropShelf.Localization.Strings.ChooseMoveFolder, Multiselect = false };
                return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
            });
            if (!result.Cancelled && IsTrayPage && _expanded) ShowTrayNotice(result.Succeeded ? Loc.T("操作已完成", "Action completed") : result.Message);
        }
        finally
        {
            _trayActionBusy = false; _trayKey = "";
            if (IsTrayPage && _expanded) RenderFileTray();
            PointerChanged(_pointerInside);
        }
    }

    private void PinAllTray()
    {
        bool all = Tray.Items.Count > 0 && Tray.Items.All(item => item.Pinned);
        Tray.SetAllPinned(!all);
        _trayKey = ""; RenderFileTray();
    }

    // Dragging a card. Inside the tray the drag only takes a new place among the others: the card is drawn again under the
    // pointer while the rest of the tray closes the gap, and the order is written once when the drag ends. Once the pointer
    // leaves the tray, the same gesture becomes an ordinary file drag that Windows hands to wherever it is dropped.
    private void TrayPointerMove(object sender, MouseEventArgs e)
    {
        if (_trayPressed is null && !_trayDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { if (_trayDragging) EndTrayReorder(false); _trayPressed = null; _trayPressCard = null; return; }
        var point = e.GetPosition(TrayContent);
        var item = (_trayDragging ? _trayDragItem : _trayPressed)!;
        if (!_trayDragging)
        {
            if (Math.Abs(point.X - _trayPressAt.X) < 4 && Math.Abs(point.Y - _trayPressAt.Y) < 4) return;
            if (!InsideTray(point)) { StartTrayDrag(item, e); return; }
            BeginTrayReorder(item, e, point);
        }
        if (!InsideTray(point)) { EndTrayReorder(false); StartTrayDrag(item, e); return; }
        MoveTrayGhost(point);
        ReorderTrayTo(e);
    }

    private bool InsideTray(Point point)
        => point.X >= -6 && point.Y >= -6 && point.X <= TrayContent.ActualWidth + 6 && point.Y <= TrayContent.ActualHeight + 6;

    private void BeginTrayReorder(TrayItem item, MouseEventArgs e, Point point)
    {
        if (_trayPressCard is null || _trayGhostLayer is null) return;
        var card = _trayPressCard;
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return;
        if (Soft.For(TrayContent))
        {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        _trayGrab = e.GetPosition(card);
        _trayGhost = new Image { Source = bitmap, Width = card.ActualWidth, Height = card.ActualHeight, Opacity = .92, IsHitTestVisible = false };
        _trayGhostLayer.Children.Add(_trayGhost);
        }
        _trayDragCard = card; _trayDragCard.Opacity = .35;
        ++_trayReflow;
        _trayOriginalOrder = Tray.Items.Select(value => value.Id).ToArray();
        _trayDragItem = item; _trayDragging = true;
        TrayContent.CaptureMouse();
        MoveTrayGhost(point);
    }

    private void MoveTrayGhost(Point point)
    {
        if (_trayGhost is null) return;
        Canvas.SetLeft(_trayGhost, point.X - _trayGrab.X);
        Canvas.SetTop(_trayGhost, point.Y - _trayGrab.Y);
    }

    // The place it is over takes it: the item moves into that slot and the grid lays the cards out again, so the tray reads as
    // it will when the drag ends. Nothing is rebuilt and nothing is written until the drag does end.
    private void ReorderTrayTo(MouseEventArgs e)
    {
        if (_trayGrid is null || _trayDragItem is null) return;
        int target = -1;
        for (int i = 0; i < _trayGrid.Children.Count; i++)
        {
            if (_trayGrid.Children[i] is not FrameworkElement cell) continue;
            var local = e.GetPosition(cell);
            if (cell.Tag is TrayItem over && local.X >= 0 && local.X <= cell.ActualWidth && local.Y >= 0 && local.Y <= cell.ActualHeight) { target = Tray.IndexOf(over.Id); break; }
        }
        if (target < 0) return;
        int from = Tray.IndexOf(_trayDragItem.Id);
        if (from < 0 || from == target || !Tray.Reorder(from, target)) return;
        _trayResults = QueryTrayItems();
        // The cards that give way slide from where they were to where they are now, so the tray reads as one surface
        // shifting rather than a set of cells jumping.
        var before = new Dictionary<FrameworkElement, Point>();
        foreach (var child in _trayGrid.Children.OfType<FrameworkElement>())
            before[child] = child.TranslatePoint(new Point(0, 0), _trayGrid);
        // The cards themselves take the new order, not only their slots: a grid's children are not sorted by their row and
        // column, so the collection is put in the tray's order and the slots follow from it.
        foreach (var child in _trayGrid.Children.OfType<FrameworkElement>())
        {
            int index = child.Tag is TrayItem value ? _trayResults.FindIndex(item => item.Id == value.Id) : -1;
            if (index >= 0) { Grid.SetColumn(child, Settings.Tray.GridView ? index % _trayColumns : index); Grid.SetRow(child, Settings.Tray.GridView ? index / _trayColumns : 0); }
        }
        foreach (var child in _trayGrid.Children.OfType<FrameworkElement>()) if (child != _trayDragCard) child.RenderTransform = Transform.Identity;
        _trayGrid.UpdateLayout();
        foreach (var child in _trayGrid.Children.OfType<FrameworkElement>())
        {
            if (child == _trayDragCard || !before.TryGetValue(child, out var was)) continue;
            var here = child.TranslatePoint(new Point(0, 0), _trayGrid);
            double dx = was.X - here.X, dy = was.Y - here.Y;
            if (Math.Abs(dx) > .5 || Math.Abs(dy) > .5) SlideFrom(child, dx, dy);
        }
    }

    private void TrayPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_trayDragging) { EndTrayReorder(true); e.Handled = true; }
        _trayPressed = null; _trayPressCard = null;
    }

    private void EndTrayReorder(bool commit)
    {
        if (!_trayDragging) return;
        _trayDragging = false;
        TrayContent.ReleaseMouseCapture();
        if (_trayGhost is not null && _trayGhostLayer is not null) _trayGhostLayer.Children.Remove(_trayGhost);
        _trayGhost = null;
        if (_trayDragCard is not null) _trayDragCard.Opacity = 1;
        if (!commit && _trayOriginalOrder is { } original)
            for (int i = 0; i < original.Length; i++) { int from = Tray.IndexOf(original[i]); if (from >= 0 && i < Tray.Count && from != i) Tray.Reorder(from, i); }
        _trayOriginalOrder = null;
        _trayDragCard = null; _trayDragItem = null; _trayDragging = false;
        LayoutTrayCards();
        // One write at the end of the drag: the app saves the tray and every panel rebuilds its page from the order the
        // cards now have.
        if (commit) { _trayKey = ""; Tray.Announce(); }
        _app.Shelves?.Shelves.NotifyActivityChanged();
    }

}
