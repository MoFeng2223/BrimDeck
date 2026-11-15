using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;
public partial class MainWindow
{
    private Guid? _clipTab;
    private void RenderClipboard(bool lazy = false)
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; DevicesContent.Visibility = Visibility.Collapsed;
        TransferCount.Visibility = Visibility.Collapsed;
        ClipboardContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var measured = ShelfLayout.For(width, height);
        var layout = _shelfLayout = measured with { ClipColumn = Math.Max(0, width - 2 * measured.Side), FileColumn = 0 };
        var header = PanelHeader.For(ToolbarFor(height), layout.Side);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        // In the lowest panels the toolbar leaves 2 DIP under the page switch (PanelHeader); the page keeps 4 more below it.
        ClipboardContent.Margin = new Thickness(layout.Side, height < 150 ? 4 : 0, layout.Side, layout.Tier == ShelfTier.Compact ? 8 : 12);
        var build = new ShelfBuild(width, height, Settings, PanelTheme.Generation, Loc.Language, DateTime.Now.Ticks / TimeSpan.TicksPerMinute, VisualTreeHelper.GetDpi(this).DpiScaleY, _scale);
        bool keep = lazy && !_clipboardStale && build == _clipboardBuilt && ClipboardContent.Children.Count > 0;
        if (lazy && !keep && !_expanded && !_preview) { _clipboardStale = true; return; }
        if (!keep)
        {
            // Built while the panel is closed as well, when the page itself asks: the panel opens on what was built last.
            _clipboardStale = false; _clipboardBuilt = build;
            // The pointer may have left a row that is about to be replaced; its scroll positions are kept.
            RememberShelfScroll();
            ClipboardContent.Children.Clear(); _shelfRows.Clear(); _clipboardTabs.Clear(); _fileFrame = null;
        }
        // The header's own parts are set again either way: another page shown meanwhile has hidden them.
        RenderShelfToolbar(width);
        if (_shelfDetail is { } detail && Find(detail.Side, detail.Id) is not null) { if (!keep) ClipboardContent.Children.Add(DetailView(detail.Side, detail.Id, width - 2 * layout.Side, layout)); return; }
        _shelfDetail = null;
        RenderPageSwitch();
        if (keep) return;

