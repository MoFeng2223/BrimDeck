using Path = System.Windows.Shapes.Path;
using BrimDeck.Native;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Documents;
using System.Windows.Automation;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// Additive file-only page; storage and shell operations remain owned by ShelfService.
public partial class MainWindow
{
    private bool _transferStale;
    private ShelfBuild? _transferBuilt;
    private double _transferScroll;
    private Grid? _transferRoot;
    private TransferDropView? _transferDropView;
    private void TransferPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Transfer);


    private void InitializeTransfer()
    {
        _app.Shelf.Changed += TransferChanged;
        TransferFiles.Changed += TransferChanged;
        Closed += (_, _) => { _app.Shelf.Changed -= TransferChanged; TransferFiles.Changed -= TransferChanged; _movingTimer?.Stop(); };
        HoverZone.AllowDrop = true;
        HoverZone.PreviewDragEnter += TransferDragEnter;
        HoverZone.PreviewDragOver += TransferDragOver;
        HoverZone.PreviewDragLeave += TransferDragLeave;
        HoverZone.PreviewDrop += TransferDrop;
    }

    private void TransferChanged()
    {
        _transferStale = true;
        if (IsTransferPage && _expanded && !_preview) RenderTransfer();
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        if (count != _transferShown && !_expanded) RenderCompact();
    }

    private void RenderTransfer(bool lazy = false)
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed;
        MusicContent.Visibility = Visibility.Collapsed;
        MonitorContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Collapsed;
        ClipboardContent.Visibility = Visibility.Collapsed;
        TransferCount.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed;
        ApplicationPages.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var layout = _shelfLayout = ShelfLayout.For(width, height);
        var header = PanelHeader.For(ToolbarFor(height), layout.Side);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        TransferCount.Margin = new Thickness(layout.Side, height < 150 ? 0 : 4, layout.Side, layout.Tier == ShelfTier.Compact ? 8 : 12);
        var build = new ShelfBuild(width, height, Settings, PanelTheme.Generation, Loc.Language, DateTime.Now.Ticks / TimeSpan.TicksPerMinute, VisualTreeHelper.GetDpi(this).DpiScaleY, _scale);
        bool resizingCards = Resizing && _resizePage == DeckPage.Transfer && _shelfDetail is null
            && _transferRoot is not null && TransferCount.Children.Contains(_transferRoot);
        bool keep = resizingCards || lazy && !_transferStale && build == _transferBuilt && TransferCount.Children.Count > 0;
        if (lazy && !keep && !_expanded && !_preview) { _transferStale = true; return; }
        RenderShelfToolbar(width);
        if (_shelfDetail is { } detail && Find(detail.Side, detail.Id) is not null)
        {
            if (!keep)
            {
                TransferCount.Children.Clear();
                TransferCount.Children.Add(DetailView(detail.Side, detail.Id, width - 2 * layout.Side, layout));
            }
        }
        else
        {
            _shelfDetail = null;
            ClipboardSearchButton.Visibility = Visibility.Collapsed;
            ShelfToolbar.Children.Clear();
            RenderPageSwitch();
            if (!keep) BuildTransferContent(width, layout);
        }
        _transferStale = false;
        _transferBuilt = build;
    }

    private void BuildTransferContent(double width, ShelfLayout layout)
    {

        ShelfToolbar.Children.Clear();
        ClipboardSearchButton.Visibility = Visibility.Collapsed;
        RenderPageSwitch();
        TransferToolbar(layout);
        TransferCount.Children.Clear();
        var root = _transferRoot = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var scroll = new TransferScrollViewer(Settings.TransferAnimation) { IsLayoutResizing = () => Resizing, Content = row, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        root.Children.Add(scroll);


        scroll.ScrollChanged += (_, _) => _transferScroll = scroll.HorizontalOffset;
        var offset = _transferScroll;
        scroll.Loaded += (_, _) => scroll.ScrollToHorizontalOffset(offset);
        // One row at every height. Narrow panels show a partial next card as a scroll cue.
        double cardWidth = 144;
        _shelfRows.Clear();
        foreach (var file in ShelfData.Files)
        {
            var body = new Grid { Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(new Image { Source = TransferFiles.Icon(file.Path), Height = 48, Width = 48, Margin = new Thickness(0, 0, 0, 6) });
            var name = new TextBlock { Text = file.Name, Foreground = UI.Brush(UI.Primary), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center };
            name.ToolTip = file.Path; ToolTipService.SetInitialShowDelay(name, 650);
            Grid.SetRow(name, 1); body.Children.Add(name);
            bool exists = TransferFiles.Exists(file.Path);
            string facts = !exists ? Loc.T("已不存在", "Missing")
                : TransferFiles.IsFolder(file.Path) ? FolderFacts(file.Path)
                : TransferFiles.FileSize(file.Path) is { } bytes ? Shelf.Size(bytes) : "—";
            var footer = new Grid { Height = 26, Margin = new Thickness(0, 2, 0, 0) };
            var size = new TextBlock { Text = facts, Foreground = UI.Brush(UI.Tertiary), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(TransferTool(MenuIcons.Copy, Loc.T("复制文件", "Copy file"), () =>
            {
                if (!TransferFiles.Exists(file.Path)) { Flash(file.Id, Loc.T("文件已不存在", "File missing")); return; }
                Flash(file.Id, _app.Shelf.Put(ShelfService.FileData([file.Path]))
                    ? Loc.T("已复制", "Copied") : Loc.T("复制失败，请重试", "Copy failed"));
            }));
            actions.Children.Add(TransferTool(MenuIcons.FilePath, Loc.T("复制路径", "Copy path"), () =>
                Flash(file.Id, _app.Shelf.Put(ShelfService.TextData(file.Path))
                    ? Loc.T("已复制路径", "Path copied") : Loc.T("复制失败，请重试", "Copy failed"))));
            actions.Children.Add(TransferTool(MenuIcons.Details, Loc.T("查看详情", "Details"), () => OpenDetail(ShelfSide.Files, file.Id)));
            actions.Children.Add(TransferTool(BinIcon, Loc.T("从暂存区移除", "Remove from shelf"), () => RemoveFiles([file])));
            var more = new Border { Visibility = Visibility.Hidden, Child = new Viewbox {
                Child = actions, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            footer.Children.Add(size); footer.Children.Add(more);
            footer.Tag = new RowEnd(size, more);
            Grid.SetRow(footer, 2); body.Children.Add(footer);
            var card = new Border { Width = cardWidth, Height = cardWidth, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = UI.Brush(UI.Hairline), Background = Brushes.Transparent, Child = body, Cursor = Cursors.Hand, Focusable = true, RenderTransform = new TranslateTransform() };
            System.Windows.Automation.AutomationProperties.SetName(card, file.Name);
            _shelfRows[file.Id] = (card, footer);
            AttachRow(card, footer, ShelfSide.Files, file.Id, _picked.Contains(file.Id),
                () => ClickFile(file), () => FileMenu(file), null);
            AttachFileDrag(card, () => TransferFiles.Exists(file.Path) ? [file] : []);
            card.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && ReferenceEquals(e.OriginalSource, card)) { ClickFile(file); e.Handled = true; }
            };
            card.GotKeyboardFocus += (_, _) => { if (_flash?.Id != file.Id) ShowMore(footer, true); };
            card.LostKeyboardFocus += (_, _) => { if (!card.IsKeyboardFocusWithin && !card.IsMouseOver && _flash?.Id != file.Id) ShowMore(footer, false); };
            if (_flash is { } flash && flash.Id == file.Id && flash.Until > Environment.TickCount64)
                ShowFlash(card, footer, flash.Text);
            row.Children.Add(card);
        }
        scroll.Opacity = _dropping ? .12 : 1;
        bool empty = row.Children.Count == 0;
        if (empty) scroll.Visibility = Visibility.Collapsed;
        _fileFrame = null;
        _transferDropView = new TransferDropView(empty);
        root.Children.Add(_transferDropView);
        _transferDropView.SetDragging(_dropping);
        // Like the device tiles, keep the three lines together; extra panel height surrounds
        // the tile instead of stretching the gap between its icon and name.
        void FitSquares()
        {
            if (root.ActualHeight <= 0) return;
            // Reserve scrollbar room consistently so appearing/disappearing bars cannot
            // alternate the tile size at the overflow threshold.
            double side = Math.Min(144, Math.Max(48, root.ActualHeight - SystemParameters.HorizontalScrollBarHeight - 12));
            foreach (var tile in row.Children.OfType<Border>())
            {
                tile.Width = tile.Height = side;
                if (tile.Child is not Grid content || content.Children[0] is not Image icon) continue;
                var title = (TextBlock)content.Children[1];
                var footer = (Grid)content.Children[2];
                bool compact = side < 110;
                // Give the icon the actual room left by the two text rows, rather than
                // reserving a fixed 70 DIP that needlessly shrinks it in short panels.
                content.Margin = new Thickness(6, compact ? 4 : 6, 6, compact ? 4 : 6);
                icon.Margin = new Thickness(0, 0, 0, compact ? 3 : 6);
                footer.Margin = new Thickness(0, compact ? 1 : 2, 0, 0);
                title.FontSize = compact ? 11 : 12;
                title.Measure(new Size(Math.Max(1, side - 14), double.PositiveInfinity));
                double available = side - 2 - content.Margin.Top - content.Margin.Bottom
                    - title.DesiredSize.Height - footer.Height - footer.Margin.Top - icon.Margin.Bottom;
                icon.Width = icon.Height = Math.Clamp(available, 16, 48);
            }
        }
        root.SizeChanged += (_, _) => FitSquares();
        root.Loaded += (_, _) => FitSquares();
        TransferCount.Children.Add(root);
        // PageHandover owns page entry animation. Layout and data refreshes must not replay it.

    }
    // Inserts this page's total size, "clear" and "drag all" at the right end of the top toolbar,
    // just left of the settings button, next to the search button. This page has no category
    // filter, so every operation targets all file records.
    private void TransferToolbar(ShelfLayout layout)
    {
        if (SettingsButton.Parent is not Panel tools) return;
        // Drop whatever this page inserted on the previous pass so re-renders cannot pile up.
        const string mark = "transfer-tool";
        foreach (var stale in tools.Children.OfType<FrameworkElement>().Where(c => ReferenceEquals(c.Tag, mark)).ToList())
            tools.Children.Remove(stale);
        // Lay out the tools so the total size sits left of the clear and drag-all buttons.
        if (ShelfData.Files.Count > 0)
        {
            long total = ShelfData.Files.Sum(file => TransferFiles.FileSize(file.Path) ?? (TransferFiles.IsFolder(file.Path) ? TransferFiles.Folder(file.Path).Size ?? 0 : 0));
            var size = Label(Shelf.Size(total), layout.Aux, UI.Tertiary, HorizontalAlignment.Center);
            size.Margin = new Thickness(0, 0, 6, 0);
            size.Tag = mark;
            tools.Children.Insert(tools.Children.IndexOf(SettingsButton), size);
        }
        if (Shelf.ClearableFiles(ShelfData, null).Count > 0)
        {
            var clear = ClearButton(Loc.T("清除全部文件记录", "Clear every file listed"), () =>
            {
                var gone = Shelf.ClearFiles(ShelfData, null);
                if (gone.Count > 0) RemoveFiles(gone); else _app.Shelf.Edited();
            });
            clear.Tag = mark;
            tools.Children.Insert(tools.Children.IndexOf(SettingsButton), clear);
        }
        if (VisibleFiles().Any(file => TransferFiles.Exists(file.Path)))
        {
            var drag = DragAllButton();
            drag.Tag = mark;
            tools.Children.Insert(tools.Children.IndexOf(SettingsButton), drag);
        }
    }
    private Button TransferTool(string geometry, string label, Action action)
    {
        // MenuGlyph includes trailing space for a menu label; standalone tools must not keep it.
        var glyph = MenuGlyph(geometry, UI.Secondary);
        glyph.Margin = new Thickness(0);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button { Width = 26, Height = 26, Padding = new Thickness(0),
            Style = (Style)FindResource("PanelIconButton"), Content = glyph, ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }    // Animate a separate offset property: ScrollViewer's HorizontalOffset is read-only.
    // Repeated wheel input extends the destination while starting from the current visible position.

    private Guid? _fileTab;
    private readonly HashSet<Guid> _picked = [];
    private Guid? _pickAnchor;
    // Files dragged over the panel (TransferDragEnter): the page shown before, to go back to if they are not dropped.
    private bool _dropping, _dropOpened, _draggingOut;
    private DeckPage? _dropReturn;
    private DispatcherTimer? _dropLeave;
    private Rectangle? _fileFrame;
    private int _transferShown = -1;
    // ---- The collapsed count ----
    // The width of the fixed marks of the row: the clock, the count and the devices (CompactMusicLayout).
    // The count shows the whole number, and its width follows the number's digits.
    private static double FixedFace(double scale, params double[] widths)
    {
        var shown = widths.Where(width => width > 0).ToArray();
        return shown.Sum() + Math.Max(0, shown.Length - 1) * CompactMusicLayout.Gap(scale);
    }
    private FrameworkElement? _compactTransfer;
    private FrameworkElement? TransferBadge(double scale)
    {
        int count = Settings.ShowsTransfer(EffectiveStyle) ? ShelfData.Files.Count : 0;
        _transferShown = count; _compactTransfer = null;
        if (count == 0) return null;
        double s = CompactMusicLayout.SafeScale(scale), icon = CompactMusicLayout.Whole(14 * s), gap = CompactMusicLayout.Whole(3 * s);
        // The number starts on a whole device pixel and the badge ends on one. Layout rounding moves a margin that lies between two
        // pixels onto the next (17 DIPs are 25.5 pixels at 150% and become 26), which took the room from the number: a count of two
        // digits no longer fit and, wrapping as every TextBlock does by default (App.xaml), stood on two lines cut by the badge.
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX, start = DevicePixels.Up(icon + gap, dpi);
        var number = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture), FontFamily = UI.PanelFont, FontSize = CompactMusicLayout.Half(11.5 * s), FontWeight = FontWeights.SemiBold,
            Foreground = UI.Brush(UI.Primary), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
        };
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double digits = number.DesiredSize.Width;
        number.Margin = new Thickness(start, 0, 0, 0);
        var badge = new Grid { Width = DevicePixels.Up(start + digits, dpi), Height = CompactMusicLayout.Whole(16 * s), Background = Brushes.Transparent };
        // The tray on the 16 unit grid of the row's other marks (PowerIcons), scaled to the icon's size with the same 1.35 DIP line,
        // rather than a larger drawing stretched to fit, which thickened and blurred it.
        double grid = icon / 16;
        var tray = new Path
        {
            Data = Geometry.Parse("M 1.5,9 L 3.5,3.5 H 12.5 L 14.5,9 V 12.5 A 1,1 0 0 1 13.5,13.5 H 2.5 A 1,1 0 0 1 1.5,12.5 Z M 1.5,9 H 5.5 L 6.5,10.5 H 9.5 L 10.5,9 H 14.5"),
            Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.35 / grid, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, LayoutTransform = new ScaleTransform(grid, grid), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        badge.Children.Add(tray); badge.Children.Add(number);
        string tip = Loc.T($"文件中转：{count} 个文件", count == 1 ? "File transfer: 1 file" : $"File transfer: {count} files");
        badge.ToolTip = tip; AutomationProperties.SetName(badge, tip);
        _compactTransfer = badge;
        return badge;
    }
    // Opened while the pointer is on the count, the panel opens on this page.
    private bool PointerOnTransfer()
    {
        if (_compactTransfer is not { IsVisible: true } badge || PresentationSource.FromVisual(badge) is null) return false;
        var rect = new Rect(badge.PointToScreen(new Point()), badge.PointToScreen(new Point(badge.ActualWidth, badge.ActualHeight)));
        rect.Inflate(4, 6);
        return rect.Contains(WindowsHost.Cursor());
    }

    private List<TransferFile> VisibleFiles() => ShelfData.Files.ToList();

    private void ShowDropFrame()
    {
        if (CurrentPage == DeckPage.Transfer) _transferDropView?.SetDragging(_dropping);
        if (_fileFrame is null) return;
        var accent = PanelTheme.ColorOf("accent");
        _fileFrame.Fill = _dropping ? new SolidColorBrush(Color.FromArgb(18, accent.R, accent.G, accent.B)) : Brushes.Transparent;
        _fileFrame.Stroke = _dropping ? new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B)) : Brushes.Transparent;
    }
    private const string DragOutIcon = "M 8,1.5 V 14.5 M 1.5,8 H 14.5 M 6,3.5 L 8,1.5 L 10,3.5 M 6,12.5 L 8,14.5 L 10,12.5 M 3.5,6 L 1.5,8 L 3.5,10 M 12.5,6 L 14.5,8 L 12.5,10";
    private const double DragAllWidth = 24;
    private Border DragAllButton()
    {
        var glyph = MenuGlyph(DragOutIcon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = glyph, Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        var tip = HoverTip(button, Loc.T("拖出全部文件", "Drag out all files"));
        AutomationProperties.SetName(button, Loc.T("拖出全部文件", "Drag out all files"));
        button.MouseEnter += (_, _) => { button.Background = UI.Brush(PanelTheme.Hex("hover")); glyph.Stroke = UI.Brush(UI.Primary); };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; glyph.Stroke = UI.Brush(UI.Secondary); };
        // A click without a drag keeps saying what it does.
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; tip.IsOpen = true; };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        AttachFileDrag(button, () => VisibleFiles().Where(file => TransferFiles.Exists(file.Path)).ToList());
        return button;
    }
    // A filter: its text, a dot in the category's color, and the underline when it is the one shown.
    private static string FolderFacts(string path)
    {
        var stats = TransferFiles.Folder(path);
        if (stats.Size is { } size) return Shelf.Size(size);
        return stats.Items is { } items ? Loc.T($"{items} 项", items == 1 ? "1 item" : $"{items} items") : "…";
    }

    // The time or size at the end of a row; under the pointer it gives way to "›", which shows the whole entry, and to one more
    // button at its left (extra): "粘贴为纯文本" on a text, "从文件中转中移除" on a file.
    private void AttachFileDrag(FrameworkElement source, Func<List<TransferFile>> files)
    {
        Point? start = null;
        MouseButton button = MouseButton.Left;
        bool dragged = false;
        source.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            button = e.ChangedButton;
            dragged = false;
            start = e.GetPosition(source);
        };
        source.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != button) return;
            start = null;
            if (dragged) { e.Handled = true; dragged = false; }
        };
        source.MouseLeave += (_, e) =>
        {
            if ((button == MouseButton.Left ? e.LeftButton : e.RightButton) != MouseButtonState.Pressed) start = null;
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || (button == MouseButton.Left ? e.LeftButton : e.RightButton) != MouseButtonState.Pressed) return;
            var now = e.GetPosition(source);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            var list = files();
            if (list.Count == 0) return;
            dragged = true;
            bool move = (button == MouseButton.Left ? Settings.TransferDragOut : Settings.TransferRightDragOut) == TransferDragOut.Move;
            var data = ShelfService.FileData(list.Select(file => file.Path), move);
            data.SetData(ShelfFormat, ShelfSide.Files + "|" + string.Join(",", list.Select(file => file.Id)));
            if (source is Border row) row.Background = _picked.Count > 0 && list.Count > 1 ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
            DragOut(source, data, move ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy);
            if (move) WatchMoved(list);
            e.Handled = true;
        };
    }
    // Dragged out of the panel, clipboard entries are copied; files are copied, or, when the drag is set to move, moved where the
    // target takes moves (a chat window or a browser takes only copies).
    // drive takes a while). So they are looked for every second for five minutes; a file still there was copied, or not dropped.
    private readonly Dictionary<Guid, long> _movingOut = [];
    private DispatcherTimer? _movingTimer;
    private void WatchMoved(IReadOnlyList<TransferFile> files)
    {
        long until = Environment.TickCount64 + 5 * 60_000;
        foreach (var file in files) _movingOut[file.Id] = until;
        CheckMoved(null, EventArgs.Empty);
        if (_movingOut.Count == 0) return;
        _movingTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _movingTimer.Tick -= CheckMoved; _movingTimer.Tick += CheckMoved;
        _movingTimer.Start();
    }
    private void CheckMoved(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        var gone = new List<TransferFile>();
        foreach (var (id, until) in _movingOut.ToList())
        {
            var file = ShelfData.Files.FirstOrDefault(f => f.Id == id);
            if (file is null || now > until) { _movingOut.Remove(id); continue; }
            if (TransferFiles.Exists(file.Path)) continue;
            _movingOut.Remove(id);
            TransferFiles.Forget(file.Path);
            gone.Add(file);
        }
        if (gone.Count > 0) RemoveFiles(gone);
        if (_movingOut.Count == 0) _movingTimer?.Stop();
    }
    // A picture is dragged as a file as well (ShelfService.PictureFile), which is how browsers and Electron applications take one.
    private void ClickFile(TransferFile file)
    {
        bool ctrl = PasteInput.KeyDown(0x11), shift = PasteInput.KeyDown(0x10);
        if (ctrl || shift)
        {
            // Ctrl adds or removes one file; Shift takes every file from the last one chosen to this one.
            var visible = VisibleFiles();
            if (shift && _pickAnchor is { } anchor && visible.FindIndex(f => f.Id == anchor) is var from and >= 0)
            {
                int to = visible.FindIndex(f => f.Id == file.Id);
                if (!ctrl) _picked.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _picked.Add(visible[i].Id);
            }
            else if (!_picked.Remove(file.Id)) { _picked.Add(file.Id); _pickAnchor = file.Id; }
            RenderTransfer();
            return;
        }
        if (_picked.Count > 0) { _picked.Clear(); RenderTransfer(); }
        _pickAnchor = file.Id;
        if (!TransferFiles.Exists(file.Path)) return;
        PasteFiles([file]);
    }
    private void PasteFiles(IReadOnlyList<TransferFile> files)
    {
        var present = files.Where(file => TransferFiles.Exists(file.Path)).ToList();
        if (present.Count == 0) return;
        _app.Shelf.Paste(ShelfService.FileData(present.Select(file => file.Path)), done => { foreach (var file in present) Flash(file.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")); });
    }
    private static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { }
    }
    private static void ShowInFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ---- Files dragged in ----
    // Into the collapsed island the panel opens on this page; into the panel on another page it turns to this page. The file transfer
    // column lights up; over a file category, that category does too. Taken away without dropping, the panel goes back.
    private void TransferDragEnter(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !Settings.TransferPage || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
        if (_dropping) return;
        _dropping = true;
        _shelfDetail = null; _clipboardEdit = null; _clipboardSearching = false; _clipboardQuery = "";
        if (!_expanded)
        {
            _dropOpened = true; _dropReturn = null;
            _hover.Stop();
            if (CurrentPage != DeckPage.Transfer) SelectPage(DeckPage.Transfer, remember: false);
            SetExpanded(true);
        }
        else
        {
            _dropOpened = false;
            _dropReturn = CurrentPage == DeckPage.Transfer ? null : CurrentPage;
            if (CurrentPage != DeckPage.Transfer) SelectPage(DeckPage.Transfer, remember: false);
        }
        RenderTransfer();
    }
    private void TransferDragOver(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        e.Effects = DragDropEffects.Copy;
        // A file category under the pointer lights up as well (Tab's own DragOver); elsewhere the whole column takes the files.
        e.Handled = true;
    }
    private void TransferDragLeave(object sender, DragEventArgs e)
    {
        if (!_dropping) return;
        // Leave also fires when the pointer moves between parts of the panel; it counts only if no part takes the drag right after.
        _dropLeave ??= new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _dropLeave.Tick -= DropLeft; _dropLeave.Tick += DropLeft;
        _dropLeave.Stop(); _dropLeave.Start();
    }
    private void DropLeft(object? sender, EventArgs e) => EndDrop(dropped: false);
    private void TransferDrop(object sender, DragEventArgs e)
    {
        _dropLeave?.Stop();
        if (_draggingOut || !_dropping) return;
        e.Handled = true;
        EndDrop(dropped: true);
        AddDropped(e.Data.GetData(DataFormats.FileDrop) as string[] ?? [], null);
    }
    private void AddDropped(string[] paths, Guid? category)
    {
        foreach (var path in paths) TransferFiles.Forget(path);
        if (paths.Length == 0) return;
        _fileOffset = 0; _picked.Clear();
        _app.Shelf.AddFiles(paths, null);
    }
    private void EndDrop(bool dropped)
    {
        _dropLeave?.Stop();
        if (!_dropping) return;
        _dropping = false;
        if (!dropped)
        {
            if (_dropOpened) { _dropOpened = false; if (!_pointerInside) SetExpanded(false); }
            else if (_dropReturn is { } back) SelectPage(back, remember: false);
            _dropReturn = null;
        }
        _dropOpened = false; _dropReturn = null;
        if (IsTransferPage && _expanded) RenderTransfer();
    }

}