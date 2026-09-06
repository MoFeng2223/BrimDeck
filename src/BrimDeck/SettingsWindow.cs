using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using BrimDeck.Core;

namespace BrimDeck;

public sealed class SettingsWindow : Window
{
    private const string Surface = "#131315", Sidebar = "#0C0C0E", Card = "#1C1C1F", Outline = "#14FFFFFF", Divider = "#12FFFFFF", Inset = "#14FFFFFF";
    private const string TextPrimary = "#F2F2F4", TextSecondary = "#9B9BA3", TextTertiary = "#6C6C74";
    private readonly App _app;
    private readonly StackPanel _navigation = new();
    private readonly StackPanel _content = new();
    private readonly TextBlock _status = UI.Line("自动保存", 11, TextSecondary);
    private readonly ScrollViewer _scroll;
    private readonly string[] _pages = ["外观与尺寸", "悬停与动画", "窗口行为", "页面与应用", "关于与价格"];
    private int _page;
    private bool _preview;
    private Button? _previewButton;
    // Rows added with "+" whose application has not been chosen yet; they exist only in the window.
    private int _pendingRows;
    private StackPanel? _appRows;
    private Border? _dragRow;
    private bool _dragMoved;
    public FrameworkElement RootVisual { get; }
    private DeckSettings S => _app.Settings;

    public static string StyleName(CompactStyle style) => style switch { CompactStyle.Notch => "刘海", CompactStyle.Capsule => "胶囊", _ => "指示条" };
    public static string BehaviorName(WindowBehavior behavior) => behavior switch { WindowBehavior.Normal => "正常显示", WindowBehavior.Line => "指示条", _ => "完全隐藏" };

