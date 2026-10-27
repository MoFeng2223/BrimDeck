using System.IO;
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

// The plugins page: the folder button and the scan button on the left, the loaded extensions under a divider, each row
// opening like the app rules row to its page's size and origin, with an enable switch of its own in the header — off
// takes the tab off the island and stops what the extension reads. The scan loads plugin folders added or changed while the app
// runs and retires the ones taken away; the plugins that could not be read are listed by name and reason.
public sealed partial class SettingsWindow
{
    // Holds the list, so a flip, a switch or a scan rebuilds the list alone.
    private Border? _pluginsHost;
    // Which plugin rows stand open, kept while the page is rebuilt.
    private readonly bool[] _pluginOpen = new bool[4];

    private void PluginsPage()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 18) };
        actions.Children.Add(OutlineAction(Loc.T("打开插件文件夹", "Open plugins folder"),
            "M 1.5,4.4 H 4.4 L 5.6,3 H 10.5 V 9.5 H 1.5 Z", OpenPluginsFolder,
            Loc.T("打开存放插件文件夹的目录。", "Opens the folder the plugins live in.")));
        var reload = OutlineAction(Loc.T("重新加载", "Reload"),
            "M 9.1,4.2 A 3.7,3.7 0 1 0 9.9,7.4 M 9.1,2.2 V 4.2 H 7.1",
            () => { IslandExtensions.Scan(); ShowPage(PluginsPageNumber); },
            Loc.T("重新扫描插件文件夹：新放入的插件被装入，被修改的插件被替换，被删除的插件被停用；无需重启本软件。",
                "Scans the plugins folder again: plugins added are loaded, plugins changed are replaced, plugins taken away are retired; no restart needed."));
        reload.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reload);
        _content.Children.Add(actions);

        SectionTitle(Loc.T("已加载", "Loaded"));
        _pluginsHost = new Border { Child = PluginsList() };
        Group(_pluginsHost);

        if (IslandExtensions.Failures.Count > 0)
        {
            SectionTitle(Loc.T("未能加载", "Could not load"));
            Group(IslandExtensions.Failures.Select(failure => Row(System.IO.Path.GetFileName(failure.File), Wrapped(failure.Error, true))).ToArray());
        }
        if (IslandExtensions.Skipped.Count > 0)
        {
            SectionTitle(Loc.T("已跳过", "Skipped"));
            Group(IslandExtensions.Skipped.Select(skipped => Row(System.IO.Path.GetFileName(skipped.File), Wrapped(skipped.Note, false))).ToArray());
        }
    }

    // An outline button with a small stroke glyph before its text, as the app rules' Add app button is.
    private Button OutlineAction(string text, string glyph, Action action, string tip)
    {
        var button = new Button { Style = (Style)FindResource("SettingsOutlineButton"), ToolTip = tip };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Stroke(glyph, 12, 12, TextSecondary));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        button.Content = content;
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(IslandExtensions.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + IslandExtensions.Folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SaveStatus(ex.Message, true);
        }
    }

    private void RefreshPlugins()
    {
        if (_pluginsHost is not null) _pluginsHost.Child = PluginsList();
    }

    // The rows stand in the order the reader arranged them — read from DeckPages.Order, the same list the island's tabs
    // and the wheel walk — so this page cannot say one order and the panel another.
    private FrameworkElement PluginsList()
    {
        if (IslandExtensions.OccupiedCount == 0)
        {
            var empty = Wrapped(Loc.T("未装入插件。", "No plugins loaded."), false);
            empty.Margin = new Thickness(0, 6, 0, 8);
            return empty;
        }
        _pluginRows = DeckPages.Order(S).Select(DeckPages.SlotOf).Where(slot => slot >= 0 && IslandExtensions.Occupied(slot)).ToList();
        var list = new StackPanel();
        _pluginRowElements = [];
        Array.Clear(_pluginHandles, 0, _pluginHandles.Length);
        for (int at = 0; at < _pluginRows.Count; at++)
        {
            if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            var row = PluginRow(_pluginRows[at], at, _pluginRows.Count);
            _pluginRowElements.Add(row);
            list.Children.Add(row);
        }
        _pluginList = list;
        // Where a dragged row will land, drawn over the list rather than inserted into it: a child added between rows
        // would shift the very indices the drag is measuring.
        var host = new Grid();
        host.Children.Add(list);
        _dropLine = new Border
        {
            Height = 2, CornerRadius = new CornerRadius(1), Background = UI.Brush(TextSecondary), IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Margin = new Thickness(0, -8, 0, 0)
        };
        host.Children.Add(_dropLine);
        return host;
    }

    private List<int> _pluginRows = [];
    private List<FrameworkElement> _pluginRowElements = [];
    private StackPanel? _pluginList;
    private Border? _dropLine;
    private int _dragSlot = -1, _dragTarget = -1;
    private Point _dragFrom;
    private bool _dragging;

    // One loaded plugin: a header row — the handle it is dragged by, its glyph and title, its enable switch, and the arrow
    // that opens the settings under it; open, the page's size and the id and folder it came from. The switch is the plugin's
    // own: off takes its tab off the island and stops what it reads, in every state of the panel.
    private FrameworkElement PluginRow(int slot, int position, int total)
    {
        var extension = IslandExtensions.Slots[slot]!;
        string id = extension.Id, title = IslandExtensions.Title(slot);
        var entry = S.Extensions.FirstOrDefault(e => e.Id == id);
        bool enabled = entry?.Shown ?? true;
        string file = IslandExtensions.Source(slot);

        var column = new StackPanel();
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(DragHandle(slot, position, total, title));
        var glyph = new System.Windows.Shapes.Path
        {
            Data = IslandExtensions.SafeGlyph(extension.Glyph), Stroke = UI.Brush(TextSecondary), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(glyph, 1);
        header.Children.Add(glyph);
        var name = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 2);
        header.Children.Add(name);
        var enable = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(enable, Loc.T("启用 ", "Enable ") + title);
        enable.Checked += (_, _) => ChangePlugin(id, current => current with { Shown = true });
        enable.Unchecked += (_, _) => ChangePlugin(id, current => current with { Shown = false });
        Grid.SetColumn(enable, 3);
        header.Children.Add(enable);
        void Flip() { _pluginOpen[slot] = !_pluginOpen[slot]; RefreshPlugins(); }
        var arrow = ChevronButton(Loc.T("展开 ", "Expand ") + title, Flip);
        arrow.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(arrow, 4);
        header.Children.Add(arrow);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        column.Children.Add(header);

        var details = new StackPanel { Margin = new Thickness(42, 0, 0, 10), Visibility = _pluginOpen[slot] ? Visibility.Visible : Visibility.Collapsed };
        var dimensions = new Grid { MinHeight = 44 }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        // The page's own limits, which a plugin's manifest can raise: the fields offer exactly the sizes the page can take,
        // and show the size it is actually using even when an older file saved less.
        var page = DeckPages.ExtensionPage(slot);
        var (floorWidth, ceilingWidth, floorHeight, ceilingHeight) = DeckPages.Limits(S, page);
        var (usedWidth, usedHeight) = DeckPages.Size(S, page);
        var width = Dimension(Loc.T("宽度", "Width"), usedWidth, floorWidth, ceilingWidth, value => Resize(s => SetSlotSize(s, slot, value, null), page));
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), usedHeight, floorHeight, ceilingHeight, value => Resize(s => SetSlotSize(s, slot, null, value), page));
        height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        details.Children.Add(dimensions);
        // The controls the plugin's own manifest declared. The host draws them — a reader sets a plugin up here instead
        // of opening its folder, and an author writes no settings interface at all.
        foreach (var row in DeclaredRows(id, IslandExtensions.Declared(slot))) details.Children.Add(row);
        // What the author declared that the host could not use. Without this a control that vanished reads as the host
        // ignoring it on purpose — the same reason a refused bridge message is answered.
        var notes = IslandExtensions.Notes(slot);
        if (notes.Count > 0)
        {
            var heading = TextLine(Loc.T("已忽略的声明项", "Declared entries the host skipped"), 11.5, TextTertiary);
            heading.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(heading);
            foreach (var note in notes.Take(6))
            {
                var line = TextLine(note, 11.5, TextTertiary);
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 2, 0, 0);
                details.Children.Add(line);
            }
            if (notes.Count > 6)
            {
                var more = TextLine(Loc.T($"另有 {notes.Count - 6} 条", $"{notes.Count - 6} more"), 11.5, TextTertiary);
                more.Margin = new Thickness(0, 2, 0, 0);
                details.Children.Add(more);
            }
        }
        var source = TextLine(id + (file.Length > 0 ? " · " + file : ""), 11.5, TextTertiary);
        source.Margin = new Thickness(0, 2, 0, 0);
        if (file.Length > 0) source.ToolTip = IslandExtensions.Folder + "\\" + file;
        details.Children.Add(source);
        column.Children.Add(details);
        PointArrow(arrow, _pluginOpen[slot]);
        return column;
    }

    // Writes one dimension of an extension page's size into its own settings entry. The other dimension is read back
    // first, and the host clamps both: a page never ends up smaller than its plugin asked for.
    private static void SetSlotSize(DeckSettings settings, int slot, double? width, double? height)
    {
        var page = DeckPages.ExtensionPage(slot);
        var (nowWidth, nowHeight) = DeckPages.Size(settings, page);
        DeckPages.SetSize(settings, page, width ?? nowWidth, height ?? nowHeight);
    }

    // The six dots a reader drags by. Only this starts a drag, so a click anywhere else on the row keeps opening and
    // closing it the way it always did. The keyboard moves it one place at a time, and its name says which place that
    // is, since the position is the whole point of the control. With a single plugin there is no place to move it to,
    // so the space is kept — the rows line up either way — and the handle is not offered.
    private FrameworkElement DragHandle(int slot, int position, int total, string title)
    {
        if (total < 2) return new Border { Width = 16, Margin = new Thickness(0, 0, 4, 0) };
        var dots = Stroke("M 9,7 L 9.01,7 M 15,7 L 15.01,7 M 9,12 L 9.01,12 M 15,12 L 15.01,12 M 9,17 L 9.01,17 M 15,17 L 15.01,17", 14, 14, TextTertiary);
        dots.StrokeThickness = 2.4; dots.StrokeStartLineCap = dots.StrokeEndLineCap = PenLineCap.Round;
        dots.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var handle = new Button
        {
            Content = dots, Style = (Style)FindResource("SettingsGlyphButton"), Cursor = Cursors.SizeNS,
            Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T("拖动可调整这个插件在面板里的位置；Alt+↑ / Alt+↓ 上移、下移。", "Drag to move this plugin's place in the panel; Alt+Up and Alt+Down move it one step.")
        };
        _pluginHandles[slot] = handle;
        AutomationProperties.SetName(handle, Loc.T($"{title} 的位置，第 {position + 1} 个，共 {total} 个", $"{title} position, {position + 1} of {total}"));
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragSlot = slot; _dragTarget = position; _dragFrom = e.GetPosition(null); _dragging = false;
            // Taking the focus the button would have taken itself: the gesture is marked handled before the class
            // handler runs, so a reader who clicks the handle and then reaches for Alt+↑ would otherwise press into a
            // control that never had the keyboard.
            handle.Focus();
            handle.CaptureMouse(); e.Handled = true;
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragSlot < 0) return;
            if (!_dragging && Math.Abs(e.GetPosition(null).Y - _dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            _dragTarget = BoundaryAt(e.GetPosition(_pluginList).Y);
            ShowDropLine(_dragTarget);
            e.Handled = true;
        };
        handle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // Letting go of the mouse before the list is rebuilt: the row that commits the move is on its way out, and
            // a capture held by a vanished element is only dropped when WPF notices.
            bool commit = _dragging && _dragSlot == slot;
            int boundary = _dragTarget;
            handle.ReleaseMouseCapture();
            if (commit) MoveTo(slot, boundary);
            ReleaseDrag();
            e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) => ReleaseDrag();
        handle.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt) return;
            // With Alt held WPF reports the key as System and keeps the arrow in SystemKey — the arrow is the whole
            // gesture here, so reading Key alone is reading the modifier.
            var arrow = e.Key == Key.System ? e.SystemKey : e.Key;
            if (arrow == Key.Up) { MoveTo(slot, position - 1); RefocusHandle(slot); e.Handled = true; }
            else if (arrow == Key.Down) { MoveTo(slot, position + 2); RefocusHandle(slot); e.Handled = true; }
        };
        return handle;
    }

    // The handle of each loaded slot, so a move made at the keyboard can be repeated: the list is rebuilt by every
    // change, and the new handle for the same plugin is where the focus belongs.
    private readonly Button?[] _pluginHandles = new Button?[4];

    // The boundary the pointer is over: 0 is above the first row, the count is below the last one. Rows measure
    // against the list they live in, so a scrolled page still reads the right place.
    private int BoundaryAt(double y)
    {
        if (_pluginList is not { } list) return 0;
        for (int at = 0; at < _pluginRowElements.Count; at++)
        {
            var row = _pluginRowElements[at];
            if (y < row.TranslatePoint(new Point(0, 0), list).Y + row.ActualHeight / 2) return at;
        }
        return _pluginRowElements.Count;
    }

    private void ShowDropLine(int boundary)
    {
        if (_dropLine is not { } line || _pluginList is not { } list || _pluginRowElements.Count == 0) return;
        var rows = _pluginRowElements;
        double y = boundary >= rows.Count
            ? rows[^1].TranslatePoint(new Point(0, 0), list).Y + rows[^1].ActualHeight
            : rows[boundary].TranslatePoint(new Point(0, 0), list).Y;
        line.Margin = new Thickness(0, Math.Max(0, y - 1), 0, 0);
        line.Visibility = Visibility.Visible;
    }

    private void ReleaseDrag()
    {
        if (_dropLine is { } line) line.Visibility = Visibility.Collapsed;
        _dragSlot = -1; _dragTarget = -1; _dragging = false;
    }

    // The dragged plugin leaves its place and enters the boundary the line showed. Moving down shifts by one, because
    // the boundary counted the row before it was taken out.
    private void MoveTo(int slot, int boundary)
    {
        var ids = _pluginRows.Select(row => IslandExtensions.Slots[row]?.Id).ToList();
        if (ids.Any(id => string.IsNullOrEmpty(id))) return;
        int from = _pluginRows.IndexOf(slot);
        if (from < 0) return;
        var order = ids.Select(id => id!).ToList();
        var moved = order[from];
        order.RemoveAt(from);
        order.Insert(Math.Clamp(boundary > from ? boundary - 1 : boundary, 0, order.Count), moved);
        Change(settings => settings.ReorderExtensions(order));
        RefreshPlugins();
    }

    // The keyboard moves a row one place at a time, so the handle must still hold the focus once the list is made again:
    // a second Alt+↓ landing on nothing would leave a plugin movable exactly once. The rebuilt handle does not exist in
    // the layout yet, so the focus waits for the list to be measured.
    private void RefocusHandle(int slot)
    {
        if (_pluginHandles[slot] is not { } handle) return;
        Dispatcher.BeginInvoke(new Action(() => { if (handle.IsVisible) Keyboard.Focus(handle); }), DispatcherPriority.Input);
    }

    // Writes the plugin's own entry, creating it on the first change so an untouched plugin keeps its defaults.
    private void ChangePlugin(string id, Func<ExtensionPageSettings, ExtensionPageSettings> edit)
    {
        Change(s =>
        {
            var at = s.Extensions.FindIndex(e => e.Id == id);
            if (at < 0) { s.Extensions.Add(edit(new ExtensionPageSettings(id))); return; }
            s.Extensions[at] = edit(s.Extensions[at]);
        });
        RefreshPlugins();
    }

    // One row per declared control, in the order the manifest wrote them. The declaration is the whole design: the
    // labels are the host's own words in the interface's language, the number's range and step come from the author,
    // and a plugin with nothing declared gets the row exactly as it was.
    private IEnumerable<FrameworkElement> DeclaredRows(string id, IReadOnlyList<ExtensionSetting> declared)
    {
        if (declared.Count == 0) yield break;
        var values = IslandExtensions.ValuesFor(id);
        foreach (var setting in declared)
        {
            var current = values.TryGetValue(setting.Key, out var kept) ? kept : ExtensionSettingStore.Initial(setting);
            var label = setting.Label.For(Loc.IsEnglish);
            switch (setting.Kind)
            {
                case ExtensionSettingKind.Toggle:
                    var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = current.On, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(toggle, label);
                    toggle.Checked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("toggle", true));
                    toggle.Unchecked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("toggle", false));
                    yield return Row(label, toggle);
                    break;
                case ExtensionSettingKind.Number:
                    yield return SliderRow(label, current.Number, setting.Minimum, setting.Maximum, setting.Step, "",
                        value => WriteDeclared(id, setting, new ExtensionSettingValue("number", false, value)));
                    break;
                default:
                    var options = new UniformGrid { Columns = Math.Min(setting.Options.Count, 3) };
                    foreach (var option in setting.Options)
                    {
                        var words = option.Label.For(Loc.IsEnglish);
                        var choice = new RadioButton
                        {
                            Content = words, Style = (Style)FindResource("SettingsSegment"),
                            GroupName = id + "." + setting.Key, IsChecked = current.Choice == option.Value
                        };
                        AutomationProperties.SetName(choice, label + " · " + words);
                        choice.Checked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("choice", false, 0, option.Value));
                        options.Children.Add(choice);
                    }
                    yield return Row(label, SegmentBorder(options));
                    break;
            }
        }
    }

    // Written the way the settings window writes everything: a copy, the edit, the app's apply — and that apply is what
    // tells the plugin's own pages, so a click here and the page's setSetting end in the same place. The value is
    // honoured against the declaration on the way in, so the file can only hold what the author's own range allows.
    private void WriteDeclared(string id, ExtensionSetting setting, ExtensionSettingValue value)
        => Change(s => s.SetExtensionValue(id, setting.Key, ExtensionSettingStore.Honor(setting, value)));

    // A page wrote one of its own controls: the row this window shows has to say the same thing now.
    internal void PluginValuesChanged() { if (_pluginsHost is not null) RefreshPlugins(); }
}
