using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// The file tray page: what the user is moving waits here as a grid of cards. A card is the file's thumbnail when it has
// one, its kind's glyph otherwise, so a page of pictures and a page of documents do not read the same way.
// The grid adapts: the card size comes from the width it has and the number of items, and a short row is centred rather
// than stretched across the panel.
public partial class MainWindow
{
    private FileTray Tray => _app.Tray;
    private Border? _trayFrame;
    private Grid? _trayOverlay;
    private Button? _trayClear;
    private string _trayKey = "";
    // The item that arrived last: only it plays the arrival, and the grid scrolls to it.
    private string? _trayArrived;
    // Where a press started, so a click on a card is not taken for a drag.
    private Point _trayPressAt;
    private TrayItem? _trayPressed;
    private FrameworkElement? _trayPressCard;
    // A drag of a card: inside the tray it takes a new place, outside it becomes an ordinary file drag.
    private bool _trayDragging;
    private TrayItem? _trayDragItem;
    private FrameworkElement? _trayDragCard;
    private Grid? _trayGrid;
    private Canvas? _trayGhostLayer;
    private Image? _trayGhost;
    private Point _trayGrab;
    private int _trayColumns = 1;
    private bool _trayWired;
    private DispatcherTimer? _trayRetention;
    // Thumbnails are kept by path and write time, so rebuilding the page never decodes the same picture twice.
    private static readonly Dictionary<string, (DateTime Stamp, BitmapSource? Image)> TrayThumbs = [];
    private static readonly string[] ThumbExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico"];

