using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The full library and the island share one History. This window owns only its query, selection and an explicit edit draft.
internal sealed class ClipboardLibraryWindow : Window
{
    private readonly ClipboardHistory _history;
    private readonly ClipboardDragDrop _drag;
    private readonly ApplicationLabelMotion _applicationMotion;
    private readonly Action<string, bool>? _setApplicationPinned, _setApplicationHidden;
    private Popup? _appearancePopup;
    private readonly Action<ClipboardEntry, bool> _activate;
    private readonly Func<(bool Enabled, bool Paused, string Problem)> _status;
    private readonly Action _recording, _settings;
    private readonly Func<string, bool> _confirm;
    private readonly TextBox _search, _categoryName;
    private readonly TextBlock _stats, _results, _message, _recordingLabel;
    private readonly StackPanel _categories = new(), _typeBar = new() { Orientation = Orientation.Horizontal };
    private readonly ClipboardEntryEditor _inspector;
    private readonly Func<ClipboardSettings> _preferences;
    private readonly ComboBox _source;
    private readonly ListBox _list;
    private readonly Grid _body;
    private readonly DockPanel _center;
    private readonly Border _inspectorHost, _detailBackdrop;
    private readonly Button _detailsButton, _backToList;
    private bool _compact, _detailsOpen;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly HashSet<ClipboardContentKind> _types = [];
    private ClipboardArchiveScope _archive;
    private string _collection = "", _sourceName = "", _selectedId = "";
    private bool _favorites, _unknownSourceOnly, _byApplication, _refreshing, _disposed, _forceClose;
    internal int ResultCount => _list.Items.Count;
    internal bool HasUnsavedChanges => _inspector.IsDirty;
    internal string SearchText { get => _search.Text; set => _search.Text = value; }
    internal ClipboardEntry? SelectedEntry => _list.SelectedItem as ClipboardEntry;

