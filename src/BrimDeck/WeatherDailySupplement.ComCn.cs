using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 和风日预报的国内补充通道：只在和风已选、城市在中国、且未来 14 天核心字段有缺口时补数据。
// 它不是可独立选择的来源，不进 WeatherSources.All；适用条件与缺口判断都收在这里，聚合器不认识具体来源字符串。
internal static class ChinaDailySupplement
{
    internal const string Id = "weathercomcn";
    internal static string Name => Loc.T("中国天气网", "China Weather");

    // 聚合器只问「这个通道适用吗」，不用写死来源 ID 和地域规则。
    internal static bool Applies(string sourceId, City city) => sourceId == HeFengSource.Id && Geo.IsChina(city);

    // 核心字段齐全就不补；缺一天就整批拉取，沿用原来的补缺边界。
    internal static bool Missing(IReadOnlyCollection<DayPoint>? days, DateTime today)
    {
        if (days is null) return true;
        return !Enumerable.Range(0, 14).All(offset =>
            days.Any(day => day.Date == WeatherClock.DateKey(today.AddDays(offset)) &&
                day.HighC.HasValue && day.LowC.HasValue && day.Code.HasValue && day.NightCode.HasValue &&
                day.PrecipMm.HasValue && day.WindDir is not null && day.WindMaxKmh.HasValue));
    }

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
            string stamp = WeatherClock.MonthKey(month);
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
        return await WeatherCache.GetAsync<string>(Id, "id|" + name, TimeSpan.FromDays(30), async ct =>
        {
            var result = await WeatherHttp.GetAsync(Id, $"https://toy1.weather.com.cn/search?cityname={Json.Esc(name)}", ct,
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
        => WeatherCache.GetAsync<string>(Id, key, ttl, async ct =>
            (await WeatherHttp.GetAsync(Id, url, ct, request => request.Headers.Referrer = new Uri("https://www.weather.com.cn/"), seconds)).Body, token);

    private static string? ExtractArray(string body)
    {
        int start = body.IndexOf('['), end = body.LastIndexOf(']');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }

}
