using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BrimDeck.Core;

namespace BrimDeck;

// Browsing the ribbon never changes the draft: only a click, Enter or an automation invocation chooses a swatch.
internal sealed class ClipboardCollectionColorRibbon : Grid
{
    private const double CellWidth = 30, StripHeight = 36;
    private static readonly string[] Palette = CreatePalette();
    private readonly ScrollViewer _scroll;
    private readonly Border _strip, _selection;
    private readonly Canvas _overlay;
    private double _pressX, _pressOffset;
    private bool _pressed, _dragged;
    private int _selected = -1;
    private string _color = "";
    internal event Action<string>? Chosen;
    internal string SelectedColor => _color;

    internal ClipboardCollectionColorRibbon()
    {
        Height = 52; Background = Brushes.Transparent; Focusable = true; Cursor = Cursors.Hand;
        AutomationProperties.SetAutomationId(this, "clipboard-appearance-ribbon");
        AutomationProperties.SetName(this, Loc.T("标签背景色带", "Label background color ribbon"));
        AutomationProperties.SetHelpText(this, Loc.T("左右拖动浏览颜色，点击选择。方向键浏览，Enter 选色，Home／End 到两端。", "Drag horizontally to browse; click to choose. Arrow keys browse, Enter selects, Home/End reach the ends."));
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string color in Palette)
        {
            var swatch = new Button { Width = CellWidth, Height = StripHeight, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false, ToolTip = color };
            var template = new ControlTemplate(typeof(Button));
            var fill = new FrameworkElementFactory(typeof(Border)); fill.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            template.VisualTree = fill; swatch.Template = template;
            AutomationProperties.SetAutomationId(swatch, "clipboard-appearance-swatch-" + color[1..]); AutomationProperties.SetName(swatch, color);
            swatch.Click += (_, e) => { Choose(color); e.Handled = true; }; colors.Children.Add(swatch);
        }
        _scroll = new ScrollViewer { Content = colors, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, PanningMode = PanningMode.HorizontalOnly };
        _strip = new Border { Height = StripHeight, Child = _scroll, VerticalAlignment = VerticalAlignment.Center };
        _strip.SetResourceReference(Border.CornerRadiusProperty, "PanelR12"); Children.Add(_strip);
        _selection = new Border { Width = 34, Height = 50, BorderThickness = new Thickness(2), BorderBrush = Brushes.White, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 2, Opacity = .3, Color = Colors.Black } };
        _selection.SetResourceReference(Border.CornerRadiusProperty, "PanelR12");
        _overlay = new Canvas { IsHitTestVisible = false }; _overlay.Children.Add(_selection); Children.Add(_overlay);
        _scroll.ScrollChanged += (_, _) => PositionSelection();
        SizeChanged += (_, _) => PositionSelection();
        LayoutUpdated += (_, _) =>
        {
            var bounds = new Rect(0, 0, _scroll.ActualWidth, StripHeight); double radius = _strip.CornerRadius.TopLeft;
            if (_scroll.Clip is not RectangleGeometry clip || clip.Rect != bounds || clip.RadiusX != radius) _scroll.Clip = new RectangleGeometry(bounds, radius, radius);
        };
        Loaded += (_, _) => CenterSelection();
    }

    internal void SetColor(string value)
    {
        string color = ClipboardCollection.NormalizeColor(value);
        if (_color == color) return;
        _color = color;
        _selected = color.Length == 0 ? -1 : Enumerable.Range(0, Palette.Length).MinBy(index => ColorMath.Distance(Palette[index], color));
        _selection.Background = color.Length == 0 ? null : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        AutomationProperties.SetItemStatus(this, color.Length == 0 ? Loc.T("默认", "Default") : color);
        CenterSelection();
    }
    private void CenterSelection()
    {
        _scroll.ScrollToHorizontalOffset(Math.Max(0, (_selected < 0 ? 13.5 : _selected + .5) * CellWidth - ActualWidth / 2)); PositionSelection();
    }
    private void PositionSelection()
    {
        double center = (_selected + .5) * CellWidth - _scroll.HorizontalOffset;
        _selection.Visibility = _selected >= 0 && center >= 17 && center <= ActualWidth - 17 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_selection, center - 17); Canvas.SetTop(_selection, 1);
    }
    private void Choose(string color) { SetColor(color); Chosen?.Invoke(color); }
    private void ChooseAt(double x)
    {
        int index = Math.Clamp((int)Math.Floor((_scroll.HorizontalOffset + Math.Clamp(x, 0, Math.Max(0, ActualWidth - .01))) / CellWidth), 0, Palette.Length - 1);
        Choose(Palette[index]);
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.Handled) return;
        Focus(); _pressX = e.GetPosition(this).X; _pressOffset = _scroll.HorizontalOffset; _dragged = false; _pressed = CaptureMouse(); e.Handled = true;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_pressed || !IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _pressed = false; ReleaseMouseCapture(); return; }
        double delta = e.GetPosition(this).X - _pressX;
        if (Math.Abs(delta) >= SystemParameters.MinimumHorizontalDragDistance) _dragged = true;
        if (_dragged) _scroll.ScrollToHorizontalOffset(Math.Clamp(_pressOffset - delta, 0, _scroll.ScrollableWidth));
        e.Handled = true;
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_pressed) return;
        double x = e.GetPosition(this).X; bool choose = !_dragged && Math.Abs(x - _pressX) < SystemParameters.MinimumHorizontalDragDistance && x >= 0 && x <= ActualWidth && e.GetPosition(this).Y is >= 0 and <= 52;
        _pressed = false; ReleaseMouseCapture(); if (choose) ChooseAt(x); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _pressed = false; }
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        _scroll.ScrollToHorizontalOffset(Math.Clamp(_scroll.HorizontalOffset - e.Delta / 120d * CellWidth * 3, 0, _scroll.ScrollableWidth)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        double offset = _scroll.HorizontalOffset;
        switch (e.Key)
        {
            case Key.Left: offset -= CellWidth; break;
            case Key.Right: offset += CellWidth; break;
            case Key.PageUp: offset -= ActualWidth; break;
            case Key.PageDown: offset += ActualWidth; break;
            case Key.Home: offset = 0; break;
            case Key.End: offset = _scroll.ScrollableWidth; break;
            case Key.Enter: case Key.Space: ChooseAt(ActualWidth / 2); e.Handled = true; return;
            default: return;
        }
        _scroll.ScrollToHorizontalOffset(Math.Clamp(offset, 0, _scroll.ScrollableWidth)); e.Handled = true;
    }
    private static string[] CreatePalette()
    {
        // Each hue family runs from pale to vivid to dark, followed by neutral colors; existing custom HEX remains exact.
        var colors = new List<string>();
        (double Saturation, double Value)[] shades = [(.16, 1), (.3, 1), (.46, 1), (.65, 1), (.83, 1), (1, 1), (1, .8), (.95, .64), (.9, .46)];
        for (int hue = 0; hue < 360; hue += 30)
            foreach (var (s, v) in shades) { var (r, g, b) = ColorMath.FromHsv(hue, s, v); colors.Add(ColorMath.ToHex(r, g, b)); }
        for (int i = 0; i <= 10; i++) { byte gray = (byte)Math.Round(255 * (1 - i / 10d)); colors.Add(ColorMath.ToHex(gray, gray, gray)); }
        return colors.Distinct(StringComparer.Ordinal).ToArray();
    }
}
