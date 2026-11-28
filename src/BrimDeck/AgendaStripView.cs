using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// The drawing of the collapsed row's planner part (CompactPart.Agenda): for each kind shown (AgendaStrip.Shown) a mark and its
// words, side by side. An event: a calendar, its time and its title ("16:30 取快递"); within the hour the time is how long is
// left to it, in the accent; under way the mark is a ring that fills as it goes, and the words after the title say how long is
// left. To-dos: a list and how many are unfinished, or the first one's words and "+2"; "过期 1" in the caution color when some
// are past their due day. The note: a page and its words. A text is never longer than AgendaStrip.MaxUnits.
//
// The part is a fixed part of the row (MainWindow.CompactAgenda.cs). An auto-width surface takes what it asks for (level 0). A
// surface with a width of its own keeps it, and the part gives up what does not fit, a step at a time:
//   0: everything.
//   1: the to-dos as a number.
//   2: beside a note, the event as its time alone.
//   3: the one text left (the note's, else the event's title) glides in the room there is (slot), as the title of a song does.
//   4: no words: the event's time, the to-dos' number, the note's mark alone.
//   5: the marks alone, with a dot that says there is something.
internal static class AgendaStripView
{
    public const int Levels = 6, ScrollLevel = 3;

    // The element, each kind's stretch along it (for the page the panel opens on, MainWindow.CompactAgenda.cs) and the gliding
    // text of level 3, if any.
    public sealed record Built(Grid Element, IReadOnlyList<(AgendaStripKind Kind, double Left, double Right)> Kinds, MusicMarquee? Marquee);

    // The marks on the 16 unit grid of the row's other marks (CompactStacks.Tray), drawn with the same 1.35 DIP line.
    private static readonly Geometry Calendar = Frozen("M 2.5,4 H 13.5 A 1,1 0 0 1 14.5,5 V 12.5 A 1,1 0 0 1 13.5,13.5 H 2.5 A 1,1 0 0 1 1.5,12.5 V 5 A 1,1 0 0 1 2.5,4 Z M 1.5,7 H 14.5 M 5,2.5 V 5 M 11,2.5 V 5");
    private static readonly Geometry List = Frozen("M 2,4 L 3.2,5.2 L 5.2,3 M 2,8 L 3.2,9.2 L 5.2,7 M 2,12 L 3.2,13.2 L 5.2,11 M 7.5,4.2 H 14 M 7.5,8.2 H 14 M 7.5,12.2 H 14");
    private static readonly Geometry Page = Frozen("M 4,2 H 9.5 L 13,5.5 V 13 A 1,1 0 0 1 12,14 H 4 A 1,1 0 0 1 3,13 V 3 A 1,1 0 0 1 4,2 Z M 5.5,7.5 H 10.5 M 5.5,10 H 10.5 M 5.5,12.2 H 8");
    private static Geometry Frozen(string data) { var geometry = Geometry.Parse(data); geometry.Freeze(); return geometry; }

    public static double Height(double scale) => CompactMusicLayout.Whole(18 * CompactMusicLayout.SafeScale(scale));
    private static double Font(double s) => CompactMusicLayout.Half(11.5 * s);
    // The least room a gliding text is given; with less, the part goes on to level 4.
    public static double MinimumSlot(double scale) => CompactMusicLayout.Whole(36 * CompactMusicLayout.SafeScale(scale));

