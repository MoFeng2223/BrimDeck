// Island adaptation of ClipboardView.Dock / Edge-Drop Header.horizontal (767b6d63, Apache-2.0).
// This is a view of the active original shelf, not a second shelf or an attachment owner.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;
using DropShelf.Models;
using ShelfEntry = DropShelf.Models.ShelfItem;

namespace BrimDeck;

public partial class MainWindow
{
    private enum ShelfFilter { All, Files, Folders, Images, Text, Links }
    private ShelfFilter _trayFilter;
    private bool _trayPinnedOnly, _traySearchOpen;
    private string _trayQuery = "", _trayHeaderLanguage = "", _trayModelId = "";
    private List<ShelfEntry> _trayResults = [];
    private readonly List<(Button Button, ShelfFilter Filter, string Name)> _trayTypes = [];
    private StackPanel? _trayTypesHost, _trayRight;
    private Grid? _trayCenter, _traySearchField;
    private Border? _traySearchCapsule;
    private TextBox? _traySearch;
    private TextBlock? _trayPickerText, _traySearchPlaceholder, _trayEmpty;
    private Button? _trayPicker, _traySearchToggle, _traySearchClear, _trayPinned, _trayLayout, _trayActions, _trayMore, _traySettings;
    private ColumnDefinition? _traySearchClearColumn;
    private bool _trayGridApplied;
    private int _trayReflow;

