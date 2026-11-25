using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// Drawing shared by the planner page and its fields: text at a fixed line height, stroked icons in a fixed 24 unit box (so they sit
// centred whatever their own bounds), and the theme's colors at a given opacity.
internal static class AgendaDraw
{
    public static TextBlock Words(string text, double size, double line, string color, FontWeight? weight = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(color), FontWeight = weight ?? FontWeights.Normal,
            LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    // A 24 unit drawing shown at size, stroked as the page switch's icons are.
    public static Viewbox Icon(string data, double size, string color, double stroke = 1.65)
    {
        var path = new Path { Data = Geometry.Parse(data), Stroke = UI.Brush(color), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    // The color at opacity times its own.
    public static string Tint(string color, double opacity)
    {
        var c = (Color)ColorConverter.ConvertFromString(color);
        return Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B).ToString(CultureInfo.InvariantCulture);
    }

    // An opaque color share of the way from under to over: the block view's blocks, which the "now" line runs under.
    public static string Mix(string under, string over, double share)
    {
        var a = (Color)ColorConverter.ConvertFromString(under);
        var b = (Color)ColorConverter.ConvertFromString(over);
        byte Part(byte x, byte y) => (byte)Math.Round(x + (y - x) * share);
        return Color.FromRgb(Part(a.R, b.R), Part(a.G, b.G), Part(a.B, b.B)).ToString(CultureInfo.InvariantCulture);
    }

    public static double Width(string text, double size, FontWeight? weight = null)
        => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UI.PanelFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, Brushes.White, 1).WidthIncludingTrailingWhitespace;

    // A button that draws only its content; hover and press are the caller's (paint gets over and down).
    public static Button Bare(object content, string name, Action click, Action<bool, bool>? paint = null)
    {
        var button = new Button
        {
            Content = content, Cursor = Cursors.Hand, Focusable = false, Background = Brushes.Transparent,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        AutomationProperties.SetName(button, name);
        if (paint is not null)
        {
            Feedback.SetOwnHover(button, true);
            bool over = false, down = false;
            button.MouseEnter += (_, _) => paint(over = true, down);
            button.MouseLeave += (_, _) => paint(over = false, down = false);
            button.PreviewMouseLeftButtonDown += (_, _) => paint(over, down = true);
            button.PreviewMouseLeftButtonUp += (_, _) => paint(over, down = false);
        }
        button.Click += (_, e) => { e.Handled = true; click(); };
        button.MouseRightButtonUp += (_, e) => e.Handled = true;
        return button;
    }

    // A face with a fill that changes under the pointer. rest: its fill at rest, or transparent; hover: its fill under the pointer,
    // by default the hover fill on a bare face and the selected fill on a filled one. Pressed, it takes the pressed fill.
    public static Button Face(PanelLook look, FrameworkElement child, string name, Action click, double height, double radius, Thickness padding, string? rest = null, string? hover = null)
    {
        var face = new Border { Height = height, CornerRadius = new CornerRadius(look.Corner(radius)), Padding = padding, Child = child, Background = rest is null ? Brushes.Transparent : UI.Brush(rest) };
        string over = hover ?? (rest is null ? look.Hex("hover") : look.FillSelected);
        var button = Bare(face, name, click, (on, down) =>
            face.Background = down ? UI.Brush(look.Hex("pressed")) : on ? UI.Brush(over) : rest is null ? Brushes.Transparent : UI.Brush(rest));
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .4;
        return button;
    }

    // A small icon button (24 x 20, the icon at 13): the month's arrows, a to-do's actions.
    public static Button IconButton(PanelLook look, string data, string name, Action click, double width = 24, double height = 20, double icon = 13)
    {
        var glyph = Icon(data, icon, look.Secondary);
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        // In the exact middle, not moved to a whole pixel (CentredIcon).
        glyph.UseLayoutRounding = false;
        var grid = new Grid { Width = width, Children = { glyph } };
        var button = Face(look, grid, name, click, height, 6, new Thickness(0));
        button.ToolTip = name;
        return button;
    }

    // A capsule button of the forms (24 high): the fill (over the form's own), or the text color for the main one.
    public static Button Pill(PanelLook look, string text, Action click, bool main = false, bool danger = false)
    {
        string fill = main ? look.Primary : look.Fill;
        var label = Label(text, 12, main ? look.Hex("surface") : danger ? look.Hex("danger") : look.Primary, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var face = new Border { Height = 24, CornerRadius = new CornerRadius(look.Corner(12)), Padding = new Thickness(12, 0, 12, 0), Background = UI.Brush(fill), Child = label };
        label.FitHeight(face, 24);
        var button = Bare(face, text, click, (over, down) =>
        {
            if (main) face.Opacity = down ? .76 : over ? .88 : 1;
            else face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : fill);
        });
        button.IsEnabledChanged += (_, _) => { button.Opacity = button.IsEnabled ? 1 : .4; button.Cursor = button.IsEnabled ? Cursors.Hand : Cursors.Arrow; };
        return button;
    }

    // A button's words, centred top to bottom in the height they are given by their letters as drawn (InkCentredText), whatever
    // the font and display scale. Words centres its line box instead, and the panel font's Chinese sits about 1 DIP low in it.
    // The text keeps the font's own line height: InkCentredText measures the letters in that line.
    public static InkCentredText Label(string text, double size, string color, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(color), FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return new InkCentredText(block) { VerticalAlignment = VerticalAlignment.Stretch };
    }

    // An icon beside a Label: centred top to bottom exactly, not moved to a whole device pixel, so that its middle meets the
    // middle of the label's letters at any display scale. The icons are drawn centred in their 24 unit box.
    public static Viewbox CentredIcon(string data, double size, string color)
    {
        var icon = Icon(data, size, color);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.UseLayoutRounding = false;
        return icon;
    }

    // A label's box is height tall, or one device pixel taller where that leaves the letters exactly in its middle
    // (InkCentredText.FitHeight); words: everything the label may show.
    public static void Fit(Button button, InkCentredText label, double height, params string[] words)
    {
        if (button.Content is FrameworkElement face) label.FitHeight(face, height, words);
    }

    // A segmented switch: a track, and on it the chosen segment's fill. The other segments take no fill under the pointer; their
    // content turns brighter (brighten), so the switch reads as one control. The segments are equally wide.
    //
    // The fill lies under the segments and is placed on the chosen one once they are laid out. from: the segment chosen before,
    // when the switch is drawn anew for another choice (the page draws its head again on each choice); the fill then glides from
    // it to the chosen one, as the panel's page switch does (Soft.Choose, Soft.Follow), unless animate is false.
    public static Border Segments(PanelLook look, IReadOnlyList<Button> segments, int chosen, int? from, bool animate)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var segment in segments) row.Children.Add(segment);
        var shift = new TranslateTransform();
        var fill = new Border { Background = UI.Brush(look.FillSelected), CornerRadius = new CornerRadius(look.Corner(SegmentRadius)), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch, RenderTransform = shift, IsHitTestVisible = false, Visibility = Visibility.Hidden };
        bool placed = false;
        row.SizeChanged += (_, _) => Place();
        foreach (var segment in segments) segment.SizeChanged += (_, _) => Place();
        return new Border { Background = UI.Brush(look.Hex("switchFill")), CornerRadius = new CornerRadius(look.Corner(SegmentRadius + SegmentInset)), Padding = new Thickness(SegmentInset),
            Child = new Grid { Children = { fill, row } }, VerticalAlignment = VerticalAlignment.Center };

        // When the segments are laid out or change size (their words change with the counts): the fill takes the chosen one's place.
        void Place()
        {
            if (chosen < 0 || chosen >= segments.Count || segments[chosen] is not { ActualWidth: > 0 } target) return;
            double x = target.TranslatePoint(new Point(), row).X;
            if (placed && Math.Abs(shift.X - x) < .01 && Math.Abs(fill.Width - target.ActualWidth) < .01) return;
            fill.Width = target.ActualWidth;
            if (!placed && from is int was && was != chosen && was >= 0 && was < segments.Count && animate)
            {
                shift.X = segments[was].TranslatePoint(new Point(), row).X;
                Soft.Glide(shift, TranslateTransform.XProperty, x, true, Soft.Choose);
            }
            else if (!placed || shift.HasAnimatedProperties == false) shift.X = x;
            placed = true;
            fill.Visibility = Visibility.Visible;
        }
    }