    // slot: the room of level 3's gliding text. rested: the text that has glided its laps already; shown again, it starts at rest.
    public static Built Row(AgendaStripContent content, IReadOnlyList<AgendaStripKind> shown, DateTime now, AgendaStripEventForm eventForm, AgendaStripTodoForm todoForm,
        int level, double slot, double scale, double dpi, PanelLook look, string? rested = null)
    {
        double s = CompactMusicLayout.SafeScale(scale), height = Height(scale), middle = CompactLine.Middle(height, dpi), font = Font(s);
        double icon = CompactMusicLayout.Whole(14 * s), afterIcon = CompactMusicLayout.Whole(4 * s), between = CompactMusicLayout.Whole(5 * s), apart = CompactMusicLayout.Whole(10 * s);
        var grid = new Grid { Height = height, Background = Brushes.Transparent };
        var kinds = new List<(AgendaStripKind, double, double)>();
        MusicMarquee? marquee = null;
        bool hasNote = shown.Contains(AgendaStripKind.Note) && content.Note is not null;
        // The one text that glides at level 3.
        AgendaStripKind? glides = level != ScrollLevel ? null : hasNote ? AgendaStripKind.Note : shown.Contains(AgendaStripKind.Event) && content.Event is not null && eventForm == AgendaStripEventForm.Title ? AgendaStripKind.Event : null;
        double x = 0;

        void Mark(FrameworkElement mark)
        {
            mark.HorizontalAlignment = HorizontalAlignment.Left; mark.VerticalAlignment = VerticalAlignment.Top; mark.UseLayoutRounding = false;
            mark.Margin = new Thickness(x, middle - icon / 2, 0, 0);
            grid.Children.Add(mark);
            x += icon;
        }
        // Words start on a whole device pixel (CompactStacks.Tray tells why) and stand on the row's baseline.
        void Words(string text, string color, bool strong, double gap)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = UI.PanelFont, FontSize = font, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Foreground = UI.Brush(color),
                TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Left
            };
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Taken before the margin is set: measured again with it (CompactLine.Place), the block's size holds the margin too.
            double start = DevicePixels.Up(x + gap, dpi), width = block.DesiredSize.Width;
            block.Margin = new Thickness(start, 0, 0, 0);
            CompactLine.Place(block, CompactLine.Above(middle, font, dpi));
            grid.Children.Add(block);
            x = start + width;
        }
        // The gliding text in its room; without room, nothing.
        void Glide(string text, double gap)
        {
            double room = Math.Floor(Math.Max(0, slot));
            if (room <= 0) return;
            double start = DevicePixels.Up(x + gap, dpi);
            marquee = new MusicMarquee
            {
                Text = text, Width = room, Height = height, FontSize = font, EdgeWidth = CompactMusicLayout.Whole(6 * s), ScrollAllowed = room >= MinimumSlot(scale), CenterWhenFits = false,
                Backdrop = look.MarqueeBackdrop, InkFade = look.InkFade, Baseline = CompactLine.Above(middle, font, dpi), Laps = true, Rested = text == rested,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(start, 0, 0, 0)
            };
            grid.Children.Add(marquee);
            x = start + room;
        }

        foreach (var kind in shown)
        {
            double from = x > 0 ? x + apart : 0;
            x = from;
            switch (kind)
            {
                case AgendaStripKind.Event when content.Event is { } item:
                {
                    bool soon = item.State == AgendaStripState.Soon, running = item.State == AgendaStripState.Running;
                    Mark(running ? Ring(item.Progress, icon, s, look) : Glyph(Calendar, icon, soon ? look.Accent : look.Secondary));
                    if (level >= 5) break;
                    string time = AgendaStrip.Time(item, now), title = AgendaStrip.Clip(item.Event.Title);
                    bool titled = eventForm == AgendaStripEventForm.Title && level < 4 && !(level >= 2 && hasNote);
                    if (!titled) { Words(time, soon ? look.Accent : look.Primary, true, afterIcon); break; }
                    // Under way the title comes first and what is left after it; else the time, then the title.
                    if (running)
                    {
                        if (glides == kind) Glide(title, afterIcon); else Words(title, look.Primary, false, afterIcon);
                        Words(time, look.Secondary, false, between);
                    }
                    else
                    {
                        Words(time, soon ? look.Accent : item.State == AgendaStripState.AllDay ? look.Secondary : look.Primary, item.State != AgendaStripState.AllDay, afterIcon);
                        if (glides == kind) Glide(title, between); else Words(title, look.Primary, false, between);
                    }
                    break;
                }
                case AgendaStripKind.Todo when content.Todos is { } todos:
                {
                    string caution = look.Hex("caution");
                    bool onlyLate = todos.OverdueDays > 0, worded = todoForm == AgendaStripTodoForm.Text && level == 0;
                    Mark(Glyph(List, icon, onlyLate && worded ? caution : look.Secondary));
                    if (level >= 5) break;
                    if (worded)
                    {
                        // With only overdue ones left, the first of them in the caution color and how long it is overdue.
                        if (onlyLate) Words(AgendaStrip.Clip(todos.Text) + " · " + AgendaStrip.OverdueFor(todos.OverdueDays), caution, false, afterIcon);
                        else Words(AgendaStrip.Clip(todos.Text), look.Primary, false, afterIcon);
                        if (todos.More > 0) Words("+" + todos.More.ToString(CultureInfo.InvariantCulture), look.Secondary, true, between);
                        if (!onlyLate && todos.Overdue > 0) Words(AgendaStrip.Overdue(todos.Overdue), caution, true, between);
                    }
                    else
                    {
                        Words(todos.Open > 99 ? "99+" : todos.Open.ToString(CultureInfo.InvariantCulture), look.Primary, true, afterIcon);
                        if (todos.Overdue > 0 && level < 4) Words(AgendaStrip.Overdue(todos.Overdue), caution, true, between);
                    }
                    break;
                }
                case AgendaStripKind.Note when content.Note is { } note:
                {
                    Mark(Glyph(Page, icon, look.Secondary));
                    if (level >= 4) break;
                    string text = AgendaStrip.Clip(note);
                    if (glides == kind) Glide(text, afterIcon); else Words(text, look.Primary, false, afterIcon);
                    break;
                }
                default: x = from > 0 ? from - apart : 0; continue;
            }
            // The marks alone: a dot at the mark's corner says the kind has something.
            if (level >= 5)
            {
                double dot = Math.Max(3, CompactMusicLayout.Whole(4 * s));
                grid.Children.Add(new Ellipse
                {
                    Width = dot, Height = dot, Fill = UI.Brush(look.Accent), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    UseLayoutRounding = false, Margin = new Thickness(x - dot * .6, middle - icon / 2 - dot * .25, 0, 0)
                });
                x += dot * .4;
            }
            kinds.Add((kind, from, x));
        }
        grid.Width = DevicePixels.Up(x, dpi);
        return new(grid, kinds, marquee);
    }

    // The part on a notch at a side edge: the kinds one below the other, each a mark with a short figure under it where the
    // column has the room (an event's clock time, the to-dos' number); the note is its mark alone.
    public static Built Column(AgendaStripContent content, IReadOnlyList<AgendaStripKind> shown, DateTime now, double column, double scale, double dpi, PanelLook look)
    {
        double s = CompactMusicLayout.SafeScale(scale), icon = CompactMusicLayout.Whole(14 * s), font = CompactMusicLayout.Half(9.5 * s), gap = CompactMusicLayout.Whole(6 * s);
        var grid = new Grid { Width = column, Background = Brushes.Transparent };
        var kinds = new List<(AgendaStripKind, double, double)>();
        double y = 0;
        foreach (var kind in shown)
        {
            double from = y > 0 ? y + gap : 0;
            FrameworkElement? mark = null; string? figure = null; string color = look.Primary;
            switch (kind)
            {
                case AgendaStripKind.Event when content.Event is { } item:
                    bool soon = item.State == AgendaStripState.Soon;
                    mark = item.State == AgendaStripState.Running ? Ring(item.Progress, icon, s, look) : Glyph(Calendar, icon, soon ? look.Accent : look.Secondary);
                    if (item.State == AgendaStripState.Later) figure = AgendaStrip.Time(item, now);
                    break;
                case AgendaStripKind.Todo when content.Todos is { } todos:
                    mark = Glyph(List, icon, look.Secondary);
                    figure = todos.Open > 99 ? "99+" : todos.Open.ToString(CultureInfo.InvariantCulture);
                    if (todos.Overdue > 0) color = look.Hex("caution");
                    break;
                case AgendaStripKind.Note when content.Note is not null:
                    mark = Glyph(Page, icon, look.Secondary); break;
            }
            if (mark is null) continue;
            y = from;
            mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, y, 0, 0);
            grid.Children.Add(mark);
            y += icon;
            if (figure is not null)
            {
                var block = new TextBlock
                {
                    Text = figure, FontFamily = UI.PanelFont, FontSize = font, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(color), TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
                };
                Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
                block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // A figure wider than the column is left out rather than cut.
                if (block.DesiredSize.Width <= column + .5)
                {
                    block.Margin = new Thickness(0, y + CompactMusicLayout.Whole(1 * s), 0, 0);
                    grid.Children.Add(block);
                    y += CompactMusicLayout.Whole(1 * s) + Math.Ceiling(block.DesiredSize.Height);
                }
            }
            kinds.Add((kind, from, y));
        }
        grid.Height = Math.Ceiling(y);
        return new(grid, kinds, null);
    }

    private static Path Glyph(Geometry data, double size, string color)
    {
        double unit = size / 16;
        return new Path
        {
            Data = data, Stroke = UI.Brush(color), StrokeThickness = 1.35 / unit, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 16, Height = 16, LayoutTransform = new ScaleTransform(unit, unit), IsHitTestVisible = false
        };
    }

    // An event under way: a faint ring with the part gone by in the accent, from the top clockwise.
    private static FrameworkElement Ring(double progress, double size, double s, PanelLook look)
    {
        double stroke = Math.Max(1.5, CompactMusicLayout.Half(2 * s)), diameter = size - CompactMusicLayout.Whole(2 * s), radius = (diameter - stroke) / 2, centre = size / 2;
        var accent = look.ColorOf("accent");
        var ring = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        var track = new Ellipse { Width = diameter, Height = diameter, Stroke = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B)), StrokeThickness = stroke };
        Canvas.SetLeft(track, centre - diameter / 2); Canvas.SetTop(track, centre - diameter / 2);
        ring.Children.Add(track);
        double share = Math.Clamp(double.IsFinite(progress) ? progress : 0, 0, 1);
        if (share >= .999) { track.Stroke = new SolidColorBrush(accent); return ring; }
        if (share <= .001) return ring;
        double angle = share * 2 * Math.PI;
        var end = new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle));
        var arc = new PathGeometry([new PathFigure(new Point(centre, centre - radius), [new ArcSegment(end, new Size(radius, radius), 0, share > .5, SweepDirection.Clockwise, true)], false)]);
        arc.Freeze();
        ring.Children.Add(new Path { Data = arc, Stroke = new SolidColorBrush(accent), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return ring;
    }
}
