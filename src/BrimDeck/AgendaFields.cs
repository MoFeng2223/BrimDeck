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
    private static readonly Dictionary<(string, string, double, int, TextFormattingMode, double, double, VerticalAlignment, Thickness, Thickness), (double Top, double Bottom)> Spans = [];
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
        // Where none does (an odd number of pixels left above and below them), they go half a pixel up rather than down: rounded to
        // even, the time fields' figures sat half a pixel low at some scales and high at others.
        double at = line.TransformToAncestor(this).Transform(new Point()).Y - _shift.Y;
        double shift = Math.Ceiling((ActualHeight / 2 - (at + (top + bottom) / 2)) * pixels - .5) / pixels;
        if (Math.Abs(shift - _shift.Y) > 1e-6) _shift.Y = shift;
    }

    // Where Sample's letters start and end below the top of the line, in DIP, drawn in a box with this one's template and font, as
    // tall as this one and its line placed as this one's is (the same frame, padding and alignment). Where the line is centred on
    // half a pixel (a 17 high line in a 28 high field at 100%), layout rounding moves the line a whole pixel but its letters are
    // drawn from where it would have been, a pixel lower in it than in a box only as tall as its line: measured so, the time
    // fields' figures stayed 1.5 pixels low.
    private (double Top, double Bottom) Span(double pixels)
    {
        var mode = TextOptions.GetTextFormattingMode(this);
        // The boxes' templates differ only in their corners (AgendaFields.Box), which do not move the line.
        var key = (_sample!, FontFamily.Source, FontSize, FontWeight.ToOpenTypeWeight(), mode, pixels, ActualHeight, VerticalContentAlignment, BorderThickness, Padding);
        if (Spans.TryGetValue(key, out var span)) return span;
        var probe = new TextBox { Template = Template, Text = _sample, FontFamily = FontFamily, FontSize = FontSize, FontWeight = FontWeight, FontStyle = FontStyle,
            Foreground = Brushes.White, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = BorderThickness, Padding = Padding,
            Height = ActualHeight, VerticalContentAlignment = VerticalContentAlignment, UseLayoutRounding = true };
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
    private readonly OpenerPopup _popup;
    private readonly Border _frame;
    private DateOnly _month, _chosen, _today;
    private Action<DateOnly?>? _picked;
    private string? _clear;
    // Open and not leaving: what holds the panel open, and what Esc closes first.
    public bool IsOpen => _popup.Showing;
    public event Action? Closed;

    public AgendaDatePopup(PanelLook look)
    {
        _look = look;
        _frame = new Border { Background = UI.Brush(look.Hex("popup")), BorderBrush = UI.Brush(look.Hex("popupBorder")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(look.Corner(10)), Padding = new Thickness(8, 6, 8, 8), Width = 224 };
        _popup = new OpenerPopup { Child = _frame };
        _popup.Closed += (_, _) => Closed?.Invoke();
    }

    public void Show(FrameworkElement target, DateOnly chosen, DateOnly today, Action<DateOnly> picked) => Show(target, chosen, today, day => { if (day is { } d) picked(d); }, null);

    // Shown from target, or closed when it is already open from there (a second click on the field).
    public void Show(FrameworkElement target, DateOnly chosen, DateOnly today, Action<DateOnly?> picked, string? clear)
    {
        if (_popup.Showing && ReferenceEquals(_popup.PlacementTarget, target)) { _popup.Close(); return; }
        _chosen = chosen; _today = today; _picked = picked; _clear = clear;
        _month = new DateOnly(chosen.Year, chosen.Month, 1);
        PanelTheme.Text(_frame, target);
        Build();
        _popup.Open(target);
    }

    public void Close() => _popup.Close();

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
        PopupMotion.AfterClose(() => picked?.Invoke(day));
    }
}

