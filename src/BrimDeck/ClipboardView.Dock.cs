// Header.horizontal / ShelfSearch translation from Edge-Drop 767b6d63 (Apache-2.0).
// BrimDeck adaptation: fixed type track, inline classification and on-demand search keep the 40-DIP header.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private readonly Grid _dockHeader = new() { Height = ClipboardDockLayout.HeaderHeight, Background = Brushes.Transparent };
    private readonly StackPanel _dockTypes = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
    private readonly StackPanel _dockRight = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
    private readonly Grid _dockCenter = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dockSearch = new() { Height = ClipboardDockLayout.SearchHeight, Width = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DockPanel _categoryBar = new() { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    private readonly ScrollViewer _categoryScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly List<(Button Button, ClipboardContentKind[] Kinds)> _typeButtons = [];
    private Button _searchToggle = null!, _searchClear = null!;
    private Grid _searchField = null!;
    private ColumnDefinition _searchActionColumn = null!;
    private bool _searchOpen, _emojiOpen;
    internal event Action<DeckPage>? PageRequested;
    internal Func<IReadOnlyList<DeckPage>>? AvailablePages { get; set; }

    private TextBox BuildDockHeader()
    {
        AutomationProperties.SetAutomationId(_dockHeader, "clipboard-dock-header");
        _dockHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _dockHeader.ColumnDefinitions.Add(new());
        _dockHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Children.Add(_dockHeader); _dockHeader.Children.Add(_dockTypes);
        Grid.SetColumn(_dockCenter, 1); _dockHeader.Children.Add(_dockCenter);
        Grid.SetColumn(_dockRight, 2); _dockHeader.Children.Add(_dockRight);
        _dockCenter.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _dockCenter.ColumnDefinitions.Add(new());
        var track = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var trackFill = new Border { Child = track }; trackFill.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); trackFill.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        _dockTypes.Children.Add(trackFill);
        void Type(string glyph, string name, params ClipboardContentKind[] kinds)
        {
            var icon = glyph == "code" ? ClipboardCollectionVisuals.Icon("code", 14)! : EdgeDropGlyph.Create(glyph);
            var button = Round(icon, name, () => { SetEmojiOpen(false); _kindFilter.Clear(); foreach (var kind in kinds) _kindFilter.Add(kind); Refresh(true); });
            button.Width = 28; button.Height = 26; track.Children.Add(button); _typeButtons.Add((button, kinds));
            AutomationProperties.SetAutomationId(button, "clipboard-type-" + glyph);
        }
        Type("clockCounterClockwise", Loc.T("全部历史", "All history"));
        Type("textT", Loc.T("文本", "Text"), ClipboardContentKind.Text, ClipboardContentKind.RichText, ClipboardContentKind.Code);
        Type("link", Loc.T("链接", "Links"), ClipboardContentKind.Link);
        Type("image", Loc.T("图片与截图", "Images and screenshots"), ClipboardContentKind.Image, ClipboardContentKind.Screenshot);
        Type("files", Loc.T("文件", "Files"), ClipboardContentKind.Files);
        Type("palette", Loc.T("颜色", "Colors"), ClipboardContentKind.Color);
        Type("code", Loc.T("代码", "Code"), ClipboardContentKind.Code);

        _dockSearch.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); _dockSearch.SetResourceReference(Border.CornerRadiusProperty, "PanelR12");
        AutomationProperties.SetAutomationId(_dockSearch, "clipboard-search-capsule");
        var field = new Grid(); field.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); field.ColumnDefinitions.Add(new());
        _searchActionColumn = new() { Width = new GridLength(0) }; field.ColumnDefinitions.Add(_searchActionColumn);
        _searchToggle = Round(EdgeDropGlyph.Create("magnifyingGlass", 14), Loc.T("展开搜索 · Ctrl+F", "Expand search · Ctrl+F"), () => SetSearchOpen(!_searchOpen, focus: !_searchOpen));
        _searchToggle.Width = _searchToggle.Height = 28; AutomationProperties.SetAutomationId(_searchToggle, "clipboard-search-toggle"); field.Children.Add(_searchToggle);
        _searchField = new Grid { Visibility = Visibility.Collapsed }; Grid.SetColumn(_searchField, 1); field.Children.Add(_searchField);
        var search = new TextBox { Style = (Style)FindResource("ClipboardField"), MaxLength = 512, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(search, Loc.T("搜索剪贴板", "Search clipboard")); AutomationProperties.SetAutomationId(search, "clipboard-search");
        var placeholder = new TextBlock { Text = Loc.T("搜索…", "Search..."), FontSize = 12, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary"); _searchField.Children.Add(placeholder); _searchField.Children.Add(search);
        search.PreviewMouseLeftButtonDown += (_, _) => InputRequested?.Invoke();
        AutomationProperties.SetHelpText(search, Loc.T("Ctrl+F 搜索，↓ 选择，Enter 或 Alt+1…9 使用默认动作，Ctrl+Enter 仅复制", "Ctrl+F searches; Down selects; Enter or Alt+1…9 uses the default action; Ctrl+Enter copies only"));
        _searchClear = Round(EdgeDropGlyph.Create("x", 9), Loc.T("清除搜索／收起搜索", "Clear / collapse search"), () => { if (search.Text.Length > 0) search.Clear(); else SetSearchOpen(false); });
        _searchClear.Width = _searchClear.Height = 18; _searchClear.Focusable = false; _searchClear.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_searchClear, "clipboard-search-clear"); Grid.SetColumn(_searchClear, 2); field.Children.Add(_searchClear);
        search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (search.Text.Length > 0) SetSearchOpen(true);
            _searchToggle.Tag = _searchOpen || search.Text.Length > 0 ? "Selected" : null;
            if (!_changing) Refresh();
        };
        _dockSearch.Child = field; _dockCenter.Children.Add(_dockSearch);

        _modes = new IslandSegmentedControl([Loc.T("标签", "Tags"), Loc.T("应用", "Apps")]) { Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(_categoryBar, "clipboard-classification");
        _collectionMode = _modes.Buttons[0]; _applicationMode = _modes.Buttons[1];
        AutomationProperties.SetAutomationId(_collectionMode, "clipboard-mode-collections"); AutomationProperties.SetAutomationId(_applicationMode, "clipboard-mode-applications");
        _collectionMode.Click += (_, _) => ApplicationsMode = false; _applicationMode.Click += (_, _) => ApplicationsMode = true;
        DockPanel.SetDock(_modes, Dock.Left); _categoryBar.Children.Add(_modes);
        _categoryScroll.Content = _tabs;
        AutomationProperties.SetAutomationId(_categoryScroll, "clipboard-classification-scroll");
        _dockHeader.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta == 0 || !CategoryWheel(e.OriginalSource as DependencyObject)) return;
            if (e.Delta < 0) _categoryScroll.LineRight(); else _categoryScroll.LineLeft();
            e.Handled = true;
        };
        _dockHeader.AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            // ScrollViewer handles even an empty axis. Let the original event reach the host, not a second synthetic wheel.
            if (!CategoryWheel(e.OriginalSource as DependencyObject)) e.Handled = false;
        }), handledEventsToo: true);
        _drag.AttachScroller(_categoryScroll, Orientation.Horizontal); _categoryBar.Children.Add(_categoryScroll);
        Grid.SetColumn(_categoryBar, 1); _dockCenter.Children.Add(_categoryBar);
        _dockCenter.SizeChanged += (_, _) => FitDockHeader();

        _favorites = Round(EdgeDropGlyph.Create("star", 15), Loc.T("只看收藏", "Favorites only"), () => { _favoritesOnly = !_favoritesOnly; Refresh(); });
        _favorites.Width = _favorites.Height = 28; _drag.AttachFavorites(_favorites); _dockRight.Children.Add(_favorites);
        _sortButton = Round(EdgeDropGlyph.Create("arrowLineDown", 15), Loc.T("排序：时间 / 复制次数", "Sort: time / copy count"), ShowSort);
        _sortButton.Width = _sortButton.Height = 28; _sortButton.Margin = new Thickness(4, 0, 0, 0); _dockRight.Children.Add(_sortButton);
        AutomationProperties.SetAutomationId(_sortButton, "clipboard-sort");
        // Left-click and keyboard pickers open explicitly; native close animation must not make them right-click menus.
        ContextMenuService.SetIsEnabled(_sortButton, false);
        var settingsIcon = new Grid { Width = 16, Height = 16 }; settingsIcon.Children.Add(EdgeDropGlyph.Create("gearSix", 16));
        _statusDot = new Border { Width = 3, Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        settingsIcon.Children.Add(_statusDot);
        _settingsButton = Round(settingsIcon, Loc.T("剪贴板设置", "Clipboard settings"), () => { _collapse(); _openSettings(); });
        _settingsButton.Width = _settingsButton.Height = 28; _settingsButton.Margin = new Thickness(4, 0, 0, 0); _dockRight.Children.Add(_settingsButton);
        AutomationProperties.SetAutomationId(_settingsButton, "clipboard-settings");
        ContextMenuService.SetIsEnabled(_settingsButton, false);
        // Blank header space and controls use the host's configured right-click settings action.
        // Application, collection and card menus handle only their own targets.
        _layout = Round(EdgeDropGlyph.Create("files"), Loc.T("切换网格 / 横向列表", "Toggle grid / strip"), () => { _setGrid(!_settings().GridView); ApplyLayout(); });
        _filter = Round(EdgeDropGlyph.Create("files"), Loc.T("来源、类型与归档筛选", "Source, type and archive filters"), ShowFilters);
        BuildNotice();
        return search;
    }
    private bool CategoryWheel(DependencyObject? source) => ReferenceEquals(Find<ScrollViewer>(source), _categoryScroll)
        && _categoryScroll.IsVisible && _categoryScroll.ViewportWidth > 0 && _categoryScroll.ScrollableWidth > .5;

    private void ClearHistory()
    {
        if (Confirm(new(PromptId.ClipboardClearHistory, Loc.T("清空未收藏、未归档的历史？此操作不可撤销。原文件和系统剪贴板不会更改。", "Clear non-favorite, non-archived history? This cannot be undone. Original files and the system clipboard are unchanged.")))) _history.Clear();
    }
    private void RefreshDockHeader(int count)
    {
        foreach (var (button, kinds) in _typeButtons) button.Tag = !_emojiOpen && _kindFilter.SetEquals(kinds) ? "Selected" : null;
        AutomationProperties.SetItemStatus(_dockHeader, Loc.T($"{count} 条记录", $"{count} entries"));
        _sortButton.Tag = SortOrder == ClipboardSort.Count ? "Selected" : null;
        string order = SortOrder == ClipboardSort.Count ? Loc.T("复制次数从多到少", "Copy count, highest first") : Loc.T("复制时间从新到旧", "Copy time, newest first");
        _sortButton.ToolTip = Loc.T("排序：", "Sort: ") + order; AutomationProperties.SetName(_sortButton, (string)_sortButton.ToolTip);
    }
    private void SetSearchOpen(bool open, bool focus = false)
    {
        if (open && _emojiOpen) SetEmojiOpen(false);
        bool changed = _searchOpen != open; _searchOpen = open;
        _searchField.Visibility = _searchClear.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _searchActionColumn.Width = new GridLength(open ? 24 : 0);
        _searchToggle.Tag = open || _search.Text.Length > 0 ? "Selected" : null;
        AutomationProperties.SetHelpText(_searchToggle, open ? Loc.T("收起搜索，保留当前查询", "Collapse search and keep the current query") : Loc.T("展开搜索 · Ctrl+F", "Expand search · Ctrl+F"));
        FitDockHeader(animate: changed);
        if (focus)
        {
            InputRequested?.Invoke();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!_disposed && _searchOpen && IsVisible) { _search.Focus(); _search.SelectAll(); } });
        }
        else if (!open && _search.IsKeyboardFocusWithin) _searchToggle.Focus();
    }
    internal void RestoreHeaderFocus() { if (_searchOpen) _search.Focus(); else _searchToggle.Focus(); }
    private void FitDockHeader(bool animate = false)
    {
        if (_searchToggle is null || _dockCenter.ActualWidth <= 0) return;
        // Seven fixed types and the sort action use compact gutters, not hidden icons or a clipped search field.
        bool narrow = ActualWidth < 600;
        foreach (var (button, _) in _typeButtons) button.Width = narrow ? 25 : 28;
        _dockTypes.Margin = new Thickness(narrow ? 8 : 16, 0, narrow ? 4 : 8, 0);
        _dockRight.Margin = new Thickness(narrow ? 4 : 8, 0, narrow ? 8 : 16, 0);
        double available = Math.Max(30, _dockCenter.ActualWidth - _dockSearch.Margin.Right);
        // Fixed type controls never disappear. On narrow pages only the expanded search temporarily replaces navigation.
        bool showCategories = !_emojiOpen && (!_searchOpen || available >= ClipboardDockLayout.SearchWidth + _modes.Width + _modes.Margin.Right + 60);
        _categoryBar.Visibility = showCategories ? Visibility.Visible : Visibility.Collapsed;
        _dockSearch.Visibility = _emojiOpen ? Visibility.Collapsed : Visibility.Visible;
        double width = _searchOpen ? Math.Min(ClipboardDockLayout.SearchWidth, available) : 30;
        if (Math.Abs(_dockSearch.Width - width) > .1)
        {
            double from = _dockSearch.ActualWidth; _dockSearch.BeginAnimation(WidthProperty, null); _dockSearch.Width = width;
            if (animate && Soft.For(this) && IsLoaded) _dockSearch.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(Soft.Choose)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
        }
    }
    private void ShowPages()
    {
        var menu = Menu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_settingsButton.ContextMenu, menu)) _settingsButton.ContextMenu = null; };
        foreach (var page in AvailablePages?.Invoke() ?? DeckPages.Order)
        {
            string title = page switch { DeckPage.Usage => Loc.T("用量", "Usage"), DeckPage.Music => Loc.T("音乐", "Music"), DeckPage.Monitor => Loc.T("监控", "Monitor"), DeckPage.FileTray => Loc.T("文件中转", "File shelf"), _ => Loc.T("剪贴板", "Clipboard") };
            AddMenu(menu, title, () => PageRequested?.Invoke(page));
        }
        menu.PlacementTarget = _settingsButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
}