    internal ClipboardLibraryWindow(ClipboardHistory history, Action<ClipboardEntry, bool> activate,
        Func<(bool Enabled, bool Paused, string Problem)> status, Action recording, Action settings, Func<string, bool>? confirm = null, Func<ClipboardSettings>? preferences = null, Action<string, bool>? setApplicationPinned = null, Action<string, bool>? setApplicationHidden = null)
    {
        var localPreferences = new ClipboardSettings();
        _history = history; _activate = activate; _status = status; _recording = recording; _settings = settings; _preferences = preferences ?? (() => localPreferences);
        _setApplicationPinned = setApplicationPinned; _setApplicationHidden = setApplicationHidden;
        Soft.SetReduced(this, _preferences().Mode == ModuleMode.Performance);
        _drag = new ClipboardDragDrop(history, dragging => { if (!dragging) Refresh(); }, text => { if (_message is not null) _message.Text = text; });
        _confirm = confirm ?? (message => MessageBox.Show(this, message, Loc.T("剪贴板库", "Clipboard Library"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ClipboardLibraryWindow).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
        _inspector = new ClipboardEntryEditor(history, _preferences, activate, _confirm);
        _inspector.Feedback += text => { if (_message is not null) _message.Text = text; };
        _inspector.Saved += id => FocusEntry(id);
        Title = Loc.T("BrimDeck 剪贴板库", "BrimDeck Clipboard Library"); Width = 1180; Height = 760; MinWidth = 860; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "PanelSurface"); SetResourceReference(ForegroundProperty, "PanelPrimary");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display); SettingsChrome.Apply(this);
        var root = new Grid(); root.SetResourceReference(Panel.BackgroundProperty, "PanelSurface"); root.RowDefinitions.Add(new() { Height = new GridLength(52) }); root.RowDefinitions.Add(new());
        var caption = new DockPanel { LastChildFill = true, Margin = new Thickness(18, 0, 6, 0) };
        var captionActions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(captionActions, Dock.Right); caption.Children.Add(captionActions);
        foreach (var (label, action) in new (string, Action)[] { ("−", () => WindowState = WindowState.Minimized), ("□", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized), ("×", Close) })
        {
            var button = Button(label, action); button.Width = 36; WindowChrome.SetIsHitTestVisibleInChrome(button, true); captionActions.Children.Add(button);
            AutomationProperties.SetName(button, label == "×" ? Loc.T("关闭", "Close") : label == "−" ? Loc.T("最小化", "Minimize") : Loc.T("最大化 / 还原", "Maximize / restore"));
        }
        var heading = ClipboardPreview.Text(Loc.T("剪贴板库", "Clipboard Library"), 15); heading.FontWeight = FontWeights.SemiBold; heading.VerticalAlignment = VerticalAlignment.Center; caption.Children.Add(heading); root.Children.Add(caption);
        var body = _body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(188) }); body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = new GridLength(310) }); Grid.SetRow(body, 1); root.Children.Add(body);
        var sidebar = new DockPanel { Margin = new Thickness(10, 12, 10, 12) };
        var sideChrome = new Border { Child = sidebar, BorderThickness = new Thickness(0, 1, 1, 0) }; sideChrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); sideChrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); body.Children.Add(sideChrome);
        var add = new StackPanel { Margin = new Thickness(4, 8, 4, 0) }; DockPanel.SetDock(add, Dock.Bottom); sidebar.Children.Add(add);
        _categoryName = Field(Loc.T("新分类名称", "New category name")); _categoryName.MaxLength = 32; add.Children.Add(_categoryName);
        AutomationProperties.SetAutomationId(_categoryName, "clipboard-collection-name");
        _categoryName.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { SaveCategory(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _categoryName.Clear(); _search?.Focus(); e.Handled = true; }
        };
        var categoryActions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        categoryActions.Children.Add(Button(Loc.T("添加分类", "Add collection"), SaveCategory)); categoryActions.Children.Add(Button(Loc.T("取消", "Cancel"), () => _categoryName.Clear())); add.Children.Add(categoryActions);
        var categoryScroll = new ScrollViewer { Content = _categories, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        _drag.AttachScroller(categoryScroll, Orientation.Vertical); sidebar.Children.Add(categoryScroll);
        _applicationMotion = new ApplicationLabelMotion(body, _categories);
        var center = _center = new DockPanel { Margin = new Thickness(18, 12, 18, 12) }; Grid.SetColumn(center, 1); body.Children.Add(center);
        var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); center.Children.Add(tools);
        var searchRow = new DockPanel(); var create = Button(Loc.T("＋ 新建片段", "＋ New snippet"), () => { if (DiscardDraft()) { LoadDraft(null, true); SetDetailsOpen(true); _inspector.FocusEditor(); } }); DockPanel.SetDock(create, Dock.Right); searchRow.Children.Add(create);
        _detailsButton = Button(Loc.T("详情", "Details"), () => SetDetailsOpen(!_detailsOpen));
        AutomationProperties.SetAutomationId(_detailsButton, "clipboard-library-details"); DockPanel.SetDock(_detailsButton, Dock.Right); searchRow.Children.Add(_detailsButton);
        _search = Field(Loc.T("搜索内容、URL、代码、标签、备注", "Search content, URLs, code, tags and notes")); _search.Margin = new Thickness(0, 0, 8, 0); _search.MaxLength = 512;
        _search.ToolTip = Loc.T("搜索内容、URL、代码、标签、备注 · Ctrl+F 聚焦搜索", "Search content, URLs, code, tags and notes · Ctrl+F to focus search");
        AutomationProperties.SetHelpText(_search, (string)_search.ToolTip); searchRow.Children.Add(_search); tools.Children.Add(searchRow);
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(animate: false); };
        _stats = ClipboardPreview.Text("", 10.5, "PanelTertiary"); _stats.Margin = new Thickness(0, 10, 0, 10);
        _stats.TextWrapping = TextWrapping.NoWrap; _stats.TextTrimming = TextTrimming.CharacterEllipsis; tools.Children.Add(_stats);
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = Button(Loc.T("清除筛选", "Clear filters"), () => SetFilter(new())); DockPanel.SetDock(clear, Dock.Right); filterRow.Children.Add(clear);
        _source = new ComboBox { Style = (Style)FindResource("LibraryCombo"), MinWidth = 130, MaxWidth = 220, Height = 30, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(_source, Loc.T("按来源应用筛选", "Filter by source application")); PopupMotion.Attach(_source);
        _source.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var group = (_source.SelectedItem as ComboBoxItem)?.Tag as ClipboardApplicationGroup;
            _sourceName = group?.Source ?? ""; _unknownSourceOnly = group?.Unknown == true;
            if (_collection.Length == 0) _byApplication = group is not null;
            Refresh(animate: true);
        };
        filterRow.Children.Add(_source); tools.Children.Add(filterRow);
        // A horizontal scroll keeps all type choices reachable at minimum width instead of silently hiding filters.
        var typeScroll = new ScrollViewer { Content = _typeBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Margin = new Thickness(0, 0, 0, 8) }; tools.Children.Add(typeScroll);
        _results = ClipboardPreview.Text("", 11, "PanelSecondary"); _results.Margin = new Thickness(0, 3, 0, 8);
        _results.TextWrapping = TextWrapping.NoWrap; _results.TextTrimming = TextTrimming.CharacterEllipsis; tools.Children.Add(_results);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); center.Children.Add(footer);
        _message = ClipboardPreview.Text("", 11, "PanelAccent"); AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite); footer.Children.Add(_message);
        var statusRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; var preferencesButton = Button(Loc.T("设置", "Settings"), _settings); DockPanel.SetDock(preferencesButton, Dock.Right); statusRow.Children.Add(preferencesButton);
        var recordingButton = Button(Loc.T("启用 / 暂停", "Enable / pause"), () => { _recording(); RefreshStatus(); }); DockPanel.SetDock(recordingButton, Dock.Right); statusRow.Children.Add(recordingButton);
        _recordingLabel = ClipboardPreview.Text("", 10.5, "PanelTertiary"); _recordingLabel.VerticalAlignment = VerticalAlignment.Center; statusRow.Children.Add(_recordingLabel); footer.Children.Add(statusRow);
        _list = new ListBox { Style = (Style)FindResource("ClipboardList"), ItemContainerStyle = (Style)FindResource("LibraryRow"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled); _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true); _list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling); _list.SetValue(VirtualizingPanel.IsVirtualizingWhenGroupingProperty, true);
        var factory = new FrameworkElementFactory(typeof(ClipboardLibraryItem)); _list.ItemTemplate = new DataTemplate { VisualTree = factory };
        var group = new FrameworkElementFactory(typeof(TextBlock)); group.SetBinding(TextBlock.TextProperty, new Binding("Name")); group.SetValue(TextBlock.FontSizeProperty, 12d); group.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); group.SetValue(MarginProperty, new Thickness(0, 12, 0, 10)); group.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = group } });
        _list.SelectionChanged += SelectionChanged; _drag.AttachEntries(_list);
        _list.AddHandler(ClipboardLibraryItem.ActionRequestedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is ClipboardLibraryItem { Entry: { } entry } row) { Activate(entry, row.RequestedAction == "copy"); e.Handled = true; } }));
        _list.PreviewMouseLeftButtonUp += (_, e) => { if (Find<Button>(e.OriginalSource as DependencyObject) is null && Find<ListBoxItem>(e.OriginalSource as DependencyObject) is not null && SelectedEntry is not null) SetDetailsOpen(true); };
        _list.MouseDoubleClick += (_, e) => { if (Find<Button>(e.OriginalSource as DependencyObject) is null && SelectedEntry is not null) { SetDetailsOpen(true); _inspector.FocusEditor(); e.Handled = true; } };
        AutomationProperties.SetName(_list, Loc.T("剪贴板视觉时间线", "Clipboard visual timeline")); AutomationProperties.SetAutomationId(_list, "clipboard-library-timeline"); center.Children.Add(_list);
        _detailBackdrop = new Border { Opacity = .35, Visibility = Visibility.Collapsed }; _detailBackdrop.SetResourceReference(Border.BackgroundProperty, "PanelSurface");
        Grid.SetColumnSpan(_detailBackdrop, 3); Panel.SetZIndex(_detailBackdrop, 1); body.Children.Add(_detailBackdrop);
        _detailBackdrop.MouseLeftButtonDown += (_, e) => { SetDetailsOpen(false); e.Handled = true; };
        var inspector = _inspectorHost = new Border { BorderThickness = new Thickness(1, 1, 0, 0), Padding = new Thickness(14, 12, 14, 12) }; inspector.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); inspector.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder");
        var detailContent = new DockPanel();
        _backToList = Button(Loc.T("返回列表", "Back to list"), () => SetDetailsOpen(false)); _backToList.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(_backToList, "clipboard-library-back"); DockPanel.SetDock(_backToList, Dock.Top); detailContent.Children.Add(_backToList);
        detailContent.Children.Add(new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        inspector.Child = detailContent; Grid.SetColumn(inspector, 2); Panel.SetZIndex(inspector, 2); body.Children.Add(inspector);
        Content = root;
        body.SizeChanged += (_, _) => ApplyResponsiveLayout();
        _history.Changed += HistoryChanged;
        Closing += (_, e) => { if (!_forceClose && !DiscardDraft()) e.Cancel = true; };
        Closed += (_, _) => { _disposed = true; if (_appearancePopup is not null) _appearancePopup.IsOpen = false; _searchDelay.Stop(); _applicationMotion.Dispose(); _history.Changed -= HistoryChanged; };
        SourceInitialized += (_, _) => { FitToScreen(); RefreshTheme(); };
        Closed += (_, _) => _historyRefresh?.Abort();
        DpiChanged += (_, _) => FitToScreen();
        PreviewKeyDown += KeyPressed;
        IsVisibleChanged += (_, _) => RefreshWhenPresented();
        StateChanged += (_, _) =>
        {
            _body.Visibility = WindowState == WindowState.Minimized ? Visibility.Collapsed : Visibility.Visible;
            RefreshWhenPresented();
        };
        Refresh();
    }

    internal void SetFilter(ClipboardQuery query)
    {
        _archive = query.Archive; _collection = query.CollectionId; _favorites = query.FavoritesOnly; _sourceName = query.Source; _unknownSourceOnly = query.UnknownSourceOnly;
        _byApplication = _collection.Length == 0 && (_sourceName.Length > 0 || _unknownSourceOnly);
        _types.Clear(); foreach (var type in query.Kinds) _types.Add(type);
        _search.Text = query.Text; _searchDelay.Stop(); Refresh(animate: true);
    }
    private ClipboardQuery Query(string? collection = null) => new() { Text = _search.Text, CollectionId = collection ?? _collection, FavoritesOnly = _favorites, Source = _sourceName, UnknownSourceOnly = _unknownSourceOnly, Kinds = _types.ToArray(), Archive = _archive };
    internal void Refresh(string? focusId = null, bool animate = false)
    {
        if (_disposed || _refreshing || _drag.IsDragging) return;
        _inspector.RefreshPresentation();
        Soft.SetReduced(this, _preferences().Mode == ModuleMode.Performance);
        if (!Soft.For(this)) { _applicationMotion.Clear(); Soft.StopTree(this); }
        if (_collection.Length > 0 && !_history.Collections.Any(group => group.Id == _collection)) _collection = "";
        if (_unknownSourceOnly && !_history.Items.Any(item => string.IsNullOrWhiteSpace(item.SourceName))
            || _sourceName.Length > 0 && !_history.Sources.Contains(_sourceName, StringComparer.OrdinalIgnoreCase)
            || _unknownSourceOnly && _preferences().IsApplicationHidden("") || _sourceName.Length > 0 && _preferences().IsApplicationHidden(_sourceName)) { _sourceName = ""; _unknownSourceOnly = false; }
        _refreshing = true;
        try
        {
            string id = focusId ?? SelectedEntry?.Id ?? _selectedId;
            var scroller = FindScroll(_list); double offset = scroller?.VerticalOffset ?? 0;
            var items = _history.Query(Query()).ToList();
            var view = new ListCollectionView(items); view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipboardEntry.CopiedAt), new ClipboardDayGroup()));
            // Hold the selector's intermediate null while replacing its view; metadata refresh must not clear the detail preview.
            _list.ItemsSource = view; _list.SelectedItem = items.FirstOrDefault(item => item.Id == id) ?? (!_inspector.IsDirty && !_inspector.IsNew && !_inspector.IsSaving ? items.FirstOrDefault() : null);
            _selectedId = SelectedEntry?.Id ?? id;
            if (scroller is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (!_disposed) scroller.ScrollToVerticalOffset(offset); });
            if (!_inspector.IsDirty && !_inspector.IsNew && !_inspector.IsSaving) LoadDraft(SelectedEntry);
            else if (_inspector.EntryId is { } draftId && !_history.Items.Any(item => item.Id == draftId)) _message.Text = Loc.T("正在编辑的记录已从库中移除，请取消此草稿。", "The entry being edited was removed. Cancel this draft.");
            var stats = _history.Statistics(DateTimeOffset.Now);
            _stats.Text = Loc.T($"{stats.Total} 条记录 · 今日新增 {stats.Today} · 收藏 {stats.Favorites} · 已归档 {stats.Archived} · 复用 {stats.Uses} 次 · {stats.Bytes / 1048576d:0.#} MB",
                $"{stats.Total} entries · {stats.Today} new today · {stats.Favorites} favorites · {stats.Archived} archived · {stats.Uses} reuses · {stats.Bytes / 1048576d:0.#} MB");
            _stats.ToolTip = _stats.Text;
            _results.Text = items.Count > 0 ? Loc.T($"{items.Count} 条匹配 · 单击查看详情 · Ctrl+F 搜索", $"{items.Count} matches · Select to inspect · Ctrl+F to search")
                : Loc.T("没有匹配记录。调整筛选，或启用记录后复制内容。", "No matching entries. Adjust filters, or enable recording and copy something.");
            string shortcuts = Loc.T("列表：Enter 按设置复制或回贴 · Ctrl+Enter 仅复制 · Delete 删除\n编辑：Ctrl+S 保存 · Esc 返回列表（保留草稿）", "Timeline: Enter follows the copy/paste setting · Ctrl+Enter copies only · Delete removes\nEditor: Ctrl+S saves · Esc returns to the list (keeps draft)");
            _results.ToolTip = shortcuts; AutomationProperties.SetHelpText(_list, shortcuts);
            var choices = new List<ComboBoxItem> { new() { Content = Loc.T("全部来源应用", "All source apps") } };
            choices.AddRange(_history.ApplicationGroups(Query()).Where(group => !_preferences().IsApplicationHidden(group.Source)).Select(group => new ComboBoxItem { Content = group.Label, Tag = group }));
            _source.ItemsSource = choices;
            _source.SelectedIndex = _sourceName.Length == 0 && !_unknownSourceOnly ? 0 : Math.Max(0, choices.FindIndex(choice => choice.Tag is ClipboardApplicationGroup group
                && group.Unknown == _unknownSourceOnly && string.Equals(group.Source, _sourceName, StringComparison.OrdinalIgnoreCase)));
            BuildCategories(); BuildTypes(); RefreshStatus();
            if (animate && IsVisible) PageHandover.Brighten(_list);
        }
        finally { _refreshing = false; }
    }
    internal void RefreshStatus()
    {
        var (enabled, paused, problem) = _status();
        _recordingLabel.Text = problem.Length > 0 ? problem : !enabled ? Loc.T("记录未启用 · 本机加密存储", "Recording off · encrypted locally") : paused ? Loc.T("记录已暂停", "Recording paused") : Loc.T("正在记录 · 本机加密存储", "Recording · encrypted locally");
    }
    private bool Presented => IsVisible && WindowState != WindowState.Minimized;
    private void RefreshWhenPresented()
    { if (Presented && _historyDirty) { _historyDirty = false; Refresh(); } }
    private bool _historyDirty;
    private DispatcherOperation? _historyRefresh;
    private void HistoryChanged()
    {
        _historyDirty = true;
        if (!Presented || _historyRefresh is not null) return;
        _historyRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        { _historyRefresh = null; if (!_disposed) RefreshWhenPresented(); });
    }
    private void BuildCategories()
    {
        _categories.Children.Clear();
        void Add(string title, string id, bool favorites, ClipboardArchiveScope archive, int count)
        {
            var row = new DockPanel(); var badge = ClipboardPreview.Text(count.ToString(), 10.5, "PanelTertiary"); badge.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            var name = ClipboardPreview.Text(title, 12); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(name);
            var button = Button(row, () => { _byApplication = false; _collection = id; _sourceName = ""; _unknownSourceOnly = false; _favorites = favorites; _archive = archive; Refresh(animate: true); }, id.Length == 0 ? "LibraryNav" : "LibraryCollectionNav");
            button.Tag = !_byApplication && _collection == id && _favorites == favorites && _archive == archive ? "Selected" : null;
            AutomationProperties.SetName(button, title + " · " + count); button.ToolTip = title; _categories.Children.Add(button);
            if (id.Length == 0)
            {
                if (favorites) _drag.AttachFavorites(button);
                else if (archive == ClipboardArchiveScope.Active) _drag.AttachHistory(button);
                return;
            }
            if (_history.Collections.FirstOrDefault(group => group.Id == id) is { } collection) ClipboardCollectionVisuals.Apply(button, collection, row);
            _drag.AttachCollection(button, id, Orientation.Vertical);
            button.ToolTip = title + Loc.T(" · 右键管理 · 拖动排序 · 拖入条目整理", " · Right-click to manage · Drag to reorder or drop entries here");
            var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
            PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
            void Menu(string label, Action action) { var item = new MenuItem { Header = label, Style = (Style)FindResource("TrayMenuItem") }; item.Click += (_, _) => action(); menu.Items.Add(item); }
            Menu(Loc.T("名称与标签外观…", "Name & label appearance…"), () =>
            {
                if (_appearancePopup is not null) _appearancePopup.IsOpen = false;
                _appearancePopup = ClipboardCollectionAppearance.Open(button, _history, id);
            });
            Menu(Loc.T("移到最前", "Move to start"), () => { if (_history.Collections.FirstOrDefault() is { } first) _history.ReorderCollection(id, first.Id); });
            Menu(Loc.T("移到最后", "Move to end"), () => { if (_history.Collections.LastOrDefault() is { } last) _history.ReorderCollection(id, last.Id, true); });
            Menu(Loc.T("删除分类（保留记录）…", "Delete collection (keep entries)…"), () =>
            {
                if (!_confirm(Loc.T("删除分类？其中的条目将保留在历史中，原文件和系统剪贴板不会更改。", "Delete this collection? Its entries stay in history; original files and the system clipboard are unchanged."))) return;
                _history.RemoveCollection(id);
            });
            button.ContextMenu = menu;
            button.MouseRightButtonUp += (_, e) => { menu.PlacementTarget = button; menu.IsOpen = true; e.Handled = true; };
        }
        var baseline = Query("") with { Source = "", UnknownSourceOnly = false, FavoritesOnly = false, Archive = ClipboardArchiveScope.Active };
        Add(Loc.T("历史", "History"), "", false, ClipboardArchiveScope.Active, _history.Query(baseline).Count());
        Add(Loc.T("收藏", "Favorites"), "", true, ClipboardArchiveScope.Active, _history.Query(baseline with { FavoritesOnly = true }).Count());
        Add(Loc.T("已归档", "Archived"), "", false, ClipboardArchiveScope.Archived, _history.Query(baseline with { Archive = ClipboardArchiveScope.Archived }).Count());
        void Heading(string title)
        {
            var heading = ClipboardPreview.Text(title, 10.5, "PanelTertiary"); heading.Margin = new Thickness(10, 20, 0, 8); _categories.Children.Add(heading);
        }
        Heading(Loc.T("收藏夹", "COLLECTIONS"));
        foreach (var category in _history.Collections) Add(category.Name, category.Id, false, ClipboardArchiveScope.Active, _history.Query(baseline with { CollectionId = category.Id }).Count());
        Heading(Loc.T("应用 · 自动分类", "APPLICATIONS · AUTO"));
        void Application(string title, string source, bool unknown, int count, string automationId)
        {
            var row = new DockPanel(); var badge = ClipboardPreview.Text(count.ToString(), 10.5, "PanelTertiary"); DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            bool application = source.Length > 0 || unknown, pinned = application && _preferences().IsApplicationPinned(source);
            if (pinned)
            {
                var pin = EdgeDropGlyph.Create("pushPin", 10); pin.Margin = new Thickness(0, 0, 5, 0); DockPanel.SetDock(pin, Dock.Left); row.Children.Add(pin);
            }
            var name = ClipboardPreview.Text(title, 12); name.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(name);
            var button = Button(row, () =>
            {
                _byApplication = true; _collection = ""; _sourceName = source; _unknownSourceOnly = unknown; _favorites = false; _archive = ClipboardArchiveScope.Active; Refresh(animate: true);
            }, "LibraryNav");
            button.AllowDrop = false;
            button.Tag = _byApplication && _unknownSourceOnly == unknown && string.Equals(_sourceName, source, StringComparison.OrdinalIgnoreCase) ? "Selected" : null;
            AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, title + " · " + count);
            button.ToolTip = title + Loc.T(" · 根据复制来源自动分类，不改变收藏夹归属", " · Automatically grouped by copy source, independently of collections");
            AutomationProperties.SetItemStatus(button, pinned ? Loc.T("已固定", "Pinned") : "");
            if (application || _preferences().HiddenApplications.Length > 0)
            {
                var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
                PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
                ClipboardApplicationMenu.Populate(menu, this, button, source, unknown, _history, _preferences,
                    (name, value) => { if (_setApplicationPinned is { } set) set(name, value); else _preferences().SetApplicationPinned(name, value); Refresh(); },
                    (name, value) => { if (_setApplicationHidden is { } set) set(name, value); else _preferences().SetApplicationHidden(name, value); Refresh(); },
                    _confirm, _applicationMotion.Remove, text => _message.Text = text);
                button.ContextMenu = menu;
                button.MouseRightButtonUp += (_, e) => { menu.PlacementTarget = button; menu.IsOpen = true; e.Handled = true; };
            }
            _categories.Children.Add(button);
        }
        Application(Loc.T("全部历史", "All history"), "", false, _history.Query(baseline).Count(), "clipboard-application-history");
        var pins = _preferences().PinnedApplications;
        foreach (var group in _history.ApplicationGroups(baseline).Where(group => !_preferences().IsApplicationHidden(group.Source)).OrderBy(group =>
        {
            int index = Array.FindIndex(pins, source => string.Equals(source, group.Source, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        })) Application(group.Label, group.Source, group.Unknown, group.Count, group.Unknown ? "clipboard-application-unknown" : "clipboard-application-source-" + group.Source);
    }
    private void BuildTypes()
    {
        _typeBar.Children.Clear();
        foreach (var type in Enum.GetValues<ClipboardContentKind>())
        {
            int count = _history.Query(Query() with { Kinds = [] }).Count(item => item.Content.Kind == type);
            var button = Button(ClipboardContentTypes.Label(type) + " " + count, () => { if (!_types.Remove(type)) _types.Add(type); Refresh(animate: true); });
            button.Tag = _types.Contains(type) ? "Selected" : null; button.Margin = new Thickness(0, 0, 4, 0); button.Height = 26;
            AutomationProperties.SetHelpText(button, Loc.T("可多选，再次点击取消", "Multi-select; click again to remove")); _typeBar.Children.Add(button);
        }
    }
    private void SaveCategory()
    {
        bool saved = _history.AddCollection(_categoryName.Text) is not null;
        if (saved) { _categoryName.Clear(); _message.Text = Loc.T("分类已添加", "Collection added"); }
        else _message.Text = Loc.T("分类名称不能为空、重复或超过 32 字；最多 20 个分类。", "Use a unique name of up to 32 characters; at most 20 collections.");
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (!DiscardDraft())
        {
            _refreshing = true; _list.SelectedItem = _history.Items.FirstOrDefault(item => item.Id == _selectedId); _refreshing = false; return;
        }
        _selectedId = SelectedEntry?.Id ?? ""; LoadDraft(SelectedEntry);
    }
    private void LoadDraft(ClipboardEntry? entry, bool create = false)
    {
        bool changed = _inspector.EntryId != entry?.Id || _inspector.IsNew != create;
        _inspector.Load(entry, create);
        // A live capture refreshes metadata in place; only an explicit detail change uses the shared content transition.
        if (changed && _inspector.IsVisible) PageHandover.Brighten(_inspector);
    }
    internal bool SaveDraft() => _inspector.Save();
    private bool DiscardDraft() => _inspector.TryDiscard();
    private void DeleteEntry(ClipboardEntry entry)
    {
        if (_inspector.EntryId != entry.Id && DiscardDraft()) LoadDraft(entry);
        if (_inspector.EntryId == entry.Id) _inspector.Delete();
    }
    private void Activate(ClipboardEntry entry, bool copyOnly) { _activate(entry, copyOnly); _message.Text = copyOnly ? Loc.T("已请求复制", "Copy requested") : Loc.T("已请求回贴；系统阻止时可手动 Ctrl+V", "Paste requested; use Ctrl+V if Windows blocks it"); }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed || e.Handled) return;
        var key = HotkeyGesture.KeyOf(e); var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && _compact && _detailsOpen) { SetDetailsOpen(false); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && key == Key.F) { SetDetailsOpen(false); _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && key == Key.S && _inspector.IsDirty) { SaveDraft(); e.Handled = true; }
        else if (Find<TextBox>(e.OriginalSource as DependencyObject) is null && Find<Button>(e.OriginalSource as DependencyObject) is null && SelectedEntry is { } entry)
        {
            if (key == Key.Enter && modifiers is ModifierKeys.None or ModifierKeys.Control && Find<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) { Activate(entry, modifiers == ModifierKeys.Control || !_preferences().PasteOnActivate); e.Handled = true; }
            else if (key == Key.Delete && modifiers == ModifierKeys.None && Find<ListBoxItem>(e.OriginalSource as DependencyObject) is not null) { DeleteEntry(entry); e.Handled = true; }
        }
        if (key == Key.Escape && _search.Text.Length > 0 && !_inspector.IsDirty) { _search.Clear(); e.Handled = true; }
    }
    internal void FocusEntry(string? id)
    {
        _searchDelay.Stop(); Refresh();
        if (id is not null && _history.Items.FirstOrDefault(item => item.Id == id) is { } entry)
        {
            if (!DiscardDraft()) return;
            SetFilter(new ClipboardQuery { Archive = entry.Archived ? ClipboardArchiveScope.Archived : ClipboardArchiveScope.Active });
            _list.SelectedItem = entry; _selectedId = entry.Id; LoadDraft(entry); _list.ScrollIntoView(entry); SetDetailsOpen(true);
        }
        else _search.Focus();
    }
    private Button Button(object content, Action action, string style = "LibraryButton")
    {
        var button = new Button { Content = content, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 3, 3) }; button.Click += (_, _) => action();
        if (content is string name) AutomationProperties.SetName(button, name); return button;
    }
    private TextBox Field(string name)
    {
        var field = new TextBox { Style = (Style)FindResource("LibraryField"), MinHeight = 30, ToolTip = name, Tag = name };
        AutomationProperties.SetName(field, name); return field;
    }
    private void ApplyResponsiveLayout()
    {
        double width = _body.ActualWidth;
        if (width <= 0) return;
        bool compact = width < 1040;
        // Keep one editor alive across the breakpoint so resizing never discards a draft or duplicates history state.
        if (compact && !_compact && (_inspector.IsDirty || _inspector.IsNew)) _detailsOpen = true;
        _compact = compact;
        _body.ColumnDefinitions[0].Width = new GridLength(compact ? 160 : 188);
        _body.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 310);
        _center.Margin = new Thickness(compact ? 12 : 18, 12, compact ? 12 : 18, 12);
        _detailsButton.Visibility = _backToList.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _detailsButton.Tag = compact && _detailsOpen ? "Selected" : null;
        Grid.SetColumn(_inspectorHost, compact ? 1 : 2); Grid.SetColumnSpan(_inspectorHost, compact ? 2 : 1);
        _inspectorHost.Width = compact ? Math.Min(360, Math.Max(0, width - 160)) : double.NaN;
        _inspectorHost.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _inspectorHost.Visibility = !compact || _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        _detailBackdrop.Visibility = compact && _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        _center.IsEnabled = !compact || !_detailsOpen;
        KeyboardNavigation.SetTabNavigation(_inspectorHost, compact ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
    }
    private void SetDetailsOpen(bool open)
    {
        if (!_compact || _detailsOpen == open) return;
        _detailsOpen = open; ApplyResponsiveLayout();
        if (open) { PageHandover.Brighten(_inspectorHost); _backToList.Focus(); }
        else _detailsButton.Focus();
    }
    private void FitToScreen()
    {
        var (_, _, work, scale) = WindowsHost.WindowScreen(this);
        double width = Math.Max(1, work.Width / scale - 24), height = Math.Max(1, work.Height / scale - 24);
        MinWidth = Math.Min(860, width); MinHeight = Math.Min(540, height);
        if (WindowState == WindowState.Normal) { Width = Math.Min(Width, width); Height = Math.Min(Height, height); }
    }
    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (FindScroll(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    internal void ForceClose() { _forceClose = true; _historyRefresh?.Abort(); Close(); }
    internal void RefreshTheme() { _applicationMotion.Clear(); SettingsChrome.SetTheme(this, PanelDark()); }
    private bool PanelDark() => Application.Current?.TryFindResource("PanelSurface") is SolidColorBrush brush && brush.Color.R + brush.Color.G + brush.Color.B < 384;
    private static T? Find<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T found) return found; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); } return null;
    }
}