// A short list to pick one line of, in a pop-up above (or under) what opened it, as wide as its longest line: how an event
// repeats, which times of a repeating event to delete. A tick at the right marks the chosen line, where there is one.
internal sealed class AgendaChoicePopup
{
    public sealed record Choice(string Text, Action Pick, bool Chosen = false);

    private readonly PanelLook _look;
    private readonly OpenerPopup _popup;
    private readonly Border _frame;
    public bool IsOpen => _popup.Showing;

    public AgendaChoicePopup(PanelLook look)
    {
        _look = look;
        _frame = new Border { Background = UI.Brush(look.Hex("popup")), BorderBrush = UI.Brush(look.Hex("popupBorder")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(look.Corner(10)), Padding = new Thickness(4) };
        _popup = new OpenerPopup { Child = _frame };
    }

    // Shown from target, or closed when it is already open from there.
    public void Show(FrameworkElement target, IReadOnlyList<Choice> choices)
    {
        if (_popup.Showing && ReferenceEquals(_popup.PlacementTarget, target)) { _popup.Close(); return; }
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
            var button = AgendaDraw.Face(look, row, choice.Text, () => { Close(); PopupMotion.AfterClose(pick); }, 26, 6, new Thickness(8, 0, 8, 0));
            AgendaDraw.Fit(button, label, 26);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ((Border)button.Content).HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(button);
        }
        _frame.Child = stack;
        PanelTheme.Text(_frame, target);
        _popup.Open(target);
    }

    public void Close() => _popup.Close();

    private const string Tick = "M20 6 9 17l-5-5";
}

// A drop-down beside what opened it (a field, a button), which fades in and out in its own window. WPF's closing on a press outside
// (StaysOpen false) takes the window away at once, so its leaving was shown by a picture in a second window (PopupMotion), and the
// two windows never changed in the same frame: closed while it still faded in, which a click a little quick already did, its words
// blinked or doubled. So it stays open until its leaving is drawn (StaysOpen true) and watches presses itself, as the panel's menus
// do (PointerPressObserver): one outside it and its opener closes it; one on its opener goes to the opener, whose click closes it
// (Show while Showing) or, while it leaves, brings it back from where its leaving had come to. It goes at once when its opener
// leaves the screen (the panel closes, the page is drawn again).
//
// It lies 4 above what opened it, or 4 below where there is no room above (Popup's own Top placement turned below kept the offset
// up and covered the opener's lower edge).
internal sealed class OpenerPopup : Popup
{
    private const double Gap = 4;
    private Native.PointerPressObserver? _presses;
    // Counts the entrances and leavings: a leaving that ends after the pop-up was opened again does nothing.
    private int _motion;

    // Open and not leaving.
    public bool Showing { get; private set; }

    public OpenerPopup()
    {
        StaysOpen = true; AllowsTransparency = true; PopupAnimation = PopupAnimation.None;
        Placement = PlacementMode.Custom;
        CustomPopupPlacementCallback = Place;
        Closed += (_, _) => { _presses?.Dispose(); _presses = null; };
    }

    // The places offered, in screen pixels from the opener's top-left corner: above it, then below it.
    private CustomPopupPlacement[] Place(Size popup, Size target, Point offset)
    {
        double gap = Gap * (PlacementTarget is { } opener ? VisualTreeHelper.GetDpi(opener).DpiScaleY : 1);
        return [new(new Point(0, -popup.Height - gap), PopupPrimaryAxis.Horizontal), new(new Point(0, target.Height + gap), PopupPrimaryAxis.Horizontal)];
    }

