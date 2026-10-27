namespace BrimDeck.Calendar;

public static class MonthGrid
{
    public static DateOnly First(DateOnly day) => new(day.Year, day.Month, 1);

    public static DateOnly Start(DateOnly month, int firstDay)
    {
        var first = First(month);
        int delta = ((int)first.DayOfWeek - Math.Clamp(firstDay, 0, 6) + 7) % 7;
        return first.AddDays(-delta);
    }

    // Always six rows: switching months must never change the cell size or push the panel around.
    public static IReadOnlyList<DateOnly> Build(DateOnly month, int firstDay, int weeks = 6)
    {
        var start = Start(month, firstDay);
        var days = new DateOnly[weeks * 7];
        for (int index = 0; index < days.Length; index++) days[index] = start.AddDays(index);
        return days;
    }

    // The week a single day belongs to: same rows, anchored on that day rather than on a month's first.
    public static DateOnly WeekStart(DateOnly day, int firstDay)
    {
        int delta = ((int)day.DayOfWeek - Math.Clamp(firstDay, 0, 6) + 7) % 7;
        return day.AddDays(-delta);
    }

    public static IReadOnlyList<DateOnly> Week(DateOnly day, int firstDay)
    {
        var start = WeekStart(day, firstDay);
        var days = new DateOnly[7];
        for (int index = 0; index < 7; index++) days[index] = start.AddDays(index);
        return days;
    }

    public static int DayIndex(int firstDay, int index) => (Math.Clamp(firstDay, 0, 6) + index) % 7;
}

// Pure layout arithmetic, so the view never measures before it can decide what to show.
// Three shapes: month + agenda side by side, month alone (tap a day to open its agenda), and a single
// week strip when the panel is too short for six rows. The card chrome (2 DIP margin + padding) comes off first.
public sealed record CalendarLayout(
    double Width, double Height, bool SideBySide, bool AgendaOnly, bool WeekView,
    double MonthWidth, double AgendaWidth,
    double Header, double Weekday, double CellHeight,
    bool ShowLunar, bool ShowDots, bool ShowTitles, int AgendaRows, double Gap, double Padding)
{
    public double MonthArea => SideBySide ? MonthWidth : Width;
    public int Columns => 7;

    public static CalendarLayout For(double width, double height, bool lunar, bool agendaOnly = false)
    {
        double w = double.IsFinite(width) ? Math.Clamp(width, 300, 4000) : 480;
        double h = double.IsFinite(height) ? Math.Clamp(height, 80, 4000) : 266;
        bool week = !agendaOnly && h < 170;
        bool side = !agendaOnly && w >= 500 && (h >= 190 || week);
        double gap = h >= 220 ? 6 : 4;
        double padding = h < 200 ? 4 : 6;
        double header = h < 200 ? 22 : 28;
        double weekday = h < 200 ? 13 : 17;
        double chrome = (padding + 2) * 2;
        double body = h - chrome - header - weekday;
        double cell = week ? Math.Max(24, body) : Math.Max(15, body / 6);
        double month = side ? Math.Clamp(w * (week ? 0.52 : 0.56), 250, week ? 620 : 460) : w;
        double agenda = side ? Math.Max(160, w - month - gap) : w;
        int rows = week ? 1 : 6;
        int agendaRows = Math.Max(1, (int)((h - chrome - header - 20) / 22));
        // A tall week cell has room for the first two events of each day; a month cell does not.
        bool titles = week && cell >= 56;
        return new(w, h, side, agendaOnly, week, month, agenda, header, weekday, cell,
            lunar && cell >= 30, cell >= 20, titles, agendaRows, gap, padding);
    }
}