    public static Button Segment(PanelLook look, FrameworkElement content, string name, bool on, Action click, Action<bool> brighten, double height, Thickness padding)
    {
        content.HorizontalAlignment = HorizontalAlignment.Center;
        var face = new Border { Height = height, CornerRadius = new CornerRadius(look.Corner(SegmentRadius)), Padding = padding, Child = content, Background = Brushes.Transparent };
        var button = Bare(face, name, on ? () => { } : click, (over, down) =>
        {
            if (on) return;
            brighten(over);
            face.Opacity = down ? .6 : 1;
        });
        if (on) button.Cursor = Cursors.Arrow;
        return button;
    }

    private const double SegmentRadius = 6, SegmentInset = 2;

    public const string Chevron = "m6 9 6 6 6-6", Left = "m15 18-6-6 6-6", Right = "m9 18 6-6-6-6", Plus = "M5 12h14M12 5v14";
}

// The fields of the planner's forms, made to be used on any page of the panel: the planner's event form and to-do rows, and the
// notices page's form for adding a notice to the planner. Each draws from the panel's look and takes the keyboard through the
// panel's own typing (MainWindow.TakeKeyboardOnClick), passed in as keyboard: the panel does not hold the keyboard otherwise.
internal static class AgendaFields
{
    // A text box without a frame of its own: the caller's fill shows through; an accent ring while it has the keyboard when ring.
    public static TextBox Box(PanelLook look, Action<TextBox> keyboard, double size = 12.5, bool ring = true)
    {
        var frame = new FrameworkElementFactory(typeof(Border), "Frame");
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(look.Corner(8)));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        host.SetValue(ScrollViewer.FocusableProperty, false);
        frame.AppendChild(host);
        var box = new LetterBox
        {
            Template = new ControlTemplate(typeof(TextBox)) { VisualTree = frame }, FontFamily = UI.PanelFont, FontSize = size, Foreground = UI.Brush(look.Primary),
            CaretBrush = UI.Brush(look.Primary), SelectionBrush = UI.Brush(look.Accent), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null
        };
        InputMethod.SetIsInputMethodEnabled(box, true);
        if (ring)
        {
            box.BorderThickness = new Thickness(1.5);
            box.GotKeyboardFocus += (_, _) => box.BorderBrush = UI.Brush(look.Accent);
            box.LostKeyboardFocus += (_, _) => box.BorderBrush = Brushes.Transparent;
        }
        keyboard(box);
        return box;
    }

    // A hint for box, laid over it: a box like it (LetterBox, its template, font, padding and frame) that only shows the words, so
    // they are where the typed words will be to the pixel at any display scale. It takes no pointer, focus or keyboard.
    public static TextBox Hint(TextBox box, string text, string color)
    {
        var hint = new LetterBox
        {
            Silent = true, Template = box.Template, Text = text, FontFamily = box.FontFamily, FontSize = box.FontSize, FontWeight = box.FontWeight, Foreground = UI.Brush(color),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = box.BorderThickness, Padding = box.Padding, Margin = box.Margin,
            VerticalContentAlignment = box.VerticalContentAlignment, TextAlignment = box.TextAlignment, IsReadOnly = true, Focusable = false, IsTabStop = false,
            IsHitTestVisible = false, IsUndoEnabled = false, Sample = (box as LetterBox)?.Sample
        };
        return hint;
    }

    // A one-line field with a grey hint (28 high, the fill over the form's own fill): the event's title.
    public static AgendaTextField Text(PanelLook look, Action<TextBox> keyboard, string hint) => new(look, keyboard, hint);

    // A time ("17:30"), typed or stepped by a quarter hour with the arrow keys or the wheel.
    public static AgendaTimeField Time(PanelLook look, Action<TextBox> keyboard, string name, bool endOfDay = false) => new(look, keyboard, name, endOfDay);

    // A day ("11月22日 ⌄") chosen on a month that opens under it.
    public static AgendaDateField Date(PanelLook look, string name) => new(look, name);

    // The panel's check box: a 14 DIP rounded square, filled with a tick when checked.
    public static ToggleButton Check(PanelLook look, bool isChecked, string name)
    {
        var box = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(look.Corner(4)), BorderThickness = new Thickness(1.5) };
        var tick = new Path { Data = Geometry.Parse("M3.6,7.2 L6,9.5 L10.4,4.7"), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stroke = UI.Brush(look.Hex("surface")) };
        var face = new Grid { Width = 14, Height = 14, Background = Brushes.Transparent, Children = { box, new Canvas { Width = 14, Height = 14, Children = { tick } } } };
        var toggle = new ToggleButton { Content = face, Cursor = Cursors.Hand, Focusable = false, IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Top,
            Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) } };
        Feedback.SetOwnHover(toggle, true);
        bool over = false;
        void Paint()
        {
            bool on = toggle.IsChecked == true;
            string edge = over ? look.Secondary : look.Tertiary;
            box.BorderBrush = UI.Brush(edge);
            box.Background = on ? UI.Brush(edge) : Brushes.Transparent;
            tick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        toggle.Checked += (_, _) => Paint(); toggle.Unchecked += (_, _) => Paint();
        toggle.MouseEnter += (_, _) => { over = true; Paint(); }; toggle.MouseLeave += (_, _) => { over = false; Paint(); };
        AutomationProperties.SetName(toggle, name);
        Paint();
        return toggle;
    }
}

