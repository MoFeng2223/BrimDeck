using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class WeatherComCnSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("weathercomcn", Loc.T("中国天气网", "China Weather"), Loc.T("国内实况、月历、历史降水、预警与生活指数", "China current, calendar, history, alerts & indices"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.History, WeatherDomain.Air, WeatherDomain.Warning, WeatherDomain.Indices], WeatherRegion.China);

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
        bool wantExtras = need.Contains(WeatherDomain.Warning) || need.Contains(WeatherDomain.Indices);
        // 实况偶尔很慢，单独限时 4 秒并落盘 5 分钟：它拖不住已经拿到的每日预报。
        var currentTask = wantCurrent ? Body($"{id}|current", TimeSpan.FromMinutes(5), $"https://d1.weather.com.cn/sk_2d/{id}.html", token, 4) : Task.FromResult<string?>(null);
        // 预警与生活指数同在一个 weather_index 页面里；预警要尽量新，所以只缓存 10 分钟。
        var extrasTask = wantExtras ? Body($"{id}|index", TimeSpan.FromMinutes(10), $"https://d1.weather.com.cn/weather_index/{id}.html", token, 6) : Task.FromResult<string?>(null);
        // 24 天窗口可能跨月，按起止日期枚举涉及的每个月份；月历一个月只变一次，缓存 6 小时。
        var months = new List<Task<string?>>();
        if (wantDays)
        {
            var start = DateTime.Today.AddDays(-10); var end = DateTime.Today.AddDays(13);
            for (var month = new DateTime(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
            {
                string stamp = month.ToString("yyyyMM");
                months.Add(Body($"{id}|{stamp}", TimeSpan.FromHours(6), $"https://d1.weather.com.cn/calendar_new/{stamp[..4]}/{id}_{stamp}.html", token, 6));
            }
        }
        var currentBody = await currentTask;
        // 实况页面偶尔返回残缺内容：解析失败只丢实况这一块，已取得的月历/预警/指数不能跟着一起丢。
        try
        {
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
        }
        catch { }
        var bodies = await Task.WhenAll(months);
        if (await extrasTask is { } extras) ParseExtras(extras, part);
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
                    string? windRaw = Json.Text(item, "wd1");
                    string? windPower = Json.Text(item, "ws1");
                    if (!days.TryGetValue(date, out var day)) days[date] = day = new DayPoint { Date = date };
                    if (high is not null) { day.HighC ??= high; day.LowC ??= low; }
                    // w1/wd1 都是「白天转夜间」合成串：拆开后分别落到白天与夜间字段，供逐日双图标和底部风行使用。
                    string[] weatherParts = string.IsNullOrWhiteSpace(condition) ? [] : condition.Split('转', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (weatherParts.Length > 0 && day.Condition is null) { day.Condition = weatherParts[0]; day.Code = WeatherCodes.FromText(weatherParts[0]); }
                    if (weatherParts.Length > 1 && day.NightCondition is null) { day.NightCondition = weatherParts[1]; day.NightCode = WeatherCodes.FromText(weatherParts[1]); }
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
        if (days.Count > 0) part.Days = [.. days.Values];
        return part;
    }

    // 中国天气网的风级在风向串里（如「西北风3-4级」），ws1 偶尔单独给出；微风按 0 级处理。
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

    // weather_index 页面里内嵌两段 JS：
    //   alarmDZ.w[] 每项是数组式对象：w5 类型、w7 等级、w8 发布时间、w9 正文、w13 标题、w15 失效时间、w14=Cancel 表示已解除；
    //   dataZS.zs 里以「两字母前缀_xxx」成对给出生活指数：lk/cl/gj/pl/co/pj/hc/gl…，分别有 _name/_hint/_des_s。
    private static void ParseExtras(string body, WeatherPart part)
    {
        if (ExtractJsObject(body, "alarmDZ") is { } alarm)
        {
            using var doc = JsonDocument.Parse(alarm);
            if (Json.Get(doc.RootElement, "w") is { ValueKind: JsonValueKind.Array } list)
            {
                var warnings = new List<WarningItem>();
                foreach (var item in list.EnumerateArray())
                {
                    if (string.Equals(Json.Text(item, "w14"), "Cancel", StringComparison.OrdinalIgnoreCase)) continue;
                    string? type = Json.Text(item, "w5"), level = Json.Text(item, "w7");
                    warnings.Add(new WarningItem
                    {
                        Title = Json.Text(item, "w13") ?? $"{type}{level}预警",
                        Text = Json.Text(item, "w9") ?? "",
                        Type = type ?? "",
                        Level = level ?? "",
                        Start = Json.Text(item, "w8") ?? "",
                        End = Json.Text(item, "w15") ?? ""
                    });
                }
                // 空列表也是一次有效回答（「当前无预警」），要让聚合层标出来源。
                part.Warnings = warnings;
            }
        }
        if (ExtractJsObject(body, "dataZS") is { } zsBody)
        {
            using var doc = JsonDocument.Parse(zsBody);
            if (Json.Get(doc.RootElement, "zs") is { ValueKind: JsonValueKind.Object } zs)
            {
                string raw = Json.Text(zs, "date") ?? "";
                string date = raw.Length >= 8 ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}" : DateTime.Today.ToString("yyyy-MM-dd");
                var indices = new List<IndexItem>();
                foreach (var prop in zs.EnumerateObject())
                {
                    if (!prop.Name.EndsWith("_name", StringComparison.Ordinal)) continue;
                    string prefix = prop.Name[..^5];
                    if (prefix.Length == 0) continue;
                    indices.Add(new IndexItem
                    {
                        Date = date,
                        Name = prop.Value.GetString() ?? "",
                        Category = prefix,
                        Level = Json.Text(zs, prefix + "_hint") ?? "",
                        Text = Json.Text(zs, prefix + "_des_s") ?? ""
                    });
                }
                if (indices.Count > 0) part.Indices = indices;
            }
        }
    }

    // 页面里的数据是 `var name = {...};` 形式，不是纯 JSON；按花括号配平截出对象，跳过字符串里的括号。
    private static string? ExtractJsObject(string body, string name)
    {
        int at = body.IndexOf("var " + name, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = body.IndexOf('{', at);
        if (start < 0) return null;
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < body.Length; i++)
        {
            char ch = body[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return body[start..(i + 1)];
        }
        return null;
    }

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
