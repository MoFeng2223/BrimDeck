using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BrimDeck.Core;

namespace BrimDeck;

// The date beside the clock (DeckSettings.ClockDate); none when it is off.
internal readonly record struct ClockDate(ClockDatePosition Position, ClockDateFormat Format, ClockWeekday Weekday)
{
    public static ClockDate? Of(DeckSettings settings) => settings.ClockDate ? new(settings.ClockDatePosition, settings.ClockDateFormat, settings.ClockWeekday) : null;
}

// The time on the collapsed notch or capsule, with the date when it is on. Each digit takes the advance of the widest digit and
// the hour always has room for two, so the face keeps one width through the day and nothing beside it moves when the minute changes.
internal sealed class ClockFace : FrameworkElement
{
    public const double FaceHeight = 24;
    public static readonly Color White = Color.FromRgb(0xF5, 0xF5, 0xF7);
    private static readonly FontFamily Orbitron = Bundled("Orbitron"), InstrumentSerif = Bundled("Instrument Serif"), Unbounded = Bundled("Unbounded"), BebasNeue = Bundled("Bebas Neue"), Fredoka = Bundled("Fredoka");
    private static FontFamily Bundled(string name) => Fonts.Bundled(name);
    // The plain styles are set in the panel's font (the user's own when one is chosen); the styles with their own face keep it.
    private static Typeface Plain => Fonts.Face(UiFontRole.Panel);
    private static Typeface Bold => Fonts.Face(UiFontRole.Panel, FontWeights.SemiBold);
    private static Typeface Face(FontFamily family, FontWeight? weight = null, bool italic = false)
        => new(family, italic ? FontStyles.Italic : FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal);

    // The date is dimmer than the time. Beside the time its digits are this much of the time's height and share its baseline;
    // on a line of its own the time is at most TwoLineTime tall and the date TwoLineDate, TwoLineGap apart (heights of the digits' ink).
    private const double DateOpacity = .62, BesideRatio = .76, BesideGap = 6, TwoLineTime = 10, TwoLineDate = 6.6, TwoLineGap = 3.5;

    private readonly ClockStyle _style;
    private readonly bool _h24, _seconds;
    private readonly Color[] _colors;
    private readonly ClockDate? _date;
    private DateTime _time;
    private double? _reserved;
    // The font the reserved width was measured in: another font measures it again.
    private int _reservedFont = -1;
    // The collapsed row's height scale. The face is laid out at 1 and its outlines are drawn scaled: every glyph is filled as
    // geometry, so a scaled outline is the same as one built at the larger size.
    private readonly double _scale;
    private readonly bool _plain;
    // Where the face is painted and how wide: a drawn glyph takes the gradient's color where it stands (GlyphBrush).
    private double _paintStart, _paintWidth = 1;

    // One color fills the face; more are spread evenly across it from left to right.
    // look: the look of the panel it is drawn on (MainWindow.Look); the main look by default.
    public ClockFace(ClockStyle style, bool h24, bool seconds, Color[] colors, DateTime time, double scale = 1, PanelLook? look = null, ClockDate? date = null)
    {
        var Look = look ?? PanelTheme.Main;
        _style = style; _h24 = h24; _seconds = seconds; _colors = colors.Length > 0 ? colors : [ThemeWhite(Look)]; _time = time; _date = date;
        _scale = CompactMusicLayout.SafeScale(scale);
        Height = FaceHeight * _scale; IsHitTestVisible = false;
        // Under high contrast nothing is dimmed or glows (ClockColors.Glows).
        _plain = Look.HighContrast;
        if (!ClockColors.Glows(style, _plain)) return;
        var glow = _colors[_colors.Length / 2];
        // A glow lights up a dark surface; on a light one it only smears the digits, so it stays a faint halo there.
        bool dark = Look.Current.IsDark;
        if (style == ClockStyle.Neon) Effect = new DropShadowEffect { Color = glow, ShadowDepth = 0, BlurRadius = (dark ? 9 : 4) * _scale, Opacity = dark ? .85 : .3 };
        else if (style == ClockStyle.Segment) Effect = new DropShadowEffect { Color = glow, ShadowDepth = 0, BlurRadius = (dark ? 5 : 3) * _scale, Opacity = dark ? .6 : .2 };
    }

