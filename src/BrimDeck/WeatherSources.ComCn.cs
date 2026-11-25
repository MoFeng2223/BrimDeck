using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

internal static class ChinaDailyForecast
{
    internal const string Id = "weathercomcn";
    internal static string Name => Loc.T("中国天气网", "China Weather");

    internal static async Task<List<DayPoint>> FetchAsync(City city, DateTime today, CancellationToken token)
    {
        var (name, _) = Geo.Parts(city);
        // 和风城市 id 与中国天气网代码同源：结构化 id（如洪山区 101200113）直接定位到区县站，
        // 免去按城市名搜索一次；没有 id 时退回原路径按城市名解析。
        string? id = city.LocationId is { Length: >= 9 } code && code.StartsWith("101") && code.All(char.IsDigit)
            ? code
            : await ResolveIdAsync(name, token);
        if (id is null) return [];
        var months = new List<Task<string?>>();
        var start = today;
        var end = today.AddDays(13);
        for (var month = new DateTime(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            string stamp = month.ToString("yyyyMM");
            months.Add(Body($"{id}|{stamp}", TimeSpan.FromHours(6), $"https://d1.weather.com.cn/calendar_new/{stamp[..4]}/{id}_{stamp}.html", token, 6));
        }
        var bodies = await Task.WhenAll(months);
        var days = new Dictionary<string, DayPoint>();
        foreach (var body in bodies)
        {
            if (body is null || ExtractArray(body) is not { } json) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string? raw = Json.Text(item, "date"); if (raw is null || raw.Length != 8) continue;
                    string date = $"{raw[..4]}-{raw[4..6]}-{raw[6..]}";
                    var category = Json.Text(item, "cla")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (!DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateValue)
                        || dateValue < start || dateValue > end || category?.Any(tag => tag is "obs" or "history") == true) continue;
                    string? condition = Json.Text(item, "w1");
                    double? high = Json.Number(item, "max");
                    double? low = Json.Number(item, "min");
                    double? rain = Json.Number(item, "rain1");
                    string? windRaw = Json.Text(item, "wd1");
                    string? windPower = Json.Text(item, "ws1");
                    if (!days.TryGetValue(date, out var day)) days[date] = day = new DayPoint { Date = date };
                    day.HighC ??= high;
                    day.LowC ??= low;
                    // w1/wd1 都是「白天转夜间」合成串：拆开后分别落到白天与夜间字段，供逐日双图标和底部风行使用。
                    string[] weatherParts = string.IsNullOrWhiteSpace(condition) ? [] : condition.Split('转', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (weatherParts.Length > 0 && day.Condition is null) { day.Condition = weatherParts[0]; day.Code = WeatherCodes.FromText(weatherParts[0]); }
                    if (weatherParts.Length > 1 && day.NightCondition is null) { day.NightCondition = weatherParts[1]; day.NightCode = WeatherCodes.FromText(weatherParts[1]); }
                    if (Json.Text(item, "w2") is { Length: > 0 } night && !string.IsNullOrWhiteSpace(night))
                    {
                        day.NightCondition ??= night;
                        day.NightCode ??= WeatherCodes.FromText(night);
                    }
                    if (rain is not null) day.PrecipMm ??= rain;
                    string[] windParts = string.IsNullOrWhiteSpace(windRaw) ? [] : windRaw.Split('转', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (windParts.Length > 0 && day.WindDir is null)
                    {
                        day.WindDir = windParts[0];
                        day.WindDirDeg = WeatherCodes.DegreesFromText(windParts[0]);
                        day.WindMaxKmh = WindPowerKmh(windParts[0], windPower);
                    }
                    if (windParts.Length > 1 && day.NightWindDir is null)
                    {
                        day.NightWindDir = windParts[1];
                        day.NightWindDirDeg = WeatherCodes.DegreesFromText(windParts[1]);
                        day.NightWindMaxKmh = WindPowerKmh(windParts[1], null);
                    }
                }
            }
            catch { }
        }
        return [.. days.Values];
    }

    private static double? WindPowerKmh(string? text, string? fallback)
    {
        foreach (var part in new[] { text, fallback })
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (part.Contains("微风")) return 0;
            if (WeatherCodes.FromWindPower(part) is { } kmh) return kmh;
        }
        return null;
    }

    private static async Task<string?> ResolveIdAsync(string name, CancellationToken token)
    {
        // 城市 ID 是静态数据，落盘 30 天：启动后不必先搜一次城市，断网也能定位到已有城市。
        return await WeatherCache.GetAsync<string>("weathercomcn", "id|" + name, TimeSpan.FromDays(30), async ct =>
        {
            var result = await WeatherHttp.GetAsync("weathercomcn", $"https://toy1.weather.com.cn/search?cityname={Json.Esc(name)}", ct,
                request => request.Headers.Referrer = new Uri("https://www.weather.com.cn/"), 3);
            if (result.Body is null) return null;
            string json = result.Body.Trim().TrimStart('(').TrimEnd(')');
            if (ExtractArray(json) is not { } array) return null;
            using var doc = JsonDocument.Parse(array);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? reference = Json.Text(item, "ref");
                string? found = reference?.Split('~')[0];
                if (found is { Length: >= 9 } && found.All(char.IsDigit) && found.StartsWith("101")) return found;
            }
            return null;
        }, token);
    }

    // 响应体落盘：国内站点不稳定，缓存同时也是断网时的兜底。
    private static Task<string?> Body(string key, TimeSpan ttl, string url, CancellationToken token, int seconds)
        => WeatherCache.GetAsync<string>("weathercomcn", key, ttl, async ct =>
            (await WeatherHttp.GetAsync("weathercomcn", url, ct, request => request.Headers.Referrer = new Uri("https://www.weather.com.cn/"), seconds)).Body, token);

    private static string? ExtractArray(string body)
    {
        int start = body.IndexOf('['), end = body.LastIndexOf(']');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }

}