    private Button ShelfDockButton(object content, string name, Action action, string id)
    {
        var button = new Button { Content = content, Width = 28, Height = 28, Style = (Style)_trayFrame!.FindResource("ClipboardRoundButton") };
        AutomationProperties.SetName(button, name); AutomationProperties.SetAutomationId(button, id); button.ToolTip = name;
        button.Click += (_, e) => { e.Handled = true; action(); };
        // Pickers are explicit left-click/keyboard actions; toolbar right-click belongs to the host setting.
        ContextMenuService.SetIsEnabled(button, false);
        return button;
    }
    private void BuildTrayDockHeader()
    {
        _trayBar!.Children.Clear(); _trayBar.ColumnDefinitions.Clear(); _trayTypes.Clear();
        _trayHeaderLanguage = Loc.Language;
        AutomationProperties.SetAutomationId(_trayBar, "shelf-dock-header");
        _trayBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _trayBar.ColumnDefinitions.Add(new()); _trayBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _trayTypesHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
        var track = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var trackFill = new Border { Child = track }; trackFill.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); trackFill.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        _trayTypesHost.Children.Add(trackFill); _trayBar.Children.Add(_trayTypesHost);
        void Type(ShelfFilter filter, string glyph, string name)
        {
            var button = ShelfDockButton(EdgeDropGlyph.Create(glyph), name, () => { _trayFilter = filter; RefreshTrayProjection(resetScroll: true); }, "shelf-type-" + filter.ToString().ToLowerInvariant());
            button.Height = 26; track.Children.Add(button); _trayTypes.Add((button, filter, name));
        }
        Type(ShelfFilter.All, "stack", Loc.T("全部项目", "All items"));
        Type(ShelfFilter.Files, "files", Loc.T("文件", "Files"));
        Type(ShelfFilter.Folders, "folderOpen", Loc.T("文件夹", "Folders"));
        Type(ShelfFilter.Images, "image", Loc.T("图片", "Images"));
        Type(ShelfFilter.Text, "textT", Loc.T("文本", "Text"));
        Type(ShelfFilter.Links, "link", Loc.T("链接", "Links"));
        _trayCenter = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _trayCenter.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); _trayCenter.ColumnDefinitions.Add(new());
        Grid.SetColumn(_trayCenter, 1); _trayBar.Children.Add(_trayCenter);
        _traySearchCapsule = new Border { Width = 30, Height = ClipboardDockLayout.SearchHeight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
        _traySearchCapsule.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); _traySearchCapsule.SetResourceReference(Border.CornerRadiusProperty, "PanelR12");
        AutomationProperties.SetAutomationId(_traySearchCapsule, "shelf-search-capsule");
        var field = new Grid(); field.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); field.ColumnDefinitions.Add(new());
        _traySearchClearColumn = new() { Width = new GridLength(0) }; field.ColumnDefinitions.Add(_traySearchClearColumn);
        _traySearchToggle = ShelfDockButton(EdgeDropGlyph.Create("magnifyingGlass"), Loc.T("搜索文件中转 · Ctrl+F", "Search file shelf · Ctrl+F"), () => SetTraySearchOpen(!_traySearchOpen, focus: !_traySearchOpen), "shelf-search-toggle");
        field.Children.Add(_traySearchToggle);
        _traySearchField = new Grid(); Grid.SetColumn(_traySearchField, 1); field.Children.Add(_traySearchField);
        _traySearchPlaceholder = ClipboardPreview.Text(Loc.T("搜索…", "Search..."), 12, "PanelTertiary"); _traySearchPlaceholder.VerticalAlignment = VerticalAlignment.Center; _traySearchPlaceholder.IsHitTestVisible = false;
        _traySearch = new TextBox { Style = (Style)_trayFrame!.FindResource("ClipboardField"), MaxLength = 512, Text = _trayQuery, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_traySearch, Loc.T("搜索文件中转", "Search file shelf")); AutomationProperties.SetAutomationId(_traySearch, "shelf-search");
        _traySearch.PreviewMouseLeftButtonDown += (_, _) => BeginClipboardInput();
        _traySearchField.Children.Add(_traySearchPlaceholder); _traySearchField.Children.Add(_traySearch);
        _traySearch.TextChanged += (_, _) => { _trayQuery = _traySearch.Text; RefreshTrayProjection(resetScroll: true); };
        _traySearchClear = ShelfDockButton(EdgeDropGlyph.Create("x", 9), Loc.T("清除／收起搜索", "Clear / collapse search"), () => { if (_trayQuery.Length > 0) _traySearch.Clear(); else SetTraySearchOpen(false); }, "shelf-search-clear");
        _traySearchClear.Width = _traySearchClear.Height = 18; Grid.SetColumn(_traySearchClear, 2); field.Children.Add(_traySearchClear);
        _traySearchCapsule.Child = field; _trayCenter.Children.Add(_traySearchCapsule);
        _trayPickerText = ClipboardPreview.Text("", 11, "PanelSecondary"); _trayPickerText.TextWrapping = TextWrapping.NoWrap; _trayPickerText.TextTrimming = TextTrimming.CharacterEllipsis;
        var pickerContent = new DockPanel(); var caret = EdgeDropGlyph.Create("caretDown", 9); caret.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(caret, System.Windows.Controls.Dock.Right); pickerContent.Children.Add(caret); pickerContent.Children.Add(_trayPickerText);
        _trayPicker = ShelfDockButton(pickerContent, Loc.T("选择当前置物架", "Choose current shelf"), () => OpenShelfChooser(_trayPicker!), "shelf-picker");
        _trayPicker.Width = double.NaN; _trayPicker.HorizontalContentAlignment = HorizontalAlignment.Stretch; _trayPicker.Padding = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(_trayPicker, 1); _trayCenter.Children.Add(_trayPicker);
        _trayRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
        _trayPinned = ShelfDockButton(EdgeDropGlyph.Create("pushPin", 15), Loc.T("只看已固定", "Pinned only"), () => { _trayPinnedOnly = !_trayPinnedOnly; RefreshTrayProjection(resetScroll: true); }, "shelf-pinned");
        _trayLayout = ShelfDockButton(EdgeDropGlyph.Create("files", 15), Loc.T("切换网格／横向列表", "Toggle grid / strip"), ToggleTrayLayout, "shelf-layout");
        _trayActions = ShelfDockButton(EdgeDropGlyph.Create("sparkle", 15), DropShelf.Localization.Strings.QuickActionsTitle, () => OpenTrayActions(_trayActions!), "shelf-actions");
        _trayMore = ShelfDockButton(EdgeDropGlyph.Create("dotsSixVertical", 15), Loc.T("更多置物架操作", "More shelf actions"), () => OpenTrayMore(_trayMore!), "shelf-more");
        _traySettings = ShelfDockButton(EdgeDropGlyph.Create("gearSix", 16), Loc.T("文件中转置物架设置", "File shelf settings"), () => { SetExpanded(false, keyboard: true); _app.OpenShelfSettings(); }, "shelf-settings");
        foreach (var button in new[] { _trayPinned, _trayLayout, _trayActions, _trayMore, _traySettings }) { if (_trayRight.Children.Count > 0) button.Margin = new Thickness(4, 0, 0, 0); _trayRight.Children.Add(button); }
        Grid.SetColumn(_trayRight, 2); _trayBar.Children.Add(_trayRight);
        _trayCenter.SizeChanged += (_, _) => FitTrayDockHeader();
        SetTraySearchOpen(_traySearchOpen);
    }
    private void ToggleTrayLayout()
    {
        Settings.Tray.GridView = !Settings.Tray.GridView; _app.QueueSave(); RefreshTrayProjection();
    }
    private void RefreshTrayDockHeader()
    {
        bool focus = _traySearch?.IsKeyboardFocusWithin == true;
        int selectionStart = _traySearch?.SelectionStart ?? 0, selectionLength = _traySearch?.SelectionLength ?? 0;
        if (_trayHeaderLanguage != Loc.Language)
        {
            BuildTrayDockHeader();
            if (focus)
            {
                BeginClipboardInput();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_expanded && IsTrayPage && _traySearchOpen) { _traySearch!.Focus(); _traySearch.Select(selectionStart, selectionLength); } });
            }
        }
        foreach (var (button, filter, name) in _trayTypes)
        {
            button.Tag = _trayFilter == filter ? "Selected" : null;
            AutomationProperties.SetItemStatus(button, QueryTrayItems(filter).Count.ToString());
        }
        _trayPickerText!.Text = Tray.Model.WindowTitle + Loc.T($" · {Tray.Count}", $" · {Tray.Count}");
        _trayPicker!.ToolTip = Tray.Model.WindowTitle + " · " + TrayLabel();
        _trayPinned!.Tag = _trayPinnedOnly ? "Selected" : null;
        _trayLayout!.Tag = Settings.Tray.GridView ? "Selected" : null;
        _trayActions!.IsEnabled = Tray.Count > 0 && !_trayActionBusy && !Tray.Model.IsBusy;
        _traySearchPlaceholder!.Visibility = _trayQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(_trayBar!, TrayLabel()); FitTrayDockHeader();
    }
    private void SetTraySearchOpen(bool open, bool focus = false)
    {
        bool changed = _traySearchOpen != open; _traySearchOpen = open;
        if (_traySearchField is null) return;
        _traySearchField.Visibility = _traySearchClear!.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _traySearchClearColumn!.Width = new GridLength(open ? 24 : 0); _traySearchToggle!.Tag = open || _trayQuery.Length > 0 ? "Selected" : null;
        FitTrayDockHeader(changed);
        if (focus)
        {
            BeginClipboardInput();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (_expanded && IsTrayPage && _traySearchOpen) { _traySearch!.Focus(); _traySearch.SelectAll(); } });
        }
        else if (!open && _traySearch!.IsKeyboardFocusWithin) _traySearchToggle.Focus();
    }
    private void FitTrayDockHeader(bool animate = false)
    {
        if (_trayCenter is null || _trayCenter.ActualWidth <= 0) return;
        bool narrow = TrayContent.ActualWidth < 600;
        // Keep six type filters, pin, more and settings reachable; secondary actions remain in More.
        _trayLayout!.Visibility = _trayActions!.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        _trayTypesHost!.Margin = new Thickness(narrow ? 8 : 16, 0, narrow ? 4 : 8, 0); _trayRight!.Margin = new Thickness(narrow ? 4 : 8, 0, narrow ? 8 : 16, 0);
        double available = Math.Max(30, _trayCenter.ActualWidth - 8);
        _trayPicker!.Visibility = !_traySearchOpen && available >= 100 || _traySearchOpen && available >= ClipboardDockLayout.SearchWidth + 100 ? Visibility.Visible : Visibility.Collapsed;
        double width = _traySearchOpen ? Math.Min(ClipboardDockLayout.SearchWidth, available) : 30;
        if (Math.Abs(_traySearchCapsule!.Width - width) <= .1) return;
        double from = _traySearchCapsule.ActualWidth; _traySearchCapsule.BeginAnimation(WidthProperty, null); _traySearchCapsule.Width = width;
        if (animate && Soft.For(TrayContent) && IsLoaded) _traySearchCapsule.BeginAnimation(WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(Soft.Choose)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }
    private List<ShelfEntry> QueryTrayItems(ShelfFilter? filter = null)
    {
        var kind = filter ?? _trayFilter;
        string[] terms = _trayQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Tray.Items.Where(item => (!_trayPinnedOnly || item.Pinned) && (kind switch
        {
            ShelfFilter.Files => item.Kind == ShelfItemKind.File,
            ShelfFilter.Folders => item.Kind == ShelfItemKind.Folder,
            ShelfFilter.Images => item.Kind == ShelfItemKind.File && IslandFilePreview.IsImage(item.Value),
            ShelfFilter.Text => item.Kind == ShelfItemKind.Text,
            ShelfFilter.Links => item.Kind == ShelfItemKind.Link,
            _ => true
        }) && terms.All(term => item.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Value.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Detail.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    private void RefreshTrayProjection(bool resetScroll = false)
    {
        if (resetScroll) { _trayScroll?.ScrollToHorizontalOffset(0); _trayScroll?.ScrollToVerticalOffset(0); }
        _trayKey = ""; if (IsTrayPage && _expanded) RenderFileTray();
    }
    private void LayoutTrayCards()
    {
        if (_layingOutTray || _trayGrid is null || _trayScroll is null) return;
        _layingOutTray = true;
        var positions = _trayCards.ToDictionary(pair => pair.Key, pair => pair.Value.TranslatePoint(new Point(), _trayScroll));
        var oldResults = _trayResults;
        bool changedModel = _trayModelId != Tray.Model.Id;
        bool grid = Settings.Tray.GridView, changedMode = grid != _trayGridApplied;
        int oldColumns = _trayColumns;
        double oldPitch = _trayGridApplied ? _trayCardHeight + ClipboardDockLayout.CardGap : _trayCardWidth + ClipboardDockLayout.CardGap;
        double oldOffset = _trayGridApplied ? _trayScroll.VerticalOffset : _trayScroll.HorizontalOffset;
        int anchorAt = oldPitch > 0 ? Math.Max(0, (int)(oldOffset / oldPitch)) * (_trayGridApplied ? oldColumns : 1) : 0;
        string? anchor = anchorAt < oldResults.Count ? oldResults[anchorAt].Id : null;
        double inset = oldPitch > 0 ? oldOffset % oldPitch : 0;
        try
        {
            _trayResults = QueryTrayItems(); _trayModelId = Tray.Model.Id;
            _trayScroll.VerticalScrollBarVisibility = grid ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            _trayScroll.HorizontalScrollBarVisibility = grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            double width = _trayScroll.ViewportWidth > 0 ? _trayScroll.ViewportWidth : Math.Max(1, TrayContent.ActualWidth - ClipboardDockLayout.ListInset * 2);
            _trayColumns = grid ? Math.Max(1, (int)Math.Floor((width + ClipboardDockLayout.CardGap) / (ClipboardDockLayout.CardWidth + ClipboardDockLayout.CardGap))) : Math.Max(1, _trayResults.Count);
            _trayCardWidth = grid ? (width + ClipboardDockLayout.CardGap) / _trayColumns - ClipboardDockLayout.CardGap : ClipboardDockLayout.CardWidth;
            _trayCardHeight = ClipboardDockLayout.CardHeight;
            double pitch = _trayCardWidth + ClipboardDockLayout.CardGap;
            int rows = grid ? Math.Max(1, (_trayResults.Count + _trayColumns - 1) / _trayColumns) : 1;
            _trayGrid.ColumnDefinitions.Clear(); _trayGrid.RowDefinitions.Clear();
            for (int i = 0; i < _trayColumns; i++) _trayGrid.ColumnDefinitions.Add(new() { Width = new GridLength(pitch) });
            for (int i = 0; i < rows; i++) _trayGrid.RowDefinitions.Add(new() { Height = new GridLength(_trayCardHeight + ClipboardDockLayout.CardGap) });
            _trayGrid.Margin = grid ? new Thickness(0, 0, -ClipboardDockLayout.CardGap, -ClipboardDockLayout.CardGap) : new Thickness(0, 0, 0, -ClipboardDockLayout.CardGap);
            if (changedModel) { _trayScroll.ScrollToHorizontalOffset(0); _trayScroll.ScrollToVerticalOffset(0); }
            else if ((changedMode || oldColumns != _trayColumns) && anchor is not null && _trayResults.FindIndex(item => item.Id == anchor) is var index && index >= 0)
            {
                if (grid) _trayScroll.ScrollToVerticalOffset(index / _trayColumns * (_trayCardHeight + ClipboardDockLayout.CardGap) + Math.Min(inset, _trayCardHeight));
                else _trayScroll.ScrollToHorizontalOffset(index * pitch + Math.Min(inset, _trayCardWidth));
            }
            _trayGridApplied = grid; UpdateVisibleTrayCards();
            if (_trayArrived is not null)
            {
                int index = _trayResults.FindIndex(item => item.Id == _trayArrived);
                if (index >= 0) { if (grid) _trayScroll.ScrollToVerticalOffset(index / _trayColumns * (_trayCardHeight + ClipboardDockLayout.CardGap)); else _trayScroll.ScrollToHorizontalOffset(index * pitch); }
                _trayArrived = null;
            }
        }
        finally { _layingOutTray = false; }
        int generation = ++_trayReflow;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _trayReflow || !IsTrayPage || !_expanded) return;
            _trayGrid.UpdateLayout(); UpdateVisibleTrayCards();
            foreach (var (id, card) in _trayCards)
            {
                if (card == _trayDragCard || !positions.TryGetValue(id, out var before)) continue;
                card.RenderTransform = Transform.Identity;
                if (!Soft.For(TrayContent) || changedModel) continue;
                var delta = before - card.TranslatePoint(new Point(), _trayScroll);
                if (delta.Length > .5) SlideFrom(card, delta.X, delta.Y);
            }
        });
    }
    private bool HandleTrayShortcut(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (key is Key.ImeProcessed or Key.DeadCharProcessed) return false;
        if (key == Key.F && modifiers == ModifierKeys.Control) { SetTraySearchOpen(true, focus: true); return true; }
        if (key == Key.Escape)
        {
            if (_trayDragging) return false;
            if (_trayMenu?.IsOpen == true) { CloseTrayPopups(); return true; }
            if (_trayQuery.Length > 0) { _traySearch!.Clear(); return true; }
            if (_traySearchOpen) { SetTraySearchOpen(false); return true; }
            return false;
        }
        if (key == Key.Down && ReferenceEquals(source, _traySearch))
        {
            var first = _trayResults.FirstOrDefault(); if (first is not null && _trayCards.TryGetValue(first.Id, out var card)) card.Focus(); return true;
        }
        return false;
    }
    private void CloseTrayPopups() { if (_trayMenu is not null) _trayMenu.IsOpen = false; _trayPressed = null; _trayPressCard = null; }
}