// A text box whose one line of letters sits in the middle of its height as drawn, the way the panel's button labels do
// (InkCentredText): a TextBox centres its line box, in which the panel font's Chinese sits about 1 DIP low (2 to 4 device pixels
// at 150% and more). Sample: the letters it centres ("中" for Chinese words, anything else for figures); null leaves the box as
// it is. Its hint takes the same Sample (InkCentredText.Sample), so the hint is where the typed words go.
//
// A text box lays its line out unlike a TextBlock (at 100% its letters came 0.7 pixel lower than a TextBlock's in the same font),
// so the letters are measured in a box like this one: Sample drawn once in it for each font, size and display scale, against the
// top of its line (WPF's TextBoxView). After each layout, as InkCentredLabel does for the settings buttons, this box's line is
// found where it was laid out and moved by whole device pixels to put the letters' middle at the box's.
internal sealed class LetterBox : TextBox
{
    private static readonly Dictionary<(string, string, double, int, TextFormattingMode, double), (double Top, double Bottom)> Spans = [];
    private string? _sample;
    private FrameworkElement? _host, _line;
    private readonly TranslateTransform _shift = new();

    public LetterBox()
    {
        Loaded += (_, _) => { LayoutUpdated -= Recentre; LayoutUpdated += Recentre; };
        Unloaded += (_, _) => LayoutUpdated -= Recentre;
        SizeChanged += Recentre;
    }

    public string? Sample { get => _sample; set { _sample = value; Recentre(null, EventArgs.Empty); } }

    // A box that only shows words (a hint, AgendaFields.Hint): not offered to screen readers, which read the hint as the real box's name.
    public bool Silent { get; init; }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => Silent ? null! : base.OnCreateAutomationPeer();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild("PART_ContentHost") as FrameworkElement;
        _line = null;
        if (_host is not null) _host.RenderTransform = _shift;
    }

    private void Recentre(object? sender, EventArgs e)
    {
        if (_host is null || ActualHeight <= 0) return;
        if (_sample is null) { _shift.Y = 0; return; }
        double pixels = UI.PixelsPerDip(this);
        if (pixels <= 0 || (_line ??= Line(_host)) is not { } line || Template is null) return;
        var (top, bottom) = Span(pixels);
        // Where the line's top is in the box without the shift, and the whole pixels that bring the letters' middle to the box's.
        double at = line.TransformToAncestor(this).Transform(new Point()).Y - _shift.Y;
        double shift = Math.Round((ActualHeight / 2 - (at + (top + bottom) / 2)) * pixels) / pixels;
        if (Math.Abs(shift - _shift.Y) > 1e-6) _shift.Y = shift;
    }

    // Where Sample's letters start and end below the top of the line, in DIP, drawn in a box with this one's template and font.
    private (double Top, double Bottom) Span(double pixels)
    {
        var mode = TextOptions.GetTextFormattingMode(this);
        // The boxes' templates differ only in their corners (AgendaFields.Box), which do not move the line.
        var key = (_sample!, FontFamily.Source, FontSize, FontWeight.ToOpenTypeWeight(), mode, pixels);
        if (Spans.TryGetValue(key, out var span)) return span;
        var probe = new TextBox { Template = Template, Text = _sample, FontFamily = FontFamily, FontSize = FontSize, FontWeight = FontWeight, FontStyle = FontStyle,
            Foreground = Brushes.White, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(probe, mode);
        VisualTreeHelper.SetRootDpi(probe, new DpiScale(pixels, pixels));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        probe.Arrange(new Rect(probe.DesiredSize));
        var (top, bottom) = InkCentredText.Ink(probe, pixels);
        double lineTop = Line(probe) is { } line ? line.TransformToAncestor(probe).Transform(new Point()).Y : 0;
        span = (top - lineTop, bottom - lineTop);
        if (Spans.Count > 32) Spans.Clear();
        Spans[key] = span;
        return span;
    }

    // The element the box draws its lines in (WPF's TextBoxView), whose top is the first line's.
    private static FrameworkElement? Line(DependencyObject node)
    {
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(node); i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement element && child.GetType().Name == "TextBoxView") return element;
            if (Line(child) is { } found) return found;
        }
        return null;
    }
}