    public static string Title(ClockStyle style) => style switch
    {
        ClockStyle.Digits => Loc.T("叠放", "Stacked digits"), ClockStyle.Dots => Loc.T("点阵", "Dot matrix"),
        ClockStyle.Segment => Loc.T("数码管", "Seven segment"), ClockStyle.Neon => Loc.T("霓虹", "Neon"), ClockStyle.Serif => Loc.T("衬线", "Serif"),
        ClockStyle.Wide => Loc.T("宽体", "Wide"), ClockStyle.Condensed => Loc.T("窄体", "Condensed"), ClockStyle.Pixel => Loc.T("像素", "Pixel"),
        ClockStyle.Rounded => Loc.T("圆润", "Rounded"), _ => Loc.T("简约", "Simple")
    };
    // The cover color is used when a song with a cover is playing; otherwise the clock takes the theme's clock color, which
    // "white" stands for. Every color is drawn as the theme can read it on its surface (see Look.Readable). Under high
    // contrast it is always the theme's clock color, the system's window text (ClockColors.Shown).
    public static Color[] Palette(DeckSettings settings, Color? cover, PanelLook? look = null)
    {
        var Look = look ?? PanelTheme.Main;
        return (ClockColors.Shown(settings.ClockColor, Look.HighContrast) switch
        {
            ClockColor.Gradient => settings.ClockGradient.Select(Parse).ToArray(),
            ClockColor.Custom => [Parse(settings.ClockCustomColor)],
            ClockColor.Cover => [cover ?? ThemeWhite(Look)],
            var preset => [Preset(preset, Look)]
        }).Select(color => Look.Readable(color, PanelThemes.TextContrast)).ToArray();
    }
    private static Color ThemeWhite(PanelLook look) => look.ColorOf("clock");
    // Amber, cyan, pink and lime are the panel theme's own versions of those colors; "white" is the theme's clock color.
    public static Color Preset(ClockColor color, PanelLook? look = null) => PanelTheme.ToColor((look ?? PanelTheme.Main).Current.ClockPreset(color));
    public static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    // A swatch for colors at evenly spaced stops; the diagonal shows every stop on a round swatch.
    public static Brush Fill(IReadOnlyList<Color> colors, bool diagonal = true)
    {
        if (colors.Count == 1) return new SolidColorBrush(colors[0]);
        var stops = new GradientStopCollection(colors.Select((color, i) => new GradientStop(color, (double)i / (colors.Count - 1))));
        return new LinearGradientBrush(stops, new Point(0, 0), diagonal ? new Point(1, 1) : new Point(1, 0));
    }

    public DateTime Time
    {
        get => _time;
        set { if (value == _time) return; _time = value; InvalidateVisual(); }
    }
    // The widest the face gets on any day: a two-digit hour, month and day, every weekday, morning and afternoon, and every
    // month name when the date spells it. Every digit takes the same width, so the seconds and the year need no sample of their own.
    // In DIPs at the face's scale. A scaled face is rounded up to a whole DIP so the regions beside it stay on whole DIPs;
    // the default face keeps its measured width, as in the settings preview.
    public double ReservedWidth => _scale == 1 ? UnitReserved : Math.Ceiling(UnitReserved * _scale - .001);
    private double UnitReserved
    {
        get
        {
            if (_reserved is null || _reservedFont != Fonts.Version) { _reservedFont = Fonts.Version; _reserved = MeasureReserved(); }
            return _reserved.Value;
        }
    }
    private double MeasureReserved()
    {
        bool months = Loc.IsEnglish && _date is { Format: ClockDateFormat.MonthDayWords or ClockDateFormat.YearWords };
        int[] shown = months ? [.. Enumerable.Range(1, 12)] : [12];
        return shown.SelectMany(month => Enumerable.Range(22, 7).SelectMany(day => new[] { 10, 22 }.Select(hour => new DateTime(2025, month, day, hour, 0, 0))))
            .Max(time => Layout(null, time, 0, Brushes.White));
    }

    protected override Size MeasureOverride(Size availableSize) => new(ReservedWidth, FaceHeight * _scale);

