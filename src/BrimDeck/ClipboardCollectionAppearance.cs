using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using Path = System.Windows.Shapes.Path;

namespace BrimDeck;

// An empty tint deliberately leaves the host's existing style alone. Explicit backgrounds use contrast-safe ink in any theme.
internal static class ClipboardCollectionVisuals
{
    internal static readonly DependencyProperty ColorProperty = DependencyProperty.RegisterAttached("Color", typeof(string), typeof(ClipboardCollectionVisuals), new PropertyMetadata("", ColorChanged));
    private static readonly DependencyPropertyKey HasColorKey = DependencyProperty.RegisterAttachedReadOnly("HasColor", typeof(bool), typeof(ClipboardCollectionVisuals), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey FillKey = DependencyProperty.RegisterAttachedReadOnly("Fill", typeof(Brush), typeof(ClipboardCollectionVisuals), new PropertyMetadata(null));
    private static readonly DependencyPropertyKey InkKey = DependencyProperty.RegisterAttachedReadOnly("Ink", typeof(Brush), typeof(ClipboardCollectionVisuals), new PropertyMetadata(null));
    public static readonly DependencyProperty HasColorProperty = HasColorKey.DependencyProperty, FillProperty = FillKey.DependencyProperty, InkProperty = InkKey.DependencyProperty;
    public static string GetColor(DependencyObject target) => (string)target.GetValue(ColorProperty);
    public static void SetColor(DependencyObject target, string value) => target.SetValue(ColorProperty, value);
    public static bool GetHasColor(DependencyObject target) => (bool)target.GetValue(HasColorProperty);
    public static Brush? GetFill(DependencyObject target) => (Brush?)target.GetValue(FillProperty);
    public static Brush? GetInk(DependencyObject target) => (Brush?)target.GetValue(InkProperty);
    private static void ColorChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        string color = ClipboardCollection.NormalizeColor(args.NewValue as string);
        bool custom = color.Length > 0;
        var fill = custom ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) : null;
        fill?.Freeze();
        target.SetValue(FillKey, fill); target.SetValue(InkKey, fill is not null ? Ink(fill.Color) : null); target.SetValue(HasColorKey, custom);
    }
    internal static Brush Ink(Color color)
    {
        static double Linear(byte component) { double c = component / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Brushes.Black : Brushes.White;
    }
    internal static void Apply(Button button, ClipboardCollection collection, Panel label)
    {
        SetColor(button, collection.BackgroundColor);
        if (GetHasColor(button))
            foreach (var text in label.Children.OfType<TextBlock>())
            {
                text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
                text.Opacity = 1;
            }
        if (Icon(collection.Icon) is not { } icon) return;
        icon.Margin = new Thickness(0, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        label.Children.Insert(label is DockPanel ? Math.Max(0, label.Children.Count - 1) : 0, icon);
    }
    internal static Path? Icon(string id, double size = 13)
    {
        string data = id switch
        {
            "folder" => "M2,6 Q2,4 4,4 H9 L12,7 H20 Q22,7 22,9 V18 Q22,20 20,20 H4 Q2,20 2,18 Z",
            "star" => "M12,2 L15,8 L22,9 L17,14 L18,21 L12,18 L6,21 L7,14 L2,9 L9,8 Z",
            "heart" => "M12,21 L3,12 C-3,3 7,-2 12,6 C17,-2 27,3 21,12 Z",
            "bookmark" => "M5,3 H19 V22 L12,17 L5,22 Z",
            "sparkles" => "M12,2 L15,9 L22,12 L15,15 L12,22 L9,15 L2,12 L9,9 Z M3,2 V6 M1,4 H5",
            "code" => "M8,5 L2,12 L8,19 M16,5 L22,12 L16,19 M14,3 L10,21",
            "image" => "M3,3 H21 V21 H3 Z M3,17 L9,11 L14,16 L17,13 L21,17 M17,7 A1,1 0 1 1 15,7 A1,1 0 1 1 17,7",
            "link" => "M9,15 L15,9 M8,10 L5,13 C0,18 6,24 11,19 L14,16 M10,8 L13,5 C18,0 24,6 19,11 L16,14",
            "palette" => "M21,12 C21,5 16,2 11,3 C3,3 0,13 5,18 C8,22 13,21 12,17 C11,14 17,18 20,15 Z M8,8 H8.1 M13,7 H13.1 M17,11 H17.1 M6,13 H6.1",
            "briefcase" => "M3,7 H21 V21 H3 Z M8,7 V3 H16 V7 M3,12 Q12,17 21,12 M10,13 V16 H14 V13",
            "note" => "M4,2 H15 L21,8 V22 H4 Z M15,2 V8 H21 M8,12 H17 M8,16 H17",
            "bolt" => "M14,2 L4,14 H11 L10,22 L21,9 H13 Z",
            _ => ""
        };
        if (data.Length == 0) return null;
        var path = new Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.SetBinding(Path.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return path;
    }
    internal static (string Id, string Name)[] Icons() => [("", Loc.T("无图标", "No icon")), ("folder", Loc.T("文件夹", "Folder")), ("star", Loc.T("星标", "Star")),
        ("heart", Loc.T("爱心", "Heart")), ("bookmark", Loc.T("书签", "Bookmark")), ("sparkles", Loc.T("灵感", "Sparkles")), ("code", Loc.T("代码", "Code")),
        ("image", Loc.T("图片", "Image")), ("link", Loc.T("链接", "Link")), ("palette", Loc.T("调色板", "Palette")), ("briefcase", Loc.T("工作", "Work")),
        ("note", Loc.T("笔记", "Note")), ("bolt", Loc.T("闪电", "Bolt"))];
}

// Name, color and icon share one draft; browsing, dismissal and cancellation never publish history changes.
internal sealed class ClipboardCollectionAppearance : StackPanel
{
    private readonly ClipboardHistory _history;
    private readonly string _id;
    private readonly Action _close;
    private readonly TextBox _name, _hex;
    private readonly TextBlock _message;
    private readonly Button _preview, _default;
    private readonly ClipboardCollectionColorRibbon _ribbon;
    private readonly List<(Button Button, string Value)> _icons = [];
    private string _icon;
    private ClipboardCollectionAppearance(ClipboardHistory history, ClipboardCollection collection, Action close)
    {
        _history = history; _id = collection.Id; _icon = collection.Icon; _close = close; Width = 272;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ClipboardCollectionAppearance).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
        AutomationProperties.SetName(this, Loc.T("名称与标签外观", "Name & label appearance"));
        _name = new TextBox { Style = (Style)FindResource("LibraryField"), Text = collection.Name, MaxLength = 32, Height = 36, Padding = new Thickness(10, 6, 10, 6), Tag = Loc.T("名称", "Name"), BorderThickness = new Thickness(0) };
        AutomationProperties.SetAutomationId(_name, "clipboard-appearance-name"); AutomationProperties.SetName(_name, Loc.T("收藏夹名称", "Collection name")); Children.Add(_name);
        _ribbon = new ClipboardCollectionColorRibbon { Margin = new Thickness(0, 14, 0, 6) }; Children.Add(_ribbon);
        var details = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 10) };
        AutomationProperties.SetAutomationId(details, "clipboard-appearance-details"); Children.Add(details);
        _hex = new TextBox { Style = (Style)FindResource("LibraryField"), MaxLength = 7, Text = collection.BackgroundColor, Tag = "#RRGGBB", ToolTip = Loc.T("自定义背景色，留空使用默认样式", "Custom background color; leave empty for the default style"), MinHeight = 30 };
        AutomationProperties.SetAutomationId(_hex, "clipboard-appearance-color"); AutomationProperties.SetName(_hex, (string)_hex.ToolTip); details.Children.Add(_hex);
        var icons = new WrapPanel { Margin = new Thickness(-2, 8, -2, 8) }; details.Children.Add(icons);
        foreach (var (id, label) in ClipboardCollectionVisuals.Icons())
        {
            var button = Button(ClipboardCollectionVisuals.Icon(id, 14) is { } icon ? icon : Loc.T("无", "–"), label, () => { _icon = id; UpdatePreview(); });
            button.Width = 30; button.Padding = new Thickness(5, 0, 5, 0);
            AutomationProperties.SetAutomationId(button, "clipboard-appearance-icon-" + (id.Length == 0 ? "none" : id));
            _icons.Add((button, id)); icons.Children.Add(button);
        }
        var previewRow = new DockPanel(); details.Children.Add(previewRow);
        var reset = Button(Loc.T("恢复默认", "Reset"), Loc.T("恢复默认颜色和无图标，保留名称", "Reset color and icon, keep the name"), () => { _icon = ""; _hex.Clear(); UpdatePreview(); });
        AutomationProperties.SetAutomationId(reset, "clipboard-appearance-reset"); DockPanel.SetDock(reset, Dock.Right); previewRow.Children.Add(reset);
        _preview = new Button { Style = (Style)FindResource("ClipboardTab"), Height = 28, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Focusable = false, MaxWidth = 164, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_preview, "clipboard-appearance-preview"); previewRow.Children.Add(_preview);
        _message = ClipboardPreview.Text("", 10.5, "PanelSecondary"); _message.Margin = new Thickness(0, 0, 0, 8); _message.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite); AutomationProperties.SetAutomationId(_message, "clipboard-appearance-message"); Children.Add(_message);
        var actions = new DockPanel(); Children.Add(actions);
        var options = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(options, Dock.Left); actions.Children.Add(options);
        _default = Button(Loc.T("默认", "Default"), Loc.T("使用当前主题的默认标签背景", "Use the current theme's default label background"), () => { _hex.Clear(); UpdatePreview(); });
        AutomationProperties.SetAutomationId(_default, "clipboard-appearance-color-default"); options.Children.Add(_default);
        var more = Button("⋯", Loc.T("图标与自定义颜色", "Icon & custom color"), () => { details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; });
        AutomationProperties.SetAutomationId(more, "clipboard-appearance-more"); more.Width = 28; options.Children.Add(more);
        var commit = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(commit);
        var cancel = Button(Loc.T("取消", "Cancel"), Loc.T("取消", "Cancel"), close); AutomationProperties.SetAutomationId(cancel, "clipboard-appearance-cancel"); commit.Children.Add(cancel);
        var save = Button(Loc.T("保存", "Save"), Loc.T("保存名称与外观", "Save name and appearance"), Save); save.Style = (Style)FindResource("LibraryPrimaryButton"); AutomationProperties.SetAutomationId(save, "clipboard-appearance-save"); commit.Children.Add(save);
        _ribbon.Chosen += color => _hex.Text = color;
        _hex.TextChanged += (_, _) => UpdatePreview(); _name.TextChanged += (_, _) => UpdatePreview();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
            if (e.Key == Key.Escape) { close(); e.Handled = true; }
            else if (e.Key == Key.Enter && (e.OriginalSource == _hex || e.OriginalSource == _name)) { Save(); e.Handled = true; }
        };
        UpdatePreview();
    }
    private Button Button(object content, string label, Action action)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("LibraryButton"), Height = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(2), ToolTip = label };
        AutomationProperties.SetName(button, label); button.Click += (_, e) => { action(); e.Handled = true; }; return button;
    }
    private void Message(string text) { _message.Text = text; _message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void UpdatePreview()
    {
        string color = ClipboardCollection.NormalizeColor(_hex.Text);
        bool valid = _hex.Text.Trim().Length == 0 || color.Length > 0;
        Message(valid ? "" : Loc.T("请输入 #RGB 或 #RRGGBB，留空恢复默认。", "Enter #RGB or #RRGGBB, or leave empty for Default."));
        foreach (var (button, value) in _icons) { bool selected = value == _icon; button.Tag = selected ? "Selected" : null; button.SetResourceReference(Control.BorderBrushProperty, selected ? "PanelAccent" : "PanelPopupBorder"); }
        _default.Tag = valid && color.Length == 0 ? "Selected" : null;
        if (!valid) return;
        _ribbon.SetColor(color);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = _name.Text.Trim(), FontSize = 11, FontWeight = FontWeights.Medium, MaxWidth = 112, TextTrimming = TextTrimming.CharacterEllipsis });
        label.Children.Add(new TextBlock { Text = _history.Query(collectionId: _id).Count().ToString(), FontSize = 10.5, Margin = new Thickness(5, 0, 0, 0), Opacity = .65 });
        _preview.Content = label; ClipboardCollectionVisuals.Apply(_preview, new(_id, _name.Text.Trim()) { BackgroundColor = color, Icon = _icon }, label);
    }
    private void Save()
    {
        if (_history.UpdateCollection(_id, _name.Text, _hex.Text, _icon, out string problem)) _close();
        else Message(problem);
    }
    internal static Popup? Open(FrameworkElement anchor, ClipboardHistory history, string id, double scale = 1, Action? opened = null, Action? closed = null)
    {
        if (history.Collections.FirstOrDefault(group => group.Id == id) is not { } collection) return null;
        // The stable owner survives category refresh/removal; the anchor's screen position preserves local placement.
        var owner = Window.GetWindow(anchor);
        Point position = owner is not null ? anchor.TranslatePoint(new Point(0, anchor.ActualHeight), owner) : default;
        var popup = new Popup { PlacementTarget = (UIElement?)owner ?? anchor, Placement = owner is not null ? PlacementMode.Relative : PlacementMode.Bottom,
            HorizontalOffset = owner is not null ? position.X : 0, VerticalOffset = (owner is not null ? position.Y : 0) + 6,
            StaysOpen = false, AllowsTransparency = true };
        var editor = new ClipboardCollectionAppearance(history, collection, () => popup.IsOpen = false);
        // The caller supplies presentation scale; WPF applies monitor DPI. Extra options scroll on small work areas.
        var work = Native.WindowsHost.WorkAreaAt(anchor.PointToScreen(new Point()));
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            MaxHeight = Math.Max(80, work.Height / (VisualTreeHelper.GetDpi(anchor).DpiScaleY * scale) - 40) };
        var chrome = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(14), Child = scroll, LayoutTransform = new ScaleTransform(scale, scale) };
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR16");
        popup.Child = chrome; PopupMotion.Attach(popup);
        popup.Opened += (_, _) => { opened?.Invoke(); editor._name.Focus(); editor._name.SelectAll(); };
        popup.Closed += (_, _) => closed?.Invoke();
        popup.IsOpen = true; return popup;
    }
}