// A child (a check box, a due day) set beside the first line of a text of fixed line height (Reference, a TextBlock with
// LineHeight), its middle level with the middle of that line's letters as drawn at the display scale in use, on whole device
// pixels. Laid out from the top of the text's first line. The reference need not be shown: only its font and line are read.
internal sealed class InkLineAligned : Decorator
{
    public InkLineAligned(TextBlock reference, UIElement child) { Reference = reference; Child = child; VerticalAlignment = VerticalAlignment.Top; }

    public TextBlock Reference { get; }

    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(constraint);
        double line = double.IsFinite(Reference.LineHeight) ? Reference.LineHeight : 0;
        return Child is null ? new Size() : new Size(Child.DesiredSize.Width, Math.Max(line, Top() + Child.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size size)
    {
        Child?.Arrange(new Rect(0, Top(), size.Width, Child.DesiredSize.Height));
        return size;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateMeasure();

    // The child's top: its middle at the letters' middle (Chinese letters for Chinese text, capitals otherwise), on a whole pixel.
    private double Top()
    {
        if (Child is null) return 0;
        double pixels = UI.PixelsPerDip(this);
        if (pixels <= 0) return 0;
        var (top, bottom) = InkCentredText.InkSpan(Reference, Reference.Text, pixels, ownLine: true);
        return Math.Max(0, Math.Round(((top + bottom) / 2 - Child.DesiredSize.Height / 2) * pixels) / pixels);
    }
}

// A one-line text field with a hint: AgendaFields.Text.
internal sealed class AgendaTextField : Grid
{
    public TextBox Box { get; }
    private readonly TextBox _hint;
    public event Action? Changed;
    public string Text { get => Box.Text; set => Box.Text = value; }

    public AgendaTextField(PanelLook look, Action<TextBox> keyboard, string hint)
    {
        Height = 28;
        Box = AgendaFields.Box(look, keyboard);
        Box.Background = UI.Brush(look.Fill);
        // The typed words centred by their letters (LetterBox); the hint drawn in a box like it, so it is where they go.
        Box.Padding = new Thickness(7, 0, 7, 0);
        ((LetterBox)Box).Sample = "中";
        AutomationProperties.SetName(Box, hint);
        _hint = AgendaFields.Hint(Box, hint, look.Tertiary);
        Children.Add(Box); Children.Add(_hint);
        Box.TextChanged += (_, _) => { _hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Changed?.Invoke(); };
    }
}

// A time field: AgendaFields.Time. Value is the time of day, null while the text is not a time; with endOfDay, "24:00" is a day.
internal sealed class AgendaTimeField : Grid
{
    public TextBox Box { get; }
    private readonly PanelLook _look;
    private readonly bool _endOfDay;
    private bool _setting;
    public event Action? Changed;
    public TimeSpan? Value { get; private set; }

    public AgendaTimeField(PanelLook look, Action<TextBox> keyboard, string name, bool endOfDay)
    {
        _look = look; _endOfDay = endOfDay;
        Width = 58; Height = 28;
        Box = AgendaFields.Box(look, keyboard);
        Box.Background = UI.Brush(look.Fill);
        Box.TextAlignment = TextAlignment.Center; Box.MaxLength = 5;
        // The figures in the middle of the field by their height as drawn (LetterBox).
        ((LetterBox)Box).Sample = "0";
        Typography.SetNumeralAlignment(Box, FontNumeralAlignment.Tabular);
        InputMethod.SetIsInputMethodEnabled(Box, false);
        AutomationProperties.SetName(Box, name);
        Children.Add(Box);
        Box.TextChanged += (_, _) =>
        {
            Value = Parse(Box.Text, _endOfDay);
            Box.Foreground = UI.Brush(Value is null && Box.Text.Length > 0 ? _look.Hex("danger") : _look.Primary);
            if (!_setting) Changed?.Invoke();
        };
        // Leaving the field writes the time it read in full ("930" becomes "09:30").
        Box.LostKeyboardFocus += (_, _) => { if (Value is { } value) Set(value); };
        Box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down) { Step(e.Key == Key.Up ? 15 : -15); e.Handled = true; }
        };
        Box.PreviewMouseWheel += (_, e) => { if (!Box.IsKeyboardFocusWithin) return; Step(e.Delta > 0 ? 15 : -15); e.Handled = true; };
    }

    public bool IsEnabledField { set { Box.IsEnabled = value; Opacity = value ? 1 : .4; } }

    public void Set(TimeSpan value)
    {
        _setting = true;
        Box.Text = Format(value);
        _setting = false;
        Value = Parse(Box.Text, _endOfDay);
        Changed?.Invoke();
    }