    protected override void OnRender(DrawingContext dc)
    {
        double width = Layout(null, _time, 0, Brushes.White), scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The start is a whole device pixel after scaling, as at the default size.
        double x = Math.Round((ReservedWidth - width * _scale) / 2 * scale) / scale / _scale;
        _paintStart = x; _paintWidth = Math.Max(1, width);
        Brush paint = Fill(_colors);
        if (paint is LinearGradientBrush gradient) { gradient.MappingMode = BrushMappingMode.Absolute; gradient.StartPoint = new Point(x, 0); gradient.EndPoint = new Point(x + width, 0); }
        paint.Freeze();
        if (_scale != 1) dc.PushTransform(new ScaleTransform(_scale, _scale));
        Layout(dc, _time, x, paint);
        if (_scale != 1) dc.Pop();
    }

    // Device pixels per DIP of the unscaled face.
    private double Device => _scale * VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // Draws the face from x when dc is set; returns the width either way.
    private double Layout(DrawingContext? dc, DateTime time, double x, Brush paint)
    {
        const double middle = FaceHeight / 2;
        if (_date is not { } date) return TimeLine(dc, time, x, paint, 1, middle);
        string text = ClockDates.Text(time, date.Format, date.Weekday, Loc.IsEnglish);
        // The stacked digits are two lines already, so the date stands beside them: before them for above, after them for below.
        var position = _style != ClockStyle.Digits ? date.Position : date.Position switch
        {
            ClockDatePosition.Above => ClockDatePosition.Left, ClockDatePosition.Below => ClockDatePosition.Right, var beside => beside
        };
        double device = Device, full = TimeInk(device);
        if (position is ClockDatePosition.Left or ClockDatePosition.Right)
        {
            // The pixel date is whole blocks of whole device pixels, at least one.
            double ink = _style switch
            {
                ClockStyle.Pixel => 7 * Math.Max(1, Math.Floor(full / 7 * device * BesideRatio)) / device,
                ClockStyle.Digits => full,
                _ => full * BesideRatio
            };
            // On the time's baseline; the stacked digits have none, so the date is centred on them.
            double dateMiddle = _style == ClockStyle.Digits ? middle : middle + (full - ink) / 2;
            double timeWidth = TimeLine(null, time, 0, paint, 1, middle), dateWidth = DateLine(null, text, 0, paint, ink, dateMiddle);
            if (position == ClockDatePosition.Left)
            {
                DateLine(dc, text, x, paint, ink, dateMiddle);
                TimeLine(dc, time, x + dateWidth + BesideGap, paint, 1, middle);
            }
            else
            {
                TimeLine(dc, time, x, paint, 1, middle);
                DateLine(dc, text, x + timeWidth + BesideGap, paint, ink, dateMiddle);
            }
            return timeWidth + BesideGap + dateWidth;
        }
        // Two lines, each centred on the wider.
        double k, timeInk, dateInk, gap;
        if (_style == ClockStyle.Pixel)
        {
            var (blocks, dateBlocks, gapPixels) = PixelLines(device);
            k = blocks / device / 2; timeInk = 7 * blocks / device; dateInk = 7 * dateBlocks / device; gap = gapPixels / device;
        }
        else
        {
            // Dots smaller than about 1 DIP blur together and thin segments break up, so the drawn dates are a little larger.
            k = Math.Min(1, TwoLineTime / full); timeInk = full * k; dateInk = _style switch { ClockStyle.Dots => 7.5, ClockStyle.Segment => 7.2, _ => TwoLineDate }; gap = TwoLineGap;
        }
        double top = (FaceHeight - (timeInk + gap + dateInk)) / 2;
        bool above = position == ClockDatePosition.Above;
        double timeMiddle = above ? top + dateInk + gap + timeInk / 2 : top + timeInk / 2;
        double dateMiddle2 = above ? top + dateInk / 2 : top + timeInk + gap + dateInk / 2;
        double timeWide = TimeLine(null, time, 0, paint, k, timeMiddle), dateWide = DateLine(null, text, 0, paint, dateInk, dateMiddle2);
        double width = Math.Max(timeWide, dateWide);
        TimeLine(dc, time, x + (width - timeWide) / 2, paint, k, timeMiddle);
        DateLine(dc, text, x + (width - dateWide) / 2, paint, dateInk, dateMiddle2);
        return width;
    }

