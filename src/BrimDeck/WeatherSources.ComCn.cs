using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class WeatherComCnSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("weathercomcn", Loc.T("中国天气网", "China Weather"), Loc.T("国内实况、月历与历史降水", "China current, calendar & history"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.History, WeatherDomain.Air], WeatherRegion.China);

    public bool CanServe(City city, WeatherSettings settings)
    {
        var (name, _) = Geo.Parts(city);
        return Geo.IsChina(city) && name.Length > 0 && Geo.HasChinese(name);
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        var (name, _) = Geo.Parts(city);
        // 和风城市 id 与中国天气网代码同源：结构化 id（如洪山区 101200113）直接定位到区县站，
        // 免去按城市名搜索一次；没有 id 时退回原路径按城市名解析。
        string? id = city.LocationId is { Length: >= 9 } code && code.StartsWith("101") && code.All(char.IsDigit)
            ? code
            : await ResolveIdAsync(name, token);
        if (id is null) return null;
        var part = new WeatherPart();
        bool wantCurrent = need.Contains(WeatherDomain.Current) || need.Contains(WeatherDomain.Air);
        bool wantDays = need.Contains(WeatherDomain.Daily) || need.Contains(WeatherDomain.History);
        // 实况偶尔很慢，单独限时 4 秒并落盘 5 分钟：它拖不住已经拿到的每日预报。
        var currentTask = wantCurrent ? Body($"{id}|current", TimeSpan.FromMinutes(5), $"http://d1.weather.com.cn/sk_2d/{id}.html", token, 4) : Task.FromResult<string?>(null);
        // 24 天窗口可能跨月，按起止日期枚举涉及的每个月份；月历一个月只变一次，缓存 6 小时。
        var months = new List<Task<string?>>();
        if (wantDays)
        {
            var start = DateTime.Today.AddDays(-10); var end = DateTime.Today.AddDays(13);
            for (var month = new DateTime(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
            {
                string stamp = month.ToString("yyyyMM");
                months.Add(Body($"{id}|{stamp}", TimeSpan.FromHours(6), $"http://d1.weather.com.cn/calendar_new/{stamp[..4]}/{id}_{stamp}.html", token, 6));
            }
        }
        var currentBody = await currentTask;
        if (currentBody is not null && ExtractObject(currentBody) is { } sk)
        {
            using var doc = JsonDocument.Parse(sk);
            var root = doc.RootElement;
            string? condition = Json.Text(root, "weather");
            string? windDir = Json.Text(root, "WD");
            part.Current = new CurrentPart
            {
                Temperature = Json.Number(root, "temp"), Humidity = ParsePercent(Json.Text(root, "SD")),
                PressureHpa = Json.Number(root, "qy"), VisibilityKm = ParseDistance(Json.Text(root, "njd")),
                PrecipMm = Json.Number(root, "rain"), Code = WeatherCodes.FromText(condition), Condition = condition,
                WindDir = windDir, WindDirDeg = WeatherCodes.DegreesFromText(windDir),
                WindKmh = WeatherCodes.FromWindPower(Json.Text(root, "WS")),
                IsDay = DateTime.Now.Hour is >= 6 and < 18
            };
            if (Json.Integer(root, "aqi") is { } aqi && aqi > 0)
                part.Air = new AirPart { Aqi = aqi, Pm25 = Json.Integer(root, "aqi_pm25"), Level = aqi <= 50 ? 1 : aqi <= 100 ? 2 : aqi <= 150 ? 3 : 4, Text = WeatherCodes.AqiText(aqi) };
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
                    string? condition = Json.Text(item, "w1");
                    double? high = Json.Number(item, "max");
                    double? low = Json.Number(item, "min");
                    double? rain = Json.Number(item, "rainobs");
                    string? windDir = Json.Text(item, "wd1");
                    if (!days.TryGetValue(date, out var day)) days[date] = day = new DayPoint { Date = date };
                    if (high is not null) { day.HighC ??= high; day.LowC ??= low; }
                    if (!string.IsNullOrWhiteSpace(condition) && day.Condition is null) { day.Condition = condition; day.Code = WeatherCodes.FromText(condition); }
                    if (rain is not null) day.PrecipMm ??= rain;
                    if (!string.IsNullOrWhiteSpace(windDir) && !windDir.Contains("微风")) { day.WindDir ??= windDir; day.WindDirDeg ??= WeatherCodes.DegreesFromText(windDir); }
                }
            }
            catch { }
        }
        if (days.Count > 0) part.Days = [.. days.Values];
        return part;
    }

    private static async Task<string?> ResolveIdAsync(string name, CancellationToken token)
    {
        // 城市 ID 是静态数据，落盘 30 天：启动后不必先搜一次城市，断网也能定位到已有城市。
        return await WeatherCache.GetAsync<string>("weathercomcn", "id|" + name, TimeSpan.FromDays(30), async ct =>
        {
            var result = await WeatherHttp.GetAsync("weathercomcn", $"http://toy1.weather.com.cn/search?cityname={Json.Esc(name)}", ct,
                request => request.Headers.Referrer = new Uri("http://www.weather.com.cn/"), 3);
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
            (await WeatherHttp.GetAsync("weathercomcn", url, ct, request => request.Headers.Referrer = new Uri("http://www.weather.com.cn/"), seconds)).Body, token);

    private static string? ExtractObject(string body)
    {
        int start = body.IndexOf('{'), end = body.LastIndexOf('}');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }

    private static string? ExtractArray(string body)
    {
        int start = body.IndexOf('['), end = body.LastIndexOf(']');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }

    private static int? ParsePercent(string? text) => int.TryParse(text?.TrimEnd('%'), out var value) ? value : null;
    private static double? ParseDistance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, "[\\d.]+");
        return match.Success && double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
