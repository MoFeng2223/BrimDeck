namespace BrimDeck.Core.Agenda;

// How the planner page lays itself out (AgendaLayout) and the dates its month and week show. Weeks begin on Monday.
// Wide: the month (210 to 238 wide) beside the chosen day. Narrow: the chosen day's week over the day, the three switches beside
// the month's name. Short: the day alone under a row naming it, for a page too low for a week.
public enum AgendaLayout { Wide, Narrow, Short }

public static class AgendaCalendar
{
    // The month column; a day's cell is at least MinCellHeight and at most MaxCellHeight high, its six rows filling the column, and
    // its date and marks are placed as in a cell of CellHeight (higher in a lower cell). The month needs its title (24), the
    // weekdays (4 + 18), a gap (4) that keeps the first row's chosen fill off the weekdays, and six rows. A week is WeekHeight
    // high; Narrow needs its title, the week (6 + 46) and room for a row of the day and the add bar.
    public const double MonthWidth = 238, CellHeight = 36, MinCellHeight = 30, MaxCellHeight = 50, WeekHeight = 46, MonthGap = 4, MonthHead = 24 + 4 + 18 + MonthGap;
    public const double WideHeight = MonthHead + 6 * MinCellHeight, NarrowHeight = 24 + 6 + WeekHeight + 6 + 40 + 34;
    // Below this panel width the month gives way to the week. From WideWidth to FullWidth the month (down to NarrowMonthWidth,
    // 30 a day) and the gap holding the line between it and the day (Divider, down to NarrowDivider) narrow, so the day keeps
    // room for its title and its three switches.
    public const double WideWidth = 540, FullWidth = 600, NarrowMonthWidth = 210, Divider = 29, NarrowDivider = 21;

    // panelWidth: the page's width; height: the height under the panel's header that the page has.
    public static AgendaLayout LayoutFor(double panelWidth, double height)
        => panelWidth >= WideWidth && height >= WideHeight ? AgendaLayout.Wide : height >= NarrowHeight ? AgendaLayout.Narrow : AgendaLayout.Short;

    // The wide month's width and the gap after it, for the page's width: whole DIPs, full from FullWidth on.
    public static (double Month, double Divider) WideColumns(double panelWidth)
    {
        double share = Math.Clamp((panelWidth - WideWidth) / (FullWidth - WideWidth), 0, 1);
        return (Math.Round(NarrowMonthWidth + share * (MonthWidth - NarrowMonthWidth)), Math.Round(NarrowDivider + share * (Divider - NarrowDivider)));
    }

    // A cell's height in the wide month: six rows fill what the column has under its head, within the cell's limits.
    public static double CellHeightFor(double height) => Math.Clamp(Math.Floor((height - MonthHead) / 6), MinCellHeight, MaxCellHeight);

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

    // The block view's time line shows the working day, DayHours from DayStart (8:00 to 18:00), in the height it has: an hour is as
    // high as fits them between the line's top (TimelineTop) and the fade at its foot (TimelineFoot), in whole DIPs, at least
    // MinHourHeight and at most MaxHourHeight (a taller view shows more hours).
    public const int DayStart = 8, DayHours = 10;
    public const double TimelineTop = 8, TimelineFoot = 18, MinHourHeight = 14, MaxHourHeight = 32;

    // An hour's height for a time line whose view is view high; before the view has a height, the most.
    public static double HourHeightFor(double view)
        => view > 0 ? Math.Clamp(Math.Floor((view - TimelineTop - TimelineFoot) / DayHours), MinHourHeight, MaxHourHeight) : MaxHourHeight;

    // The minute of the day the block view's time line opens at (its top edge), always on a whole hour so its name shows whole. It
    // opens on DayStart, showing the working day, unless what must be seen lies outside it: on today now, with an hour before and an
    // hour after it; on another day the first timed event that starts on the day (one from the evening before only when there is
    // none) and its first hour. Then the view moves just far enough to show it.
    public static double OpeningMinute(DateOnly day, DateTime now, IEnumerable<AgendaSegment> segments)
    {
        if (day == DateOnly.FromDateTime(now)) return Opening(now.TimeOfDay.TotalMinutes - 60, now.TimeOfDay.TotalMinutes + 60);
        var timed = segments.Where(segment => !segment.AllDay).ToList();
        var first = (timed.Where(segment => !segment.FromEarlier).Select(segment => (DateTime?)segment.Start).Min()) ?? timed.Select(segment => (DateTime?)segment.Start).Min();
        if (first is not { } start) return DayStart * 60;
        double minute = (start - AgendaTime.Midnight(day)).TotalMinutes;
        return Opening(minute, minute + 60);
    }

    // The whole hour the view opens at so that from to to (minutes of the day) is in it: DayStart where it is, else the nearest.
    private static double Opening(double from, double to)
    {
        double top = DayStart * 60;
        if (from < top) top = Math.Floor(from / 60) * 60;
        else if (to > top + DayHours * 60) top = Math.Ceiling(to / 60) * 60 - DayHours * 60;
        return Math.Clamp(top, 0, (24 - DayHours) * 60);
    }
}