    // The pixel clock on two lines, in device pixels: the time's block, the date's block and the space between them. The time keeps
    // its block when the lines fit the face's height, the date about 0.6 of it; a block too large for that height steps down.
    private static (int Time, int Date, int Gap) PixelLines(double device)
    {
        double room = FaceHeight * device;
        int gap = (int)Math.Max(2, Math.Round(2.5 * device, MidpointRounding.AwayFromZero));
        for (int time = (int)Math.Round(PixelStairs.Step(2, device) * device); time > 1; time--)
            for (int date = Math.Max(1, (int)Math.Round(time * .62)); date >= 1; date--)
                if (7 * (time + date) + gap <= room) return (time, date, gap);
        return (1, 1, gap);
    }

    // The height of the time's digits at its full size.
    private double TimeInk(double device) => _style switch
    {
        ClockStyle.Dots => 6 * 2 + 1.7,
        ClockStyle.Segment => SegmentHeight,
        ClockStyle.Pixel => 7 * PixelStairs.Step(2, device),
        ClockStyle.Digits => Ink(new Run("0", Bold, 11)).Height,
        _ => Ink(MainRun("0", 1)).Height
    };

    // The time at k of its size, its digits centred on middle; returns its width.
    private double TimeLine(DrawingContext? dc, DateTime time, double x, Brush paint, double k, double middle)
    {
        string hour = _h24 ? time.ToString("HH", CultureInfo.InvariantCulture) : (time.Hour % 12 == 0 ? 12 : time.Hour % 12).ToString(CultureInfo.InvariantCulture);
        string minute = time.ToString("mm", CultureInfo.InvariantCulture), secs = time.ToString("ss", CultureInfo.InvariantCulture);
        string clock = hour + ":" + minute + (_seconds ? ":" + secs : "");
        // Both languages mark the 12-hour clock with AM or PM after the time.
        string? period = _h24 ? null : time.Hour < 12 ? "AM" : "PM";
        switch (_style)
        {
            case ClockStyle.Dots: return Dots(dc, clock, x, 2 * k, middle);
            case ClockStyle.Pixel: return Blocks(dc, clock, x, PixelStairs.Step(2 * k, Device), middle);
            case ClockStyle.Segment: return Segments(dc, clock, x, k, middle);
            case ClockStyle.Digits:
            {
                // Hours over minutes, both as two digits, the minutes dimmer; the seconds, smaller still, follow at the middle.
                var hours = new Run(hour.PadLeft(2, '0'), Bold, 11 * k, -.02);
                var minutes = hours with { Text = minute, Opacity = .55 };
                double width = Math.Max(RunWidth(hours), RunWidth(minutes));
                Draw(dc, x + (width - RunWidth(hours)) / 2, Baseline(hours, middle - 5.75 * k), paint, hours);
                Draw(dc, x + (width - RunWidth(minutes)) / 2, Baseline(minutes, middle + 5.75 * k), paint, minutes);
                if (!_seconds) return width;
                var seconds = new Run(secs, Bold, 8.5 * k, Opacity: .55);
                return Draw(dc, x + width + 2.5 * k, Baseline(seconds, middle), paint, seconds) - x;
            }
            default:
            {
                var main = MainRun(clock, k);
                Run? label = period is null ? null : _style switch
                {
                    ClockStyle.Neon => new Run(period, Face(Orbitron, FontWeights.SemiBold), 8 * k, .04, Cells: false),
                    ClockStyle.Rounded => new Run(period, Face(Fredoka, FontWeights.SemiBold), 8.5 * k, .03, Opacity: .8, Cells: false),
                    ClockStyle.Minimal => new Run(period, Plain, 10 * k, Opacity: .62, Cells: false),
                    _ => new Run(period, Plain, 10 * k, Opacity: .7, Cells: false)
                };
                double baseline = Baseline(main, middle), start = x;
                x = Draw(dc, x, baseline, paint, main);
                if (label is { } after) x = Draw(dc, x + 3 * k, baseline, paint, after);
                return x - start;
            }
        }
    }
    private Run MainRun(string clock, double k) => _style switch
    {
        ClockStyle.Neon => new Run(clock, Face(Orbitron, FontWeights.SemiBold), 11.5 * k, .06),
        ClockStyle.Serif => new Run(clock, Face(InstrumentSerif, italic: true), 19 * k, .01),
        ClockStyle.Wide => new Run(clock, Face(Unbounded, FontWeights.SemiBold), 11.5 * k, .01),
        ClockStyle.Condensed => new Run(clock, Face(BebasNeue), 19 * k, .05),
        // Fredoka SemiBold, round and soft. Its digits are 0.72 em tall like Orbitron's, so it takes the neon clock's size.
        ClockStyle.Rounded => new Run(clock, Face(Fredoka, FontWeights.SemiBold), 11.5 * k, .02),
        _ => new Run(clock, Bold, 12.5 * k)
    };