        ClipboardContent.Children.Add(ClipColumn(layout));
        RefocusTyping();
    }
    // During a resize the page is built again only where its sizes step (row height, type size, what the file rows show); in between
    // the file column takes the width the new size gives it and the clipboard column the rest. The details keep the width they were
    // built for until the drag ends.
    private void StretchClipboard() { if (_shelfDetail is null) RenderClipboard(); }
    private List<ClipEntry> VisibleClips() => Searching
        ? ShelfData.Clips.Where(clip => clip.Kind != ClipKind.Image && Shelf.Matches(clip.Text, _clipboardQuery)).ToList()
        : ShelfData.Clips.Where(clip => _clipTab is not { } id || clip.Categories.Contains(id)).ToList();

    private Grid ClipColumn(ShelfLayout layout)
    {
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Header + ListGap) });
        column.RowDefinitions.Add(new RowDefinition());
        column.Children.Add(ColumnHeader(ShelfSide.Clipboard, layout.ClipColumn, layout));
        FrameworkElement body;
        if (_clipboardEdit is not null) body = TextEditor(layout);
        else
        {
            var clips = VisibleClips();
            if (clips.Count == 0) body = ClipEmpty(layout);
            else
            {
                // Only the rows in view are built (ClipboardRowList); a row reaches from the list's left edge, its text 8 DIP in.
                var list = new ClipboardRowList(
                    item => { var row = ClipRow((ClipEntry)item, layout); row.Margin = new Thickness(0); return row; },
                    (item, row) => { if (_shelfRows.TryGetValue(((ClipEntry)item).Id, out var kept) && kept.Row == row) _shelfRows.Remove(((ClipEntry)item).Id); })
                { ItemsSource = clips, Margin = new Thickness(-8, 0, 0, 0) };
                Fade(PassWheel(list.Viewer), ShelfSide.Clipboard, _clipOffset);
                body = list;
            }
        }
        Grid.SetRow(body, 1); column.Children.Add(body);
        return column;
    }

    private FrameworkElement ClipEmpty(ShelfLayout layout)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, layout.Header / 2) };
        bool favorites = _clipTab == ShelfData.Favorites;
        string text = Searching ? Loc.T("没有匹配的内容", "Nothing matches")
            : favorites ? Loc.T("常用的内容放在这里，一直保留", "Favorites are kept here until you delete them")
            : _clipTab is not null ? Loc.T("这个分类还没有内容", "This category is empty")
            : !Settings.ClipboardRecord ? Loc.T("剪贴板记录尚未开启", "Clipboard history is off")
            : Loc.T("复制的内容会显示在这里", "What you copy appears here");
        panel.Children.Add(Label(text, layout.Body, UI.Secondary, HorizontalAlignment.Center));
        if (!Searching && favorites)
        {
            panel.Children.Add(Label(Loc.T("右键任意一条，选择“加入常用”", "Right-click an entry and choose Add to favorites"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
            var add = PillButton(Loc.T("在设置中添加常用语", "Add phrases in settings"), primary: false, () => _app.OpenSettingsPage(SettingsWindow.ClipboardSettingsPage));
            add.Margin = new Thickness(0, 10, 0, 0); add.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(add);
        }
        else if (!Searching && _clipTab is null && !Settings.ClipboardRecord)
        {
            panel.Children.Add(Label(Loc.T("开启后保存在本机，加密存放", "Kept on this computer, encrypted"), layout.Aux, UI.Tertiary, HorizontalAlignment.Center, top: 4));
            var on = PillButton(Loc.T("开启记录", "Turn on"), primary: true, () => _app.UpdateSettings(SettingsWith(s => s.ClipboardRecord = true)));
            on.Margin = new Thickness(0, 10, 0, 0); on.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(on);
        }
        return panel;
    }

    private Border ClipRow(ClipEntry clip, ShelfLayout layout)
    {
        bool two = layout.TwoLines && clip.Kind is ClipKind.Text or ClipKind.Code && Shelf.LineCount(clip.Text.Trim()) > 1 || layout.TwoLines && clip.Kind == ClipKind.Image;
        double height = two ? layout.Row + (clip.Kind == ClipKind.Image ? Math.Max(0, layout.ThumbHeight + 6 - layout.Row) : layout.Body * 1.45) : layout.Row;
        var frame = RowFrame(layout, height, false);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var content = ClipContent(clip, layout, two);
        grid.Children.Add(content);
        // In "全部", the categories an entry is in, in their order: a dot in each one's color, a star for "常用", at most three (the
        // tip names them all). Under the pointer they give way, as the time does (ShowMore).
        FrameworkElement? mark = null;
        var marked = _clipTab is null ? ShelfData.ClipCategories.Where(c => clip.Categories.Contains(c.Id)).ToList() : [];
        if (marked.Count > 0)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, ToolTip = string.Join(Loc.T("、", ", "), marked.Select(Shelf.CategoryName)) };
            foreach (var category in marked.Take(3))
            {
                FrameworkElement glyph = category.Id == ShelfData.Favorites ? StarGlyph(9, UI.Tertiary, filled: true) : new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(category.Color) };
                glyph.VerticalAlignment = VerticalAlignment.Center;
                if (marks.Children.Count > 0) glyph.Margin = new Thickness(3, 0, 0, 0);
                marks.Children.Add(glyph);
            }
            mark = marks;
            Grid.SetColumn(mark, 1); grid.Children.Add(mark);
        }
        var aux = AuxSlot(clip.Manual ? Loc.T("常用语", "Phrase") : Shelf.When(clip.Copied, DateTimeOffset.Now), layout, () => OpenDetail(ShelfSide.Clipboard, clip.Id),
            extra: clip.Kind == ClipKind.Image ? null : (MenuIcons.PlainText, Loc.T("粘贴为纯文本", "Paste as plain text"), () => PasteClip(clip, plain: true)));
        if (aux.Tag is RowEnd end) end.Mark = mark;
        Grid.SetColumn(aux, 2); grid.Children.Add(aux);
        frame.Child = grid;
        AutomationProperties.SetName(frame, clip.Kind == ClipKind.Image ? Loc.T("图片", "Picture") : Shelf.FirstLine(clip.Text).First);
        _shelfRows[clip.Id] = (frame, aux);
        AttachRow(frame, aux, ShelfSide.Clipboard, clip.Id, picked: false,
            click: () => PasteClip(clip),
            menu: () => ClipMenu(clip),
            drag: () => ClipDrag(clip));
        if (_flash is { } flash && flash.Id == clip.Id && flash.Until > Environment.TickCount64) ShowFlash(frame, aux, flash.Text);
        return frame;
    }
    private FrameworkElement ClipContent(ClipEntry clip, ShelfLayout layout, bool two)
    {
        switch (clip.Kind)
        {
            case ClipKind.Image:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var thumb = new Border { Width = layout.Thumb, Height = layout.ThumbHeight, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = UI.Brush(UI.Fill), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
                if (_app.Shelf.Picture(clip.Id, (int)Math.Ceiling(layout.Thumb * 2)) is { } picture)
                    thumb.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                panel.Children.Add(thumb);
                panel.Children.Add(Label(Loc.T("图片", "Picture") + $"  {clip.PixelWidth}×{clip.PixelHeight}", layout.Aux, UI.Secondary, HorizontalAlignment.Left, left: 8));
                return panel;
            }
            case ClipKind.Color when Shelf.TryColor(clip.Text, out var c):
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(PanelTheme.Corner(3)), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
                var text = Highlighted(clip.Text.Trim(), layout.Body, UI.Primary, mono: true); text.Margin = new Thickness(8, 0, 0, 0);
                panel.Children.Add(text);
                return panel;
            }
            case ClipKind.Link:
            {
                var (host, rest) = Shelf.SplitLink(clip.Text);
                var block = new TextBlock { FontFamily = UI.PanelFont, FontSize = layout.Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                if (Searching) AddHighlighted(block.Inlines, clip.Text.Trim(), UI.Primary);
                else { block.Inlines.Add(new Run(host) { Foreground = UI.Brush(UI.Primary) }); block.Inlines.Add(new Run(rest) { Foreground = UI.Brush(UI.Tertiary) }); }
                return block;
            }
            default:
            {
                bool code = clip.Kind == ClipKind.Code;
                var (first, more) = Shelf.FirstLine(clip.Text);
                if (two)
                {
                    var lines = clip.Text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Trim().Length > 0).ToList();
                    string shown = string.Join("\n", lines.Take(2).Select(line => code ? line.Replace("\t", "  ") : line.Trim()));
                    var block = Highlighted(shown, layout.Body, UI.Primary, mono: code);
                    block.TextWrapping = TextWrapping.NoWrap;
                    if (lines.Count > 2) { block.Inlines.Add(new Run("  +" + (lines.Count - 2) + Loc.T(" 行", " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux }); }
                    return block;
                }
                var line = Highlighted(code ? first : first, layout.Body, UI.Primary, mono: code);
                if (more > 0) line.Inlines.Add(new Run("  +" + more + Loc.T(" 行", more == 1 ? " line" : " lines")) { Foreground = UI.Brush(UI.Tertiary), FontSize = layout.Aux, FontFamily = UI.PanelFont });
                return line;
            }
        }
    }

    private DataObject ClipDrag(ClipEntry clip)
    {
        var data = _app.Shelf.ClipData(clip);
        if (_app.Shelf.PictureFile(clip) is { } path) data.SetFileDropList([path]);
        return data;
    }

    // ---- Actions ----
    private void PasteClip(ClipEntry clip, bool plain = false)
        => _app.Shelf.Paste(_app.Shelf.ClipData(clip, plain), done => Flash(clip.Id, done ? Loc.T("已粘贴", "Pasted") : Loc.T("已复制", "Copied")));
    // Copied again, the entry moves to the top as any new copy does, and the list shows it there.
    private void CopyClip(ClipEntry clip)
    {
        if (!_app.Shelf.Put(_app.Shelf.ClipData(clip))) return;
        Shelf.Recopied(ShelfData, clip, DateTimeOffset.Now);
        _clipOffset = 0;
        Flash(clip.Id, Loc.T("已复制", "Copied"));
        _app.Shelf.Edited();
    }

    private string _clipboardQuery = "";
    private bool _clipboardSearching;
    private (ShelfSide Side, Guid Id)? _shelfDetail;
    // The text entry being edited in place of the list (MainWindow.ShelfDetail.cs).
    private Guid? _clipboardEdit;
    // The row that was just pasted: its time reads "已粘贴" on a blue background for a moment.
    private (Guid Id, string Text, long Until)? _flash;
    private DispatcherTimer? _flashTimer;
    private ContextMenu? _shelfMenu;
    private TextBox? _searchBox, _editBox;
    private double _clipOffset, _fileOffset;
    private readonly List<(Border Tab, Guid? Id, ShelfSide Side)> _clipboardTabs = [];
    private readonly Dictionary<Guid, (Border Row, Grid Aux)> _shelfRows = [];
    private ShelfLayout _shelfLayout = ShelfLayout.For(ClipboardSizes.Default.Width, ClipboardSizes.Default.Height);

    // The details replace the page switch and the clock with their own header.
    internal bool ShelfHidesSwitch => _shelfDetail is not null;
    // The panel stays open while a menu of the page is open, while the user types in it, and while files are dragged over it.
    internal bool ShelfHolds => _shelfMenu?.IsOpen == true || _clipboardEdit is not null || _dropping || _draggingOut || _searchBox?.IsKeyboardFocusWithin == true;
    private ShelfData ShelfData => _app.Shelf.Data;

    private void InitializeClipboard()
    {
        _app.Shelf.Changed += ClipboardChanged;
        Closed += (_, _) => _app.Shelf.Changed -= ClipboardChanged;
        // A press anywhere else on the panel lets go of the search box: the keyboard goes back to the window it came from, and the
        // search and its results stay.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_searchBox is not { IsKeyboardFocusWithin: true } box || box.Parent is not Visual host) return;
            if (e.OriginalSource is Visual pressed && (host == pressed || host.IsAncestorOf(pressed))) return;
            EndTyping(); Keyboard.ClearFocus();
        };
        // Typing ends when the user goes to another window; the panel then closes as usual once the pointer is away.
        Deactivated += (_, _) =>
        {
            if (_clipboardEdit is not null && !_editCommitting) { _clipboardEdit = null; if (IsClipboardPage) RenderClipboard(); }
            if (_expanded && !_pointerInside && !ShelfHolds) PointerChanged(false);
        };
    }

    // A copy or a dropped file while the panel is closed is shown when it opens (ApplyDefaultMusicPage), not built at every copy;
    // one made while another page shows, when the corresponding page is next rendered.
    private bool _clipboardStale;
    private void ClipboardChanged()
    {
        _clipboardStale = true;
        if (IsClipboardPage && _expanded && !_preview) RenderClipboard();
    }

    // ---- The page ----
    // What the page's rows were last built for, besides the entries and what the page itself has chosen (the categories, the
    // search, the details), each change of which builds it again (ClipboardChanged, and the page's own actions). The minute is part of
    // it, as the rows show how long ago each copy was made.
    private sealed record ShelfBuild(double Width, double Height, object Settings, int Theme, string Language, long Minute, double Dpi, double Scale);
    private ShelfBuild? _clipboardBuilt;

    // lazy: asked for with the data or the settings (RenderUsage) rather than by a change of the page itself. The usage is read every
    // minute, and each read used to build this page again, twice, whether or not the panel was open. A lazy render keeps the rows
    // while what they were built for is the same; while the panel is closed it builds nothing, and the page is built as the panel
    // opens (ApplyDefaultMusicPage), as it is after a copy made meanwhile.
    private void RememberShelfScroll()
    {
        foreach (var viewer in ClipboardContent.Children.OfType<Grid>().SelectMany(grid => FindScrolls(grid)))
        {
            if (Equals(viewer.Tag, ShelfSide.Clipboard)) _clipOffset = viewer.VerticalOffset;
            else if (Equals(viewer.Tag, ShelfSide.Files)) _fileOffset = viewer.VerticalOffset;
        }
        static IEnumerable<ScrollViewer> FindScrolls(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer && viewer.Tag is ShelfSide) yield return viewer;
                else foreach (var inner in FindScrolls(child)) yield return inner;
            }
        }
    }

    private object? Find(ShelfSide side, Guid id) => side == ShelfSide.Clipboard ? ShelfData.Clips.FirstOrDefault(clip => clip.Id == id) : ShelfData.Files.FirstOrDefault(file => file.Id == id);
    private bool Searching => _clipboardSearching && _clipboardQuery.Trim().Length > 0;
    // ---- Columns ----
    // The space between a column's header (its filters' underline) and its list.
    private const double ListGap = 4;
    // A list or text with nothing to scroll lets the wheel through to the panel (PanelWheel: the volume or the pages); one that
    // scrolls keeps it. A scroll viewer marks every wheel handled, so this takes the mark back when there is nothing to scroll.
    private static T PassWheel<T>(T viewer) where T : ScrollViewer
    {
        viewer.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) => { if (viewer.ScrollableHeight < 1) e.Handled = false; }), true);
        return viewer;
    }
    // A list scrolls inside its column (ClipboardRowList, which starts 8 DIP left of the column so a row's background reaches past its
    // text); the scroll bar is only a thin handle (App.xaml). This marks the list's side (RememberShelfScroll), goes back to its
    // scroll position, and fades the list out at the bottom while more of it lies below.
    private static ScrollViewer Fade(ScrollViewer viewer, ShelfSide side, double offset)
    {
        viewer.Tag = side;
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0)); fade.GradientStops.Add(new GradientStop(Colors.Black, .9)); fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        void Mask() => viewer.OpacityMask = viewer.ScrollableHeight > 1 && viewer.VerticalOffset < viewer.ScrollableHeight - 1 ? fade : null;
        viewer.ScrollChanged += (_, _) => Mask();
        viewer.Loaded += (_, _) => { if (offset > 0) viewer.ScrollToVerticalOffset(offset); Mask(); };
        return viewer;
    }

    private FrameworkElement ColumnHeader(ShelfSide side, double width, ShelfLayout layout)
    {
        var row = new Grid { Height = layout.Header, VerticalAlignment = VerticalAlignment.Top };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string name = side == ShelfSide.Clipboard ? Loc.T("剪贴板", "Clipboard") : Loc.T("文件中转", "File transfer");
        // The name and the filters, left to right; "分类 ⌄" last takes what room is left, its word trimmed to it.
        var tabs = new DockPanel { LastChildFill = false, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, -4, 0) };
        Grid.SetColumn(buttons, 1);
        row.Children.Add(tabs); row.Children.Add(buttons);
        var title = Label(name, layout.Title, UI.Primary, HorizontalAlignment.Left); title.FontWeight = FontWeights.SemiBold;
        tabs.Children.Add(title);
        if (Searching)
        {
            int found = side == ShelfSide.Clipboard ? VisibleClips().Count : VisibleFiles().Count;
            tabs.Children.Add(Label(Loc.T($"{found} 个结果", found == 1 ? "1 result" : $"{found} results"), layout.Aux, UI.Tertiary, HorizontalAlignment.Left, left: 10));
            return row;
        }
        double buttonsWidth = 0;
        if (side == ShelfSide.Clipboard)
        {
            // "清除" takes away at once what the filter shown holds (Shelf.Clear): in "全部" all that is not kept; in a category of the
            // user's, its entries, those also in another category only leaving this one. "常用" has none. The button stays while the
            // list shows anything, even when all of it is kept and a press takes nothing away.
            if (VisibleClips().Count > 0)
            {
                string tip = _clipTab == ShelfData.Favorites ? Loc.T("“常用”中的内容不会被清除", "What is in Favorites is not cleared")
                    : _clipTab is { } id && ShelfData.ClipCategories.FirstOrDefault(c => c.Id == id) is { } category
                    ? Loc.T($"清除“{category.Name}”中的全部内容", $"Clear everything in {category.Name}") : Loc.T("清除全部内容，“常用”和一直保留的分类不受影响", "Clear everything but Favorites and the categories kept");
                buttons.Children.Add(ClearButton(tip, () =>
                {
                    if (Shelf.Clearable(ShelfData, _clipTab).Count == 0) return;
                    var gone = Shelf.Clear(ShelfData, _clipTab);
                    if (gone.Count > 0) DeleteClips(gone); else _app.Shelf.Edited();
                }));
                buttonsWidth += ClearWidth;
            }
        }
        var categories = ShelfData.Categories(side);
        var own = ShelfData.OwnCategories(side).ToList();
        var selected = side == ShelfSide.Clipboard ? _clipTab : _fileTab;
        double titleWidth = TextWidth(name, layout.Title, FontWeights.SemiBold);
        // "全部" and "常用" always show. The user's categories follow them when they all fit; else they go into one filter with "⌄"
        // in the third place.
        bool favorites = categories.Any(category => category.Id == ShelfData.Favorites);
        double fixedWidth = categories.Count == 0 ? 0 : BarWidth + TabWidth(Loc.T("全部", "All"), layout, false) + (favorites ? TabWidth(Loc.T("常用", "Favorites"), layout, false) : 0);
        double ownWidth = own.Sum(category => TabWidth(category.Name, layout, true));
        if (side == ShelfSide.Files)
        {
            // "清除" as on the clipboard: every file in "全部", a category's files in it (those also in another category only leave
            // it); the files themselves stay where they are.
            bool clearable = Shelf.ClearableFiles(ShelfData, _fileTab).Count > 0;
            double clear = clearable ? ClearWidth : 0;
            buttonsWidth = clear;
            // "拖出全部" (its icon) shows only while there is a file to drag out, and gives way to the filters in a column too narrow for
            // both "全部 ⌄" and it (each file can still be dragged out on its own).
            if (VisibleFiles().Any(file => TransferFiles.Exists(file.Path)))
            {
                double free = width - clear - ButtonsMargin - titleWidth - 8;
                double least = fixedWidth + (own.Count == 0 ? 0 : Math.Min(ownWidth, PickerChrome));
                if (least + DragAllWidth <= free) { buttons.Children.Add(DragAllButton()); buttonsWidth += DragAllWidth; }
            }
            if (clearable)
            {
                string tip = _fileTab is { } id && ShelfData.FileCategories.FirstOrDefault(c => c.Id == id) is { } category
                    ? Loc.T($"清除“{category.Name}”中的文件记录", $"Clear the files listed in {category.Name}") : Loc.T("清除全部文件记录", "Clear every file listed");
                buttons.Children.Add(ClearButton(tip, () =>
                {
                    var gone = Shelf.ClearFiles(ShelfData, _fileTab);
                    if (gone.Count > 0) RemoveFiles(gone); else _app.Shelf.Edited();
                }));
            }
        }
        double room = width - buttonsWidth - ButtonsMargin - 4;
        bool fits = titleWidth + fixedWidth + ownWidth <= room;
        if (categories.Count > 0)
        {
            tabs.Children.Add(new Border { Width = 1, Height = 12, Background = UI.Brush(UI.Hairline), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            tabs.Children.Add(Tab(side, null, Loc.T("全部", "All"), layout, selected is null));
            if (favorites) tabs.Children.Add(Tab(side, ShelfData.Favorites, Loc.T("常用", "Favorites"), layout, selected == ShelfData.Favorites));
            if (fits)
                foreach (var category in own) tabs.Children.Add(Tab(side, category.Id, category.Name, layout, selected == category.Id, category.Color));
            else if (own.Count > 0)
            {
                tabs.LastChildFill = true;
                tabs.Children.Add(FilterPicker(side, own.FirstOrDefault(category => category.Id == selected), layout, width - buttonsWidth - ButtonsMargin - titleWidth - fixedWidth - PickerChrome));
            }
        }
        // The size of everything on the file transfer, where there is room for it beside the filters.
        if (side == ShelfSide.Files && fits && layout.FileColumn >= 260 && ShelfData.Files.Count > 0 && room - titleWidth - fixedWidth - ownWidth > 70)
        {
            long total = ShelfData.Files.Sum(file => TransferFiles.FileSize(file.Path) ?? (TransferFiles.IsFolder(file.Path) ? TransferFiles.Folder(file.Path).Size ?? 0 : 0));
            var size = Label(Shelf.Size(total), layout.Aux, UI.Tertiary, HorizontalAlignment.Right);
            size.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Insert(0, size);
        }
        return row;
    }
    // The divider between the column's name and its filters, with its margins; the buttons' own margin (4 at the left, -4 at the
    // right); and "⌄" with the picker's padding, what is left of "分类 ⌄" when its word has no room.
    private const double BarWidth = 13, ButtonsMargin = 0, PickerChrome = 25;
    private double TabWidth(string text, ShelfLayout layout, bool dot) => Math.Min(140, TextWidth(text, layout.Body, FontWeights.Normal)) + 12 + (dot ? 11 : 0);
    // The user's categories when they do not all fit: "分类 ⌄", or the one shown, underlined, with "⌄". A click lists them.
    private Border FilterPicker(ShelfSide side, ShelfCategory? shown, ShelfLayout layout, double labelRoom)
    {
        // The dot at the left, "⌄" at the right, the word between them, trimmed when the header is short of room.
        var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        if (shown is not null)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(shown.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, System.Windows.Controls.Dock.Left); content.Children.Add(dot);
        }
        // Without the settings' 1 DIP drop: beside the panel's filters that put the arrow below their middle (measured on screen).
        var chevron = SettingsWindow.Chevron(); chevron.FontSize = 11; chevron.Foreground = UI.Brush(UI.Secondary);
        chevron.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(chevron, System.Windows.Controls.Dock.Right); content.Children.Add(chevron);
        string text = shown?.Name ?? Loc.T("分类", "Categories");
        // In the narrowest file column only "⌄" (and the dot of the category shown) is left.
        var label = Label(text, layout.Body, shown is null ? UI.Tertiary : UI.Primary, HorizontalAlignment.Left); label.MaxWidth = 110;
        if (labelRoom >= 16) content.Children.Add(label);
        // "分类" shows whole or not at all (never "分…"); a category's own long name is trimmed.
        if (shown is null)
        {
            double whole = TextWidth(text, layout.Body, FontWeights.Normal);
            label.SizeChanged += (_, e) => { if (e.NewSize.Width + .5 < whole) label.Visibility = Visibility.Collapsed; };
        }
        var face = new Grid();
        face.Children.Add(content);
        if (shown is not null) face.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(UI.Accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0) });
        var picker = new Border { Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = face, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(picker, Loc.T("分类：", "Categories: ") + text);
        picker.MouseEnter += (_, _) => { label.Foreground = UI.Brush(UI.Primary); chevron.Foreground = UI.Brush(UI.Primary); };
        picker.MouseLeave += (_, _) => { label.Foreground = UI.Brush(shown is null ? UI.Tertiary : UI.Primary); chevron.Foreground = UI.Brush(UI.Secondary); };
        picker.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenMenu(CategoryListMenu(side), picker); };
        picker.MouseRightButtonUp += (_, e) => e.Handled = true;
        return picker;
    }
    // An icon button's note: shown as soon as the pointer is on the button and gone as soon as it leaves. WPF's own note waits a
    // moment, and stays open while the pointer moves toward it, over the button beside.
    private static ToolTip HoverTip(FrameworkElement owner, string text)
    {
        var tip = new ToolTip { Content = text, PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4 };
        owner.MouseEnter += (_, _) => tip.IsOpen = true;
        owner.MouseLeave += (_, _) => tip.IsOpen = false;
        owner.Unloaded += (_, _) => tip.IsOpen = false;
        return tip;
    }
    // "清除": a bin, 22 DIP square with 2 at its right; what it clears is said when the pointer rests on it.
    private static readonly Brush DangerRed = UI.Brush("#D83B2C");
    private const double ClearWidth = 24;
    private const string BinIcon = "M 2.5,4.5 H 13.5 M 6,4.5 V 3 H 10 V 4.5 M 4,4.5 L 4.7,13.5 H 11.3 L 12,4.5 M 6.8,7 V 11 M 9.2,7 V 11";
    private Border ClearButton(string tip, Action click)
    {
        var glyph = MenuGlyph(BinIcon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = glyph, Background = Brushes.Transparent,
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        HoverTip(button, tip);
        AutomationProperties.SetName(button, tip);
        // Destructive action: hovering turns the bin stroke red so the pointer show it is dangerous.
        button.MouseEnter += (_, _) => { button.Background = UI.Brush(PanelTheme.Hex("hover")); glyph.Stroke = DangerRed; };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; glyph.Stroke = UI.Brush(UI.Secondary); };
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }
    // "拖出全部": held and dragged, it takes every file shown here, as copies, to a folder or another application. Its icon is the
    // four arrows of moving, as the pointer shows over it; what it does is said when the pointer rests on it.
    private Border Tab(ShelfSide side, Guid? id, string text, ShelfLayout layout, bool selected, string? color = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        if (color is not null) content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = layout.Body, Foreground = UI.Brush(selected ? UI.Primary : UI.Tertiary),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
        content.Children.Add(label);
        var face = new Grid();
        face.Children.Add(content);
        // The underline is in the panel theme's accent, so it follows the theme.
        if (selected) face.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(UI.Accent), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0) });
        var tab = new Border
        {
            Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Child = face, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, AllowDrop = true
        };
        AutomationProperties.SetName(tab, text);
        if (selected) AutomationProperties.SetItemStatus(tab, Loc.T("已选中", "Selected"));
        _clipboardTabs.Add((tab, id, side));
        tab.MouseEnter += (_, _) => label.Foreground = UI.Brush(UI.Primary);
        tab.MouseLeave += (_, _) => { if (!selected) label.Foreground = UI.Brush(UI.Tertiary); };
        tab.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (side == ShelfSide.Clipboard) _clipTab = id; else { _fileTab = id; _picked.Clear(); }
            if (side == ShelfSide.Clipboard) _clipOffset = 0; else _fileOffset = 0;
            RenderClipboard();
        };
        tab.MouseRightButtonUp += (_, e) => e.Handled = true;
        // Entries dragged from the list onto a category, and files dragged in from the computer onto a file category, go into that
        // category, staying in the others they are in. "全部" takes only files from the computer.
        tab.DragOver += (_, e) => { e.Effects = TabAccepts(side, id, e.Data) ? DragDropEffects.Copy : DragDropEffects.None; HighlightTab(tab, e.Effects != DragDropEffects.None); e.Handled = true; };
        tab.DragLeave += (_, _) => HighlightTab(tab, false);
        tab.Drop += (_, e) => { e.Handled = true; HighlightTab(tab, false); DropOnTab(side, id, e.Data); };
        return tab;
    }
    private void HighlightTab(Border tab, bool on)
    {
        var accent = PanelTheme.ColorOf("accent");
        tab.BorderThickness = new Thickness(on ? 1 : 0);
        tab.BorderBrush = on ? new SolidColorBrush(accent) : null;
        if (on) tab.Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        else tab.Background = Brushes.Transparent;
    }
    private const string ShelfFormat = "BrimDeck.ShelfItems";
    private static bool TabAccepts(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat)) return id is not null && data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal);
        return side == ShelfSide.Files && data.GetDataPresent(DataFormats.FileDrop);
    }
    private void DropOnTab(ShelfSide side, Guid? id, IDataObject data)
    {
        if (data.GetDataPresent(ShelfFormat) && data.GetData(ShelfFormat) is string text && text.StartsWith(side + "|", StringComparison.Ordinal))
        {
            var ids = text[(side.ToString().Length + 1)..].Split(',').Select(part => Guid.TryParse(part, out var g) ? g : Guid.Empty).ToHashSet();
            if (id is { } category) SetCategory(side, ids, category, add: true);
            return;
        }
        if (side == ShelfSide.Files && data.GetData(DataFormats.FileDrop) is string[] paths) { EndDrop(dropped: true); AddDropped(paths, id); }
    }
    // Puts entries in a category or takes them out of it; their other categories stay.
    private void SetCategory(ShelfSide side, IEnumerable<Guid> ids, Guid category, bool add)
    {
        var set = ids.ToHashSet();
        Shelf.SetCategory(side == ShelfSide.Clipboard ? ShelfData.Clips.Where(clip => set.Contains(clip.Id)).Select(clip => clip.Categories)
            : ShelfData.Files.Where(file => set.Contains(file.Id)).Select(file => file.Categories), category, add);
        _app.Shelf.Edited();
    }
    // The categories of the entries given, one list each.
    private List<List<Guid>> CategoriesOf(ShelfSide side, IReadOnlyList<Guid> ids) => side == ShelfSide.Clipboard
        ? [.. ShelfData.Clips.Where(clip => ids.Contains(clip.Id)).Select(clip => clip.Categories)]
        : [.. ShelfData.Files.Where(file => ids.Contains(file.Id)).Select(file => file.Categories)];

    // ---- Rows ----
    private Border RowFrame(ShelfLayout layout, double height, bool picked)
    {
        var frame = new Border
        {
            Height = height, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Padding = new Thickness(8, 0, 4, 0), Margin = new Thickness(-8, 0, 0, 0),
            Background = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent, Cursor = Cursors.Hand
        };
        return frame;
    }
    private TextBlock Highlighted(string text, double size, string color, bool mono = false)
    {
        var block = new TextBlock { FontFamily = mono ? new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI") : UI.PanelFont, FontSize = mono ? size - .5 : size, Foreground = UI.Brush(color),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        AddHighlighted(block.Inlines, text, color);
        return block;
    }
    // The search's matches on a blue background.
    private void AddHighlighted(InlineCollection inlines, string text, string color)
    {
        if (!Searching) { inlines.Add(new Run(text)); return; }
        var accent = PanelTheme.ColorOf("accent");
        var mark = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        int at = 0;
        foreach (var (start, length) in Shelf.MatchesIn(text, _clipboardQuery))
        {
            if (start > at) inlines.Add(new Run(text[at..start]));
            inlines.Add(new Run(text.Substring(start, length)) { Background = mark, Foreground = UI.Brush(UI.Primary) });
            at = start + length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    private Grid AuxSlot(string text, ShelfLayout layout, Action open, bool warn = false, (string Icon, string Note, Action Act)? extra = null)
    {
        var slot = new Grid { VerticalAlignment = VerticalAlignment.Center, MinWidth = 22, Margin = new Thickness(8, 0, 0, 0) };
        var label = Label(text, layout.Aux, warn ? "#E5C890" : UI.Tertiary, HorizontalAlignment.Right);
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        // The settings' chevron (the icon font's, as the expandable rows there), turned to point right; no hover note.
        var chevron = SettingsWindow.Chevron(); chevron.FontSize = 11; chevron.Margin = new Thickness(0); chevron.Foreground = UI.Brush(UI.Secondary);
        chevron.RenderTransform = new RotateTransform(-90);
        // A 22 DIP face; the press is taken over the row's whole height and 36 DIP across, reaching into the row's right padding.
        // Beside "粘贴为纯文本" both press areas narrow, so the two faces stand 4 DIP apart.
        var detail = RowAction(chevron, extra is null ? 36 : 28, new Thickness(0, 0, 4, 0), layout, open);
        AutomationProperties.SetName(detail, Loc.T("查看详情", "Details"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        // The extra button, its menu icon taking the press over 26 DIP across; it shows with "›", and takes no room before.
        if (extra is { } one)
        {
            var glyph = MenuGlyph(one.Icon, UI.Secondary); glyph.Margin = new Thickness(0); glyph.HorizontalAlignment = HorizontalAlignment.Center;
            var button = RowAction(glyph, 26, new Thickness(2, 0, 2, 0), layout, one.Act);
            HoverTip(button, one.Note);
            AutomationProperties.SetName(button, one.Note);
            button.Visibility = Visibility.Collapsed;
            actions.Children.Add(button);
        }
        actions.Children.Add(detail);
        var more = new Border { Child = actions, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -4, 0), Visibility = Visibility.Hidden };
        slot.Children.Add(label); slot.Children.Add(more);
        slot.Tag = new RowEnd(label, more);
        return slot;
    }
    // A button at a row's end: a 22 DIP face (its icon centred, hover lights it) inside a press area as tall as the row.
    private static Border RowAction(FrameworkElement icon, double width, Thickness faceMargin, ShelfLayout layout, Action act)
    {
        var face = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(PanelTheme.Corner(6)), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = faceMargin, Child = icon };
        var button = new Border { Width = width, Height = layout.Row, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = face };
        void Paint(string color) { if (icon is Shape shape) shape.Stroke = UI.Brush(color); else if (icon is TextBlock text) text.Foreground = UI.Brush(color); }
        button.MouseEnter += (_, _) => { face.Background = UI.Brush(PanelTheme.Hex("pressed")); Paint(UI.Primary); };
        button.MouseLeave += (_, _) => { face.Background = Brushes.Transparent; Paint(UI.Secondary); };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return button;
    }
    // A row's end: the time or size, the buttons that take its place under the pointer, and the category's mark beside it.
    private sealed class RowEnd(TextBlock label, Border more)
    {
        public TextBlock Label { get; } = label;
        public Border More { get; } = more;
        public FrameworkElement? Mark { get; set; }
    }
    private static void ShowMore(Grid aux, bool on)
    {
        if (aux.Tag is not RowEnd end) return;
        end.Label.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        end.More.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        if (end.Mark is not null) end.Mark.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        // The extra button takes its room only while it shows, so the row keeps its text's width the rest of the time.
        if (end.More.Child is StackPanel { Children.Count: > 1 } actions) actions.Children[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // Hover lights the row and shows "›"; press darkens it; a click (a press and release without moving away) does the row's action.
    private void AttachRow(Border frame, Grid aux, ShelfSide side, Guid id, bool picked, Action click, Func<ContextMenu> menu, Func<DataObject>? drag)
    {
        Brush rest = picked ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
        Point? pressed = null;
        bool flashing() => _flash is { } flash && flash.Id == id && flash.Until > Environment.TickCount64;
        // While the row's own menu is open the row stays lit, though the pointer is on the menu.
        bool held = false;
        frame.MouseEnter += (_, _) => { if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover")); if (!flashing()) ShowMore(aux, true); };
        frame.MouseLeave += (_, _) => { pressed = null; if (!flashing() && !held) { frame.Background = rest; ShowMore(aux, false); } };
        frame.MouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(frame); if (!flashing()) frame.Background = UI.Brush(PanelTheme.Hex("pressed")); e.Handled = true; };
        frame.MouseLeftButtonUp += (_, e) =>
        {
            if (pressed is null) return;
            pressed = null; e.Handled = true;
            if (!flashing()) frame.Background = picked ? rest : UI.Brush(PanelTheme.Hex("hover"));
            click();
        };
        frame.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var opened = menu();
            held = true;
            opened.Closed += (_, _) => { held = false; if (!frame.IsMouseOver && !flashing()) { frame.Background = rest; ShowMore(aux, false); } };
            OpenMenu(opened, frame, atPointer: true);
        };
        if (drag is not null)
            frame.MouseMove += (_, e) =>
            {
                if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(frame);
                if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressed = null; frame.Background = rest;
                var data = drag();
                data.SetData(ShelfFormat, side + "|" + id);
                DragOut(frame, data);
            };
    }
    private void DragOut(DependencyObject source, DataObject data, DragDropEffects allowed = DragDropEffects.Copy)
    {
        _draggingOut = true;
        try { DragDrop.DoDragDrop(source, data, allowed); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        finally
        {
            _draggingOut = false;
            if (_expanded && !_pointerInside) PointerChanged(false);
        }
    }
    // Files dragged out to be moved leave the list once they are gone from where they were. The drag's result does not tell: the
    // Explorer moves files itself and reports no move, and it may still be moving them after the drag ends (a large file to another
    private void Flash(Guid id, string text)
    {
        _flash = (id, text, Environment.TickCount64 + 1200);
        if (_shelfRows.TryGetValue(id, out var row)) ShowFlash(row.Row, row.Aux, text);
        _flashTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1250) };
        _flashTimer.Tick -= FlashEnded; _flashTimer.Tick += FlashEnded;
        _flashTimer.Stop(); _flashTimer.Start();
    }
    private void FlashEnded(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        if (_flash is not { } flash) return;
        _flash = null;
        if (_shelfRows.TryGetValue(flash.Id, out var row))
        {
            bool over = row.Row.IsMouseOver;
            row.Row.Background = _picked.Contains(flash.Id) ? UI.Brush(UI.FillSelected) : over ? UI.Brush(PanelTheme.Hex("hover")) : Brushes.Transparent;
            if (row.Aux.Tag is RowEnd { Label: var label } && label.Tag is string original) { label.Text = original; label.Tag = null; label.Foreground = UI.Brush(UI.Tertiary); label.Margin = new Thickness(0); }
            ShowMore(row.Aux, over);
        }
    }
    private void ShowFlash(Border row, Grid aux, string text)
    {
        var accent = PanelTheme.ColorOf("accent");
        row.Background = new SolidColorBrush(Color.FromArgb(51, accent.R, accent.G, accent.B));
        if (aux.Tag is RowEnd end)
        {
            end.Label.Tag ??= end.Label.Text;
            // On the tinted row the word keeps as far from the right edge as the entry does from the left (8 DIP: the row's right
            // padding is 4).
            end.Label.Text = text; end.Label.Foreground = UI.Brush(UI.Primary); end.Label.Margin = new Thickness(0, 0, 4, 0);
            end.Label.Visibility = Visibility.Visible; end.More.Visibility = Visibility.Hidden;
        }
    }

    // ---- Small parts ----
    private static TextBlock Label(string text, double size, string color, HorizontalAlignment align, double top = 0, double left = 0) => new()
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(color), HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(left, top, 0, 0)
    };
    private double TextWidth(string text, double size, FontWeight weight, FontFamily? font = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font ?? UI.PanelFont, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
    // "常用"'s star: outlined in the menus, filled where it marks an entry.
    private const string StarData = "M 8,1.8 L 9.9,5.7 L 14.2,6.3 L 11.1,9.3 L 11.8,13.6 L 8,11.6 L 4.2,13.6 L 4.9,9.3 L 1.8,6.3 L 6.1,5.7 Z";
    private static Path StarGlyph(double size, string color, bool filled) => new()
    {
        Data = Geometry.Parse(StarData), Stroke = UI.Brush(color), StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, Fill = filled ? UI.Brush(color) : null,
        Stretch = Stretch.Uniform, Width = size, Height = size
    };
    // A capsule button: 26 high, corner 13, 12 px text; the main action is white with black text, the others 12 % white.
    private Button PillButton(string text, bool primary, Action click)
    {
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? UI.Brush(PanelTheme.Hex("surface")) : UI.Brush(UI.Primary), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Border { Height = 26, CornerRadius = new CornerRadius(PanelTheme.Corner(13)), Padding = new Thickness(12, 0, 12, 0), Child = label,
            Background = primary ? UI.Brush(UI.Primary) : UI.Brush(UI.FillSelected) };
        var button = new Button { Content = face, Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        button.MouseEnter += (_, _) => face.Opacity = .86;
        button.MouseLeave += (_, _) => face.Opacity = 1;
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        AutomationProperties.SetName(button, text);
        return button;
    }
    private DeckSettings SettingsWith(Action<DeckSettings> change)
    {
        var copy = Settings.Copy();
        change(copy);
        return copy;
    }
}