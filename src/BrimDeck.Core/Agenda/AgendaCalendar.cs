namespace BrimDeck.Core.Agenda;

// How the planner page lays itself out (AgendaLayout) and the dates its month and week show.
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

    // The first day of day's week; weeks begin on Monday unless the settings say Sunday (DeckSettings.AgendaWeekStart).
    public static DateOnly WeekStart(DateOnly day, AgendaWeekStart first = AgendaWeekStart.Monday)
        => day.AddDays(-(((int)day.DayOfWeek + (first == AgendaWeekStart.Sunday ? 0 : 6)) % 7));

    // The seven weekdays in the order a week shows them.
    public static IReadOnlyList<DayOfWeek> Weekdays(AgendaWeekStart first = AgendaWeekStart.Monday)
        => [.. Enumerable.Range(first == AgendaWeekStart.Sunday ? 0 : 1, 7).Select(day => (DayOfWeek)(day % 7))];

    // The first day the month view shows: the first day of the week the month begins in.
    public static DateOnly FirstShown(int year, int month, AgendaWeekStart first = AgendaWeekStart.Monday) => WeekStart(new DateOnly(year, month, 1), first);

    // The weeks the month runs over: 4 (a February that begins on the week's first day), 5 or 6.
    public static int Rows(int year, int month, AgendaWeekStart first = AgendaWeekStart.Monday)
    {
        var shown = FirstShown(year, month, first);
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return (last.DayNumber - shown.DayNumber) / 7 + 1;
    }

    // Where a new event on day starts: on today the next half hour (14:20 gives 14:30), at the latest 23:00; on another day 09:00.
    public static DateTime NewEventStart(DateOnly day, DateTime now)
    {
        var midnight = AgendaTime.Midnight(day);
        if (day != DateOnly.FromDateTime(now)) return midnight.AddHours(9);
        double minutes = Math.Ceiling(now.TimeOfDay.TotalMinutes / 30) * 30;
        return midnight.AddMinutes(Math.Min(minutes, 23 * 60));
    }

    // How long a new event lasts, in minutes: what the settings offer (DeckSettings.AgendaEventMinutes) and what a file without a
    // choice has.
    public static readonly IReadOnlyList<int> EventLengths = [15, 30, 45, 60, 90, 120];
    public const int DefaultEventLength = 60;
    // Where a new event that starts at start ends; one that would run past midnight ends on the next day.
    public static DateTime NewEventEnd(DateTime start, int minutes) => start.AddMinutes(EventLengths.Contains(minutes) ? minutes : DefaultEventLength);

    // The block view's time line shows the working day, DayHours from DayStart (8:00 to 18:00), in the height it has: an hour is as
    // high as fits them between the line's top (TimelineTop) and the fade at its foot (TimelineFoot), in whole DIPs, at least
    // MinHourHeight and at most MaxHourHeight (a taller view shows more hours).
    public const int DayStart = 8, DayHours = 10;
    public const double TimelineTop = 8, TimelineFoot = 18, MinHourHeight = 14, MaxHourHeight = 32;

    // An hour's height for a time line whose view is view high; before the view has a height, the most.
    public static double HourHeightFor(double view)
        => view > 0 ? Math.Clamp(Math.Floor((view - TimelineTop - TimelineFoot) / DayHours), MinHourHeight, MaxHourHeight) : MaxHourHeight;

    // The minute of the day the block view's time line opens at (its top edge), always on a whole hour so its name shows whole:
    // the hour before the first timed event that starts on the day (one that runs on from the evening before only when there is
    // none), and DayStart on a day without a timed event. It opens no later than the last DayHours of the day. Today opens as
    // any other day does: its "now" line shows the time.
    public static double OpeningMinute(IEnumerable<AgendaSegment> segments)
    {
        var timed = segments.Where(segment => !segment.AllDay).ToList();
        var first = (timed.Where(segment => !segment.FromEarlier).Select(segment => (DateTime?)segment.Start).Min()) ?? timed.Select(segment => (DateTime?)segment.Start).Min();
        if (first is not { } start) return DayStart * 60;
        return Math.Clamp((start.Hour - 1) * 60, 0, (24 - DayHours) * 60);
    }
}
