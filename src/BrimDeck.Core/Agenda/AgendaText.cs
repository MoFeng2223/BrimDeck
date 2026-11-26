namespace BrimDeck.Core.Agenda;

// The planner's words, in the interface language (Loc). Dates are written without the year unless it differs from the year
// they are read in.
public static class AgendaText
{
    private static readonly string[] ChineseWeekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static readonly string[] EnglishWeekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] EnglishMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] EnglishMonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // "周日" / "Sun".
    public static string Weekday(DayOfWeek day) => Loc.T(ChineseWeekdays[(int)day], EnglishWeekdays[(int)day]);

    // "2026年11月" / "November 2026": the month view's title.
    public static string Month(int year, int month) => Loc.T($"{year}年{month}月", $"{EnglishMonthNames[month - 1]} {year}");

    // "11月23日" / "Nov 23"; with the year when it is not yearOf's ("2027年1月2日" / "Jan 2, 2027").
    public static string Date(DateOnly day, int? yearOf = null) => Date(day, yearOf is { } shown && shown != day.Year);
    private static string Date(DateOnly day, bool year)
        => Loc.T(year ? $"{day.Year}年{day.Month}月{day.Day}日" : $"{day.Month}月{day.Day}日",
            year ? $"{EnglishMonths[day.Month - 1]} {day.Day}, {day.Year}" : $"{EnglishMonths[day.Month - 1]} {day.Day}");

    // The day's heading: "11月22日 周日" / "Sun, Nov 22".
    public static string DayTitle(DateOnly day, DateOnly today)
        => Loc.T($"{Date(day, today.Year)} {Weekday(day.DayOfWeek)}", $"{Weekday(day.DayOfWeek)}, {Date(day, today.Year)}");

    // Beside the heading: "今天", "明天", "后天", "昨天", "前天", "4 天后", "3 天前" (English: "Today", "Tomorrow", "In 2 days",
    // "Yesterday", "2 days ago"), within 30 days; null further away.
    public static string? Relative(DateOnly day, DateOnly today)
    {
        int days = day.DayNumber - today.DayNumber;
        return days switch
        {
            0 => Loc.T("今天", "Today"),
            1 => Loc.T("明天", "Tomorrow"),
            2 => Loc.T("后天", "In 2 days"),
            -1 => Loc.T("昨天", "Yesterday"),
            -2 => Loc.T("前天", "2 days ago"),
            > 2 and <= 30 => Loc.T($"{days} 天后", $"In {days} days"),
            < -2 and >= -30 => Loc.T($"{-days} 天前", $"{-days} days ago"),
            _ => null
        };
    }

    // "14:00"; a time at 00:00 that ends day reads "24:00".
    public static string Clock(DateTime time, DateOnly day)
        => time == AgendaTime.Midnight(day.AddDays(1)) ? "24:00" : time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // "40 分钟", "2 小时" / "40 minutes", "2 hours": whole minutes up to an hour (at least 1), whole hours from then on.
    public static string Length(TimeSpan span)
    {
        long minutes = Math.Max(1, (long)Math.Ceiling(span.TotalMinutes - 1e-9));
        if (minutes < 60) return Loc.T($"{minutes} 分钟", Loc.Count(minutes, "minute", "minutes"));
        long hours = minutes / 60;
        return Loc.T($"{hours} 小时", Loc.Count(hours, "hour", "hours"));
    }

    // The status of an event's segment, the second line of its row; null when there is nothing to say (an all-day event of one day,
    // a timed event of one day on a day other than today).
    // On today (now's date): "已结束"; "进行中 · 还剩 40 分钟" ("进行中" alone when it ends on a later day); "2 小时后".
    // An event over two days, shown timed: "从昨天 22:00 开始" on its second day, "到明天 02:00" on its first ("前一天", "次日"
    // when the day shown is not today). Any other event over several days: "第 2 天，共 3 天 · 11月23日 20:00 – 11月25日 12:00".
    // On today these follow the words of the time, joined by " · ".
    public static string? Status(AgendaSegment segment, DateTime now)
    {
        var parts = new List<string>();
        var today = DateOnly.FromDateTime(now);
        if (segment.Day == today && !segment.AllDay)
        {
            var item = segment.Event;
            parts.Add(AgendaQuery.Phase(item, now) switch
            {
                AgendaPhase.Past => Loc.T("已结束", "Ended"),
                AgendaPhase.Ongoing when item.LastDay == today => Loc.T($"进行中 · 还剩 {Length(item.End - now)}", $"In progress · {Length(item.End - now)} left"),
                AgendaPhase.Ongoing => Loc.T("进行中", "In progress"),
                _ => Loc.T($"{Length(item.Start - now)}后", $"In {Length(item.Start - now)}")
            });
        }
        if (segment.MultiDay) parts.Add(Span(segment, today));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // A block's times: "09:30–11:30", the segment's own (a part running past midnight ends "24:00").
    public static string Times(AgendaSegment segment) => $"{Clock(segment.Start, segment.Day)}–{Clock(segment.End, segment.Day)}";

    // Beside a block's times: "还剩 40 分钟" / "40 minutes left" while it is under way on now's day and ends that day; null otherwise.
    public static string? Left(AgendaSegment segment, DateTime now)
    {
        var item = segment.Event;
        var today = DateOnly.FromDateTime(now);
        if (segment.AllDay || segment.Day != today || item.LastDay != today || AgendaQuery.Phase(item, now) != AgendaPhase.Ongoing) return null;
        return Loc.T($"还剩 {Length(item.End - now)}", $"{Length(item.End - now)} left");
    }

    // "第 2 天，共 3 天" / "Day 2 of 3": an all-day chip of an event over several days.
    public static string DayOf(AgendaSegment segment) => Loc.T($"第 {segment.DayIndex} 天，共 {segment.DayCount} 天", $"Day {segment.DayIndex} of {segment.DayCount}");

    private static string Span(AgendaSegment segment, DateOnly today)
    {
        var item = segment.Event;
        bool isToday = segment.Day == today;
        if (!item.AllDay && segment.DayCount == 2 && !segment.AllDay)
        {
            if (segment.FromEarlier)
            {
                var from = Clock(item.Start, item.FirstDay);
                return isToday ? Loc.T($"从昨天 {from} 开始", $"Started yesterday at {from}") : Loc.T($"从前一天 {from} 开始", $"Started the day before at {from}");
            }
            var to = Clock(item.End, item.LastDay);
            return isToday ? Loc.T($"到明天 {to}", $"Until tomorrow at {to}") : Loc.T($"到次日 {to}", $"Until the next day at {to}");
        }
        return $"{DayOf(segment)} · {Range(item)}";
    }

    // The whole of an event: "11月23日 20:00 – 11月25日 12:00", all day "11月23日 – 11月25日"; the year is written on both ends
    // when they fall in different years.
    public static string Range(AgendaEvent item)
    {
        bool year = item.FirstDay.Year != item.LastDay.Year;
        if (item.AllDay) return $"{Date(item.FirstDay, year)} – {Date(item.LastDay, year)}";
        return $"{Date(item.FirstDay, year)} {Clock(item.Start, item.FirstDay)} – {Date(item.LastDay, year)} {Clock(item.End, item.LastDay)}";
    }

    // One time of an event as its reminder names it: "今天 16:30–17:00", "明天 全天", "11月26日 周四 15:00–16:00"; a time that ends on
    // another day "今天 22:00 – 11月27日 02:00", one over several days all day "11月26日 周四 – 11月28日 周六".
    public static string When(AgendaEvent time, DateOnly today)
    {
        string Day(DateOnly day) => day.DayNumber - today.DayNumber is 0 or 1 ? Relative(day, today)! : DayTitle(day, today);
        if (time.AllDay) return time.DayCount > 1 ? $"{Day(time.FirstDay)} – {Day(time.LastDay)}" : $"{Day(time.FirstDay)} {Loc.T("全天", "all day")}";
        var from = Clock(time.Start, time.FirstDay);
        if (time.DayCount == 1) return $"{Day(time.FirstDay)} {from}–{Clock(time.End, time.FirstDay)}";
        var endDay = DateOnly.FromDateTime(time.End);
        return $"{Day(time.FirstDay)} {from} – {Date(endDay, today.Year)} {Clock(time.End, endDay)}";
    }

    // How an event repeats, for a series that starts on first: "不重复", "每天", "每个工作日", "每周二", "每月 25 日", "每年 11月25日"
    // ("Does not repeat", "Daily", "Every weekday", "Weekly on Tue", "Monthly on day 25", "Yearly on Nov 25").
    public static string Repeat(AgendaRepeat repeat, DateOnly first) => repeat switch
    {
        AgendaRepeat.Daily => Loc.T("每天", "Daily"),
        AgendaRepeat.Weekdays => Loc.T("每个工作日", "Every weekday"),
        AgendaRepeat.Weekly => Loc.T($"每{Weekday(first.DayOfWeek)}", $"Weekly on {Weekday(first.DayOfWeek)}"),
        AgendaRepeat.Monthly => Loc.T($"每月 {first.Day} 日", $"Monthly on day {first.Day}"),
        AgendaRepeat.Yearly => Loc.T($"每年 {Date(first, false)}", $"Yearly on {Date(first, false)}"),
        _ => Loc.T("不重复", "Does not repeat")
    };

    // At the end of a to-do with a due day: "11月30日截止 · 还有 8 天", "明天截止", "今天截止", "11月20日截止 · 已过期 2 天"; a ticked
    // to-do only names the day.
    public static string Due(DateOnly due, DateOnly today, bool done = false)
    {
        var date = Date(due, today.Year);
        int days = due.DayNumber - today.DayNumber;
        if (done) return Loc.T($"{date}截止", $"Due {date}");
        return days switch
        {
            0 => Loc.T("今天截止", "Due today"),
            1 => Loc.T("明天截止", "Due tomorrow"),
            > 1 => Loc.T($"{date}截止 · 还有 {days} 天", $"Due {date} · {Loc.Count(days, "day", "days")} left"),
            _ => Loc.T($"{date}截止 · 已过期 {-days} 天", $"Due {date} · {Loc.Count(-days, "day", "days")} overdue")
        };
    }

    // Over today's to-dos: "昨天有 2 项未完成" or "之前有 3 项未完成".
    public static string CarryOver(AgendaCarryOver carry)
        => carry.AllYesterday
            ? Loc.T($"昨天有 {carry.Count} 项未完成", $"{Loc.Count(carry.Count, "unfinished to-do", "unfinished to-dos")} from yesterday")
            : Loc.T($"之前有 {carry.Count} 项未完成", $"{Loc.Count(carry.Count, "unfinished to-do", "unfinished to-dos")} from earlier days");

    // "Outlook · 王敏", or the application's name alone.
    public static string Source(AgendaSource source)
    {
        var app = source.AppName.Length > 0 ? source.AppName : Loc.T("通知", "Notice");
        return source.Sender is { Length: > 0 } sender ? $"{app} · {sender}" : app;
    }
}