    public SettingsWindow(App app)
    {
        _app = app;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        Style = (Style)FindResource(typeof(Window));
        Title = "BrimDeck 设置"; Width = 820; Height = 640; MinWidth = 740; MinHeight = 520;
        var (_, work, scale) = Native.WindowsHost.PrimaryScreen();
        Width = Math.Min(Width, work.Width / scale - 24); Height = Math.Min(Height, work.Height / scale - 24);
        MinWidth = Math.Min(MinWidth, Width); MinHeight = Math.Min(MinHeight, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Native.SettingsChrome.Apply(this);

        var outer = new Grid { Background = UI.Brush(Surface) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) }); outer.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Grid { Background = UI.Brush(Sidebar) };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) }); sidebar.RowDefinitions.Add(new RowDefinition()); sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        var brand = new StackPanel { Margin = new Thickness(22, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        var brandName = new TextBlock { FontFamily = UI.TextFont, FontSize = 24, Foreground = UI.Brush(TextPrimary) };
        brandName.Inlines.Add(new Run("Brim") { FontWeight = FontWeights.SemiBold });
        brandName.Inlines.Add(new Run("Deck") { FontWeight = FontWeights.Light, Foreground = UI.Brush("#C5C5CB") });
        AutomationProperties.SetName(brandName, "BrimDeck");
        brand.Children.Add(brandName);
        brand.Children.Add(new Border
        {
            Width = 30, Height = 1, Margin = new Thickness(1, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush(Color.FromArgb(150, 197, 197, 203), Color.FromArgb(0, 197, 197, 203), 0)
        });
        sidebar.Children.Add(brand);
        _navigation.Margin = new Thickness(10, 0, 10, 0); Grid.SetRow(_navigation, 1); sidebar.Children.Add(_navigation);
        var version = UI.Line("0.1.0", 10.5, TextTertiary); version.Margin = new Thickness(22, 0, 0, 0); version.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(version, 2); sidebar.Children.Add(version);
        outer.Children.Add(sidebar);
        outer.Children.Add(new Border { Width = 1, Background = UI.Brush(Divider), HorizontalAlignment = HorizontalAlignment.Right });

        var main = new DockPanel { Margin = new Thickness(34, 56, 34, 16), MaxWidth = 720 }; Grid.SetColumn(main, 1); outer.Children.Add(main);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Height = 32 };
        var reset = ActionButton("恢复默认设置", () => { _pendingRows = 0; _app.UpdateSettings(new DeckSettings()); ShowPage(_page); });
        reset.FontSize = 11; DockPanel.SetDock(reset, Dock.Right); footer.Children.Add(reset);
        _status.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_status); DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);
        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        main.Children.Add(_scroll);

        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 12, 0) };
        windowButtons.Children.Add(WindowButton("", "最小化", () => SystemCommands.MinimizeWindow(this)));
        var maximize = WindowButton("", "最大化", () => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }); windowButtons.Children.Add(maximize);
        windowButtons.Children.Add(WindowButton("", "关闭设置", Close, true));
        Grid.SetColumnSpan(windowButtons, 2); outer.Children.Add(windowButtons);
        var frame = new Border { Child = outer, BorderBrush = UI.Brush("#1AFFFFFF"), BorderThickness = new Thickness(1) };
        Content = frame; RootVisual = frame;
        StateChanged += (_, _) =>
        {
            maximize.Content = Glyph(WindowState == WindowState.Maximized ? "" : "", 10, TextSecondary);
            maximize.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
            AutomationProperties.SetName(maximize, (string)maximize.ToolTip);
            if (WindowState == WindowState.Minimized) { _preview = false; _app.Deck.Preview(false); UpdatePreviewButton(); }
        };
        Closed += (_, _) => { _app.Deck.Preview(false); _app.FlushSettings(); };
        ShowPage(0);
    }

    public void SaveStatus(string message, bool error = false)
    {
        _status.Text = !error && message.StartsWith("已保存", StringComparison.Ordinal) ? "已保存" : message;
        _status.ToolTip = message; _status.Foreground = UI.Brush(error ? "#FF6961" : TextSecondary);
    }

    private Button ActionButton(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(primary ? "SettingsPrimaryButton" : "SettingsButton") };
        button.Click += (_, _) => action(); return button;
    }

    private Button WindowButton(string glyph, string label, Action action, bool close = false)
    {
        var button = ActionButton("", action); button.Content = Glyph(glyph, 10, TextSecondary); button.Width = 34; button.Height = 28;
        button.Padding = new Thickness(0); button.Margin = new Thickness(2, 0, 0, 0); button.ToolTip = label;
        if (close) button.Style = (Style)FindResource("SettingsCloseButton");
        WindowChrome.SetIsHitTestVisibleInChrome(button, true); AutomationProperties.SetName(button, label); return button;
    }

    private static TextBlock Glyph(string glyph, double size, string color) => new()
    { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size, Foreground = UI.Brush(color), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

    // Small vector icons drawn with strokes, independent of icon font availability.
    private static Path Stroke(string data, double width, double height, string color) => new()
    {
        Data = Geometry.Parse(data), Width = width, Height = height, Stroke = UI.Brush(color), StrokeThickness = 1.4,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    // The drag handle: two columns of three dots.
    private static UniformGrid Grip(string color)
    {
        var grip = new UniformGrid { Columns = 2, Rows = 3, Width = 9, Height = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 6; i++) grip.Children.Add(new Ellipse { Width = 2.4, Height = 2.4, Fill = UI.Brush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grip;
    }

    private static Border BrandMark(double size)
    {
        var inset = new Grid();
        inset.Children.Add(new Border { Width = size * .62, Height = size * .2, CornerRadius = new CornerRadius(size * .1), Background = UI.Brush("#F2FFFFFF"), VerticalAlignment = VerticalAlignment.Center });
        var gradient = new LinearGradientBrush(Color.FromRgb(0x4C, 0x8F, 0xFF), Color.FromRgb(0x5E, 0x5C, 0xE6), 45);
        return new Border { Child = inset, Width = size, Height = size, CornerRadius = new CornerRadius(size * .27), Background = gradient };
    }

    private void Change(Action<DeckSettings> edit) { var settings = S.Copy(); edit(settings); _app.UpdateSettings(settings); }

    public void ShowPage(int page)
    {
        if (page != 0 && _preview) { _preview = false; _app.Deck.Preview(false); }
        if (page != 3) _pendingRows = 0;
        _page = page; _navigation.Children.Clear(); _content.Children.Clear(); _previewButton = null; _scroll.ScrollToTop();
        for (int i = 0; i < _pages.Length; i++)
        {
            int target = i; bool selected = page == i;
            var text = UI.Line(_pages[i], 12.5, selected ? "#FFFFFF" : "#D6D6DB", selected ? FontWeights.SemiBold : FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            var button = new Button { Style = (Style)FindResource("SettingsNav"), Content = text, Margin = new Thickness(0, 0, 0, 3), Tag = selected ? "Selected" : null };
            button.Click += (_, _) => ShowPage(target);
            AutomationProperties.SetName(button, _pages[i]); _navigation.Children.Add(button);
        }
        var title = UI.Line(_pages[page], 22, TextPrimary, FontWeights.SemiBold); title.Margin = new Thickness(0, 0, 0, 22); _content.Children.Add(title);
        switch (page)
        {
            case 0: Appearance(); break;
            case 1: Interaction(); break;
            case 2: WindowRules(); break;
            case 3: Pages(); break;
            case 4: About(); break;
        }
    }

    private void Appearance()
    {
        SectionTitle("收起样式");
        var styles = new UniformGrid { Columns = 3, Margin = new Thickness(-6, 0, -6, 24) };
        foreach (var style in Enum.GetValues<CompactStyle>())
        {
            var button = new RadioButton { Style = (Style)FindResource("SettingsChoice"), Content = CompactStylePreview.Create(style),
                Margin = new Thickness(6, 0, 6, 0), Tag = StyleName(style), GroupName = "CompactStyle", IsChecked = S.Style == style };
            button.Checked += (_, _) => Change(s => s.Style = style);
            AutomationProperties.SetName(button, StyleName(style)); styles.Children.Add(button);
        }
        _content.Children.Add(styles);
        Group(Toggle("收起时显示配额环", S.CompactSummary, value => Change(s => s.CompactSummary = value)));
        _previewButton = ActionButton("", () => { _preview = !_preview; _app.Deck.Preview(_preview); UpdatePreviewButton(); });
        _previewButton.FontSize = 11.5; _previewButton.Background = UI.Brush(Inset); UpdatePreviewButton();
        SectionTitle("展开尺寸", _previewButton);
        // The width slider cannot go below what the enabled applications need; no explanatory text is shown.
        Group(SliderRow("宽度", S.Width, S.MinimumWidth, 1200, 10, "DIP", value => Resize(s => s.Width = value)),
            SliderRow("高度", S.Height, 140, 400, 10, "DIP", value => Resize(s => s.Height = value)));
    }

    private void UpdatePreviewButton() { if (_previewButton is not null) _previewButton.Content = _preview ? "结束预览" : "预览面板"; }
    private void Resize(Action<DeckSettings> edit)
    { Change(edit); if (!_preview) { _preview = true; _app.Deck.Preview(true); UpdatePreviewButton(); } }

    private void Interaction()
    {
        SectionTitle("悬停");
        Group(SliderRow("悬停后展开", S.OpenDelay, 0, 2000, 50, "毫秒", value => Change(s => s.OpenDelay = (int)value)),
            SliderRow("移开后收起", S.CloseDelay, 0, 2000, 50, "毫秒", value => Change(s => s.CloseDelay = (int)value)));
        SectionTitle("动画");
        var duration = SliderRow("动画时长", S.AnimationDuration, 100, 600, 20, "毫秒", value => Change(s => s.AnimationDuration = (int)value)); duration.IsEnabled = S.Animations;
        Group(Toggle("开启动画", S.Animations, value => { Change(s => s.Animations = value); duration.IsEnabled = value; }), duration);
    }

    private void WindowRules()
    {
        SectionTitle("自动调整面板");
        Group(BehaviorRow("窗口最大化", S.Maximized, value => Change(s => s.Maximized = value)),
            BehaviorRow("无边框全屏", S.Borderless, value => Change(s => s.Borderless = value)),
            BehaviorRow("独占全屏", S.Exclusive, value => Change(s => s.Exclusive = value)));
    }

    // The application list: one row per application, "+" adds a row, the row chooses its application, then its colors.
    private void Pages()
    {
        SectionTitle("页面"); Group(Toggle("AI 用量", S.UsagePage, value => { Change(s => s.UsagePage = value); ShowPage(3); }));
        SectionTitle("显示的应用");
        _appRows = new StackPanel();
        for (int i = 0; i < S.Apps.Count; i++) _appRows.Children.Add(AppRow(S.Apps[i], i));
        for (int i = 0; i < _pendingRows; i++) _appRows.Children.Add(AppRow(null, S.Apps.Count + i));
        UpdateDividers();
        var list = new StackPanel(); list.Children.Add(_appRows);
        var add = ActionButton("", () => { _pendingRows++; ShowPage(3); });
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        addContent.Children.Add(Stroke("M 5,0 L 5,10 M 0,5 L 10,5", 10, 10, TextSecondary));
        var addLabel = UI.Line("添加应用", 12.5, TextSecondary); addLabel.Margin = new Thickness(8, 0, 0, 0); addLabel.VerticalAlignment = VerticalAlignment.Center; addContent.Children.Add(addLabel);
        add.Content = addContent; add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(10, 6, 0, 6); add.Padding = new Thickness(10, 6, 12, 6);
        add.IsEnabled = Enum.GetValues<ProviderId>().Count(id => S.Entry(id) is null) > _pendingRows;
        add.ToolTip = add.IsEnabled ? "在列表末尾增加一行，然后选择应用" : "预置应用已全部添加";
        AutomationProperties.SetName(add, "添加应用");
        list.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(16, 0, 16, 0) });
        list.Children.Add(add);
        Group(list);
        var note = UI.Text("拖动左侧把手调整顺序，顺序即展开面板中的列顺序。预警色与告急色默认对所有应用相同，可以逐个修改；关闭页面不会清除列表。", 11, TextSecondary);
        note.Margin = new Thickness(2, -12, 0, 22); _content.Children.Add(note);
    }

    private Border AppRow(AppEntry? entry, int index)
    {
        var row = new Grid { Height = 46, Margin = new Thickness(10, 0, 12, 0), Background = Brushes.Transparent };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = entry is null ? Cursors.Arrow : Cursors.SizeAll,
            Child = Grip(entry is null ? "#26FFFFFF" : "#66FFFFFF"), ToolTip = entry is null ? null : "拖动调整顺序" };
        row.Children.Add(handle);
        string name = entry is null ? "选择应用" : AppPresets.Name(entry.Id);
        // A compact outlined field: the name at the left, a chevron at the right; it never fills the row.
        var select = ActionButton("", () => { });
        var selectContent = new DockPanel { Width = 116 };
        var chevron = Stroke("M 0,0 L 4,4 L 8,0", 8, 4, TextSecondary); chevron.Margin = new Thickness(8, 1, 0, 0); DockPanel.SetDock(chevron, Dock.Right); selectContent.Children.Add(chevron);
        var selectLabel = UI.Line(name, 12.5, entry is null ? TextSecondary : TextPrimary, entry is null ? FontWeights.Normal : FontWeights.SemiBold); selectLabel.VerticalAlignment = VerticalAlignment.Center;
        selectContent.Children.Add(selectLabel);
        select.Content = selectContent; select.Height = 30; select.VerticalAlignment = VerticalAlignment.Center; select.Padding = new Thickness(11, 0, 9, 0); select.Margin = new Thickness(4, 0, 0, 0);
        select.Background = UI.Brush("#0AFFFFFF"); select.BorderBrush = UI.Brush("#1FFFFFFF");
        select.ToolTip = entry is null ? "选择要显示的应用" : AppPresets.Description(entry.Id);
        AutomationProperties.SetName(select, entry is null ? "选择应用" : "应用 " + name);
        // The application menu is a styled popup list: dark surface, rounded corners, no icon column.
        var menu = new Popup { PlacementTarget = select, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var items = new StackPanel { Width = 208 };
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            bool taken = S.Entry(id) is { } existing && existing != entry;
            var item = MenuEntry(AppPresets.Name(id), taken ? "已添加" : "", !taken, id == entry?.Id);
            item.ToolTip = AppPresets.Description(id);
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                if (entry is null) { _pendingRows = Math.Max(0, _pendingRows - 1); Change(s => s.Apps.Insert(Math.Min(index, s.Apps.Count), new AppEntry { Id = id })); }
                else if (id != entry.Id) Change(s => s.Apps[index].Id = id);
                ShowPage(3);
            };
            AutomationProperties.SetName(item, "选择 " + AppPresets.Name(id)); items.Children.Add(item);
        }
        items.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(6, 4, 6, 4) });
        items.Children.Add(MenuEntry("自定义应用…", "后续版本", false, false));
        menu.Child = new Border
        {
            Child = items, Background = UI.Brush("#232326"), BorderBrush = UI.Brush("#1AFFFFFF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = .45 }
        };
        select.Click += (_, _) => menu.IsOpen = !menu.IsOpen;
        Grid.SetColumn(select, 1); row.Children.Add(select);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (entry is not null)
        {
            var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            colors.Children.Add(ColorSwatch(name, "主题", entry.Theme, entry.ThemeColor.Length == 0, value => Change(s => s.Apps[index].ThemeColor = value)));
            colors.Children.Add(ColorSwatch(name, "70%", entry.Warning, entry.WarningColor.Length == 0, value => Change(s => s.Apps[index].WarningColor = value)));
            colors.Children.Add(ColorSwatch(name, "90%", entry.Critical, entry.CriticalColor.Length == 0, value => Change(s => s.Apps[index].CriticalColor = value)));
            Grid.SetColumn(colors, 3); row.Children.Add(colors);
            var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "显示" };
            AutomationProperties.SetName(toggle, "显示 " + name);
            toggle.Checked += (_, _) => Change(s => s.Apps[index].Enabled = true); toggle.Unchecked += (_, _) => Change(s => s.Apps[index].Enabled = false);
            Grid.SetColumn(toggle, 4); row.Children.Add(toggle);
            AttachDrag(handle);
        }
        var remove = ActionButton("", () =>
        {
            if (entry is null) _pendingRows = Math.Max(0, _pendingRows - 1); else Change(s => s.Apps.RemoveAt(index));
            ShowPage(3);
        });
        remove.Content = Stroke("M 0,0 L 8,8 M 8,0 L 0,8", 8, 8, TextSecondary); remove.Width = 28; remove.Height = 26; remove.Padding = new Thickness(0); remove.Margin = new Thickness(10, 0, 0, 0);
        remove.ToolTip = "移除"; AutomationProperties.SetName(remove, "移除 " + name);
        Grid.SetColumn(remove, 5); row.Children.Add(remove);
        return new Border { Child = row, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = entry };
    }

    // One line of the application menu: the name at the left, an optional note at the right.
    private Button MenuEntry(string name, string note, bool enabled, bool current)
    {
        var content = new DockPanel();
        if (note.Length > 0)
        {
            var hint = UI.Line(note, 10.5, TextTertiary); hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(hint, Dock.Right); content.Children.Add(hint);
        }
        var label = UI.Line(name, 12.5, enabled ? TextPrimary : TextSecondary, current ? FontWeights.SemiBold : FontWeights.Normal); label.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(label);
        var item = ActionButton("", () => { }); item.Content = content; item.IsEnabled = enabled;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch; item.Padding = new Thickness(9, 6, 9, 6); item.Margin = new Thickness(0, 1, 0, 1);
        if (current) item.Background = UI.Brush(Inset);
        return item;
    }

    private Button ColorSwatch(string app, string label, string color, bool isDefault, Action<string> apply)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = UI.Brush(color), Stroke = UI.Brush("#29FFFFFF"), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center });
        var text = UI.Line(label, 10.5, TextSecondary); text.Margin = new Thickness(6, 0, 0, 0); text.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(text);
        var button = ActionButton("", () => { }); button.Content = content; button.Padding = new Thickness(7, 4, 8, 4); button.Margin = new Thickness(4, 0, 0, 0);
        button.ToolTip = $"{label}颜色 {color}" + (isDefault ? "（默认）" : "");
        AutomationProperties.SetName(button, $"{app} {label}颜色");
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4, HorizontalOffset = -4 };
        popup.Child = ColorPicker(color, isDefault, value => { popup.IsOpen = false; apply(value); ShowPage(3); });
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        return button;
    }

    // Preset swatches plus a hexadecimal field; "默认" restores the preset value.
    private Border ColorPicker(string current, bool isDefault, Action<string> choose)
    {
        var panel = new StackPanel { Width = 200 };
        var palette = new UniformGrid { Columns = 6 };
        foreach (var color in AppPresets.Palette)
        {
            bool selected = string.Equals(color, current, StringComparison.OrdinalIgnoreCase);
            var swatch = ActionButton("", () => choose(color)); swatch.Padding = new Thickness(0); swatch.Width = 31; swatch.Height = 31;
            swatch.Content = new Ellipse { Width = 20, Height = 20, Fill = UI.Brush(color), Stroke = UI.Brush(selected ? "#FFFFFFFF" : "#29FFFFFF"), StrokeThickness = selected ? 2 : 1 };
            swatch.ToolTip = color; AutomationProperties.SetName(swatch, "颜色 " + color); palette.Children.Add(swatch);
        }
        panel.Children.Add(palette);
        var entry = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var reset = ActionButton("默认", () => choose("")); reset.IsEnabled = !isDefault; reset.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(reset, Dock.Right); entry.Children.Add(reset);
        var field = new StackPanel { Orientation = Orientation.Horizontal };
        var hash = UI.Line("#", 12, TextSecondary); hash.VerticalAlignment = VerticalAlignment.Center; hash.Margin = new Thickness(0, 0, 4, 0); field.Children.Add(hash);
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), Text = current.TrimStart('#'), Width = 72, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, MaxLength = 6 };
        AutomationProperties.SetName(input, "颜色值"); field.Children.Add(input);
        var chip = new Border { Child = field, Height = 28, CornerRadius = new CornerRadius(6), Background = UI.Brush(Inset), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(9, 0, 9, 0) };
        input.GotKeyboardFocus += (_, _) => chip.BorderBrush = UI.Brush(UI.Accent); input.LostKeyboardFocus += (_, _) => chip.BorderBrush = Brushes.Transparent;
        void Commit() { var value = AppPresets.NormalizeColor(input.Text); if (value.Length > 0 && !string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) choose(value); }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        entry.Children.Add(chip); panel.Children.Add(entry);
        return new Border
        {
            Child = panel, Background = UI.Brush("#232326"), BorderBrush = UI.Brush("#1AFFFFFF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = .45 }
        };
    }

    private void UpdateDividers()
    {
        if (_appRows is null) return;
        for (int i = 0; i < _appRows.Children.Count; i++)
            if (_appRows.Children[i] is Border border) border.BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
    }

    // Reordering by dragging the handle: the row follows the pointer between the other rows and the order is saved on release.
    private void AttachDrag(Border handle)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (handle.TemplatedParent is not null) return;
            _dragRow = FindRow(handle); _dragMoved = false;
            if (_dragRow is not null) { handle.CaptureMouse(); e.Handled = true; }
        };
        handle.MouseMove += (_, e) =>
        {
            if (_dragRow is null || !handle.IsMouseCaptured || _appRows is null) return;
            double y = e.GetPosition(_appRows).Y;
            int current = _appRows.Children.IndexOf(_dragRow), target = 0;
            double top = 0;
            for (int i = 0; i < _appRows.Children.Count; i++)
            {
                double height = ((FrameworkElement)_appRows.Children[i]).ActualHeight;
                if (y < top + height / 2) { target = i; break; }
                top += height; target = i + 1;
            }
            target = Math.Clamp(target, 0, Math.Max(0, S.Apps.Count - 1));
            if (target == current) return;
            _appRows.Children.RemoveAt(current); _appRows.Children.Insert(target, _dragRow);
            _dragMoved = true; UpdateDividers();
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragRow is null) return;
            handle.ReleaseMouseCapture(); bool moved = _dragMoved; _dragRow = null; e.Handled = true;
            if (moved) CommitOrder();
        };
    }

    private Border? FindRow(DependencyObject element)
    {
        while (element is not null)
        {
            if (element is Border border && border.Tag is AppEntry) return border;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void CommitOrder()
    {
        if (_appRows is null) return;
        var order = _appRows.Children.OfType<Border>().Select(border => border.Tag as AppEntry).Where(entry => entry is not null).Select(entry => entry!.Id).ToList();
        Change(s => s.Apps = order.Select(id => s.Entry(id)).Where(entry => entry is not null).Select(entry => entry!).ToList());
        ShowPage(3);
    }

    // Test hook: move the application at one position to another, exactly as a drag would.
    internal void MoveApp(int from, int to)
    {
        Change(s => { var entry = s.Apps[from]; s.Apps.RemoveAt(from); s.Apps.Insert(Math.Clamp(to, 0, s.Apps.Count), entry); });
        ShowPage(3);
    }

    private void About()
    {
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 26) }; identity.Children.Add(BrandMark(44));
        var labels = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(UI.Line("BrimDeck", 18, TextPrimary, FontWeights.SemiBold));
        var license = UI.Line("0.1.0  ·  Apache License 2.0", 11, TextSecondary); license.Margin = new Thickness(0, 2, 0, 0); labels.Children.Add(license);
        identity.Children.Add(labels); _content.Children.Add(identity);
        SectionTitle("模型价格");
        var priceStatus = UI.Text("", 11, TextSecondary); priceStatus.Margin = new Thickness(0, 3, 0, 0);
        void UpdatePriceStatus() => priceStatus.Text = _app.Prices.UpdatedAt is { } date
            ? $"{_app.Prices.ModelCount} 个模型  ·  {date.LocalDateTime:MM-dd HH:mm} 更新" + (_app.Prices.LastError is not null || _app.Prices.IsStale ? "  ·  缓存" : "") : _app.Prices.LastError ?? "尚未更新";
        UpdatePriceStatus();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = ActionButton("更新价格", () => { }, true);
        refresh.Click += async (_, _) => { refresh.IsEnabled = false; try { await _app.Prices.RefreshAsync(true, _app.Lifetime.Token); _app.Deck.RenderUsage(); UpdatePriceStatus(); } catch (OperationCanceledException) { } finally { refresh.IsEnabled = true; } };
        refresh.Margin = new Thickness(0, 0, 6, 0); actions.Children.Add(refresh); actions.Children.Add(ActionButton("查看价格 ↗", () => UI.OpenUrl(Pricing.SourcePage)));
        Group(Row("OpenRouter", actions, priceStatus));
    }

    private void SectionTitle(string title, FrameworkElement? action = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); action.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(action); }
        var label = UI.Line(title, 12, TextSecondary, FontWeights.Medium); label.Margin = new Thickness(2, 0, 0, 0); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label); _content.Children.Add(row);
    }

    private void Group(params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(16, 0, 16, 0) });
            panel.Children.Add(row);
        }
        _content.Children.Add(new Border { Child = panel, Background = UI.Brush(Card), BorderBrush = UI.Brush(Outline), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 22) });
    }

    // One line per setting: the label at the left, the control at the right, an optional description underneath the label.
    private static Grid Row(string title, FrameworkElement control, TextBlock? subtitle = null)
    {
        var row = new Grid { MinHeight = 44, Margin = new Thickness(16, 0, 16, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, subtitle is null ? 0 : 10, 12, subtitle is null ? 0 : 10) };
        labels.Children.Add(UI.Line(title, 13, TextPrimary));
        if (subtitle is not null) labels.Children.Add(subtitle);
        row.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }

    private FrameworkElement Toggle(string title, bool value, Action<bool> changed, bool enabled = true)
    {
        var toggle = new CheckBox { Content = UI.Line(title, 13, TextPrimary), Style = (Style)FindResource("SettingsToggle"), IsChecked = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        return new Border { Child = toggle, Padding = new Thickness(16, 0, 16, 0), Height = 44 };
    }

    private FrameworkElement SliderRow(string title, double value, double min, double max, double tick, string unit, Action<double> changed)
    {
        var row = new Grid { Height = 44, Margin = new Thickness(16, 0, 16, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 84 });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = UI.Line(title, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), Text = value.ToString("0", CultureInfo.InvariantCulture), Width = 36, VerticalAlignment = VerticalAlignment.Center };
        var units = UI.Line(unit, 10.5, TextSecondary); units.Margin = new Thickness(5, 0, 0, 0); units.VerticalAlignment = VerticalAlignment.Center;
        var number = new StackPanel { Orientation = Orientation.Horizontal }; number.Children.Add(input); number.Children.Add(units);
        var chip = new Border { Child = number, Height = 26, CornerRadius = new CornerRadius(6), Background = UI.Brush(Inset), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.ToolTip = unit == "DIP" ? "逻辑像素" : null;
        input.GotKeyboardFocus += (_, _) => chip.BorderBrush = UI.Brush(UI.Accent); input.LostKeyboardFocus += (_, _) => chip.BorderBrush = Brushes.Transparent;
        Grid.SetColumn(chip, 2); row.Children.Add(chip);
        var slider = new Slider { Style = (Style)FindResource("SettingsSlider"), Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), SmallChange = tick, LargeChange = tick * 2,
            TickFrequency = tick, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, Margin = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        AutomationProperties.SetName(slider, title); AutomationProperties.SetName(input, title + "数值");
        bool syncing = false;
        slider.ValueChanged += (_, _) => { if (syncing) return; input.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture); changed(slider.Value); };
        void Commit()
        {
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue) && double.IsFinite(numberValue))
            {
                numberValue = Math.Clamp(Math.Round(numberValue), min, max); syncing = true; slider.Value = numberValue; syncing = false; changed(numberValue);
            }
            input.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture);
        }
        input.LostKeyboardFocus += (_, _) => Commit(); input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return row;
    }

    private FrameworkElement BehaviorRow(string title, WindowBehavior selected, Action<WindowBehavior> changed)
    {
        var options = new UniformGrid { Columns = 3, Width = 300 };
        foreach (var behavior in Enum.GetValues<WindowBehavior>())
        {
            var option = new RadioButton { Content = BehaviorName(behavior), Style = (Style)FindResource("SettingsSegment"), GroupName = "Behavior" + title, IsChecked = selected == behavior };
            AutomationProperties.SetName(option, title + " · " + BehaviorName(behavior)); option.Checked += (_, _) => changed(behavior); options.Children.Add(option);
        }
        return Row(title, new Border { Child = options, Background = UI.Brush(Inset), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Margin = new Thickness(0, 7, 0, 7) });
    }
}