    // Opens it from opener; while it leaves, it turns back from where it is.
    public void Open(UIElement opener)
    {
        if (!ReferenceEquals(PlacementTarget, opener))
        {
            if (PlacementTarget is UIElement old) { old.IsVisibleChanged -= OpenerShown; if (old is FrameworkElement gone) gone.Unloaded -= OpenerGone; }
            opener.IsVisibleChanged += OpenerShown;
            if (opener is FrameworkElement element) element.Unloaded += OpenerGone;
            PlacementTarget = opener;
        }
        _motion++;
        Showing = true;
        if (Child is not UIElement content) return;
        content.IsHitTestVisible = true;
        if (IsOpen)
        {
            Soft.Enter(content, content.Opacity, content.RenderTransform is TranslateTransform shift ? shift.Y : 0);
            return;
        }
        IsOpen = true;
        Soft.Enter(content);
        _presses ??= new Native.PointerPressObserver(Dispatcher, Pressed);
    }

    // Closes it, fading out while it rises (Soft.Leave); now, or where nothing moves, at once.
    public void Close(bool now = false)
    {
        if (!IsOpen) { Showing = false; return; }
        if (!Showing && !now) return;
        Showing = false;
        int motion = ++_motion;
        if (now || Child is not UIElement content || !Soft.At(content)) { Shut(); return; }
        content.IsHitTestVisible = false;
        Soft.Leave(content, () => { if (motion == _motion) Shut(); });
    }

    // Gone: the window closes and its content is as at rest for the next opening.
    private void Shut()
    {
        _motion++;
        Showing = false;
        IsOpen = false;
        if (Child is not UIElement content) return;
        content.BeginAnimation(UIElement.OpacityProperty, null);
        if (content.RenderTransform is TranslateTransform shift) shift.BeginAnimation(TranslateTransform.YProperty, null);
        content.IsHitTestVisible = true;
    }

    // A press anywhere on the screen, in device pixels: outside the pop-up and its opener it closes the pop-up.
    private void Pressed(Point point)
    {
        if (Showing && !Holds(Child, point) && !Holds(PlacementTarget, point)) Close();
    }

    private static bool Holds(UIElement? element, Point point) => element is FrameworkElement { IsVisible: true } shown && PresentationSource.FromVisual(shown) is not null
        && new Rect(shown.PointToScreen(new Point()), shown.PointToScreen(new Point(shown.ActualWidth, shown.ActualHeight))).Contains(point);

    private void OpenerShown(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is false) Close(now: true); }
    private void OpenerGone(object sender, RoutedEventArgs e) => Close(now: true);
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

// An event's reminder (AgendaReminders): the bell (the accent while there is a reminder), the reminder in words ("提前 15 分钟") and
// a chevron; a click opens the choices for a timed or an all-day event in an AgendaChoicePopup, the last of them "自定义…". That
// turns the field into an amount typed in a box and its unit, "提前 [25] [分钟 ⌄]" (an all-day event's: "提前 [2] [天 09:00 ⌄]"); the
// unit's choices end in "常用选项", which turns it back. Remind is the reminder (null: none); Valid is false while the amount typed
// cannot be one.
internal sealed class AgendaRemindField : ContentControl
{
    private readonly PanelLook _look;
    private readonly Path _bell;
    private readonly InkCentredText _label;
    private readonly Border _custom;
    private readonly StackPanel _customRow;
    private readonly TextBox _amount;
    private readonly InkCentredText _unitLabel;
    private readonly Button _chip, _unitButton;
    private int? _remind;
    private bool _allDay, _customMode, _setting;
    private AgendaReminders.Unit _unit = AgendaReminders.Unit.Minutes;
    public event Action? Changed;
    public AgendaChoicePopup Popup { get; }
    public AgendaChoicePopup UnitPopup { get; }

