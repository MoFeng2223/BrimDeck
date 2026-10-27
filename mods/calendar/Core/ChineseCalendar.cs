using System.Globalization;

namespace BrimDeck.Calendar;

public enum Festival
{
    None = 0, NewYear, Eve, Spring, Lantern, Qingming,
    Labour, DragonBoat, Qixi, MidAutumn, DoubleNinth, National, Laba
}

public sealed record DayInfo(string Lunar, Festival Festival, bool Workday, bool RestDay)
{
    public bool Marked => Festival != Festival.None || Workday || RestDay;
}

// 农历与节日。月/日来自 .NET 的 ChineseLunisolarCalendar，节气来自 SolarTerms；
// 只有「调休补班」依赖 HolidayTable。1901 年以前或 2101 年以后自动降级为空白。
public static class ChineseCalendar
{
    private static readonly ChineseLunisolarCalendar Lunar = new();

    private static readonly string[] MonthNames =
        ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];

    private static readonly string[] Digits = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九"];

    public static DayInfo Describe(DateOnly date)
    {
        var (month, day, leap, known) = LunarOf(date);
        string? term = SolarTerms.Name(date);
        string lunar = "";
        if (known)
        {
            if (term is not null) lunar = term;
            else if (day == 1) lunar = (leap ? "闰" : "") + MonthNames[month - 1];
            else lunar = DayName(day);
        }
        bool? workday = HolidayTable.Workday(date);
        return new DayInfo(lunar, FestivalOf(date, month, day, known, term), workday == true, workday == false);
    }

    private static (int Month, int Day, bool Leap, bool Known) LunarOf(DateOnly date)
    {
        try
        {
            var value = date.ToDateTime(TimeOnly.MinValue);
            int year = Lunar.GetYear(value);
            int month = Lunar.GetMonth(value);
            int day = Lunar.GetDayOfMonth(value);
            int leapMonth = Lunar.GetLeapMonth(year);
            bool leap = leapMonth > 0 && month == leapMonth;
            int actual = leapMonth > 0 && month >= leapMonth ? month - 1 : month;
            return (Math.Clamp(actual, 1, 12), Math.Clamp(day, 1, 30), leap, true);
        }
        catch (ArgumentOutOfRangeException) { return (0, 0, false, false); }
    }

    private static string DayName(int day) => day switch
    {
        <= 0 or > 30 => "",
        10 => "初十",
        20 => "二十",
        30 => "三十",
        _ => (day / 10) switch { 0 => "初", 1 => "十", 2 => "廿", _ => "三" } + Digits[day % 10]
    };

    private static Festival FestivalOf(DateOnly date, int month, int day, bool known, string? term)
    {
        if (term == "清明") return Festival.Qingming;
        if (date.Month == 1 && date.Day == 1) return Festival.NewYear;
        if (date.Month == 5 && date.Day == 1) return Festival.Labour;
        if (date.Month == 10 && date.Day == 1) return Festival.National;
        if (!known) return Festival.None;
        var (nextMonth, nextDay, _, nextKnown) = LunarOf(date.AddDays(1));
        if (nextKnown && nextMonth == 1 && nextDay == 1) return Festival.Eve;
        return month switch
        {
            1 when day == 1 => Festival.Spring,
            1 when day == 15 => Festival.Lantern,
            5 when day == 5 => Festival.DragonBoat,
            7 when day == 7 => Festival.Qixi,
            8 when day == 15 => Festival.MidAutumn,
            9 when day == 9 => Festival.DoubleNinth,
            12 when day == 8 => Festival.Laba,
            _ => Festival.None
        };
    }
}