    private void Step(int minutes)
    {
        var at = Value ?? TimeSpan.FromHours(9);
        double total = Math.Floor(at.TotalMinutes / 15) * 15 + (minutes > 0 && at.TotalMinutes % 15 != 0 ? 0 : minutes);
        double max = _endOfDay ? 24 * 60 : 24 * 60 - 15;
        if (total > max) total = 0;
        if (total < 0) total = max;
        Set(TimeSpan.FromMinutes(total));
    }

    public static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";

    // "9", "09", "930", "0930", "9:30", "9.30", "21：30"; 24:00 only as the end of a day.
    public static TimeSpan? Parse(string text, bool endOfDay)
    {
        text = text.Trim().Replace('：', ':').Replace('.', ':');
        int hours, minutes = 0;
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)
                || parts[1].Length is < 1 or > 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return null;
        }
        else
        {
            if (text.Length is < 1 or > 4 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return null;
            if (text.Length <= 2) hours = number; else { hours = number / 100; minutes = number % 100; }
        }
        if (minutes > 59) return null;
        if (hours == 24 && minutes == 0 && endOfDay) return TimeSpan.FromHours(24);
        if (hours > 23) return null;
        return new TimeSpan(hours, minutes, 0);
    }
}

// A day field: AgendaFields.Date. Shows the day without the year (with it when not this year's) and a chevron; a click opens a month.
internal sealed class AgendaDateField : ContentControl
{
    private readonly PanelLook _look;
    private readonly InkCentredText _label;
    private readonly Border _face;
    private DateOnly _date;
    public event Action? Changed;
    // The day "today" is for the year shown and the month's mark.
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);
    // While the month is open, so the panel stays open with the pointer over it (AgendaDatePopup.IsOpen).
    public AgendaDatePopup Popup { get; }

    public AgendaDateField(PanelLook look, string name)
    {
        _look = look;
        // The day and the chevron in the middle of the field, by the day's letters as drawn and the chevron's own middle.
        _label = AgendaDraw.Label("", 12.5, look.Primary);
        var chevron = AgendaDraw.CentredIcon(AgendaDraw.Chevron, 11, look.Tertiary);
        chevron.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _label, chevron } };
        string rest = look.Fill;
        _face = new Border { Height = 28, CornerRadius = new CornerRadius(look.Corner(8)), Padding = new Thickness(10, 0, 8, 0), Background = UI.Brush(rest), Child = row };
        _label.FitHeight(_face, 28);
        Popup = new AgendaDatePopup(look);
        var button = AgendaDraw.Bare(_face, name, () => Popup.Show(this, _date, Today(), day => { Date = day; }), (over, down) =>
            _face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : rest));
        Content = button;
    }

    public DateOnly Date
    {
        get => _date;
        set { bool changed = value != _date; _date = value; _label.Text.Text = AgendaText.Date(value, Today().Year); _label.InvalidateMeasure(); _label.InvalidateArrange(); if (changed) Changed?.Invoke(); }
    }

    public bool IsEnabledField { set { IsEnabled = value; Opacity = value ? 1 : .4; } }
}

// A month to pick a day on, in a pop-up under (or above) what opened it: the field of a form, a to-do's due-day button.
// clear: a line under the month that takes the choice away ("不设截止日").
internal sealed class AgendaDatePopup
{
    private readonly PanelLook _look;
    private readonly Popup _popup;
    private readonly Border _frame;
    private DateOnly _month, _chosen, _today;
    private Action<DateOnly?>? _picked;
    private string? _clear;
    public bool IsOpen => _popup.IsOpen;
    public event Action? Closed;

    public AgendaDatePopup(PanelLook look)
    {
        _look = look;
        _frame = new Border { Background = UI.Brush(look.Hex("popup")), BorderBrush = UI.Brush(look.Hex("popupBorder")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(look.Corner(10)), Padding = new Thickness(8, 6, 8, 8), Width = 224 };
        _popup = new Popup { Child = _frame, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top, VerticalOffset = -4, PopupAnimation = PopupAnimation.None };
        _popup.Closed += (_, _) => Closed?.Invoke();
    }

    public void Show(FrameworkElement target, DateOnly chosen, DateOnly today, Action<DateOnly> picked) => Show(target, chosen, today, day => { if (day is { } d) picked(d); }, null);

    public void Show(FrameworkElement target, DateOnly chosen, DateOnly today, Action<DateOnly?> picked, string? clear)
    {
        _chosen = chosen; _today = today; _picked = picked; _clear = clear;
        _month = new DateOnly(chosen.Year, chosen.Month, 1);
        PanelTheme.Text(_frame, target);
        _popup.PlacementTarget = target;
        Build();
        _popup.IsOpen = true;
    }

    public void Close() => _popup.IsOpen = false;

    private void Build()
    {
        var look = _look;
        var stack = new StackPanel();
        var head = new DockPanel { Height = 24, LastChildFill = true };
        var next = AgendaDraw.IconButton(look, AgendaDraw.Right, Loc.T("下个月", "Next month"), () => { _month = _month.AddMonths(1); Build(); });
        var previous = AgendaDraw.IconButton(look, AgendaDraw.Left, Loc.T("上个月", "Previous month"), () => { _month = _month.AddMonths(-1); Build(); });
        DockPanel.SetDock(next, Dock.Right); DockPanel.SetDock(previous, Dock.Right);
        head.Children.Add(next); head.Children.Add(previous);
        var title = AgendaDraw.Label(AgendaText.Month(_month.Year, _month.Month), 12.5, look.Primary, FontWeights.SemiBold);
        title.Margin = new Thickness(4, 0, 0, 0);
        head.Children.Add(title);
        stack.Children.Add(head);
        var days = new UniformGrid { Columns = 7, Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < 7; i++)
        {
            var name = AgendaDraw.Words(AgendaText.Weekday((DayOfWeek)((i + 1) % 7)), 10.5, 18, look.Tertiary);
            name.TextAlignment = TextAlignment.Center;
            days.Children.Add(name);
        }
        var first = AgendaCalendar.FirstShown(_month.Year, _month.Month);
        for (int i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            bool chosen = day == _chosen, inMonth = day.Month == _month.Month, isToday = day == _today;
            var label = AgendaDraw.Label(day.Day.ToString(CultureInfo.InvariantCulture), 12,
                chosen ? look.Hex("onAccent") : isToday ? look.Accent : inMonth ? look.Primary : look.Tertiary, chosen || isToday ? FontWeights.SemiBold : null);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var picked = day;
            var cell = AgendaDraw.Face(look, label, AgendaText.Date(day, _today.Year), () => Pick(picked), 26, 6, new Thickness(0), chosen ? look.Accent : null, chosen ? look.Accent : null);
            AgendaDraw.Fit(cell, label, 26);
            days.Children.Add(cell);
        }
        stack.Children.Add(days);
        if (_clear is not null)
        {
            var clear = AgendaDraw.Label(_clear, 12, look.Secondary);
            var button = AgendaDraw.Face(look, clear, _clear, () => Pick(null), 26, 6, new Thickness(6, 0, 6, 0));
            AgendaDraw.Fit(button, clear, 26);
            button.Margin = new Thickness(0, 4, 0, 0); button.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(button);
        }
        _frame.Child = stack;
    }

    private void Pick(DateOnly? day)
    {
        var picked = _picked;
        Close();
        picked?.Invoke(day);
    }
}

// A short list to pick one line of, in a pop-up above (or under) what opened it, as wide as its longest line: how an event
// repeats, which times of a repeating event to delete. A tick at the right marks the chosen line, where there is one.
internal sealed class AgendaChoicePopup
{
    public sealed record Choice(string Text, Action Pick, bool Chosen = false);

