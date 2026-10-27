namespace BrimDeck.Calendar;

// The 24 solar terms, found by bisecting the sun's apparent longitude. A truncated Meeus series
// keeps the crossing within about a minute, which is far below the one-day grid resolution.
public static class SolarTerms
{
    public static readonly string[] Names =
    [
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
        "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
        "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
    ];

    private static readonly Dictionary<int, Dictionary<DateOnly, string>> Cache = [];

    public static IReadOnlyDictionary<DateOnly, string> Of(int year)
    {
        if (Cache.TryGetValue(year, out var map)) return map;
        map = Build(year);
        Cache[year] = map;
        return map;
    }

    public static string? Name(DateOnly date) =>
        Of(date.Year).TryGetValue(date, out var name) ? name : null;

    private static Dictionary<DateOnly, string> Build(int year)
    {
        var map = new Dictionary<DateOnly, string>(24);
        for (int index = 0; index < 24; index++)
            if (Crossing(year, index) is { } day) map[day] = Names[index];
        return map;
    }

    private static DateOnly? Crossing(int year, int index)
    {
        double target = (285 + 15 * index) % 360;
        var guess = new DateTime(year, 1, 6, 12, 0, 0, DateTimeKind.Utc).AddDays(index * 15.2184);
        double low = Jd(guess.AddDays(-9)), high = Jd(guess.AddDays(9));
        if (Delta(low, target) <= 0 || Delta(high, target) >= 0) return null;
        for (int step = 0; step < 60; step++)
        {
            double middle = (low + high) / 2;
            if (Delta(middle, target) > 0) low = middle; else high = middle;
        }
        var utc = DateTime.FromOADate((low + high) / 2 - 2415018.5);
        return DateOnly.FromDateTime(utc.AddHours(8));
    }

    private static double Jd(DateTime value) => value.ToOADate() + 2415018.5;

    private static double Delta(double jd, double target)
    {
        double difference = (target - Longitude(jd)) % 360;
        if (difference > 180) difference -= 360;
        if (difference < -180) difference += 360;
        return difference;
    }

    private static double Longitude(double jd)
    {
        double t = (jd - 2451545.0) / 36525.0;
        double mean = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double anomaly = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * Math.PI / 180;
        double center = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(anomaly)
            + (0.019993 - 0.000101 * t) * Math.Sin(2 * anomaly)
            + 0.000289 * Math.Sin(3 * anomaly);
        double node = (125.04 - 1934.136 * t) * Math.PI / 180;
        double apparent = mean + center - 0.00569 - 0.00478 * Math.Sin(node);
        apparent %= 360;
        return apparent < 0 ? apparent + 360 : apparent;
    }
}