    public AgendaRemindField(PanelLook look, Action<TextBox> keyboard)
    {
        _look = look;
        Popup = new AgendaChoicePopup(look);
        UnitPopup = new AgendaChoicePopup(look);
        string rest = look.Fill;

        _label = AgendaDraw.Label("", 12, look.Primary);
        _label.Margin = new Thickness(6, 0, 0, 0);
        var chevron = AgendaDraw.CentredIcon(AgendaDraw.Chevron, 11, look.Tertiary);
        chevron.Margin = new Thickness(6, 0, 0, 0);
        var bell = Bell(false);
        _bell = (Path)((Canvas)bell.Child).Children[0];
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { bell, _label, chevron } };
        var face = new Border { Height = 28, CornerRadius = new CornerRadius(look.Corner(8)), Padding = new Thickness(9, 0, 8, 0), Background = UI.Brush(rest), Child = chipRow };
        _label.FitHeight(face, 28);
        _chip = AgendaDraw.Bare(face, Loc.T("提醒", "Reminder"), Open, (over, down) => face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : rest));

        // The custom amount: "提前", a box for the figures and the unit's button, on the field's fill.
        var before = AgendaDraw.Label(Loc.T("提前", "Before:"), 12, look.Secondary);
        before.Margin = new Thickness(6, 0, 6, 0);
        _amount = AgendaFields.Box(look, keyboard);
        _amount.Background = UI.Brush(look.FillSelected);
        _amount.TextAlignment = TextAlignment.Center; _amount.MaxLength = 4; _amount.Width = 40; _amount.Height = 22;
        ((LetterBox)_amount).Sample = "0";
        Typography.SetNumeralAlignment(_amount, FontNumeralAlignment.Tabular);
        InputMethod.SetIsInputMethodEnabled(_amount, false);
        AutomationProperties.SetName(_amount, Loc.T("提前多少", "How long before"));
        _amount.TextChanged += (_, _) => { if (!_setting) Typed(); };
        _amount.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Entered?.Invoke(); } };
        _unitLabel = AgendaDraw.Label("", 12, look.Primary);
        var unitChevron = AgendaDraw.CentredIcon(AgendaDraw.Chevron, 11, look.Tertiary);
        unitChevron.Margin = new Thickness(4, 0, 0, 0);
        var unitFace = new Border { Height = 22, CornerRadius = new CornerRadius(look.Corner(6)), Padding = new Thickness(6, 0, 5, 0), Background = Brushes.Transparent,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _unitLabel, unitChevron } } };
        _unitLabel.FitHeight(unitFace, 22);
        _unitButton = AgendaDraw.Bare(unitFace, Loc.T("单位", "Unit"), OpenUnits, (over, down) => unitFace.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : "#00000000"));
        _unitButton.Margin = new Thickness(4, 0, 0, 0);
        _customRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Bell(true), before, _amount, _unitButton } };
        _custom = new Border { Height = 28, CornerRadius = new CornerRadius(look.Corner(8)), Padding = new Thickness(9, 0, 3, 0), Background = UI.Brush(rest), Child = _customRow };

        Content = _chip;
        Show();
    }

    // The bell: in the accent while there is a reminder.
    private Viewbox Bell(bool on) => AgendaDraw.CentredIcon(BellIcon, 12, on ? _look.Accent : _look.Secondary);

    // The reminder, null for none. Setting it shows the choices (or the custom amount for one that is not among them).
    public int? Remind
    {
        get => _customMode ? Typed(false) : _remind;
        set { _remind = AgendaReminders.Normalize(value, _allDay); _customMode = _remind is { } minutes && !AgendaReminders.Choices(_allDay).Contains(minutes) && AgendaReminders.Parts(minutes, _allDay) is not null; Show(); }
    }

    // Whether what the field shows makes a reminder (the custom amount typed in full).
    public bool Valid => !_customMode || Typed(false) is not null;

    // Whether the event is all day: its choices are other ones, and a reminder becomes the other kind's default.
    public bool AllDay
    {
        get => _allDay;
        set
        {
            if (value == _allDay) return;
            int? was = Remind;
            _allDay = value;
            _customMode = false;
            _remind = AgendaReminders.Switched(was, value);
            Show();
            Changed?.Invoke();
        }
    }

    private void Show()
    {
        _bell.Stroke = UI.Brush(_remind is not null ? _look.Accent : _look.Secondary);
        _label.Text.Text = AgendaReminders.Text(_remind, _allDay);
        _label.InvalidateMeasure(); _label.InvalidateArrange();
        if (_customMode)
        {
            if (_remind is { } minutes && AgendaReminders.Parts(minutes, _allDay) is { } parts) { _unit = parts.Unit; SetAmount(parts.Amount.ToString(CultureInfo.InvariantCulture)); }
            ShowUnit();
            Content = _custom;
        }
        else Content = _chip;
    }

    private void ShowUnit()
    {
        _unitLabel.Text.Text = _allDay ? Loc.T("天 09:00", "days, 09:00") : UnitName(_unit);
        _unitLabel.InvalidateMeasure(); _unitLabel.InvalidateArrange();
    }

    private static string UnitName(AgendaReminders.Unit unit) => unit switch
    {
        AgendaReminders.Unit.Hours => Loc.T("小时", "hours"), AgendaReminders.Unit.Days => Loc.T("天", "days"), _ => Loc.T("分钟", "minutes")
    };

    private void SetAmount(string text) { _setting = true; _amount.Text = text; _setting = false; }

    // The custom amount as a reminder; null while it is not one. paint: the figures turn red while they are not.
    private int? Typed(bool paint = true)
    {
        int? remind = int.TryParse(_amount.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int amount) ? AgendaReminders.Custom(amount, _unit, _allDay) : null;
        if (paint)
        {
            _amount.Foreground = UI.Brush(remind is null ? _look.Hex("danger") : _look.Primary);
            if (remind is not null) _remind = remind;
            Changed?.Invoke();
        }
        return remind;
    }

    private void Open()
    {
        var choices = new List<AgendaChoicePopup.Choice> { new(AgendaReminders.Text(null, _allDay), () => Pick(null), _remind is null) };
        foreach (int minutes in AgendaReminders.Choices(_allDay)) choices.Add(new(AgendaReminders.Text(minutes, _allDay), () => Pick(minutes), _remind == minutes));
        bool other = _remind is { } kept && !AgendaReminders.Choices(_allDay).Contains(kept);
        choices.Add(new(Loc.T("自定义…", "Custom…"), Custom, other));
        Popup.Show(this, choices);
    }

    private void Pick(int? remind)
    {
        _remind = remind; _customMode = false;
        Show();
        Changed?.Invoke();
    }

    // "自定义…": the custom amount, starting from the reminder chosen (or the default). A click in its box takes the keyboard, as
    // the form's other boxes do.
    private void Custom()
    {
        int start = _remind ?? AgendaReminders.Default(_allDay);
        if (AgendaReminders.Parts(start, _allDay) is null) start = AgendaReminders.Default(_allDay);
        _remind = start; _customMode = true;
        Show();
        Changed?.Invoke();
    }

    // Enter in the amount's box: what the form does with Enter in its other boxes.
    public event Action? Entered;

    private void OpenUnits()
    {
        var choices = new List<AgendaChoicePopup.Choice>();
        if (!_allDay)
            foreach (var unit in new[] { AgendaReminders.Unit.Minutes, AgendaReminders.Unit.Hours, AgendaReminders.Unit.Days })
                choices.Add(new(UnitName(unit), () => { _unit = unit; ShowUnit(); Typed(); }, unit == _unit));
        choices.Add(new(Loc.T("常用选项", "Common choices"), () => Pick(_remind is { } kept && AgendaReminders.Choices(_allDay).Contains(kept) ? kept : AgendaReminders.Default(_allDay))));
        UnitPopup.Show(_unitButton, choices);
    }

    public bool PopupOpen => Popup.IsOpen || UnitPopup.IsOpen;
    public void ClosePopups() { Popup.Close(); UnitPopup.Close(); }

    public const string BellIcon = "M12 3C8.7 3 6.5 5.6 6.5 8.8v3.8l-1.7 3h14.4l-1.7-3V8.8C17.5 5.6 15.3 3 12 3zM9.6 17.6a2.4 2.4 0 0 0 4.8 0";
}

