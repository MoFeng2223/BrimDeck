using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    internal bool HasUnsavedDraft => _inspector?.IsDirty == true;
    private string _sourceFilter = "";
    private readonly HashSet<ClipboardContentKind> _kindFilter = [];
    private ClipboardArchiveScope _archiveScope;
    private Action<ClipboardEntry?>? _openLibrary;
    private Func<PromptRequest, bool>? _confirmation;
    private Button? _filter;
    private ClipboardEntryEditor? _inspector;
    internal static readonly DependencyProperty TimelineHeaderHeightProperty = DependencyProperty.Register(nameof(TimelineHeaderHeight), typeof(double), typeof(ClipboardView), new PropertyMetadata(0d));
    public double TimelineHeaderHeight { get => (double)GetValue(TimelineHeaderHeightProperty); set => SetValue(TimelineHeaderHeightProperty, value); }
    internal static readonly DependencyProperty TimelineGroupWidthProperty = DependencyProperty.Register(nameof(TimelineGroupWidth), typeof(double), typeof(ClipboardView), new PropertyMetadata(double.NaN));
    public double TimelineGroupWidth { get => (double)GetValue(TimelineGroupWidthProperty); set => SetValue(TimelineGroupWidthProperty, value); }
    internal ClipboardQuery QueryState
    {
        get => new() { Text = _search.Text, CollectionId = _collection, FavoritesOnly = _favoritesOnly, Source = _sourceFilter, UnknownSourceOnly = _unknownSourceOnly, Kinds = _kindFilter.ToArray(), Archive = _archiveScope, Sort = SortOrder };
        set
        {
            _changing = true;
            _collection = value.CollectionId; _favoritesOnly = value.FavoritesOnly; _sourceFilter = value.Source; _unknownSourceOnly = value.UnknownSourceOnly; _archiveScope = value.Archive; SortOrder = value.Sort;
            if (_collection.Length > 0) _byApplication = false;
            else if (_sourceFilter.Length > 0 || _unknownSourceOnly) _byApplication = true;
            _kindFilter.Clear(); foreach (var kind in value.Kinds) _kindFilter.Add(kind); _search.Text = value.Text;
            _changing = false; Refresh(animate: true);
        }
    }
    private void LibraryActions(Panel actions)
    {
        _filter = Round("≡", Loc.T("分类、来源、类型与归档筛选", "Grouping, source, type and archive filters"), ShowFilters);
        _filter.Margin = new Thickness(3, 0, 0, 0); AutomationProperties.SetAutomationId(_filter, "clipboard-filters"); actions.Children.Add(_filter);
        if (_openLibrary is not null)
        {
            var library = Round("↗", Loc.T("打开完整剪贴板库", "Open full Clipboard Library"), () => { ClosePopups(); _openLibrary(null); });
            library.Margin = new Thickness(3, 0, 0, 0); AutomationProperties.SetAutomationId(library, "clipboard-open-library"); actions.Children.Add(library);
        }
    }
    private readonly System.Collections.ObjectModel.ObservableCollection<ClipboardEntry> _visible = [];
    internal static readonly DependencyProperty CardCellWidthProperty = DependencyProperty.Register(nameof(CardCellWidth), typeof(double), typeof(ClipboardView), new PropertyMetadata(180d));
    internal static readonly DependencyProperty CardCellHeightProperty = DependencyProperty.Register(nameof(CardCellHeight), typeof(double), typeof(ClipboardView), new PropertyMetadata(154d));
    public double CardCellWidth { get => (double)GetValue(CardCellWidthProperty); set => SetValue(CardCellWidthProperty, value); }
    public double CardCellHeight { get => (double)GetValue(CardCellHeightProperty); set => SetValue(CardCellHeightProperty, value); }
    private void InitializeTimeline() => _list.ItemsSource = _visible;
    private void TimelineItems(List<ClipboardEntry> results)
    {
        var positions = CaptureReorder(results);
        // Preserve containers and scroll anchors. A metadata-only replacement updates its card without decoding its body.
        var ids = results.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = _visible.Count - 1; i >= 0; i--) if (!ids.Contains(_visible[i].Id)) _visible.RemoveAt(i);
        for (int i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            if (i >= _visible.Count) _visible.Add(entry);
            else if (_visible[i].Id != entry.Id)
            {
                int from = -1; for (int j = i + 1; j < _visible.Count; j++) if (_visible[j].Id == entry.Id) { from = j; break; }
                if (from >= 0) _visible.Move(from, i); else _visible.Insert(i, entry);
            }
            if (!ReferenceEquals(_visible[i], entry)) _visible[i] = entry;
        }
        if (_previewId is { } previewId && !ids.Contains(previewId) && _preview is not null) _preview.IsOpen = false;
        FitCards(); RefreshTimes(); AnimateReorder(positions);
        if (_filter is not null)
        {
            bool active = _sourceFilter.Length > 0 || _unknownSourceOnly || _kindFilter.Count > 0 || _archiveScope != ClipboardArchiveScope.Active;
            string summary = (_unknownSourceOnly ? Loc.T("未知应用 · ", "Unknown app · ") : _sourceFilter.Length > 0 ? _sourceFilter + " · " : "")
                + (_kindFilter.Count > 0 ? string.Join(" / ", _kindFilter.Select(ClipboardContentTypes.Label)) : Loc.T("全部类型", "All types"))
                + (_archiveScope == ClipboardArchiveScope.Archived ? Loc.T(" · 已归档", " · Archived") : "");
            _filter.Tag = active ? "Selected" : null; _filter.ToolTip = Loc.T("筛选：", "Filters: ") + summary;
            _filter.Content = active ? "≡ ·" : "≡";
            AutomationProperties.SetName(_filter, (string)_filter.ToolTip);
        }
        _inspector?.RefreshEntry();
    }
    private void ShowFilters()
    {
        var menu = Menu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_settingsButton.ContextMenu, menu)) _settingsButton.ContextMenu = null; };
        AddMenu(menu, Loc.T("重置全部筛选", "Reset all filters"), () => QueryState = new() { Sort = SortOrder });
        MenuItem Submenu(string label)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("LibraryMenuItem") };
            item.Loaded += (_, _) => { if (item.Template?.FindName("PART_Popup", item) is Popup popup) PopupMotion.Attach(popup); };
            menu.Items.Add(item); return item;
        }
        void Choice(MenuItem parent, string label, bool selected, Action action, bool multiple = false)
        {
            var item = new MenuItem { Header = (selected ? "✓  " : "    ") + label, Style = (Style)FindResource("TrayMenuItem"), StaysOpenOnClick = multiple };
            item.Click += (_, e) => { action(); if (multiple) item.Header = (item.Header.ToString()!.StartsWith("✓", StringComparison.Ordinal) ? "    " : "✓  ") + label; e.Handled = true; }; parent.Items.Add(item);
        }
        var sources = Submenu(Loc.T("来源应用", "Source application"));
        Choice(sources, Loc.T("全部历史", "All history"), _sourceFilter.Length == 0 && !_unknownSourceOnly, () => SelectApplication("", false));
        foreach (var group in _history.ApplicationGroups(QueryState).Where(group => !_settings().IsApplicationHidden(group.Source)))
            Choice(sources, group.Label + "  " + group.Count, _unknownSourceOnly == group.Unknown && string.Equals(_sourceFilter, group.Source, StringComparison.OrdinalIgnoreCase), () => SelectApplication(group.Source, group.Unknown));
        // Compact islands hide the tab row, so the same two modes remain reachable from their native menu.
        var grouping = Submenu(Loc.T("分类视图", "Browse by"));
        Choice(grouping, Loc.T("收藏夹", "Collections"), !_byApplication, () => ApplicationsMode = false);
        Choice(grouping, Loc.T("应用自动分类", "Applications (automatic)"), _byApplication, () => ApplicationsMode = true);
        var types = Submenu(Loc.T("内容类型（可多选）", "Content types (multi-select)"));
        Choice(types, Loc.T("全部类型", "All types"), _kindFilter.Count == 0, () => { _kindFilter.Clear(); Refresh(animate: true); });
        var untyped = _history.Query(QueryState with { Kinds = [] }).ToList();
        foreach (var kind in Enum.GetValues<ClipboardContentKind>())
            Choice(types, ClipboardContentTypes.Label(kind) + "  " + untyped.Count(item => item.Content.Kind == kind), _kindFilter.Contains(kind), () => { if (!_kindFilter.Remove(kind)) _kindFilter.Add(kind); Refresh(animate: true); }, true);
        var archive = Submenu(Loc.T("记录范围", "Entry scope"));
        foreach (var scope in Enum.GetValues<ClipboardArchiveScope>())
            Choice(archive, scope == ClipboardArchiveScope.Active ? Loc.T("当前历史", "Active history") : scope == ClipboardArchiveScope.Archived ? Loc.T("已归档", "Archived") : Loc.T("全部记录", "All entries"), _archiveScope == scope, () => { _archiveScope = scope; Refresh(animate: true); });
        var anchor = _filter is { IsVisible: true } ? _filter : _settingsButton;
        anchor.ContextMenu = menu; menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private bool Confirm(PromptRequest request) => _confirmation?.Invoke(request) ?? UserPrompts.Show(Window.GetWindow(this), request);
    private void DeleteEntry(ClipboardEntry item)
    {
        if (Confirm(new(PromptId.ClipboardDeleteEntry, Loc.T("删除此剪贴板记录及其备注？原文件和系统剪贴板不会更改，此操作不可撤销。", "Delete this clipboard entry and its notes? Original files and the clipboard are unchanged. This cannot be undone.")))) _history.Remove(item.Id);
    }
    internal void ShowDetails(ClipboardEntry? item, bool create = false)
    {
        InputRequested?.Invoke();
        _inspector ??= new ClipboardEntryEditor(_history, _settings, _activate, Confirm);
        if ((_inspector.EntryId != item?.Id || _inspector.IsNew != create) && !_inspector.TryDiscard()) return;
        if (!_inspector.IsDirty) _inspector.Load(item, create);
        if (_editor is not null) _editor.IsOpen = false;
        var content = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(toolbar, Dock.Top); content.Children.Add(toolbar);
        var close = new Button { Style = (Style)FindResource("ClipboardRoundButton"), Content = "×", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, Loc.T("关闭详情", "Close details")); close.Click += (_, _) => { if (_inspector.TryDiscard() && _editor is not null) _editor.IsOpen = false; }; DockPanel.SetDock(close, Dock.Right); toolbar.Children.Add(close);
        var note = ClipboardPreview.Text(Loc.T("详情与编辑 · Ctrl+S 保存", "Details & editing · Ctrl+S to save"), 10.5, "PanelTertiary"); note.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(note);
        var feedback = ClipboardPreview.Text("", 10.5, "PanelAccent"); feedback.Margin = new Thickness(0, 6, 0, 0); DockPanel.SetDock(feedback, Dock.Bottom); content.Children.Add(feedback);
        // The inspector survives a collapsed island. Outside dismissal must not silently erase an unsaved draft.
        if (_inspector.Parent is ScrollViewer previous) previous.Content = null;
        content.Children.Add(new ScrollViewer { Content = _inspector, MaxHeight = 390, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        var chrome = new Border { Width = 340, Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = content };
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        double scale = PresentationScale(); chrome.LayoutTransform = new ScaleTransform(scale, scale);
        var popup = _editor = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6, Child = chrome };
        void Feedback(string text) => feedback.Text = text;
        void Removed() => popup.IsOpen = false;
        _inspector.Feedback += Feedback; _inspector.Removed += Removed;
        popup.Closed += (_, _) => { _inspector.Feedback -= Feedback; _inspector.Removed -= Removed; InteractionChanged?.Invoke(); };
        PopupMotion.Attach(popup);
        popup.Opened += (_, _) => { InteractionChanged?.Invoke(); _inspector.FocusEditor(); };
        chrome.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { _inspector.Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
        };
        popup.IsOpen = true;
    }
}

internal sealed class ClipboardDayGroup : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is DateTimeOffset time ? new ClipboardTimelineGroup(DateOnly.FromDateTime(time.LocalDateTime), []).Label(DateTime.Now) : "";
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