    private readonly PanelLook _look;
    private readonly Popup _popup;
    private readonly Border _frame;
    public bool IsOpen => _popup.IsOpen;

    public AgendaChoicePopup(PanelLook look)
    {
        _look = look;
        _frame = new Border { Background = UI.Brush(look.Hex("popup")), BorderBrush = UI.Brush(look.Hex("popupBorder")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(look.Corner(10)), Padding = new Thickness(4) };
        _popup = new Popup { Child = _frame, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Top, VerticalOffset = -4, PopupAnimation = PopupAnimation.None };
    }

    public void Show(FrameworkElement target, IReadOnlyList<Choice> choices)
    {
        var look = _look;
        bool ticks = choices.Any(choice => choice.Chosen);
        var stack = new StackPanel();
        foreach (var choice in choices)
        {
            var label = AgendaDraw.Label(choice.Text, 12, look.Primary);
            var row = new Grid { Children = { label } };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (ticks)
            {
                var tick = AgendaDraw.CentredIcon(Tick, 12, choice.Chosen ? look.Accent : "#00000000");
                tick.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(tick, 1);
                row.Children.Add(tick);
            }
            var pick = choice.Pick;
            var button = AgendaDraw.Face(look, row, choice.Text, () => { Close(); pick(); }, 26, 6, new Thickness(8, 0, 8, 0));
            AgendaDraw.Fit(button, label, 26);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ((Border)button.Content).HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(button);
        }
        _frame.Child = stack;
        PanelTheme.Text(_frame, target);
        _popup.PlacementTarget = target;
        _popup.IsOpen = true;
    }

    public void Close() => _popup.IsOpen = false;

    private const string Tick = "M20 6 9 17l-5-5";
}

// How an event repeats: the repeat icon, the rule in words for a series starting on First ("每周二") and a chevron; a click opens
// the rules in an AgendaChoicePopup.
internal sealed class AgendaRepeatField : ContentControl
{
    private static readonly AgendaRepeat[] Rules = [AgendaRepeat.None, AgendaRepeat.Daily, AgendaRepeat.Weekdays, AgendaRepeat.Weekly, AgendaRepeat.Monthly, AgendaRepeat.Yearly];
    private readonly InkCentredText _label;
    private AgendaRepeat _repeat;
    private DateOnly _first;
    public event Action? Changed;
    public AgendaChoicePopup Popup { get; }

    public AgendaRepeatField(PanelLook look)
    {
        var icon = AgendaDraw.CentredIcon(Icon, 12, look.Secondary);
        _label = AgendaDraw.Label("", 12, look.Primary);
        _label.Margin = new Thickness(6, 0, 0, 0);
        var chevron = AgendaDraw.CentredIcon(AgendaDraw.Chevron, 11, look.Tertiary);
        chevron.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, _label, chevron } };
        string rest = look.Fill;
        var face = new Border { Height = 28, CornerRadius = new CornerRadius(look.Corner(8)), Padding = new Thickness(9, 0, 8, 0), Background = UI.Brush(rest), Child = row };
        _label.FitHeight(face, 28);
        Popup = new AgendaChoicePopup(look);
        Content = AgendaDraw.Bare(face, Loc.T("重复", "Repeat"), Open, (over, down) =>
            face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : rest));
        Show();
    }

    public AgendaRepeat Repeat { get => _repeat; set { bool changed = value != _repeat; _repeat = value; Show(); if (changed) Changed?.Invoke(); } }

    // The series' first day, which the weekly, monthly and yearly rules are worded by.
    public DateOnly First { get => _first; set { _first = value; Show(); } }

    private void Show()
    {
        _label.Text.Text = AgendaText.Repeat(_repeat, _first);
        _label.InvalidateMeasure(); _label.InvalidateArrange();
    }

    private void Open() => Popup.Show(this, [.. Rules.Select(rule => new AgendaChoicePopup.Choice(AgendaText.Repeat(rule, _first), () => Repeat = rule, rule == _repeat))]);

    private const string Icon = "m17 2 4 4-4 4M3 11v-1a4 4 0 0 1 4-4h14M7 22l-4-4 4-4M21 13v1a4 4 0 0 1-4 4H3";
}