// Lays its children out left to right, Gap apart, starting a new line LineGap below where the next does not fit: the forms' "重复"
// and "提醒", which go one under the other on a narrow column.
internal sealed class AgendaWrapRow : Panel
{
    public double Gap { get; set; } = 6;
    public double LineGap { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + Gap + size.Width > available.Width) { y += line + LineGap; x = 0; line = 0; }
            x += (x > 0 ? Gap : 0) + size.Width;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x);
        }
        return new Size(widest, y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + Gap + size.Width > final.Width) { y += line + LineGap; x = 0; line = 0; }
            if (x > 0) x += Gap;
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            line = Math.Max(line, size.Height);
        }
        return final;
    }
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
        // On lines of their own, the first part has the whole width: one that wraps (AgendaWrapRow) is as tall as its lines.
        if (_split) { Children[0].Measure(new Size(width, double.PositiveInfinity)); left = Children[0].DesiredSize; }
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

// The last child fills the line; the one before it, where there is one, goes before it at its own width. Where that would leave
// the last child narrower than MinFill, the first goes on a line of its own above it. The notices page's form: "待办"/"日程" before
// the title or the to-do's words.
internal sealed class AgendaLeadRow : Panel
{
    public double Gap { get; set; } = 8;
    public double LineGap { get; set; } = 6;
    public double MinFill { get; set; } = 160;
    private bool _split;

    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count == 0) return new Size();
        var fill = Children[^1];
        if (Children.Count == 1) { fill.Measure(available); return fill.DesiredSize; }
        var lead = Children[0];
        lead.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = double.IsFinite(available.Width) ? available.Width : lead.DesiredSize.Width + Gap + MinFill;
        _split = width - lead.DesiredSize.Width - Gap < MinFill;
        fill.Measure(new Size(_split ? width : width - lead.DesiredSize.Width - Gap, available.Height));
        return _split ? new Size(width, lead.DesiredSize.Height + LineGap + fill.DesiredSize.Height)
            : new Size(width, Math.Max(lead.DesiredSize.Height, fill.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count == 0) return final;
        var fill = Children[^1];
        if (Children.Count == 1) { fill.Arrange(new Rect(final)); return final; }
        var lead = Children[0];
        Size l = lead.DesiredSize, f = fill.DesiredSize;
        if (_split)
        {
            lead.Arrange(new Rect(0, 0, Math.Min(l.Width, final.Width), l.Height));
            fill.Arrange(new Rect(0, l.Height + LineGap, final.Width, f.Height));
        }
        else
        {
            double height = Math.Max(l.Height, f.Height);
            lead.Arrange(new Rect(0, (height - l.Height) / 2, l.Width, l.Height));
            double x = l.Width + Gap;
            fill.Arrange(new Rect(x, (height - f.Height) / 2, Math.Max(0, final.Width - x), f.Height));
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
//
// Compact (the notices page, whose column is about 150 wide on its narrowest panel): drawn on the page rather than as a card, the
// main button on the gray fill as "取消" is ("打开应用" is the page's main button), and the end's day shown only for an event that
// runs over several days; another start day then moves the end by as many days, so the event keeps its length. The times go onto
// further lines where the column is too narrow for them, each field whole.
internal sealed class AgendaEventForm : Border
{
    private readonly PanelLook _look;
    private readonly bool _compact;
    private readonly AgendaLeadRow _titleRow;
    private DateOnly _startWas;
    public AgendaTextField Title { get; }
    public AgendaDateField StartDay { get; }
    public AgendaTimeField StartTime { get; }
    public AgendaDateField EndDay { get; }
    public AgendaTimeField EndTime { get; }
    public ToggleButton AllDay { get; }
    public AgendaRepeatField Repeat { get; }
    public AgendaRemindField Remind { get; }
    private readonly Button _submit, _cancel, _delete;
    private readonly AgendaChoicePopup _deleteChoices;
    private readonly FrameworkElement _dash;
    public event Action? Submitted, Cancelled;
    public event Action<AgendaDeleteScope>? Deleted;
    // Set by the page for a form opened from a time of a repeating event: "删除" then asks whether only that time goes, that time
    // and the later ones (offered when the series has a time before it), or all of them.
    public bool Series { get; set; }
    public bool SeriesHasEarlier { get; set; }

    // submit: the main button's words ("添加", "保存", "加入日程"); delete: offer "删除"; compact: the notices page's form.
    public AgendaEventForm(PanelLook look, Action<TextBox> keyboard, string submit, bool delete = false, bool showStartDay = false, bool compact = false)
    {
        _look = look; _compact = compact;
        VerticalAlignment = VerticalAlignment.Top;
        if (!compact) { Background = UI.Brush(look.Fill); CornerRadius = new CornerRadius(look.Corner(8)); Padding = new Thickness(8); }
        Title = AgendaFields.Text(look, keyboard, Loc.T("标题", "Title"));
        StartDay = AgendaFields.Date(look, Loc.T("开始日期", "Start date"));
        StartTime = AgendaFields.Time(look, keyboard, Loc.T("开始时间", "Start time"));
        EndDay = AgendaFields.Date(look, Loc.T("结束日期", "End date"));
        EndTime = AgendaFields.Time(look, keyboard, Loc.T("结束时间", "End time"), endOfDay: true);
        AllDay = AgendaFields.Check(look, false, Loc.T("全天", "All day"));
        AllDay.VerticalAlignment = VerticalAlignment.Center;
        // The dash between the times, drawn as a short line in the middle of the fields' height, where their figures' middle is.
        _dash = new Rectangle { Width = 6, Height = 1.25, Fill = UI.Brush(look.Tertiary), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };

        // The start's day, the times from start to end and "全天" wrap where the column is too narrow for them; the times break
        // only where they must too (an end on another day), each field whole. The lines are as far apart as the form's rows.
        var times = new WrapPanel();
        if (showStartDay) { StartDay.Margin = new Thickness(0, 0, 6, 6); times.Children.Add(StartDay); }
        EndDay.Margin = new Thickness(0, 0, 6, 6); EndTime.Margin = new Thickness(0, 0, 0, 6);
        times.Children.Add(new WrapPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { StartTime, _dash } }, EndDay, EndTime } });
        // As tall as the fields, the box and "全天" in its middle: the box by its own, the words by their letters as drawn.
        var allDayLabel = AgendaDraw.Label(Loc.T("全天", "All day"), 11.5, look.Secondary);
        allDayLabel.Margin = new Thickness(6, 0, 0, 0);
        var allDayRow = new StackPanel { Orientation = Orientation.Horizontal, Height = 28, Margin = new Thickness(12, 0, 0, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { AllDay, allDayLabel } };
        allDayRow.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && IsWithin(source, AllDay)) return; AllDay.IsChecked = AllDay.IsChecked != true; e.Handled = true; };
        times.Children.Add(allDayRow);

        _deleteChoices = new AgendaChoicePopup(look);
        _delete = AgendaDraw.Pill(look, Loc.T("删除", "Delete"), Delete, danger: true);
        _cancel = AgendaDraw.Pill(look, Loc.T("取消", "Cancel"), () => Cancelled?.Invoke());
        _submit = AgendaDraw.Pill(look, submit, Submit, main: !compact);
        _delete.Margin = new Thickness(0, 0, 6, 0); _cancel.Margin = new Thickness(0, 0, 6, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (delete) buttons.Children.Add(_delete);
        buttons.Children.Add(_cancel); buttons.Children.Add(_submit);

        Repeat = new AgendaRepeatField(look) { VerticalAlignment = VerticalAlignment.Center };
        Remind = new AgendaRemindField(look, keyboard) { VerticalAlignment = VerticalAlignment.Center };
        times.Margin = new Thickness(0, 6, 0, -6);
        // How it repeats and its reminder side by side, one under the other where the column is too narrow; the buttons at the right,
        // on a line of their own under them where they do not fit beside them.
        var foot = new AgendaSplitRow { Margin = new Thickness(0, 6, 0, 0), Children = { new AgendaWrapRow { Children = { Repeat, Remind } }, buttons } };
        _titleRow = new AgendaLeadRow { Children = { Title } };
        Child = new StackPanel { Children = { _titleRow, times, foot } };

        Title.Changed += Check; StartTime.Changed += Check; EndTime.Changed += Check; EndDay.Changed += Check; Remind.Changed += Check;
        Remind.Entered += Submit;
        StartDay.Changed += () =>
        {
            if (_compact) EndDay.Date = EndDay.Date.AddDays(StartDay.Date.DayNumber - _startWas.DayNumber);
            _startWas = StartDay.Date;
            Repeat.First = StartDay.Date;
            Check();
        };
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

    // Something on the title's line before it (the notices page's "待办"/"日程"), above it where the line is too short for both.
    public FrameworkElement? Lead
    {
        set
        {
            if (_titleRow.Children.Count > 1) _titleRow.Children.RemoveAt(0);
            if (value is not null) _titleRow.Children.Insert(0, value);
        }
    }

    // Whether a pop-up of the form is open (a month, the repeat rules, the delete choices): the panel stays open while it is.
    public bool PopupOpen => StartDay.Popup.IsOpen || EndDay.Popup.IsOpen || Repeat.Popup.IsOpen || Remind.PopupOpen || _deleteChoices.IsOpen;
    public void ClosePopups() { StartDay.Popup.Close(); EndDay.Popup.Close(); Repeat.Popup.Close(); Remind.ClosePopups(); _deleteChoices.Close(); }

    private void Delete()
    {
        if (!Series) { Deleted?.Invoke(AgendaDeleteScope.All); return; }
        var choices = new List<AgendaChoicePopup.Choice> { new(Loc.T("只删除这一次", "This one only"), () => Deleted?.Invoke(AgendaDeleteScope.This)) };
        if (SeriesHasEarlier) choices.Add(new(Loc.T("删除这一次及以后", "This one and later ones"), () => Deleted?.Invoke(AgendaDeleteScope.Later)));
        choices.Add(new(Loc.T("删除全部", "All of them"), () => Deleted?.Invoke(AgendaDeleteScope.All)));
        _deleteChoices.Show(_delete, choices);
    }

    // Puts the values in: an all-day event's end is its last day; a timed one ending at 00:00 ends at 24:00 of the day before. For a
    // series, its first time and how it repeats. remind: its reminder (AgendaEvent.Remind), null for none; a new event is given
    // AgendaReminders.Default.
    public void Fill(string title, DateTime start, DateTime end, bool allDay, AgendaRepeat repeat, int? remind)
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
        _startWas = StartDay.Date;
        if (_compact) EndDay.Visibility = EndDay.Date != StartDay.Date ? Visibility.Visible : Visibility.Collapsed;
        AllDay.IsChecked = allDay;
        AllDayChanged();
        Remind.Remind = remind;
        Check();
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
        Remind.AllDay = allDay;
        Check();
    }

    private void Check() => _submit.IsEnabled = Value is not null && Remind.Valid;

    private void Submit() { if (Value is not null && Remind.Valid) Submitted?.Invoke(); }

    // The title takes the keyboard with its text selected (a new form, or one opened to change an event).
    public void FocusTitle(Action<Control> begin) { begin(Title.Box); Title.Box.Dispatcher.BeginInvoke(() => Title.Box.SelectAll(), System.Windows.Threading.DispatcherPriority.Input); }
}