    private void RenderFileTray()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; TrayContent.Visibility = Visibility.Visible;
        MonitorContent.Visibility = Visibility.Collapsed;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        SyncButton.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        TrayContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));

        // What the page is built from: the items and their state, not their age. A card's "3 分钟前" is refreshed by the retention
        // timer below instead of rebuilding the grid every minute.
        if (!_trayWired)
        {
            _trayWired = true;
            TrayContent.PreviewMouseMove += TrayPointerMove;
            TrayContent.PreviewMouseLeftButtonUp += TrayPointerUp;
        }

        // Existence is swept once, here, rather than asked per item per question further down (FileTray.RefreshExistence).
        Tray.RefreshExistence(DateTimeOffset.Now);
        string key = string.Join("|", Tray.Items.Select(item => $"{item.Id}:{item.Pinned}:{item.Exists}")) + $"#{width:0}x{height:0}";
        if (key == _trayKey && TrayContent.Children.Count > 0) return;
        _trayKey = key;
        BuildFileTrayPage(width, height);
        UpdateTrayRetentionTimer();
    }

    private void BuildFileTrayPage(double width, double height)
    {
        // The monitor page's own type scale (MonitorLayout.For): a tray of cards is the same reading surface, and it should not
        // carry larger type than the pages beside it.
        double body = height < 120 ? 11 : 12, caption = height < 120 ? 10 : 10.5, gap = 10;
        double cardWidth = TrayCardWidth(width, gap), thumb = cardWidth - 16;
        TrayContent.Children.Clear();

        var frame = new Border
        {
            Background = UI.Brush(PanelTheme.Hex("fill")),
            CornerRadius = new CornerRadius(PanelTheme.Corner(PanelHeader.PanelRadius - 8)),
            Padding = new Thickness(8, 6, 8, 6), AllowDrop = true,
        };
        frame.DragOver += TrayDragOver; frame.DragEnter += TrayDragOver;
        frame.DragLeave += (_, _) => TrayDragHighlight(false);
        frame.Drop += TrayDrop;
        _trayFrame = frame;

        var host = new Grid();
        var page = new DockPanel();

        // One line above the grid: what is held, and the one action the page needs. Both sit on the grid's own left and right
        // edges, so the page keeps a single pair of axes, as the music page does.
        var bar = new Grid { Margin = new Thickness(2, 0, 2, 5) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var count = UI.Centered(TrayLabel(), caption, PanelTheme.Hex("textTertiary"));
        count.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(count, 0); bar.Children.Add(count);
        var capsule = TrayCapsule();
        Grid.SetColumn(capsule, 1); bar.Children.Add(capsule);
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);

        // Cards in rows of `columns`; a row that is not full is centred, so three files read as a tray.
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        int columns = TrayColumns(width, cardWidth, gap), rows = (Tray.Count + columns - 1) / columns;
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cardWidth) });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < Tray.Count; i++)
        {
            var card = TrayCard(Tray.Items[i], cardWidth, body, thumb);
            Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns);
            grid.Children.Add(card);
        }
        page.Children.Add(grid);
        _trayGrid = grid; _trayColumns = columns;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = page, Padding = new Thickness(0, 0, 2, 0), Focusable = false,
        };
        host.Children.Add(scroll);
        // The card being dragged is drawn here, above the grid, so it can leave its own place and follow the pointer.
        _trayGhostLayer = new Canvas { IsHitTestVisible = false };
        host.Children.Add(_trayGhostLayer);

        // The drop well, above everything while a drag is over the page: one glyph, one line, the accent's outline.
        _trayOverlay = TrayDropZone(height);
        host.Children.Add(_trayOverlay);

        frame.Child = host;
        TrayContent.Children.Add(frame);

        PruneTrayThumbs();
        bool empty = Tray.Count == 0;
        if (_trayClear is { } clear) { clear.IsEnabled = !empty; clear.Opacity = empty ? .4 : 1; }
        if (_trayArrived is not null) { scroll.ScrollToEnd(); _trayArrived = null; }
    }

    private string TrayLabel()
    {
        int count = Tray.Count, pinned = Tray.PinnedCount, missing = Tray.MissingCount;
        if (count == 0) return Loc.T("文件中转", "File tray");
        string text = Loc.T($"{count} 个项目", Loc.Count(count, "item", "items"));
        if (pinned > 0) text += Loc.T($" · {pinned} 个已固定", $" · {pinned} pinned");
        if (missing > 0) text += Loc.T($" · {missing} 个已失效", $" · {missing} missing");
        return text;
    }

    // A card holds a thumbnail and a name. The picture is the file's own when it has one; a folder shows its glyph on a
    // quiet track, as do files the panel would rather not open to look inside.
    private FrameworkElement TrayCard(TrayItem item, double cardWidth, double body, double thumbSize)
    {
        bool exists = item.Exists, arrived = item.Id == _trayArrived;
        var card = new Grid { Width = cardWidth, Background = Brushes.Transparent, AllowDrop = true, Tag = item };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The picture, cropped to fill the card: a picture reads by its picture, not by its frame.
        var frame = new Border
        {
            Width = thumbSize, Height = thumbSize, CornerRadius = new CornerRadius(PanelTheme.Corner(Math.Clamp(thumbSize * .12, 6, 12))),
            Background = UI.Brush(PanelTheme.Hex("track")), ClipToBounds = true,
        };
        var image = exists ? TrayThumb(item) : null;
        frame.Child = image is not null
            ? (UIElement)new Image { Source = image, Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true }
            : new Grid { Children = { UI.CenteredIcon(item.Kind == TrayItemKind.Folder ? "\uE8B7" : "\uE7C3", Math.Clamp(thumbSize * .42, 16, 30),
                PanelTheme.Hex(item.Pinned ? "accent" : "textSecondary")) } };
        if (!exists) frame.Opacity = .4;

        var picture = new Grid { Width = thumbSize, Height = thumbSize, HorizontalAlignment = HorizontalAlignment.Center };
        picture.Children.Add(frame);
        // Pinned: a small accent dot at the top left, always shown.
        if (item.Pinned) picture.Children.Add(PinBadge());
        var remove = TrayRemoveButton(item);
        picture.Children.Add(remove);
        Grid.SetRow(picture, 0); card.Children.Add(picture);

        var name = UI.Line(item.Name, body, PanelTheme.Hex(exists ? "textPrimary" : "textTertiary"));
        name.TextAlignment = TextAlignment.Center; name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Width = cardWidth; name.Margin = new Thickness(0, 5, 0, 0);
        name.ToolTip = UI.LeftwardToolTip(exists ? item.Path : item.Path + Loc.T("（已失效）", " (missing)"));
        if (!exists) name.Text = Loc.T(item.Name + "（已失效）", item.Name + " (missing)");
        Grid.SetRow(name, 1); card.Children.Add(name);

        card.MouseEnter += (_, _) => Fade(remove, 1);
        card.MouseLeave += (_, _) => Fade(remove, 0);
        card.PreviewMouseLeftButtonDown += (_, e) => { _trayPressAt = e.GetPosition(TrayContent); _trayPressed = item; _trayPressCard = card; };
        // Handled: the window's own right-click opens settings, and a card's menu must not open both.
        card.MouseRightButtonUp += (_, e) => { OpenTrayMenu(card, item); e.Handled = true; };
        card.DragOver += TrayDragOver; card.Drop += TrayDrop; card.DragLeave += (_, _) => TrayDragHighlight(false);
        if (arrived) Arrive(card);
        return card;
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
        Add(Loc.T("打开", "Open"), "\uE8A7", () => OpenTrayItem(item), item.Exists);
        Add(Loc.T("打开所在文件夹", "Show in folder"), "\uE8B7", () => ShowTrayItemInFolder(item), item.Exists);
        menu.Items.Add(new Separator { Style = separator });
        Add(item.Pinned ? Loc.T("取消固定", "Unpin") : Loc.T("固定", "Pin"), item.Pinned ? "\uE840" : "\uE718", () => ToggleTrayPin(item));
        Add(Loc.T("从托盘移除", "Remove from tray"), "\uE78B", () => RemoveTrayItem(item));
        menu.Items.Add(new Separator { Style = separator });
        Add(Loc.T("清空未固定的项目", "Clear unpinned items"), "\uE74D", ClearTray, Tray.Items.Any(entry => !entry.Pinned));
        card.ContextMenu = menu;
        menu.Closed += (_, _) => { card.ContextMenu = null; };
        menu.IsOpen = true;
    }

    private static void OpenTrayItem(TrayItem item)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    // Windows' own way of showing a file: Explorer selects it. The folder's path is opened when the item is a folder.
    private static void ShowTrayItemInFolder(TrayItem item)
    {
        try
        {
            if (item.Kind == TrayItemKind.Folder) System.Diagnostics.Process.Start("explorer.exe", "\"" + item.Path + "\"");
            else System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    private void RemoveTrayItem(TrayItem item)
    {
        Tray.Remove(item.Id);
        _trayKey = ""; RenderFileTray();
    }

    // The menu's glyph, in the panel's small-icon size.
    private static FrameworkElement DropGlyph(double size, double opacity)
    {
        var glyph = UI.CenteredIcon("\uE8B7", size, PanelTheme.Hex("accent"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.Opacity = opacity;
        return glyph;
    }

    // The pin: a small accent square with the pin drawn inside it, at the card's top left corner.
    private static FrameworkElement PinBadge()
    {
        var badge = new Border
        {
            Background = UI.Brush(PanelTheme.Hex("accent")), CornerRadius = new CornerRadius(PanelTheme.Corner(6)),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-3, -3, 0, 0), IsHitTestVisible = false,
            Child = UI.CenteredIcon("\uE840", 9, PanelTheme.Hex("onAccent")),
        };
        return badge;
    }

    private Button TrayRemoveButton(TrayItem item)
    {
        var button = new Button
        {
            Content = UI.CenteredIcon("\uE78B", 10, PanelTheme.Hex("textSecondary")),
            Background = UI.Brush(PanelTheme.Hex("popup")), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Width = 18, Height = 18, Cursor = Cursors.Hand, Focusable = false, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0),
        };
        AutomationProperties.SetName(button, Loc.T("从托盘移除", "Remove from the tray"));
        button.ToolTip = UI.LeftwardToolTip(Loc.T("从托盘移除", "Remove from the tray"));
        button.Click += (_, _) => RemoveTrayItem(item);
        return button;
    }

    private void TrayDragHighlight(bool on)
    {
        if (_trayOverlay is { } overlay) overlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // The tray changed: rebuild the page, unless it is not on screen (then the next switch builds it).
    internal void TrayChanged()
    {
        _trayKey = "";
        // A drag holds a picture of the card and the capture of the pointer; both belong to the page this change rebuilds.
        if (_trayDragging) EndTrayReorder(false);
        if (IsTrayPage && _expanded) RenderFileTray();
    }

    private void ClearTray()
    {
        if (Tray.Clear(keepPinned: true) > 0) { _trayKey = ""; RenderFileTray(); }
    }

    // The cache holds a picture per path; the ones for items that are gone are let go with them.
    private void PruneTrayThumbs()
    {
        var kept = Tray.Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in TrayThumbs.Keys.Where(path => !kept.Contains(path)).ToList()) TrayThumbs.Remove(path);
    }

    private void ToggleTrayPin(TrayItem item)
    {
        Tray.TogglePin(item.Id);
        _trayKey = ""; RenderFileTray();
    }

    private void StartTrayDrag(TrayItem item, MouseEventArgs e)
    {
        _trayPressed = null; _trayPressCard = null;
        if (!item.Exists) return;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        var allowed = Settings.Tray.ProtectOriginals ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move;
        var done = DragDrop.DoDragDrop((DependencyObject)e.Source, data, allowed);
        if (done == DragDropEffects.None) return;
        bool moved = done.HasFlag(DragDropEffects.Move) && !File.Exists(item.Path) && !Directory.Exists(item.Path);
        if (moved || Settings.Tray.AutoRemoveAfterDragOut) { Tray.Remove(item.Id); _trayKey = ""; RenderFileTray(); }
    }

    private void TrayDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        TrayDragHighlight(files);
        e.Handled = true;
    }

    private void TrayDrop(object sender, DragEventArgs e)
    {
        TrayDragHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        int before = Tray.Count;
        if (Tray.Add(paths, DateTimeOffset.Now) == 0) return;
        _trayArrived = Tray.Items.Count > before ? Tray.Items[^1].Id : null;
        _trayKey = "";
        RenderFileTray();
        if (Settings.Tray.OpenAfterDrop && !IsTrayPage) SelectPage(DeckPage.FileTray);
    }

    // A card holds a thumbnail and a name: at least 78 wide, never more than 108, and as many as the panel's width fits of the
    // narrower of the two. Six cards across the default panel, three across the narrowest.
    private static double TrayCardWidth(double width, double gap)
    {
        double inner = Math.Max(0, width - 16);
        int fits = Math.Max(1, (int)Math.Floor((inner + gap) / (78 + gap)));
        return Math.Clamp(Math.Round(inner / fits), 78, 108);
    }

    // How many cards a row holds: as many as fit, never more than there are items, so a short row is a short row and not a
    // column of one.
    private int TrayColumns(double width, double cardWidth, double gap)
    {
        if (Tray.Count == 0) return 1;
        int fits = Math.Max(1, (int)Math.Floor((width - 16 + gap) / (cardWidth + gap)));
        return Math.Min(fits, Tray.Count);
    }

    // The drop well: the shape the whole page takes while a drag is over it. A dashed outline in the accent, one glyph and one
    // short line, so the target is obvious while the pointer is still moving.
    private Grid TrayDropZone(double height)
    {
        var zone = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent, IsHitTestVisible = false };
        zone.Children.Add(new Rectangle
        {
            Stroke = UI.Brush(PanelTheme.Hex("accent")), StrokeThickness = 1.5, StrokeDashArray = [4, 3],
            RadiusX = PanelTheme.Corner(PanelHeader.PanelRadius - 8), RadiusY = PanelTheme.Corner(PanelHeader.PanelRadius - 8),
            Fill = UI.Brush(PanelTheme.Hex("fill")), Opacity = .96,
        });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(DropGlyph(26, 1));
        var title = UI.Centered(Loc.T("松手放入中转", "Release to add to the tray"), 13.5, PanelTheme.Hex("accent"));
        title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(title);
        if (height >= 200)
        {
            var hint = UI.Centered(Loc.T("文件、文件夹都可以", "Files and folders"), 10.5, PanelTheme.Hex("textTertiary"));
            hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(hint);
        }
        zone.Children.Add(stack);
        return zone;
    }

    // The card that arrived rises the last few DIPs into place as it fades in, the same give the panel's content has.
    private static void Arrive(FrameworkElement card)
    {
        if (!Soft.Enabled) return;
        // What lands in the tray arrives the way the panel's own content does: a fade, a small rise, and a scale that starts
        // just under its size, so it is never seen coming from nothing.
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        var scale = new ScaleTransform(.92, .92);
        var shift = new TranslateTransform(0, 6);
        card.RenderTransformOrigin = new Point(.5, .5);
        card.RenderTransform = new TransformGroup { Children = { scale, shift } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.92, 1, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(Soft.Short)) { EasingFunction = Soft.Out, FillBehavior = FillBehavior.Stop });
    }

    // A card that gives its place up slides the distance it is moved over the hover time, on the monitor's curve.
    private static void SlideFrom(FrameworkElement card, double dx, double dy)
    {
        if (!Soft.Enabled) return;
        var shift = new TranslateTransform(dx, dy);
        card.RenderTransform = shift;
        var time = TimeSpan.FromMilliseconds(Soft.Hover);
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, time) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, time) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }

    // Hover feedback on the monitor's curve, as Feedback does for buttons (Soft.Hover on Soft.Follow).
    private static void Fade(UIElement layer, double target)
    {
        double from = layer.Opacity;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = target;
        if (!Soft.Enabled) return;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Soft.Hover)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }

    // A bare glyph button the size of the panel's own small icons.
    private Button TrayGlyphButton(string glyph, string name, Action click, string? color = null)
    {
        var button = new Button
        {
            Content = UI.CenteredIcon(glyph, 11, color ?? PanelTheme.Hex("textTertiary")),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Width = 20, Height = 20, Cursor = Cursors.Hand, Focusable = false,
        };
        AutomationProperties.SetName(button, name);
        button.ToolTip = UI.LeftwardToolTip(name);
        button.Click += (_, _) => click();
        return button;
    }

    // The page's quick actions in one capsule at the top right, as the reference's tray pill: pin or unpin everything, and
    // clear what is not pinned. The tally rides on the trash, so the count is where the action that empties it is.
    private FrameworkElement TrayCapsule()
    {
        var capsule = new Border
        {
            Background = UI.Brush(PanelTheme.Hex("switchFill")),
            CornerRadius = new CornerRadius(PanelTheme.Corner(14)),
            Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center,
        };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        bool all = Tray.Items.Count > 0 && Tray.Items.All(item => item.Pinned);
        strip.Children.Add(TrayCapsuleButton(all ? "\uE77A" : "\uE840",
            all ? Loc.T("全部取消固定", "Unpin everything") : Loc.T("全部固定", "Pin everything"), PinAllTray, enabled: Tray.Count > 0));

        var trashHost = new Grid { Width = 22, Height = 22 };
        var trash = TrayGlyphButton("\uE74D", Loc.T("清空未固定的项目", "Clear unpinned items"), ClearTray);
        _trayClear = trash;
        trashHost.Children.Add(trash);
        if (Tray.Count > 0)
        {
            var badge = new Border
            {
                Background = UI.Brush(PanelTheme.Hex("accent")), CornerRadius = new CornerRadius(PanelTheme.Corner(6)),
                MinWidth = 13, Height = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, -3, -3, 0),
                Child = UI.Centered(Tray.Count > 99 ? "99+" : Tray.Count.ToString(), 8.5, PanelTheme.Hex("onAccent")),
            };
            trashHost.Children.Add(badge);
        }
        strip.Children.Add(trashHost);
        capsule.Child = strip;
        return capsule;
    }

    private FrameworkElement TrayCapsuleButton(string glyph, string name, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Content = UI.CenteredIcon(glyph, 11, PanelTheme.Hex("textSecondary")),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Width = 20, Height = 20, Cursor = Cursors.Hand, Focusable = false, IsEnabled = enabled, Margin = new Thickness(1, 0, 1, 0),
        };
        AutomationProperties.SetName(button, name);
        button.ToolTip = UI.LeftwardToolTip(name);
        button.Click += (_, _) => click();
        return button;
    }

    private void PinAllTray()
    {
        bool all = Tray.Items.Count > 0 && Tray.Items.All(item => item.Pinned);
        Tray.SetAllPinned(!all);
        _trayKey = ""; RenderFileTray();
    }

    // A picture of the file, decoded at the size a card draws and frozen. Read with Delete and ReadWrite sharing, so holding a
    // thumbnail never stops the user from moving or deleting the file.
    private static BitmapSource? TrayThumb(TrayItem item)
    {
        if (!item.Exists || item.Kind != TrayItemKind.File) return null;
        string ext = System.IO.Path.GetExtension(item.Path);
        if (!ThumbExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(item.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        if (TrayThumbs.TryGetValue(item.Path, out var held) && held.Stamp == stamp) return held.Image;
        var image = DecodeTrayThumb(item.Path);
        if (TrayThumbs.Count > 96) TrayThumbs.Clear();
        TrayThumbs[item.Path] = (stamp, image);
        return image;
    }

    private static BitmapSource? DecodeTrayThumb(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 64 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var memory = new MemoryStream((int)Math.Min(info.Length, int.MaxValue));
            stream.CopyTo(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
    }

    // Dragging a card. Inside the tray the drag only takes a new place among the others: the card is drawn again under the
    // pointer while the rest of the tray closes the gap, and the order is written once when the drag ends. Once the pointer
    // leaves the tray, the same gesture becomes an ordinary file drag that Windows hands to wherever it is dropped.
    private void TrayPointerMove(object sender, MouseEventArgs e)
    {
        if (_trayPressed is null && !_trayDragging) return;
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
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        _trayGrab = e.GetPosition(card);
        _trayGhost = new Image { Source = bitmap, Width = card.ActualWidth, Height = card.ActualHeight, Opacity = .92, IsHitTestVisible = false };
        _trayGhostLayer.Children.Add(_trayGhost);
        _trayDragCard = card; _trayDragCard.Opacity = .35;
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
            if (local.X >= 0 && local.X <= cell.ActualWidth && local.Y >= 0 && local.Y <= cell.ActualHeight) { target = i; break; }
        }
        if (target < 0) return;
        int from = Tray.IndexOf(_trayDragItem.Id);
        if (from < 0 || from == target || !Tray.Reorder(from, target)) return;
        // The cards that give way slide from where they were to where they are now, so the tray reads as one surface
        // shifting rather than a set of cells jumping.
        var before = new Dictionary<FrameworkElement, Point>();
        foreach (var child in _trayGrid.Children.OfType<FrameworkElement>())
            before[child] = child.TranslatePoint(new Point(0, 0), _trayGrid);
        // The cards themselves take the new order, not only their slots: a grid's children are not sorted by their row and
        // column, so the collection is put in the tray's order and the slots follow from it.
        var cells = _trayGrid.Children.OfType<FrameworkElement>().ToList();
        var taken = cells[from];
        cells.RemoveAt(from);
        cells.Insert(target, taken);
        _trayGrid.Children.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            Grid.SetColumn(cells[i], i % _trayColumns); Grid.SetRow(cells[i], i / _trayColumns);
            _trayGrid.Children.Add(cells[i]);
        }
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
        TrayContent.ReleaseMouseCapture();
        if (_trayGhost is not null && _trayGhostLayer is not null) _trayGhostLayer.Children.Remove(_trayGhost);
        _trayGhost = null;
        if (_trayDragCard is not null) _trayDragCard.Opacity = 1;
        _trayDragCard = null; _trayDragItem = null; _trayDragging = false;
        // One write at the end of the drag: the app saves the tray and every panel rebuilds its page from the order the
        // cards now have.
        if (commit) { _trayKey = ""; Tray.Announce(); }
    }

    // Retention: unpinned items older than the chosen time go. The timer runs only while the page is on screen and there is
    // something for it to do, so an idle island still has nothing ticking.
    private void UpdateTrayRetentionTimer()
    {
        bool wanted = IsTrayPage && _expanded && Settings.Tray.Retention > TimeSpan.Zero && Tray.Items.Any(item => !item.Pinned);
        if (!wanted) { _trayRetention?.Stop(); return; }
        _trayRetention ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        if (!_trayRetention.IsEnabled)
        {
            _trayRetention.Tick -= TrayRetentionTick;
            _trayRetention.Tick += TrayRetentionTick;
            _trayRetention.Start();
        }
    }

    private void TrayRetentionTick(object? sender, EventArgs e)
    {
        if (Tray.Expire(Settings.Tray.Retention, DateTimeOffset.Now) > 0) { _trayKey = ""; RenderFileTray(); }
        else UpdateTrayRetentionTimer();
    }
}