// Which times of a repeating event "删除" takes: the one opened, that one and the later ones, or all of them.
internal enum AgendaDeleteScope { All, This, Later }

// Lays out two parts on one line, the second at its right end; where they do not fit side by side, the second goes on a line of its
// own under the first, still at the right. The forms' rows: the times on the left, the buttons on the right.
internal sealed class AgendaSplitRow : Panel
{
    public double Gap { get; set; } = 8;
    public double LineGap { get; set; } = 6;
    private bool _split;

    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count < 2) return new Size();
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Children[0].Measure(infinite); Children[1].Measure(infinite);
        Size left = Children[0].DesiredSize, right = Children[1].DesiredSize;
        _split = double.IsFinite(available.Width) && left.Width + Gap + right.Width > available.Width;
        double width = double.IsFinite(available.Width) ? available.Width : left.Width + Gap + right.Width;
        return _split ? new Size(width, left.Height + LineGap + right.Height) : new Size(width, Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count < 2) return final;
        Size left = Children[0].DesiredSize, right = Children[1].DesiredSize;
        if (_split)
        {
            Children[0].Arrange(new Rect(0, 0, Math.Min(left.Width, final.Width), left.Height));
            Children[1].Arrange(new Rect(Math.Max(0, final.Width - right.Width), left.Height + LineGap, right.Width, right.Height));
        }
        else
        {
            double height = Math.Max(left.Height, right.Height);
            Children[0].Arrange(new Rect(0, (height - left.Height) / 2, left.Width, left.Height));
            Children[1].Arrange(new Rect(final.Width - right.Width, (height - right.Height) / 2, right.Width, right.Height));
        }
        return final;
    }
}

// The form for an event (the planner's "添加日程", changing an event, giving a to-do a time; the notices page's "加入日程"): its title
// on one line, then its times, "全天" and the buttons. The start's day is shown only when ShowStartDay (the notices page, an event
// that began on an earlier day than the one shown); otherwise it is StartDay as given. The end's day starts as the start's: another
// day makes the event run over several days. Under the times, how it repeats (for a series, the times are its first time's) and the
// buttons. The main button is available only while the values make an event (AgendaBook.Shape): a title, and an end after the
// start. Enter in the title presses it. "删除" on a form opened from a time of a repeating event (Series) asks which times go.
internal sealed class AgendaEventForm : Border
{
    private readonly PanelLook _look;
    public AgendaTextField Title { get; }
    public AgendaDateField StartDay { get; }
    public AgendaTimeField StartTime { get; }
    public AgendaDateField EndDay { get; }
    public AgendaTimeField EndTime { get; }
    public ToggleButton AllDay { get; }
    public AgendaRepeatField Repeat { get; }
    private readonly Button _submit, _cancel, _delete;
    private readonly AgendaChoicePopup _deleteChoices;
    private readonly FrameworkElement _dash;
    public event Action? Submitted, Cancelled;
    public event Action<AgendaDeleteScope>? Deleted;
    // Set by the page for a form opened from a time of a repeating event: "删除" then asks whether only that time goes, that time
    // and the later ones (offered when the series has a time before it), or all of them.
    public bool Series { get; set; }
    public bool SeriesHasEarlier { get; set; }

