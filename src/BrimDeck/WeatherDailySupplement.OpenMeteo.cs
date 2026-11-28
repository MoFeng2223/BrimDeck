using BrimDeck.Core;

namespace BrimDeck;

// 和风日预报的国外补充通道：和风只给 10 天，日期轴画到未来 13 天，后几天会空。
// 国外城市没法用中国天气网，改用 Open-Meteo 的 daily；只取 Daily 域，与直接选
// Open-Meteo 时共用同一份 forecast 缓存，不额外增加请求种类。
internal static class OpenMeteoDailySupplement
{
    internal const string Name = "Open-Meteo";

    internal static bool Applies(string sourceId, City city) => sourceId == HeFengSource.Id && !Geo.IsChina(city);

    // 只认核心日级字段：Open-Meteo 不给中文天气名和夜间分段，拿这些当缺口条件会永远判缺。
    internal static bool Missing(IReadOnlyCollection<DayPoint>? days, DateTime today)
    {
        if (days is null) return true;
        return !Enumerable.Range(0, 14).All(offset =>
            days.Any(day => day.Date == WeatherClock.DateKey(today.AddDays(offset)) &&
                day.HighC.HasValue && day.LowC.HasValue && day.Code.HasValue));
    }

    internal static async Task<List<DayPoint>> FetchAsync(City city, WeatherSettings settings, DateTime today, CancellationToken token)
    {
        var part = await new OpenMeteoSource().FetchAsync(city, settings, [WeatherDomain.Daily], token);
        if (part?.Days is not { Count: > 0 }) return [];
        // 过去十天归 History 域，不能被日预报补充通道改写；只留今天起 14 天。
        string start = WeatherClock.DateKey(today), end = WeatherClock.DateKey(today.AddDays(13));
        return [.. part.Days.Where(day => string.CompareOrdinal(day.Date, start) >= 0 && string.CompareOrdinal(day.Date, end) <= 0)];
    }
}
