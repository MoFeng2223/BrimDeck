using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private const int InputMethodsPage = 10;
    private const double InputMethodRowHeight = 56;

    private void InputMethodsCatalogChanged()
    { if (IsLoaded && _page == InputMethodsPage && _appDrag is null && _stopHotkeyRecording is null) ShowPage(InputMethodsPage); }

    private void InputMethodSettingsPage()
    {
        SectionTitle(Loc.T("工作模式", "Mode"));
        var modes = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Width = 240 };
        foreach (var mode in new[] { InputMethodMode.Notify, InputMethodMode.Control })
        {
            string title = mode == InputMethodMode.Notify ? Loc.T("仅通知", "Notify only") : Loc.T("接管切换", "Control switching");
            var option = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = title,
                GroupName = "InputMethodMode", IsChecked = S.InputMethods.Mode == mode };
            AutomationProperties.SetName(option, title);
            option.Checked += (_, _) =>
            {
                if (S.InputMethods.Mode == mode) return;
                _stopHotkeyRecording?.Invoke();
                Change(s => s.InputMethods.Mode = mode); ShowPage(InputMethodsPage);
            };
            modes.Children.Add(option);
        }
        Group(Toggle(Loc.T("启用输入法模块", "Enable input-method module"), S.InputMethods.Enabled,
            enabled => { Change(s => s.InputMethods.Enabled = enabled); ShowPage(InputMethodsPage); }, help:
            Loc.T("仅通知会沿用系统切换方式；接管切换可自定义快捷键和循环顺序。",
                "Notify only keeps Windows switching. Control switching uses your shortcut and cycle order.")),
            Row(Loc.T("切换方式", "Switching mode"), SegmentBorder(modes)));
        if (S.InputMethods.Mode == InputMethodMode.Notify)
        {
            var explanation = TextLine(Loc.T("使用 Windows 或输入法原来的快捷键切换，灵动岛只在实际变化后短暂显示结果。系统原切换菜单仍会出现。",
                "Use the original Windows or input-method shortcut. The island briefly shows verified changes. The Windows switcher remains available."), 12, TextSecondary);
            explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 0, 0, 14); _content.Children.Add(explanation);
            var scope = TextLine(Loc.T("通知所有可识别的输入法。自定义快捷键、参与名单和排序保留在接管模式中，不影响系统切换。",
                "Notify for all recognized input methods. Your custom shortcut, participants and order are kept for control mode and do not affect Windows switching."), 12, TextSecondary);
            scope.TextWrapping = TextWrapping.Wrap; scope.Margin = new Thickness(0, 0, 0, 14); _content.Children.Add(scope);
            var status = TextLine(_app.InputMethods.Problem ?? _app.InputMethods.TargetProblem ?? (S.InputMethods.Enabled
                ? Loc.T("已启用仅通知；首次读取不提示，连续切换只通知稳定后的结果。", "Notify only is enabled. The first read is silent; rapid changes are combined into a stable result.")
                : Loc.T("启用模块后显示切换通知。", "Enable the module to show switching notifications.")), 12, TextSecondary);
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 0, 0, 14); _content.Children.Add(status);
            InputMethodRefreshControls(notification: true);
            return;
        }
        InputMethodControlSettings();
    }

    private void InputMethodControlSettings()
    {
        SectionTitle(Loc.T("快捷切换", "Quick switching"));
        Group(HotkeyRow(HotkeyKind.Expand, inputMethod: true));
        var gestureHelp = TextLine(InputMethodGesture.Hint(S.InputMethods.Shortcut), 12, TextSecondary);
        gestureHelp.TextWrapping = TextWrapping.Wrap; gestureHelp.Margin = new Thickness(0, 0, 0, 10); _content.Children.Add(gestureHelp);
        var resetShortcut = ActionButton(Loc.T("恢复 Win + 空格", "Restore Win + Space"), () => { Change(s => s.InputMethods.Shortcut = "Win+Space"); ShowPage(InputMethodsPage); });
        resetShortcut.HorizontalAlignment = HorizontalAlignment.Left; resetShortcut.Margin = new Thickness(0, 0, 0, 14); _content.Children.Add(resetShortcut);

        SectionTitle(Loc.T("参与循环与顺序", "Cycle participants and order"));
        var explanation = TextLine(Loc.T("只循环开启的项目，可拖动或使用上移、下移调整顺序。Windows 中仍保留所有输入法。",
            "Cycle only enabled items. Drag or use Move up and Move down to reorder. All input methods remain installed in Windows."), 12, TextSecondary);
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 0, 0, 12); _content.Children.Add(explanation);

        var installed = _app.InputMethods.Installed.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = S.InputMethods.Profiles.Select(p => installed.GetValueOrDefault(p.Id) ?? new InputMethodInfo(p.Id, p.Name, false)).ToList();
        var known = ordered.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(installed.Values.Where(p => !known.Contains(p.Id)));
        var rows = new StackPanel();
        var actions = new Dictionary<Border, (Button Up, Button Down)>();

        void RefreshArrows()
        {
            for (int i = 0; i < rows.Children.Count; i++)
                if (rows.Children[i] is Border row && actions.TryGetValue(row, out var pair))
                { pair.Up.IsEnabled = i > 0; pair.Down.IsEnabled = i < rows.Children.Count - 1; }
        }
        void SaveOrder()
        {
            var order = rows.Children.OfType<Border>().Select(b => (InputMethodInfo)b.Tag).ToArray();
            Change(s => s.InputMethods.Profiles = order.Select(item => s.InputMethods.Profiles.FirstOrDefault(p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                ?? new InputMethodChoice { Id = item.Id, Name = item.Name, Enabled = false }).ToList());
            RefreshArrows();
        }
        void Move(Border row, int delta)
        {
            EndAppDrag(false, false);
            int from = rows.Children.IndexOf(row), to = from + delta;
            if (to < 0 || to >= rows.Children.Count) return;
            var neighbor = (Border)rows.Children[to];
            rows.Children.Remove(row); rows.Children.Insert(to, row);
            SetRowOffset(row, -delta * InputMethodRowHeight); SetRowOffset(neighbor, delta * InputMethodRowHeight);
            AnimateRow(row, 0); AnimateRow(neighbor, 0); SaveOrder();
        }
        foreach (var item in ordered)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = Grip(TextTertiary) };
            grid.Children.Add(handle);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var title = TextLine(item.Name.Length > 0 ? item.Name : Loc.T("不可用的输入法", "Unavailable input method"), 13, item.Available ? TextPrimary : TextTertiary);
            title.TextTrimming = TextTrimming.CharacterEllipsis; label.Children.Add(title);
            var note = TextLine("", 11, TextTertiary); label.Children.Add(note); Grid.SetColumn(label, 1); grid.Children.Add(label);
            var choice = S.InputMethods.Profiles.FirstOrDefault(p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = choice?.Enabled == true,
                IsEnabled = item.Available || choice?.Enabled == true, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, Loc.T("参与循环：", "Include in cycle: ") + item.Name);
            void RefreshNote() => note.Text = !item.Available ? Loc.T("不可用 · 循环时跳过", "Unavailable · skipped")
                : toggle.IsChecked == true ? Loc.T("参与循环", "Included") : Loc.T("不参与循环", "Excluded");
            void SetChoice(bool enabled)
            {
                Change(s => {
                    var saved = s.InputMethods.Profiles.FirstOrDefault(p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                    if (saved is null) { saved = new() { Id = item.Id, Name = item.Name }; s.InputMethods.Profiles.Add(saved); }
                    saved.Enabled = enabled; saved.Name = item.Name;
                });
                SaveOrder(); RefreshNote();
            }
            toggle.Checked += (_, _) => SetChoice(true); toggle.Unchecked += (_, _) => SetChoice(false);
            Grid.SetColumn(toggle, 2); grid.Children.Add(toggle); RefreshNote();
            var row = new Border { Child = grid, Height = InputMethodRowHeight, Tag = item, RenderTransform = new TranslateTransform(), Background = Brushes.Transparent };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = ActionButton("↑", () => Move(row, -1)); var down = ActionButton("↓", () => Move(row, 1));
            foreach (var button in new[] { up, down }) { button.Width = 28; button.Height = 28; button.Padding = new Thickness(0); }
            AutomationProperties.SetName(up, Loc.T("上移：", "Move up: ") + item.Name); AutomationProperties.SetName(down, Loc.T("下移：", "Move down: ") + item.Name);
            up.ToolTip = Loc.T("上移", "Move up"); down.ToolTip = Loc.T("下移", "Move down");
            buttons.Children.Add(up); buttons.Children.Add(down); Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);
            actions[row] = (up, down);
            AttachDrag(handle, item.Name, () => rows, InputMethodRowHeight, _ => SaveOrder());
            rows.Children.Add(row);
        }
        RefreshArrows();
        _content.Children.Add(new Border { Child = rows, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 1) });
        int participants = S.InputMethods.Participants(installed.Values).Count;
        var status = TextLine(_app.InputMethods.Problem ?? _app.InputMethods.TargetProblem ?? (participants == 0
            ? Loc.T("请至少开启一项；没有可用项时由 Windows 处理快捷键。", "Enable at least one item. Windows handles the shortcut while the list is empty.")
            : Loc.T($"已选择 {participants} 项，按上方顺序循环。", $"{participants} selected, cycling in the order above.")), 12, TextSecondary);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 12, 0, 12); _content.Children.Add(status);
        InputMethodRefreshControls(notification: false);
    }

    private void InputMethodRefreshControls(bool notification)
    {
        var refresh = ActionButton(Loc.T("刷新输入法列表", "Refresh input methods"), () => _ = _app.InputMethods.RefreshAsync());
        refresh.HorizontalAlignment = HorizontalAlignment.Left; _content.Children.Add(refresh);
        var compatibility = TextLine(notification
            ? Loc.T("当前支持常规 64 位应用；无法读取输入状态的窗口暂不显示通知。", "Standard 64-bit apps are supported. Notifications are skipped when a window's input state cannot be read.")
            : Loc.T("当前版本支持常规 64 位应用；无法接管的窗口继续使用 Windows 原生切换。", "This version supports standard 64-bit apps. Windows handles switching in unsupported windows."), 11, TextTertiary);
        compatibility.TextWrapping = TextWrapping.Wrap; compatibility.Margin = new Thickness(0, 14, 0, 0); _content.Children.Add(compatibility);
    }

    private string InputMethodShortcutProblem(string saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return "";
        if (!InputMethodGesture.TryParse(saved, out _, out string canonical))
            return Loc.T("无法使用这个组合键，请重新录制。", "This combination cannot be used. Record another shortcut.");
        if (SameHotkey(canonical, S.HideHotkey) || SameHotkey(canonical, S.ExpandHotkey) || SameHotkey(canonical, S.ResizeHotkey))
            return Loc.T("与隐藏/显示、展开或调整大小快捷键重复。", "This conflicts with the hide/show, expand or resize shortcut.");
        return "";
    }
}