    // submit: the main button's words ("添加", "保存", "加入日程"); delete: offer "删除".
    public AgendaEventForm(PanelLook look, Action<TextBox> keyboard, string submit, bool delete = false, bool showStartDay = false)
    {
        _look = look;
        Background = UI.Brush(look.Fill); CornerRadius = new CornerRadius(look.Corner(8)); Padding = new Thickness(8); VerticalAlignment = VerticalAlignment.Top;
        Title = AgendaFields.Text(look, keyboard, Loc.T("标题", "Title"));
        StartDay = AgendaFields.Date(look, Loc.T("开始日期", "Start date"));
        StartTime = AgendaFields.Time(look, keyboard, Loc.T("开始时间", "Start time"));
        EndDay = AgendaFields.Date(look, Loc.T("结束日期", "End date"));
        EndTime = AgendaFields.Time(look, keyboard, Loc.T("结束时间", "End time"), endOfDay: true);
        AllDay = AgendaFields.Check(look, false, Loc.T("全天", "All day"));
        AllDay.VerticalAlignment = VerticalAlignment.Center;
        // The dash between the times, drawn as a short line in the middle of the fields' height, where their figures' middle is.
        _dash = new Rectangle { Width = 6, Height = 1.25, Fill = UI.Brush(look.Tertiary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

        var times = new StackPanel { Orientation = Orientation.Horizontal };
        if (showStartDay) { StartDay.Margin = new Thickness(0, 0, 6, 0); times.Children.Add(StartDay); }
        times.Children.Add(StartTime); times.Children.Add(_dash);
        EndDay.Margin = new Thickness(0, 0, 6, 0);
        times.Children.Add(EndDay); times.Children.Add(EndTime);
        // As tall as the fields, the box and "全天" in its middle: the box by its own, the words by their letters as drawn.
        var allDayLabel = AgendaDraw.Label(Loc.T("全天", "All day"), 11.5, look.Secondary);
        allDayLabel.Margin = new Thickness(6, 0, 0, 0);
        var allDayRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 28, Margin = new Thickness(12, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { AllDay, allDayLabel } };
        allDayRow.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && IsWithin(source, AllDay)) return; AllDay.IsChecked = AllDay.IsChecked != true; e.Handled = true; };
        times.Children.Add(allDayRow);

        _deleteChoices = new AgendaChoicePopup(look);
        _delete = AgendaDraw.Pill(look, Loc.T("删除", "Delete"), Delete, danger: true);
        _cancel = AgendaDraw.Pill(look, Loc.T("取消", "Cancel"), () => Cancelled?.Invoke());
        _submit = AgendaDraw.Pill(look, submit, Submit, main: true);
        _delete.Margin = new Thickness(0, 0, 6, 0); _cancel.Margin = new Thickness(0, 0, 6, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (delete) buttons.Children.Add(_delete);
        buttons.Children.Add(_cancel); buttons.Children.Add(_submit);

        Repeat = new AgendaRepeatField(look) { VerticalAlignment = VerticalAlignment.Center };
        times.Margin = new Thickness(0, 6, 0, 0);
        var foot = new AgendaSplitRow { Margin = new Thickness(0, 6, 0, 0), Children = { Repeat, buttons } };
        Child = new StackPanel { Children = { Title, times, foot } };

        Title.Changed += Check; StartTime.Changed += Check; EndTime.Changed += Check; EndDay.Changed += Check;
        StartDay.Changed += () => { Repeat.First = StartDay.Date; Check(); };
        AllDay.Checked += (_, _) => AllDayChanged(); AllDay.Unchecked += (_, _) => AllDayChanged();
        Title.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        StartTime.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        EndTime.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var at = node; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, ancestor)) return true;
        return false;
    }

    // The day "today" is, for the years the day fields write and their months' marks.
    public Func<DateOnly> Today { set { StartDay.Today = value; EndDay.Today = value; } }

    // Whether a pop-up of the form is open (a month, the repeat rules, the delete choices): the panel stays open while it is.
    public bool PopupOpen => StartDay.Popup.IsOpen || EndDay.Popup.IsOpen || Repeat.Popup.IsOpen || _deleteChoices.IsOpen;
    public void ClosePopups() { StartDay.Popup.Close(); EndDay.Popup.Close(); Repeat.Popup.Close(); _deleteChoices.Close(); }

    private void Delete()
    {
        if (!Series) { Deleted?.Invoke(AgendaDeleteScope.All); return; }
        var choices = new List<AgendaChoicePopup.Choice> { new(Loc.T("只删除这一次", "This one only"), () => Deleted?.Invoke(AgendaDeleteScope.This)) };
        if (SeriesHasEarlier) choices.Add(new(Loc.T("删除这一次及以后", "This one and later ones"), () => Deleted?.Invoke(AgendaDeleteScope.Later)));
        choices.Add(new(Loc.T("删除全部", "All of them"), () => Deleted?.Invoke(AgendaDeleteScope.All)));
        _deleteChoices.Show(_delete, choices);
    }

    // Puts the values in: an all-day event's end is its last day; a timed one ending at 00:00 ends at 24:00 of the day before. For a
    // series, its first time and how it repeats.
    public void Fill(string title, DateTime start, DateTime end, bool allDay) => Fill(title, start, end, allDay, AgendaRepeat.None);
    public void Fill(string title, DateTime start, DateTime end, bool allDay, AgendaRepeat repeat)
    {
        Title.Text = title;
        StartDay.Date = DateOnly.FromDateTime(start);
        Repeat.First = StartDay.Date;
        Repeat.Repeat = repeat;
        StartTime.Set(start.TimeOfDay);
        if (allDay)
        {
            EndDay.Date = DateOnly.FromDateTime(end);
            var timedEnd = start.AddHours(1);
            EndTime.Set(timedEnd.Date > start.Date ? TimeSpan.FromHours(24) : timedEnd.TimeOfDay);
        }
        else if (end.TimeOfDay == TimeSpan.Zero && end > start)
        {
            EndDay.Date = DateOnly.FromDateTime(end.AddDays(-1));
            EndTime.Set(TimeSpan.FromHours(24));
        }
        else
        {
            EndDay.Date = DateOnly.FromDateTime(end);
            EndTime.Set(end.TimeOfDay);
        }
        AllDay.IsChecked = allDay;
        AllDayChanged();
    }

    // The event the form describes, or null while it cannot be one.
    public (string Title, DateTime Start, DateTime End, bool AllDay)? Value
    {
        get
        {
            string title = Title.Text.Trim();
            if (title.Length == 0) return null;
            bool allDay = AllDay.IsChecked == true;
            if (allDay)
            {
                if (EndDay.Date < StartDay.Date) return null;
                return (title, AgendaTime.Midnight(StartDay.Date), AgendaTime.Midnight(EndDay.Date), true);
            }
            if (StartTime.Value is not { } from || EndTime.Value is not { } to || from >= TimeSpan.FromHours(24)) return null;
            var start = AgendaTime.Midnight(StartDay.Date) + from;
            var end = AgendaTime.Midnight(EndDay.Date) + to;
            return end > start ? (title, start, end, false) : null;
        }
    }

    private void AllDayChanged()
    {
        bool allDay = AllDay.IsChecked == true;
        StartTime.IsEnabledField = !allDay; EndTime.IsEnabledField = !allDay;
        Check();
    }

    private void Check() => _submit.IsEnabled = Value is not null;

    private void Submit() { if (Value is not null) Submitted?.Invoke(); }

    // The title takes the keyboard with its text selected (a new form, or one opened to change an event).
    public void FocusTitle(Action<Control> begin) { begin(Title.Box); Title.Box.Dispatcher.BeginInvoke(() => Title.Box.SelectAll(), System.Windows.Threading.DispatcherPriority.Input); }
}
