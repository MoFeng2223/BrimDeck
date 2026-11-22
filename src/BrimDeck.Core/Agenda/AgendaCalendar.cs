namespace BrimDeck.Core.Agenda;

// How the planner page lays itself out (AgendaLayout) and the dates its month and week show. Weeks begin on Monday.
// Wide: the month (238 wide) beside the chosen day. Narrow: the chosen day's week over the day, the three switches beside the
// month's name. Short: the day alone under a row naming it, for a page too low for a week.
public enum AgendaLayout { Wide, Narrow, Short }

public static class AgendaCalendar
{
    // The month column; a day's cell is at least CellHeight and at most MaxCellHeight high, its six rows filling the column. The
    // month needs its title (24), the weekdays (4 + 18) and six rows. A week is WeekHeight high; Narrow needs its title, the
    // week (6 + 46) and room for a row of the day and the add bar below it.
    public const double MonthWidth = 238, CellHeight = 36, MaxCellHeight = 50, WeekHeight = 46, MonthHead = 24 + 4 + 18;
    public const double WideHeight = MonthHead + 6 * CellHeight, NarrowHeight = 24 + 6 + WeekHeight + 6 + 40 + 34;
    // Below this panel width the month gives way to the week.
    public const double WideWidth = 600;

    // panelWidth: the page's width; height: the height under the panel's header that the page has.
    public static AgendaLayout LayoutFor(double panelWidth, double height)
        => panelWidth >= WideWidth && height >= WideHeight ? AgendaLayout.Wide : height >= NarrowHeight ? AgendaLayout.Narrow : AgendaLayout.Short;

    // A cell's height in the wide month: six rows fill what the column has under its head, within the cell's limits.
    public static double CellHeightFor(double height) => Math.Clamp(Math.Floor((height - MonthHead) / 6), CellHeight, MaxCellHeight);

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    // The first day the month view shows: the Monday of the week the month begins in.
    public static DateOnly FirstShown(int year, int month) => WeekStart(new DateOnly(year, month, 1));

    // The weeks the month runs over: 4 (a February from a Monday), 5 or 6.
    public static int Rows(int year, int month)
    {
        var first = FirstShown(year, month);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return (last.DayNumber - first.DayNumber) / 7 + 1;
    }

    // Where a new event on day starts: on today the next half hour (14:20 gives 14:30), at the latest 23:00; on another day 09:00.
    public static DateTime NewEventStart(DateOnly day, DateTime now)
    {
        var midnight = AgendaTime.Midnight(day);
        if (day != DateOnly.FromDateTime(now)) return midnight.AddHours(9);
        double minutes = Math.Ceiling(now.TimeOfDay.TotalMinutes / 30) * 30;
        return midnight.AddMinutes(Math.Min(minutes, 23 * 60));
    }

    // The minute of the day the block view's time line opens at (its top edge): on today an hour before now; on another day half an
    // hour before the first timed event; with none, half past seven, so 8:00 shows near the top.
    public static double OpeningMinute(DateOnly day, DateTime now, IEnumerable<AgendaSegment> segments)
    {
        if (day == DateOnly.FromDateTime(now)) return Math.Max(0, now.TimeOfDay.TotalMinutes - 60);
        var first = segments.Where(segment => !segment.AllDay).Select(segment => (DateTime?)segment.Start).Min();
        return first is { } start ? Math.Max(0, (start - AgendaTime.Midnight(day)).TotalMinutes - 30) : 7.5 * 60;
    }
}
