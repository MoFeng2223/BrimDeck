using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Page content only: the island owns its surface, placement, size, activation and opening/closing animation.
// Counts describe real entries; thumbnails use local content and never fetch link previews.
internal sealed partial class ClipboardView : Grid, IDisposable
{
    private readonly ClipboardHistory _history;
    private readonly ClipboardDragDrop _drag;
    private readonly Func<ClipboardSettings> _settings;
    private readonly Action<ClipboardEntry, bool> _activate;
    private readonly Action<ClipboardEntry, Rect, Rect, double>? _openPreviewWindow;
    private readonly Action _openSettings, _togglePause, _enable, _collapse;
    private readonly Func<(bool Enabled, bool Paused, string Problem)> _status;
    private readonly Action<bool> _setGrid;
    private readonly Action<string, bool>? _setApplicationPinned, _setApplicationHidden;
    private readonly ApplicationLabelMotion _applicationMotion;
    private readonly TextBox _search;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ListBox _list;
    private Button _favorites = null!, _layout = null!, _settingsButton = null!, _collectionMode = null!, _applicationMode = null!;
    private IslandSegmentedControl _modes = null!;
    private bool _byApplication, _unknownSourceOnly;
    internal bool ApplicationsMode
    {
        get => _byApplication;
        set
        {
            if (_byApplication == value) return;
            // Switching navigation must not leave an invisible filter from the other classification system.
            _byApplication = value; _collection = ""; _sourceFilter = ""; _unknownSourceOnly = false; Refresh(animate: true);
        }
    }
    private readonly TextBlock _empty;
    private readonly Grid _emptyHost = new();
    private Border _statusDot = null!;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private string _collection = "";
    private bool _favoritesOnly, _changing, _disposed;
    private bool? _gridApplied;
    private bool Compact => ActualHeight is > 0 and < 130;
    internal event Action? InputRequested, InteractionChanged;
    internal bool PopupOpen => _drag.IsDragging || _editor?.IsOpen == true || _activeMenu?.IsOpen == true || PreviewOpen;
    internal static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ClipboardView), new PropertyMetadata(172d));
    internal static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(ClipboardView), new PropertyMetadata(126d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); set => SetValue(CardHeightProperty, value); }
    private Popup? _editor;
    private ContextMenu? _activeMenu;
    internal int ResultCount => _list.Items.Count;
    internal string SearchText { get => _search.Text; set => _search.Text = value; }
    internal bool FavoritesOnly { get => _favoritesOnly; set { _favoritesOnly = value; Refresh(); } }
    internal string CollectionId { get => _collection; set { _byApplication = false; _collection = value; _sourceFilter = ""; _unknownSourceOnly = false; Refresh(); } }

    internal ClipboardView(ClipboardHistory history, Func<ClipboardSettings> settings, Action<ClipboardEntry, bool> activate,
        Action openSettings, Action togglePause, Action enable, Func<(bool Enabled, bool Paused, string Problem)> status, Action<bool> setGrid, Action collapse, Action<ClipboardEntry?>? openLibrary = null, Func<PromptRequest, bool>? confirm = null, Action<ClipboardSort>? setSort = null, Action<string, bool>? setApplicationPinned = null, Action<ClipboardEntry, Rect, Rect, double>? openPreviewWindow = null, Action<string, bool>? setApplicationHidden = null)
    {
        _openPreviewWindow = openPreviewWindow; _setApplicationHidden = setApplicationHidden;
        _history = history; _settings = settings; _activate = activate; _openSettings = openSettings; _togglePause = togglePause; _enable = enable; _status = status; _setGrid = setGrid;
        _collapse = collapse; _openLibrary = openLibrary; _confirmation = confirm; SortOrder = settings().SortOrder; _setSort = setSort; _setApplicationPinned = setApplicationPinned; UseLayoutRounding = true; SnapsToDevicePixels = true;
        _drag = new ClipboardDragDrop(history, dragging => { InteractionChanged?.Invoke(); if (!dragging) Refresh(); }, ShowNotice);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ClipboardView).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
        var root = this;
        root.RowDefinitions.Add(new() { Height = new GridLength(ClipboardDockLayout.HeaderHeight) });
        root.RowDefinitions.Add(new() { Height = new GridLength(0) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        _search = BuildDockHeader();
        SizeChanged += (_, _) => { FitDockHeader(); FitCards(); };
        _list = new ListBox { Style = (Style)FindResource("ClipboardList"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            ItemContainerStyle = (Style)FindResource("ClipboardDockItem"), SelectionMode = SelectionMode.Single, Focusable = false };
        var tile = new FrameworkElementFactory(typeof(ClipboardTimelineCard));
        tile.SetBinding(ClipboardTimelineCard.HeaderHeightProperty, new Binding(nameof(TimelineHeaderHeight)) { Source = this });
        var factory = new FrameworkElementFactory(typeof(ClipboardCard));
        factory.SetValue(Grid.RowProperty, 1);
        factory.SetValue(ClipboardCard.DockModeProperty, true);
        factory.SetBinding(WidthProperty, new Binding(nameof(CardWidth)) { Source = this });
        factory.SetBinding(HeightProperty, new Binding(nameof(CardHeight)) { Source = this });
        tile.AppendChild(factory);
        _list.ItemTemplate = new DataTemplate(typeof(ClipboardEntry)) { VisualTree = tile };
        _list.Margin = new Thickness(ClipboardDockLayout.ListInset, ClipboardDockLayout.ListTop, ClipboardDockLayout.ListInset, ClipboardDockLayout.ListBottom);
        _list.VerticalAlignment = VerticalAlignment.Stretch;
        InitializeTimeline();
        _list.SizeChanged += (_, _) => FitCards();
        // Capture before the virtual grid changes slots, then reuse the same keyed motion as sorting.
        _list.AddHandler(IslandCardPanel.ReflowingEvent, new RoutedEventHandler((_, _) => AnimateReorder(CaptureReorderPositions())));
        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true); _list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _list.SetValue(ScrollViewer.CanContentScrollProperty, true);
        Grid.SetRow(_list, 3); root.Children.Add(_list);
        AutomationProperties.SetName(_list, Loc.T("剪贴板历史记录", "Clipboard history")); AutomationProperties.SetAutomationId(_list, "clipboard-history");
        _list.AddHandler(ClipboardCard.PreviewRequestedEvent, new RoutedEventHandler((_, args) =>
        { if (args.OriginalSource is ClipboardCard { Entry: { } entry }) { ShowPreview(entry); args.Handled = true; } }));
        _list.AddHandler(ClipboardCard.FavoriteRequestedEvent, new RoutedEventHandler((_, args) =>
        { if (args.OriginalSource is ClipboardCard { Entry: { } item }) { _history.ToggleFavorite(item.Id); args.Handled = true; } }));
        _list.AddHandler(ClipboardCard.RecallRequestedEvent, new RoutedEventHandler((_, args) =>
        { if (args.OriginalSource is ClipboardCard { Entry: { } entry } card) { _activate(entry, card.RequestedCopyOnly); args.Handled = true; } }));
        InitializeCardActions(); InitializeHoverPreview();
        _drag.AttachEntries(_list, ClickCard);
        _list.PreviewMouseWheel += (_, args) =>
        {
            if (Descendant<ScrollViewer>(_list) is { } scroll)
            {
                if (_settings().GridView) { if (args.Delta < 0) scroll.LineDown(); else scroll.LineUp(); }
                else { if (args.Delta < 0) scroll.LineRight(); else scroll.LineLeft(); }
                args.Handled = true;
            }
        };
        _empty = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
        _emptyHost.Children.Add(_empty); _emptyHost.MouseLeftButtonDown += (_, _) => { if (!_status().Enabled) { _enable(); Refresh(); } };
        Grid.SetRow(_emptyHost, 3); root.Children.Add(_emptyHost);
        _history.Changed += HistoryChanged;
        _clock.Tick += (_, _) => { if (IsVisible) RefreshTimes(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { _clock.Start(); Refresh(); } else { _clock.Stop(); ClosePopups(); HideNotice(); } };
        PreviewKeyDown += KeyPressed;
        _applicationMotion = new ApplicationLabelMotion(this, _tabs);
        ApplyPerformanceMode(); ApplyLayout(); Refresh();
    }

    private ModuleMode? _presentationMode;
    internal void ApplyPerformanceMode()
    {
        var mode = _settings().Mode;
        if (_presentationMode == mode) return;
        _presentationMode = mode; Soft.SetReduced(this, mode == ModuleMode.Performance);
        if (mode == ModuleMode.Performance)
        {
            StopPreviewTimers(); CloseHoverPreview(); StopPreviewFollow();
            _applicationMotion.Clear(); ++_reorderGeneration; Soft.StopTree(this);
        }
    }

    internal void FocusSearch(bool reset = false)
    {
        if (reset) { _changing = true; _search.Clear(); _changing = false; }
        Refresh(); SetSearchOpen(true, focus: true);
    }
    internal void FocusHeader(bool reset = false)
    {
        if (reset) { _changing = true; _search.Clear(); _changing = false; Refresh(); }
        SetSearchOpen(false); _searchToggle.Focus();
    }
    internal void ClosePopups() { StopPreviewTimers(); if (_preview is not null) _preview.IsOpen = false; if (_editor is not null) _editor.IsOpen = false; if (_activeMenu is not null) _activeMenu.IsOpen = false; }
    public void Dispose() { _disposed = true; _clock.Stop(); ClosePopups(); HideNotice(); _applicationMotion.Dispose(); _history.Changed -= HistoryChanged; }
    internal void Refresh(bool animate = false)
    {
        if (_disposed || _drag.IsDragging) return;
        if (!Soft.For(this)) _applicationMotion.Clear();
        if (_collection.Length > 0 && !_history.Collections.Any(group => group.Id == _collection)) _collection = "";
        if (_unknownSourceOnly && !_history.Items.Any(item => string.IsNullOrWhiteSpace(item.SourceName))
            || _sourceFilter.Length > 0 && !_history.Sources.Contains(_sourceFilter, StringComparer.OrdinalIgnoreCase)
            || _unknownSourceOnly && _settings().IsApplicationHidden("") || _sourceFilter.Length > 0 && _settings().IsApplicationHidden(_sourceFilter)) { _sourceFilter = ""; _unknownSourceOnly = false; }
        string? selected = (_list.SelectedItem as ClipboardEntry)?.Id;
        var results = _history.Query(QueryState).ToList();
        TimelineItems(results);
        _list.SelectedItem = results.FirstOrDefault(item => item.Id == selected);
        _favorites.Tag = _favoritesOnly ? "Selected" : null;
        RefreshDockHeader(results.Count);
        BuildTabs();
        var (enabled, paused, problem) = _status();
        _statusDot.SetResourceReference(Border.BackgroundProperty, problem.Length > 0 || !enabled || paused ? "PanelAccent" : "PanelSecondary");
        string status = problem.Length > 0 ? problem : !enabled ? Loc.T("记录未启用 · 点击下方提示或在设置中启用", "Recording is off · enable it below or in settings")
            : paused ? Loc.T("记录已暂停 · 现有历史仍可使用", "Recording paused · saved history is still available") : "";
        if (IsVisible && status != _lastNoticeStatus) { _lastNoticeStatus = status; if (status.Length > 0) ShowNotice(status); }
        if (animate && IsVisible) PageHandover.Brighten(_list);
        _emptyHost.Visibility = !_emojiOpen && results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyHost.Cursor = !enabled ? Cursors.Hand : Cursors.Arrow;
        _empty.Text = !enabled && _history.Items.Count == 0 ? Loc.T("点击启用剪贴板记录\n仅本机保存，密码管理器的私密内容不会记录", "Click to enable clipboard history\nLocal only; password-manager private copies are excluded")
            : _search.Text.Length > 0 ? Loc.T("没有匹配的记录", "No matching history")
            : _favoritesOnly ? Loc.T("还没有收藏 · 在卡片上点击星标", "No favorites yet · star a card to keep it")
            : _byApplication && (_sourceFilter.Length > 0 || _unknownSourceOnly) ? Loc.T("此应用下没有匹配记录 · 调整筛选或切换到全部历史", "No matching entries for this app · Adjust filters or switch to All history")
            : _collection.Length > 0 ? Loc.T("这里还没有内容 · 从历史拖动条目到此分类，或右键条目整理", "Nothing here yet · drag entries here from History, or organize with their context menu")
            : Loc.T("复制文字、图片、颜色或文件后，会出现在这里\n单击或 Enter 使用默认动作 · Ctrl+Enter 仅复制 · Esc 收起", "Copy text, images, colors or files to see them here\nClick or Enter uses the default action · Ctrl+Enter copies only · Esc collapses");
    }
    private void HistoryChanged()
    {
        if (IsVisible) Refresh();
    }
    internal void ApplyLayout()
    {
        bool grid = _settings().GridView;
        bool changedLayout = _gridApplied.HasValue && _gridApplied != grid;
        var positions = changedLayout ? CaptureReorderPositions() : null;
        // Strip offsets count items, whereas the grid scrolls in DIPs. Keep a stable visible entry, not either unit.
        var anchor = changedLayout ? _visible.FirstOrDefault(entry =>
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement container) return false;
            var at = container.TranslatePoint(new Point(), _list);
            return at.X < _list.ActualWidth && at.X + container.ActualWidth > 0 && at.Y < _list.ActualHeight && at.Y + container.ActualHeight > 0;
        }) : null;
        if (_gridApplied != grid)
        {
            var panel = new FrameworkElementFactory(grid ? typeof(IslandCardPanel) : typeof(VirtualizingStackPanel));
            if (grid)
            {
                panel.SetBinding(IslandCardPanel.CellWidthProperty, new Binding(nameof(CardCellWidth)) { Source = this });
                panel.SetBinding(IslandCardPanel.CellHeightProperty, new Binding(nameof(CardCellHeight)) { Source = this });
            }
            else panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
            _list.ItemsPanel = new ItemsPanelTemplate(panel);
            _gridApplied = grid;
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, grid ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        }
        _layout.Tag = grid ? "Selected" : null; FitCards();
        if (anchor is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_disposed || _gridApplied != grid || !_visible.Contains(anchor)) return;
            _list.UpdateLayout(); _list.ScrollIntoView(anchor);
        });
        if (changedLayout) AnimateReorder(positions);
    }
    private void FitCards()
    {
        TimelineHeaderHeight = 0;
        TimelineGroupWidth = double.NaN;
        // Only the grid stretches into its measured slots; the reference strip retains upstream fixed cards.
        CardWidth = _settings().GridView ? double.NaN : ClipboardDockLayout.CardWidth; CardHeight = ClipboardDockLayout.CardHeight;
        CardCellWidth = ClipboardDockLayout.CardWidth + ClipboardDockLayout.CardGap; CardCellHeight = CardHeight + ClipboardDockLayout.CardGap;
        if (_list is not null) _list.MaxHeight = Math.Max(ClipboardDockLayout.CardHeight, ActualHeight - ClipboardDockLayout.HeaderHeight);
        FitEmojiGrid();
    }
    private void BuildTabs()
    {
        _tabs.Children.Clear();
        _modes.Select(_byApplication ? 1 : 0);
        _collectionMode.Tag = _byApplication ? null : "Selected"; _applicationMode.Tag = _byApplication ? "Selected" : null;
        if (_byApplication)
        {
            var baseline = QueryState with { CollectionId = "", Source = "", UnknownSourceOnly = false };
            var pins = _settings().PinnedApplications;
            void AppTab(string title, string source, bool unknown, int count, string automationId)
            {
                bool application = source.Length > 0 || unknown, pinned = application && _settings().IsApplicationPinned(source);
                var inner = new StackPanel { Orientation = Orientation.Horizontal };
                if (pinned) inner.Children.Add(new Viewbox { Width = 10, Height = 10, Child = EdgeDropGlyph.Create("pushPin", 10), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                inner.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Medium, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis });
                inner.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 10.5, Opacity = .65, Margin = new Thickness(5, 0, 0, 0) });
                var tab = new Button { Style = (Style)FindResource("ClipboardTab"), Content = inner, Margin = new Thickness(0, 0, 3, 0), AllowDrop = false,
                    Tag = _unknownSourceOnly == unknown && string.Equals(_sourceFilter, source, StringComparison.OrdinalIgnoreCase) ? "Selected" : null };
                AutomationProperties.SetAutomationId(tab, automationId); AutomationProperties.SetName(tab, title + " · " + count);
                AutomationProperties.SetHelpText(tab, Loc.T("根据复制来源自动分类，不改变收藏夹", "Automatically grouped by copy source; collections are unchanged"));
                AutomationProperties.SetItemStatus(tab, pinned ? Loc.T("已固定", "Pinned") : "");
                tab.Click += (_, _) => SelectApplication(source, unknown);
                if (application || _settings().HiddenApplications.Length > 0)
                {
                    var menu = Menu();
                    ClipboardApplicationMenu.Populate(menu, this, tab, source, unknown, _history, _settings,
                        (name, value) =>
                        {
                            if (_setApplicationPinned is { } set) set(name, value); else _settings().SetApplicationPinned(name, value);
                            BuildTabs(); _categoryScroll.ScrollToLeftEnd();
                        },
                        (name, value) =>
                        {
                            if (_setApplicationHidden is { } set) set(name, value); else _settings().SetApplicationHidden(name, value);
                            Refresh();
                        }, Confirm, _applicationMotion.Remove, ShowNotice);
                    tab.ContextMenu = menu;
                    tab.MouseRightButtonUp += (_, e) => { menu.PlacementTarget = tab; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; e.Handled = true; };
                }
                // Pinning changes navigation order only: even All history must not become a collection move target.
                _tabs.Children.Add(tab);
            }
            AppTab(Loc.T("全部历史", "All history"), "", false, _history.Query(baseline).Count(), "clipboard-application-history");
            foreach (var group in _history.ApplicationGroups(baseline).Where(group => !_settings().IsApplicationHidden(group.Source)).OrderBy(group =>
            {
                int index = Array.FindIndex(pins, source => string.Equals(source, group.Source, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })) AppTab(group.Label, group.Source, group.Unknown, group.Count, group.Unknown ? "clipboard-application-unknown" : "clipboard-application-source-" + group.Source);
            return;
        }
        AddTab("", Loc.T("历史", "History"));
        foreach (var group in _history.Collections) AddTab(group.Id, group.Name);
        var add = new Button { Style = (Style)FindResource("ClipboardTab"), Width = 24, Padding = new Thickness(0), Content = DrawIcon("M 3,12 H 21 M 12,3 V 21", 12) };
        AutomationProperties.SetName(add, Loc.T("添加分类", "Add collection")); add.Click += (_, _) => AddCollection(add); _tabs.Children.Add(add);
    }
    private void AddTab(string id, string title)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Medium });
        inner.Children.Add(new TextBlock { Text = _history.Query(QueryState with { CollectionId = id }).Count().ToString(), FontSize = 10.5, Opacity = .65, Margin = new Thickness(5, 0, 0, 0) });
        var tab = new Button { Style = (Style)FindResource("ClipboardTab"), Content = inner, Margin = new Thickness(0, 0, 3, 0), Tag = _collection == id ? "Selected" : null };
        AutomationProperties.SetName(tab, title); tab.Click += (_, _) => { _collection = id; _sourceFilter = ""; _unknownSourceOnly = false; Refresh(animate: true); };
        if (id.Length > 0)
        {
            if (_history.Collections.FirstOrDefault(group => group.Id == id) is { } collection) ClipboardCollectionVisuals.Apply(tab, collection, inner);
            _drag.AttachCollection(tab, id, Orientation.Horizontal);
            AutomationProperties.SetHelpText(tab, Loc.T("右键管理，拖动排序或拖入条目整理", "Right-click to manage; drag to reorder or organize entries"));
            var menu = Menu();
            AddMenu(menu, Loc.T("名称与标签外观…", "Name & label appearance…"), () =>
            {
                InputRequested?.Invoke();
                if (_editor is not null) _editor.IsOpen = false;
                _editor = ClipboardCollectionAppearance.Open(tab, _history, id, PresentationScale(), () => InteractionChanged?.Invoke(), () => { if (IsVisible) RestoreHeaderFocus(); InteractionChanged?.Invoke(); });
            });
            AddMenu(menu, Loc.T("移到最前", "Move to start"), () => { if (_history.Collections.FirstOrDefault() is { } first) _history.ReorderCollection(id, first.Id); });
            AddMenu(menu, Loc.T("移到最后", "Move to end"), () => { if (_history.Collections.LastOrDefault() is { } last) _history.ReorderCollection(id, last.Id, true); });
            AddMenu(menu, Loc.T("删除分类（保留记录）…", "Delete collection (keep entries)…"), () =>
            { if (Confirm(new(PromptId.ClipboardDeleteCollection, Loc.T("删除分类？其中的条目将保留在历史中，原文件和系统剪贴板不会更改。", "Delete this collection? Its entries stay in history; original files and the system clipboard are unchanged.")))) _history.RemoveCollection(id); });
            tab.ContextMenu = menu;
            tab.MouseRightButtonUp += (_, e) => { menu.PlacementTarget = tab; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; e.Handled = true; };
        }
        else _drag.AttachHistory(tab);
        _tabs.Children.Add(tab);
    }
    private void SelectApplication(string source, bool unknown)
    {
        _byApplication = true; _collection = ""; _sourceFilter = source; _unknownSourceOnly = unknown; Refresh(animate: true);
    }
    private void AddCollection(FrameworkElement anchor)
    {
        InputRequested?.Invoke();
        if (_editor is not null) _editor.IsOpen = false;
        var name = new TextBox { Style = (Style)FindResource("ClipboardField"), FontSize = 12, Width = 210, MaxLength = 32 };
        AutomationProperties.SetName(name, Loc.T("分类名称", "Collection name")); AutomationProperties.SetAutomationId(name, "clipboard-collection-name");
        var note = new TextBlock { Text = Loc.T("Enter 保存 · Esc 取消 · 最多 32 字", "Enter to save · Esc to cancel · Up to 32 characters"), FontSize = 10.5, MaxWidth = 230, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary");
        var panel = new StackPanel(); panel.Children.Add(name); panel.Children.Add(note);
        var chrome = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = panel };
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder");
        chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        var popup = _editor = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6, Child = chrome };
        double scale = PresentationScale(); chrome.LayoutTransform = new ScaleTransform(scale, scale);
        void Save()
        {
            var group = _history.AddCollection(name.Text);
            if (group is not null) { _collection = group.Id; popup.IsOpen = false; Refresh(); }
            else note.Text = Loc.T("名称不能为空或重复；最多 20 个分类。分类已移除时请取消。", "Use a unique name; at most 20 collections. Cancel if this collection was removed.");
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = Loc.T("取消", "Cancel"), Style = (Style)FindResource("LibraryButton"), Margin = new Thickness(0, 0, 6, 0) };
        var save = new Button { Content = Loc.T("保存", "Save"), Style = (Style)FindResource("LibraryPrimaryButton") };
        AutomationProperties.SetAutomationId(save, "clipboard-collection-save");
        cancel.Click += (_, _) => popup.IsOpen = false; save.Click += (_, _) => Save(); buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter && Find<TextBox>(e.OriginalSource as DependencyObject) == name) { Save(); e.Handled = true; }
        };
        PopupMotion.Attach(popup);
        popup.Opened += (_, _) => { InteractionChanged?.Invoke(); name.Focus(); name.SelectAll(); };
        popup.Closed += (_, _) => { if (IsVisible) RestoreHeaderFocus(); InteractionChanged?.Invoke(); };
        popup.IsOpen = true;
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        // Committing an IME composition is not a request to paste a history entry. Alt shortcuts, in contrast, arrive as
        // Key.System and must use SystemKey (the same normalization the global shortcut recorder uses).
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed || e.Handled) return;
        e.Handled = HandleShortcut(HotkeyGesture.KeyOf(e), Keyboard.Modifiers, e.OriginalSource as DependencyObject);
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (Find<ClipboardEntryEditor>(source) is not null) return false;
        bool search = Find<TextBox>(source) == _search;
        bool history = Find<ListBoxItem>(source) is not null && Find<Button>(source) is null;
        if (key == Key.Escape)
        {
            DismissFromKeyboard();
            return true;
        }
        if (key == Key.Tab && modifiers == ModifierKeys.Control) { ShowPages(); return true; }
        if (key == Key.F && modifiers == ModifierKeys.Control) { FocusSearch(); return true; }
        if ((key == Key.F10 && modifiers == ModifierKeys.Shift || key == Key.Apps && modifiers == ModifierKeys.None) && history && _list.SelectedItem is ClipboardEntry menuItem)
        { OpenItemMenu(menuItem, false); return true; }
        if (key == Key.Down && modifiers == ModifierKeys.None && (search || ReferenceEquals(source, _searchToggle)) && _list.Items.Count > 0)
        {
            _list.SelectedIndex = 0; _list.ScrollIntoView(_list.SelectedItem); _list.UpdateLayout();
            (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            return true;
        }
        // Enter on a toolbar, tab, favorite or context-menu button belongs to that control, never to the history list.
        if (key == Key.Enter && (search || history) && modifiers is ModifierKeys.None or ModifierKeys.Control
            && (_list.SelectedItem as ClipboardEntry ?? _list.Items.OfType<ClipboardEntry>().FirstOrDefault()) is { } item)
        { _activate(item, modifiers == ModifierKeys.Control || !_settings().PasteOnActivate); return true; }
        if (key == Key.Delete && modifiers == ModifierKeys.None && history && _list.SelectedItem is ClipboardEntry selected)
        { DeleteEntry(selected); return true; }
        if (key is >= Key.D1 and <= Key.D9 && modifiers == ModifierKeys.Alt)
        { int index = key - Key.D1; if (index < _list.Items.Count) _activate((ClipboardEntry)_list.Items[index], !_settings().PasteOnActivate); return true; }
        return false;
    }
    private Button Round(object content, string name, Action action)
    {
        var button = new Button { Style = (Style)FindResource("ClipboardRoundButton"), Content = content, ToolTip = name };
        ToolTipService.SetInitialShowDelay(button, 700);
        AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }
    internal void DismissFromKeyboard()
    {
        StopPreviewTimers();
        if (_activeMenu?.IsOpen == true) _activeMenu.IsOpen = false;
        else if (_emojiOpen) SetEmojiOpen(false);
        else if (_editor?.IsOpen == true) _editor.IsOpen = false;
        else if (_preview?.IsOpen == true) _preview.IsOpen = false;
        else if (_search.Text.Length > 0) _search.Clear();
        else if (_searchOpen) SetSearchOpen(false);
        else _collapse();
    }
    private double PresentationScale()
    {
        if (Window.GetWindow(this) is not { } window || PresentationSource.FromVisual(this) is null) return 1;
        var transform = TransformToAncestor(window);
        return (transform.Transform(new Point(1, 0)) - transform.Transform(new Point())).Length;
    }
    private ContextMenu Menu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
        menu.Opened += (_, _) => { _activeMenu = menu; double scale = PresentationScale(); menu.LayoutTransform = new ScaleTransform(scale, scale); InteractionChanged?.Invoke(); };
        menu.Closed += (_, _) => { if (_activeMenu == menu) _activeMenu = null; InteractionChanged?.Invoke(); };
        PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement);
        return menu;
    }
    private void AddMenu(ContextMenu menu, string label, Action action)
    { var item = new MenuItem { Header = label, Style = (Style)FindResource("TrayMenuItem") }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private static Path DrawIcon(string data, double size)
    {
        var icon = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        icon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) }); return icon;
    }
    private static T? Find<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T match) return match; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        return null;
    }
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) return match; if (Descendant<T>(child) is { } nested) return nested; }
        return null;
    }
}