    // The date in the clock's style, its digits ink tall and centred on middle; returns its width. The styles set in a font set the
    // date in it, with any character the font lacks (the Chinese weekday, 年, 月, 日) in the panel's font. The drawn styles draw its
    // digits, slashes and dashes as they draw the time's, and set the rest in the panel's font.
    private double DateLine(DrawingContext? dc, string text, double x, Brush paint, double ink, double middle)
    {
        bool dim = dc is not null && !_plain;
        if (dim) dc!.PushOpacity(DateOpacity);
        bool drawn = _style is ClockStyle.Dots or ClockStyle.Pixel or ClockStyle.Segment;
        var (face, spacing) = DateFace();
        // A space is half the digits' height in every face (a serif's own is a hairline). Characters the date's face lacks are set in
        // the panel's font a little smaller, so they look no taller than the digits beside them.
        double start = x, pad = drawn ? ink * .15 : 0, space = ink * .5, other = drawn ? .8 : face.Equals(Plain) ? 1 : .88;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { x += space; i++; continue; }
            bool glyphs = drawn && Drawable(text[i]), own = !drawn && text[i] < 0x80;
            int end = i + 1;
            while (end < text.Length && text[end] != ' ' && (drawn ? Drawable(text[end]) == glyphs : text[end] < 0x80 == own)) end++;
            string part = text[i..end];
            if (glyphs)
            {
                if (i > 0 && text[i - 1] != ' ') x += pad;
                x += _style switch
                {
                    ClockStyle.Dots => Dots(dc, part, x, ink / 6.85, middle),
                    ClockStyle.Pixel => Blocks(dc, part, x, ink / 7, middle),
                    _ => Segments(dc, part, x, ink / SegmentHeight, middle)
                };
                if (end < text.Length && text[end] != ' ') x += pad;
            }
            else
            {
                var run = own ? new Run(part, face, ink / InkPerEm(face), spacing) : new Run(part, Plain, ink / InkPerEm(Plain) * other, Cells: false);
                x = Draw(dc, x, Baseline(run, middle), paint, run);
            }
            i = end;
        }
        if (dim) dc!.Pop();
        return x - start;
    }
    private static bool Drawable(char ch) => char.IsAsciiDigit(ch) || ch is '/' or '-';
    private (Typeface Face, double Spacing) DateFace() => _style switch
    {
        ClockStyle.Neon => (Face(Orbitron, FontWeights.SemiBold), .04),
        ClockStyle.Serif => (Face(InstrumentSerif, italic: true), .01),
        ClockStyle.Wide => (Face(Unbounded, FontWeights.SemiBold), .01),
        ClockStyle.Condensed => (Face(BebasNeue), .04),
        ClockStyle.Rounded => (Face(Fredoka, FontWeights.SemiBold), .02),
        _ => (Plain, 0)
    };

    // Letter spacing is in ems and falls between characters. Digits in a cell run take the widest digit's advance.
    private readonly record struct Run(string Text, Typeface Face, double Size, double Spacing = 0, double Opacity = 1, bool Cells = true);
    private double RunWidth(Run run) => Draw(null, 0, 0, Brushes.White, run);
    // Returns where the run ends.
    private double Draw(DrawingContext? dc, double x, double baseline, Brush paint, Run run)
    {
        double spacing = run.Spacing * run.Size, cell = run.Cells ? DigitCell(run) : 0;
        bool dim = dc is not null && run.Opacity < 1 && !_plain;
        if (dim) dc!.PushOpacity(run.Opacity);
        if (!run.Cells && run.Spacing == 0)
        {
            var whole = Text(run.Text, run, paint);
            Paint(dc, whole, new Point(x, baseline - whole.Baseline), paint);
            x += whole.WidthIncludingTrailingWhitespace;
        }
        else for (int i = 0; i < run.Text.Length; i++)
        {
            var text = Text(run.Text[i].ToString(), run, paint);
            double advance = text.WidthIncludingTrailingWhitespace, box = run.Cells && char.IsAsciiDigit(run.Text[i]) ? cell : advance;
            Paint(dc, text, new Point(x + (box - advance) / 2, baseline - text.Baseline), paint);
            x += box + (i < run.Text.Length - 1 ? spacing : 0);
        }
        if (dim) dc!.Pop();
        return x;
    }
    // DrawText moves each line to the nearest whole device pixel, which undoes the sub-pixel baseline from Baseline below:
    // at 11.5 DIP the cap height is not a whole number of pixels, so the snapped digits end up to half a pixel off centre.
    // Filling the glyph outlines keeps the exact position the metrics give.
    private static void Paint(DrawingContext? dc, FormattedText text, Point origin, Brush paint)
    {
        if (dc is null) return;
        var outline = text.BuildGeometry(origin); outline.Freeze();
        dc.DrawGeometry(paint, null, outline);
    }
    private static readonly Dictionary<(Typeface, double, double), double> DigitCells = [];
    private double DigitCell(Run run)
    {
        var key = (run.Face, run.Size, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (!DigitCells.TryGetValue(key, out var cell))
            DigitCells[key] = cell = "0123456789".Max(digit => Text(digit.ToString(), run, Brushes.White).WidthIncludingTrailingWhitespace);
        return cell;
    }
    private FormattedText Text(string text, Run run, Brush paint)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, run.Face, run.Size, paint, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    // The ink of the run's digit 0, measured from its baseline.
    private Rect Ink(Run run)
    {
        var zero = Text("0", run, Brushes.White);
        var ink = zero.BuildGeometry(new Point()).Bounds;
        ink.Offset(0, -zero.Baseline);
        return ink;
    }
    // The baseline that centres the ink of the run's digits on the given height.
    private double Baseline(Run run, double middle)
    {
        var ink = Ink(run);
        return middle - (ink.Top + ink.Bottom) / 2;
    }
    // How tall a face's digits are per DIP of its size.
    private static readonly Dictionary<Typeface, double> DigitHeights = [];
    private double InkPerEm(Typeface face)
    {
        if (!DigitHeights.TryGetValue(face, out var height)) DigitHeights[face] = height = Ink(new Run("0", face, 100)).Height / 100;
        return height;
    }

    // Dot matrix: 5 × 7 digits drawn as dots, 0.85 of the pitch across, on the given pitch (2 DIP for the time); only the lit
    // dots are drawn.
    private static readonly Dictionary<char, string[]> DotFont = new()
    {
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"], ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"], ['3'] = ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"], ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"], ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"], ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
        [':'] = ["0", "0", "1", "0", "1", "0", "0"], ['/'] = ["001", "001", "010", "010", "010", "100", "100"], ['-'] = ["000", "000", "000", "111", "000", "000", "000"]
    };
    private double Dots(DrawingContext? dc, string text, double x, double pitch, double middle)
    {
        double dot = .85 * pitch, gap = .9 * pitch;
        double top = middle - (6 * pitch + dot) / 2, start = x;
        for (int i = 0; i < text.Length; i++)
        {
            var rows = DotFont[text[i]];
            double width = (rows[0].Length - 1) * pitch + dot;
            if (dc is not null)
            {
                var brush = GlyphBrush(x + width / 2);
                for (int row = 0; row < rows.Length; row++)
                    for (int column = 0; column < rows[row].Length; column++)
                        if (rows[row][column] == '1') dc.DrawEllipse(brush, null, new Point(x + column * pitch + dot / 2, top + row * pitch + dot / 2), dot / 2, dot / 2);
            }
            x += width + (i < text.Length - 1 ? gap : 0);
        }
        return x - start;
    }

    // Pixel: the dot matrix's digits as solid square blocks with no gap, so each lit block meets its neighbors and the strokes read
    // as one pixel-art glyph. A block is a whole number of device pixels (PixelStairs.Step), 2 DIP for the time. Blocks lie on
    // whole device pixels at every scale: the face starts on a whole device pixel (OnRender), and a block is the device pixels its
    // square covers.
    private double Blocks(DrawingContext? dc, string text, double x, double block, double middle)
    {
        double device = Device;
        double Snap(double value) => Math.Round(value * device, MidpointRounding.AwayFromZero) / device;
        double gap = block, top = Snap(middle - 7 * block / 2), start = x;
        for (int i = 0; i < text.Length; i++)
        {
            var rows = DotFont[text[i]];
            if (dc is not null)
            {
                var brush = GlyphBrush(x + rows[0].Length * block / 2);
                for (int row = 0; row < rows.Length; row++)
                    for (int column = 0; column < rows[row].Length; column++)
                        if (rows[row][column] == '1')
                        {
                            double left = Snap(x + column * block), right = Snap(x + (column + 1) * block);
                            double y = Snap(top + row * block), bottom = Snap(top + (row + 1) * block);
                            dc.DrawRectangle(brush, null, new Rect(left, y, right - left, bottom - y));
                        }
            }
            x += rows[0].Length * block + (i < text.Length - 1 ? gap : 0);
        }
        return x - start;
    }

    // Seven segments with 1.5 DIP strokes, 12 DIP tall at k = 1, leaning 7 degrees; only the lit segments are drawn.
    private const double Stroke = 1.5, Segment = 4.6, SegmentGlyph = Segment + 2 * Stroke - .1, SegmentHeight = 12, Slash = 4;
    private static readonly Dictionary<char, Rect> Segments7 = new()
    {
        ['a'] = new(Stroke - .05, 0, Segment, Stroke), ['b'] = new(Segment + Stroke - .1, Stroke - .25, Stroke, Segment),
        ['c'] = new(Segment + Stroke - .1, Segment + Stroke + .05, Stroke, Segment), ['d'] = new(Stroke - .05, 2 * Segment + Stroke - .2, Segment, Stroke),
        ['e'] = new(0, Segment + Stroke + .05, Stroke, Segment), ['f'] = new(0, Stroke - .25, Stroke, Segment),
        ['g'] = new(Stroke - .05, (2 * Segment + Stroke - .2) / 2, Segment, Stroke)
    };
    private static readonly string[] SegmentMap = ["abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg"];
    private double Segments(DrawingContext? dc, string text, double x, double k, double middle)
    {
        const double gap = 1.4;
        static double Advance(char ch) => ch switch { ':' => Stroke, '/' => Slash, _ => SegmentGlyph };
        double width = text.Sum(Advance) + gap * (text.Length - 1);
        if (dc is null) return width * k;
        dc.PushTransform(new TranslateTransform(x, middle - SegmentHeight / 2 * k));
        dc.PushTransform(new ScaleTransform(k, k));
        dc.PushTransform(new SkewTransform(-7, 0, width / 2, SegmentHeight / 2));
        double left = 0;
        foreach (var ch in text)
        {
            var brush = GlyphBrush(x + (left + Advance(ch) / 2) * k);
            if (ch == ':')
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(left, 3.3, Stroke, Stroke), .6, .6);
                dc.DrawRoundedRectangle(brush, null, new Rect(left, 7.6, Stroke, Stroke), .6, .6);
            }
            else if (ch == '/')
            {
                var slash = new StreamGeometry();
                using (var line = slash.Open())
                {
                    line.BeginFigure(new Point(left, SegmentHeight - .2), true, true);
                    line.LineTo(new Point(left + Stroke, SegmentHeight - .2), false, false);
                    line.LineTo(new Point(left + Slash, 0), false, false);
                    line.LineTo(new Point(left + Slash - Stroke, 0), false, false);
                }
                slash.Freeze();
                dc.DrawGeometry(brush, null, slash);
            }
            else foreach (var key in ch == '-' ? "g" : SegmentMap[ch - '0'])
            {
                var rect = Segments7[key]; rect.Offset(left, 0);
                dc.DrawRoundedRectangle(brush, null, rect, .6, .6);
            }
            left += Advance(ch) + gap;
        }
        dc.Pop(); dc.Pop(); dc.Pop();
        return width * k;
    }

    // Drawn glyphs take whole colors: the gradient's color where the glyph's middle stands, left to right across the face.
    private Brush GlyphBrush(double center)
    {
        if (_colors.Length == 1) return UI.Brush(_colors[0].ToString());
        double t = Math.Clamp((center - _paintStart) / _paintWidth, 0, 1) * (_colors.Length - 1);
        int segment = Math.Min((int)t, _colors.Length - 2);
        var (from, to, f) = (_colors[segment], _colors[segment + 1], t - segment);
        byte Mix(byte a, byte b) => (byte)Math.Round(a * (1 - f) + b * f);
        return UI.Brush(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)).ToString());
    }
}
