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
        var grid = new Grid { Width = width, Children = { glyph } };
        var button = Face(look, grid, name, click, height, 6, new Thickness(0));
        button.ToolTip = name;
        return button;
    }

    // A capsule button of the forms (24 high): the fill (over the form's own), or the text color for the main one.
    public static Button Pill(PanelLook look, string text, Action click, bool main = false, bool danger = false)
    {
        string fill = main ? look.Primary : look.Fill;
        var label = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = UI.Brush(main ? look.Hex("surface") : danger ? look.Hex("danger") : look.Primary) };
        var face = new Border { Height = 24, CornerRadius = new CornerRadius(look.Corner(12)), Padding = new Thickness(12, 0, 12, 0), Background = UI.Brush(fill), Child = label };
        var button = Bare(face, text, click, (over, down) =>
        {
            if (main) face.Opacity = down ? .76 : over ? .88 : 1;
            else face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : fill);
        });
        button.IsEnabledChanged += (_, _) => { button.Opacity = button.IsEnabled ? 1 : .4; button.Cursor = button.IsEnabled ? Cursors.Hand : Cursors.Arrow; };
        return button;
    }

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
        var box = new TextBox
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

// A one-line text field with a hint: AgendaFields.Text.
internal sealed class AgendaTextField : Grid
{
    public TextBox Box { get; }
    private readonly TextBlock _hint;
    public event Action? Changed;
    public string Text { get => Box.Text; set => Box.Text = value; }

    public AgendaTextField(PanelLook look, Action<TextBox> keyboard, string hint)
    {
        Height = 28;
        Box = AgendaFields.Box(look, keyboard);
        Box.Background = UI.Brush(look.Fill);
        Box.Padding = new Thickness(7, 0, 7, 0);
        AutomationProperties.SetName(Box, hint);
        _hint = AgendaDraw.Words(hint, 12.5, 18, look.Tertiary);
        _hint.Margin = new Thickness(10.5, 0, 10, 0); _hint.VerticalAlignment = VerticalAlignment.Center; _hint.IsHitTestVisible = false;
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
    private readonly TextBlock _label;
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
        _label = AgendaDraw.Words("", 12.5, 18, look.Primary);
        _label.VerticalAlignment = VerticalAlignment.Center;
        var chevron = AgendaDraw.Icon(AgendaDraw.Chevron, 11, look.Tertiary);
        chevron.Margin = new Thickness(6, 0, 0, 0); chevron.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { _label, chevron } };
        string rest = look.Fill;
        _face = new Border { Height = 28, CornerRadius = new CornerRadius(look.Corner(8)), Padding = new Thickness(10, 0, 8, 0), Background = UI.Brush(rest), Child = row };
        Popup = new AgendaDatePopup(look);
        var button = AgendaDraw.Bare(_face, name, () => Popup.Show(this, _date, Today(), day => { Date = day; }), (over, down) =>
            _face.Background = UI.Brush(down ? look.Hex("pressed") : over ? look.FillSelected : rest));
        Content = button;
    }

    public DateOnly Date
    {
        get => _date;
        set { bool changed = value != _date; _date = value; _label.Text = AgendaText.Date(value, Today().Year); if (changed) Changed?.Invoke(); }
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
        var title = AgendaDraw.Words(AgendaText.Month(_month.Year, _month.Month), 12.5, 18, look.Primary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(4, 0, 0, 0);
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
            var label = AgendaDraw.Words(day.Day.ToString(CultureInfo.InvariantCulture), 12, 16,
                chosen ? look.Hex("onAccent") : isToday ? look.Accent : inMonth ? look.Primary : look.Tertiary, chosen || isToday ? FontWeights.SemiBold : null);
            label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            var picked = day;
            var cell = AgendaDraw.Face(look, label, AgendaText.Date(day, _today.Year), () => Pick(picked), 26, 6, new Thickness(0), chosen ? look.Accent : null, chosen ? look.Accent : null);
            days.Children.Add(cell);
        }
        stack.Children.Add(days);
        if (_clear is not null)
        {
            var clear = AgendaDraw.Words(_clear, 12, 18, look.Secondary);
            clear.VerticalAlignment = VerticalAlignment.Center;
            var button = AgendaDraw.Face(look, clear, _clear, () => Pick(null), 26, 6, new Thickness(6, 0, 6, 0));
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
// day makes the event run over several days. The main button is available only while the values make an event (AgendaBook.Shape):
// a title, and an end after the start. Enter in the title presses it.
internal sealed class AgendaEventForm : Border
{
    private readonly PanelLook _look;
    public AgendaTextField Title { get; }
    public AgendaDateField StartDay { get; }
    public AgendaTimeField StartTime { get; }
    public AgendaDateField EndDay { get; }
    public AgendaTimeField EndTime { get; }
    public ToggleButton AllDay { get; }
    private readonly Button _submit, _cancel, _delete;
    private readonly TextBlock _dash;
    public event Action? Submitted, Cancelled, Deleted;

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
        _dash = AgendaDraw.Words("–", 12.5, 18, look.Tertiary);
        _dash.VerticalAlignment = VerticalAlignment.Center; _dash.Margin = new Thickness(6, 0, 6, 0);

        var times = new StackPanel { Orientation = Orientation.Horizontal };
        if (showStartDay) { StartDay.Margin = new Thickness(0, 0, 6, 0); times.Children.Add(StartDay); }
        times.Children.Add(StartTime); times.Children.Add(_dash);
        EndDay.Margin = new Thickness(0, 0, 6, 0);
        times.Children.Add(EndDay); times.Children.Add(EndTime);
        var allDayLabel = AgendaDraw.Words(Loc.T("全天", "All day"), 11.5, 16, look.Secondary);
        allDayLabel.VerticalAlignment = VerticalAlignment.Center; allDayLabel.Margin = new Thickness(6, 0, 0, 0);
        var allDayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Children = { AllDay, allDayLabel } };
        allDayRow.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject source && IsWithin(source, AllDay)) return; AllDay.IsChecked = AllDay.IsChecked != true; e.Handled = true; };
        times.Children.Add(allDayRow);

        _delete = AgendaDraw.Pill(look, Loc.T("删除", "Delete"), () => Deleted?.Invoke(), danger: true);
        _cancel = AgendaDraw.Pill(look, Loc.T("取消", "Cancel"), () => Cancelled?.Invoke());
        _submit = AgendaDraw.Pill(look, submit, Submit, main: true);
        _delete.Margin = new Thickness(0, 0, 6, 0); _cancel.Margin = new Thickness(0, 0, 6, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (delete) buttons.Children.Add(_delete);
        buttons.Children.Add(_cancel); buttons.Children.Add(_submit);

        var row = new AgendaSplitRow { Margin = new Thickness(0, 6, 0, 0), Children = { times, buttons } };
        Child = new StackPanel { Children = { Title, row } };

        Title.Changed += Check; StartTime.Changed += Check; EndTime.Changed += Check; EndDay.Changed += Check;
        StartDay.Changed += () => { Check(); };
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

    // Whether a month of the form is open: the panel stays open while it is.
    public bool PopupOpen => StartDay.Popup.IsOpen || EndDay.Popup.IsOpen;
    public void ClosePopups() { StartDay.Popup.Close(); EndDay.Popup.Close(); }

    // Puts the values in: an all-day event's end is its last day; a timed one ending at 00:00 ends at 24:00 of the day before.
    public void Fill(string title, DateTime start, DateTime end, bool allDay)
    {
        Title.Text = title;
        StartDay.Date = DateOnly.FromDateTime(start);
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
