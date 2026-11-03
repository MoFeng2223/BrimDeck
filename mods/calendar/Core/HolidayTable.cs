namespace BrimDeck.Calendar;

// 调休覆盖表：只有「哪一天补班、哪一天放假」需要人工维护，节日日期本身由农历与节气算出。
// key 为 yyyy-MM-dd；true 表示需要上班的周末，false 表示放假的工作日。
// 国务院每年公布下一年度安排后，在这里补几行即可，无需改代码；留空时只是少一个红点。
public static class HolidayTable
{
    private static readonly Dictionary<string, bool> Overrides = new(StringComparer.Ordinal)
    {
        // 示例（请按当年公告替换）：
        // ["2026-01-04"] = true,   // 周日补班
        // ["2026-02-16"] = false,  // 除夕放假
    };

    public static bool? Workday(DateOnly date) =>
        Overrides.TryGetValue(CalendarEvent.Key(date), out bool value) ? value : null;

    public static int Count => Overrides.Count;
}
