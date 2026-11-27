using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 和风天气：需要用户自己的 Key/Host。v1 覆盖实况、逐时、每日（含日月与晨昏蒙影）与空气；
// v7 补充分钟降水、官方预警与生活指数；时光机补过去 10 天。选定它时只读它的返回，缺项显示 "-"。
internal sealed class HeFengSource : IWeatherSource
{
    internal const string Id = "hefeng";
    // 地名搜索走独立闸门：天气通道限流时仍可搜新城市；额度在 RequestGate 里单独配置。
    internal const string GeoId = "hefenggeo";
    public WeatherSourceInfo Info { get; } = new(Id, Loc.T("和风天气", "QWeather"),
        // 官方「天气和基础服务」每月前 5 万次免费（个人用量等于免费）；台风/辐照/海洋等付费或无坐标接口的能力不接入。
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Air, WeatherDomain.History, WeatherDomain.Minutely, WeatherDomain.Warning, WeatherDomain.Indices]);
    public bool CanServe(City city, WeatherSettings settings) => settings.HeFengKeyConfigured && settings.HeFengHost.Trim().Length > 0;

    // 一次请求要用到的公共参数：坐标只保留两位小数（v1 只认这个精度）。
    private sealed record Context(string Host, string Key, City City, WeatherSettings Settings)
    {
        public string Lat => City.Latitude.ToString("0.00", CultureInfo.InvariantCulture);
        public string Lon => City.Longitude.ToString("0.00", CultureInfo.InvariantCulture);
        public string Lang => Loc.IsEnglish ? "en" : "zh";
        public string Site => $"{Lat},{Lon}|{Lang}";
        // 城市当地日期：跨过当地零点后逐时/逐天窗口整体前移，缓存键带上它，旧响应自动作废（文档专门警告过「跨天」）。
        public string Day => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(City))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // Key 随请求头明文传输，一律强制 HTTPS：用户填 http:// 也升级，避免 Key 在明文连接里裸奔。
    // 自动请求与手动探针共用同一套归一化，不能各写一遍导致行为不一致。
    internal static string NormalizeHost(string raw)
    {
        string host = raw.Trim().TrimEnd('/');
        return host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + host[7..]
             : host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host;
    }

    // 手动测试：复用实况请求那条链路（含判定与状态发布），只是坐标固定北京、绕过缓存。
    internal static async Task ProbeAsync(WeatherSettings settings, CancellationToken token)
    {
        // 先判空再归一化：NormalizeHost("") 会得到 "https://"，拿它当有效 Host 会白发一个必失败的请求。
        string rawHost = settings.HeFengHost.Trim(), key = WeatherSecrets.Key;
        if (rawHost.Length == 0 || key.Length == 0) return;
        string host = NormalizeHost(rawHost);
        var context = new Context(host, key, new City("北京", 39.90, 116.41), settings);
        await FetchRawAsync(context, $"{host}/weather/v1/current/{context.Lat}/{context.Lon}?localTime=true&lang={context.Lang}", token, probe: true);
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        var context = new Context(NormalizeHost(settings.HeFengHost), WeatherSecrets.Key, city, settings);
        // 历史单独占一段：时光机只认 LocationID、一天一次请求，挂在独立的第二轮，不混进首屏。
        // 延后轮次可能把历史与预警/指数等一起送来，这里按需拆分，互不干扰。
        bool wantHistory = need.Contains(WeatherDomain.History);
        var part = wantHistory ? await FetchV1(context, need.Where(d => d != WeatherDomain.History).ToArray(), token) : await FetchV1(context, need, token);
        if (wantHistory && await FetchHistory(context, token) is { Count: > 0 } past)
        {
            part ??= new WeatherPart();
            part.Days = part.Days is { Count: > 0 } existing
                ? [.. existing.Concat(past.Where(p => !existing.Any(e => e.Date == p.Date)))]
                : past;
        }
        // v7 扩展域：分钟/预警/指数，按本轮真正缺的域请求，逐域并入同一份 part。
        var extraNeed = need.Where(d => d is WeatherDomain.Minutely or WeatherDomain.Warning or WeatherDomain.Indices).ToArray();
        if (extraNeed.Length > 0)
        {
            var extra = await FetchExtraV7(context, extraNeed, token);
            if (extra is not null)
            {
                part ??= new WeatherPart();
                part.Minutes ??= extra.Minutes; part.Warnings ??= extra.Warnings; part.Indices ??= extra.Indices;
                part.MinuteSummary ??= extra.MinuteSummary;
            }
        }
        return part;
    }

    // v7 扩展接口：分钟降水 / 实时预警 / 生活指数。均在「天气和基础服务」每月前 5 万次免费额度内。
    private static async Task<WeatherPart?> FetchExtraV7(Context context, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        bool Want(WeatherDomain domain) => need.Contains(domain);
        string host = context.Host, loc = $"{context.Lon},{context.Lat}", lang = $"lang={context.Lang}";
        // 端点以 2026-11 官方文档 + 本地实测为准：
        // 分钟降水 /v7/minutely/5m —— 只覆盖中国行政区，国外坐标固定 400（伦敦/华盛顿实测）；
        // 预警 /weatheralert/v1/current/{lat}/{lon} —— 全球可用，无预警时返回空数组；
        // 指数 /v7/indices/1d —— 全球可用。
        // 分钟降水必须在国外直接跳过：400 虽已不会熔断来源，但每次刷新白发一个必失败的请求。
        var minute = Want(WeatherDomain.Minutely) && Geo.IsChina(context.City)
            ? FetchBodyAsync(context, "v7m|" + context.Site, TimeSpan.FromMinutes(10), $"{host}/v7/minutely/5m?location={loc}&{lang}", token)
            : Task.FromResult<string?>(null);
        var warning = Want(WeatherDomain.Warning) ? FetchBodyAsync(context, "v7w|" + context.Site, TimeSpan.FromMinutes(10), $"{host}/weatheralert/v1/current/{context.Lat}/{context.Lon}?{lang}", token) : Task.FromResult<string?>(null);
        var indices = Want(WeatherDomain.Indices) ? FetchBodyAsync(context, "v7i|" + context.Site + "|" + context.Day, TimeSpan.FromHours(2), $"{host}/v7/indices/1d?location={loc}&type=0&{lang}", token) : Task.FromResult<string?>(null);
        await Task.WhenAll(minute, warning, indices);
        var part = new WeatherPart();
        var (minutes, summary) = ParseMinutelyV7(minute.Result);
        if (minutes is { Count: > 0 }) part.Minutes = minutes;
        part.MinuteSummary = summary;
        // 解析成功（含空列表=「当前无预警」）都写入；失败/错误码保持 null，与"没请求"区分。
        if (ParseWarningV7(warning.Result) is { } warnings) part.Warnings = warnings;
        if (ParseIndicesV7(indices.Result) is { Count: > 0 } entries) part.Indices = entries;
        return part.Minutes is null && part.MinuteSummary is null && part.Warnings is null && part.Indices is null ? null : part;
    }

    // v1：坐标走路径，数值包在 { value, unit } 里，湿度/云量/降水概率是 0..1 的比例。
    private static async Task<WeatherPart?> FetchV1(Context context, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        bool Want(WeatherDomain domain) => need.Contains(domain);
        string host = context.Host;
        // localTime=true：v1 默认返回 UTC，而界面和“今天”都按城市当地时刻算，不换算会整体错一天。
        string query = $"localTime=true&lang={context.Lang}";
        // 四个接口各自过期：实况最短、逐天最长，互不牵连（官方推荐 10–30 分钟 / 30–60 分钟 / 1–6 小时）。
        // 实况是可用性探针：只有它发出真实请求并收到响应，才更新和风连通状态。
        var current = Want(WeatherDomain.Current) ? FetchBodyAsync(context, "current|" + context.Site, TimeSpan.FromMinutes(10), $"{host}/weather/v1/current/{context.Lat}/{context.Lon}?{query}", token, probe: true) : Task.FromResult<string?>(null);
        var hours = Want(WeatherDomain.Hourly) ? FetchBodyAsync(context, "hourly|" + context.Site + "|" + context.Day, TimeSpan.FromMinutes(30), $"{host}/weather/v1/hourly/{context.Lat}/{context.Lon}?hours=24&{query}", token) : Task.FromResult<string?>(null);
        var days = Want(WeatherDomain.Daily) ? FetchBodyAsync(context, "daily|" + context.Site + "|" + context.Day, TimeSpan.FromHours(2), $"{host}/weather/v1/daily/{context.Lat}/{context.Lon}?days=10&{query}", token) : Task.FromResult<string?>(null);
        var air = Want(WeatherDomain.Air) ? FetchBodyAsync(context, "air|" + context.Site, TimeSpan.FromMinutes(30), $"{host}/airquality/v1/current/{context.Lat}/{context.Lon}?lang={context.Lang}", token) : Task.FromResult<string?>(null);
        // 空气质量预报与实况分开缓存：实况 30 分钟、未来 24 小时逐时同样 30 分钟，缓存键带当地日期。
        var airHours = Want(WeatherDomain.Air) ? FetchBodyAsync(context, "airhours|" + context.Site + "|" + context.Day, TimeSpan.FromMinutes(30), $"{host}/airquality/v1/hourly/{context.Lat}/{context.Lon}?localTime=true&lang={context.Lang}", token) : Task.FromResult<string?>(null);
        await Task.WhenAll(current, hours, days, air, airHours);
        var part = new WeatherPart();
        if (ParseCurrentV1(current.Result, context.City) is { } now) part.Current = now;
        if (ParseHoursV1(hours.Result, context.City) is { Count: > 0 } list) part.Hours = list;
        if (ParseDaysV1(days.Result, context.City) is { Count: > 0 } forecast) part.Days = forecast;
        if (ParseAirV1(air.Result) is { } quality) part.Air = quality;
        if (ParseAirHoursV1(airHours.Result, context.City) is { Count: > 0 } hourAqi)
        {
            part.Air ??= new AirPart();
            part.Air.HourAqi = hourAqi;
        }
        return part.Current is null && part.Hours is null && part.Days is null && part.Air is null ? null : part;
    }

    // 带缓存取一份和风原始响应；probe 只标记这条请求是否用于连通性判定。
    private static Task<string?> FetchBodyAsync(Context context, string key, TimeSpan ttl, string url, CancellationToken token, bool probe = false)
        => WeatherCache.GetAsync<string>("hefeng", key, ttl, ct => FetchRawAsync(context, url, ct, probe), token);

    // Key 走 X-QW-Api-Key 请求头：比放在查询字符串里安全，也避免同时使用两种认证方式。
    // 实测请求并回报探针状态，与通用传输层的 SendAsync 区分开。
    private static async Task<string?> FetchRawAsync(Context context, string url, CancellationToken token, bool probe = false)
    {
        int generation = HeFengMonitor.Generation;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await WeatherHttp.GetAsync("hefeng", url, token, request => request.Headers.TryAddWithoutValidation("X-QW-Api-Key", context.Key), limits: context.Settings);
        if (probe) HeFengMonitor.Report(generation, result.Status, result.Body, watch.Elapsed);
        return result.Body;
    }

    // ---- 时光机：/v7/historical/weather 只认 LocationID、单日一次请求、最多回溯 10 天（不含今天）----

    private static async Task<List<DayPoint>?> FetchHistory(Context context, CancellationToken token)
    {
        // 手动刷新（Fresh）只该强制实况/预报；历史是定稿数据，重拉 10 天纯属浪费配额。
        using var cached = WeatherCache.Cached();
        // 优先用城市自带的 LocationID：搜索/定位时 GeoAPI 已经返回并存在 City.LocationId 里，
        // 不必再靠坐标反查——反查受限流冷却/网络影响，一次失败就会让整段历史为空。
        // 只有城市没带 id 时才回退到坐标反查。
        string? id = context.City.LocationId is { Length: > 0 } known ? known : await LookupLocationIdAsync(context, token);
        if (id is null) return null;
        // 按城市当地日期回溯：跨时区时用本机日期会整体错一天。
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(context.City))).Date;
        // 并发 4 路：冷启动要补 10 天，但不该把 10 个请求同时砸出去。
        using var gate = new SemaphoreSlim(4);
        var days = await Task.WhenAll(Enumerable.Range(1, 10).Select(async offset =>
        {
            string stamp = today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // 历史是定稿数据：昨天那条还可能被再分析修订，留 24 小时；更早的日期基本不再变，留 7 天。
            // 于是稳态下每次刷新只补 1 个新日期，而不是重拉 10 天。
            var ttl = offset == 1 ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7);
            await gate.WaitAsync(token);
            try
            {
                var body = await FetchBodyAsync(context, $"history|{id}|{context.Lang}|{stamp}", ttl, $"{context.Host}/v7/historical/weather?location={id}&date={stamp.Replace("-", "")}&lang={context.Lang}", token);
                return ParseHistory(body, stamp);
            }
            // 单日失败只丢这一天：历史是一天一条请求，不能因为某天超时/被限流就把整段 10 天清空。
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; }
            finally { gate.Release(); }
        }));
        return [.. days.Where(day => day is not null).Select(day => day!).OrderBy(day => day.Date, StringComparer.Ordinal)];
    }

    // 时光机只接受 LocationID，用与本城相同的坐标反查一次并落盘 30 天；失败就整个历史域跳过，不影响其它数据。
    private static Task<string?> LookupLocationIdAsync(Context context, CancellationToken token)
        => WeatherCache.GetAsync<string>("hefeng", $"place|{context.Lat},{context.Lon}", TimeSpan.FromDays(30), async ct =>
        {
            var body = await FetchRawAsync(context, $"{context.Host}/geo/v2/city/lookup?location={context.Lon},{context.Lat}&number=1", ct);
            if (body is null) return null;
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "location") is { ValueKind: JsonValueKind.Array } locations)
                foreach (var item in locations.EnumerateArray())
                    if (Json.Text(item, "id") is { Length: > 0 } id) return id;
            return null;
        }, token);

    // 时光机返回 weatherDaily（高低温、降水、气压、日月相）+ weatherHourly（没有日级天气现象）。
    // 全球坐标都支持；lang 决定 hourly 的天气文本语言，不传会固定返回英文。
    // 天气现象与风取当地 12 点那一小时，风速取全天最大值，和每日预报的口径保持一致。
    private static DayPoint? ParseHistory(string? body, string date)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (Json.Text(root, "code") != "200") return null;
            if (Json.Get(root, "weatherDaily") is not { ValueKind: JsonValueKind.Object } daily) return null;
            string stamp = Json.Text(daily, "date") ?? date;
            // 文档里的 sunrise/sunset 只有 "05:08" 五个字符，界面按 ISO 取第 11–16 位，这里补齐日期。
            string? Clock(string name) => Json.Text(daily, name) is { Length: >= 4 } value ? $"{stamp}T{value}" : null;
            var day = new DayPoint
            {
                Date = stamp,
                HighC = Json.Number(daily, "tempMax"),
                LowC = Json.Number(daily, "tempMin"),
                Humidity = Json.Integer(daily, "humidity"),
                PrecipMm = Json.Number(daily, "precip"),
                PressureHpa = Json.Number(daily, "pressure"),
                Sunrise = Clock("sunrise"),
                Sunset = Clock("sunset"),
                Moonrise = Clock("moonrise"),
                Moonset = Clock("moonset"),
                MoonPhase = Json.Text(daily, "moonPhase")
            };
            if (Json.Get(root, "weatherHourly") is { ValueKind: JsonValueKind.Array } hourly)
            {
                JsonElement? noon = null;
                double? maxWind = null;
                foreach (var item in hourly.EnumerateArray())
                {
                    string? time = Json.Text(item, "time");
                    if (time is null) continue;
                    if (noon is null && time.Length >= 13 && time[11..13] == "12") noon = item;
                    if (Json.Number(item, "windSpeed") is { } speed) maxWind = maxWind is null ? speed : Math.Max(maxWind.Value, speed);
                }
                var pick = noon ?? hourly.EnumerateArray().FirstOrDefault();
                if (pick.ValueKind == JsonValueKind.Object)
                {
                    day.Code = WeatherCodes.FromHeFeng(Json.Text(pick, "icon"));
                    day.Condition = Json.Text(pick, "text");
                    day.WindDir = Json.Text(pick, "windDir");
                    day.WindDirDeg = Json.Number(pick, "wind360");
                }
                day.WindMaxKmh = maxWind;
            }
            return day;
        }
        catch { return null; }
    }

    // ---- v1 数值解包：{ value, unit } → 界面统一的 °C / km/h / km / % ----
    private static double? Value(JsonElement parent, string name)
        => Json.Get(parent, name) is { } box ? Json.Number(box, "value") : null;

    private static string? UnitOf(JsonElement parent, string name)
        => Json.Get(parent, name) is { } box ? Json.Text(box, "unit") : null;

    private static double? SpeedKmh(JsonElement parent, string name) => Value(parent, name) is { } value
        ? value * (UnitOf(parent, name) switch { "km/h" or "kph" => 1, "mph" => 1.609344, "kn" => 1.852, _ => 3.6 })
        : null;

    private static double? DistanceKm(JsonElement parent, string name) => Value(parent, name) is { } value
        ? value * (UnitOf(parent, name) switch { "m" => 0.001, "ft" => 0.0003048, _ => 1 })
        : null;

    // {value, unit} 里的数值按返回单位归一到 °C / hPa / mm。和风默认公制，但控制台可以把账号切成英制；
    // 统一在入口换算后，模型和界面始终只处理公制，不必到处判断单位。
    private static double? TemperatureC(JsonElement parent, string name)
        => Value(parent, name) is { } value
            ? UnitOf(parent, name) switch
            {
                "°F" or "F" or "fahrenheit" => (value - 32) * 5 / 9,
                "K" or "kelvin" => value - 273.15,
                _ => value
            }
            : null;

    private static double? PressureHpa(JsonElement parent, string name)
        => Value(parent, name) is { } value
            ? UnitOf(parent, name) switch
            {
                "Pa" or "pa" => value / 100,
                "kPa" or "kpa" => value * 10,
                "inHg" or "inhg" => value * 33.8639,
                "mmHg" or "mmhg" => value * 1.33322,
                "atm" => value * 1013.25,
                _ => value
            }
            : null;

    private static double? PrecipMmValue(JsonElement box)
        => Json.Number(box, "value") is { } value
            ? Json.Text(box, "unit") switch
            {
                "cm" => value * 10,
                "in" or "inch" => value * 25.4,
                _ => value
            }
            : null;

    // 0..1 的比例转百分比；来源已给百分数（>1.5）时原样保留。
    private static double? Percent(JsonElement root, string name) => Json.Number(root, name) is { } value
        ? Math.Round(value <= 1.5 ? value * 100 : value, 1) : null;

    private static int? PercentInt(JsonElement root, string name)
        => Percent(root, name) is { } value ? (int)Math.Round(value) : null;

    // v1 时间默认是 UTC；带 Z 的按城市时区折算，已带偏移（localTime=true）的直接用。
    private static DateTimeOffset? Stamp(string? raw, City city)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)) return null;
        bool utc = raw.EndsWith('Z');
        return utc ? stamp.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(city, stamp))) : stamp;
    }

    // v1 只给 compass 代码（sw/nne…），界面按 8 方位归并；原始度数仍保留在 WindDirDeg 里。
    private static string? CompassText(string? compass) => compass?.ToLowerInvariant() switch
    {
        "n" or "nnw" => Loc.T("北风", "N"),
        "nne" or "ne" => Loc.T("东北风", "NE"),
        "ene" or "e" => Loc.T("东风", "E"),
        "ese" or "se" => Loc.T("东南风", "SE"),
        "sse" or "s" => Loc.T("南风", "S"),
        "ssw" or "sw" => Loc.T("西南风", "SW"),
        "wsw" or "w" => Loc.T("西风", "W"),
        "wnw" or "nw" => Loc.T("西北风", "NW"),
        _ => null
    };


    // ---- v1 解析：数值是 { value, unit } 对象，时间默认 UTC，比例字段是 0..1 ----
    private static CurrentPart? ParseCurrentV1(string? body, City city)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // 出错时返回的是 error 对象，没有 condition。
            if (Json.Get(root, "condition") is not { } condition) return null;
            var wind = Json.Get(root, "wind");
            var direction = wind is { } w && Json.Get(w, "direction") is { } dir ? dir : (JsonElement?)null;
            var precipitation = Json.Get(root, "precipitation");
            double offset = Geo.UtcOffsetHours(city);
            return new CurrentPart
            {
                Temperature = TemperatureC(root, "temperature"),
                FeelsLike = TemperatureC(root, "feelsLike"),
                Humidity = PercentInt(root, "humidity"),
                WindKmh = wind is { } windBox ? SpeedKmh(windBox, "speed") : null,
                WindGustKmh = SpeedKmh(root, "windGust"),
                WindDirDeg = direction is { } dirBox ? Json.Number(dirBox, "degree") : null,
                WindDir = direction is { } compassBox ? CompassText(Json.Text(compassBox, "compass")) : null,
                PressureHpa = PressureHpa(root, "pressure"),
                VisibilityKm = DistanceKm(root, "visibility"),
                DewPointC = TemperatureC(root, "dewPoint"),
                Uv = Json.Number(root, "uvIndex"),
                CloudPercent = Percent(root, "cloudCover"),
                PrecipMm = precipitation is { } p && Json.Get(p, "amount") is { } amount ? PrecipMmValue(amount) : null,
                WindScale = wind is { } scaleBox ? Json.Number(scaleBox, "scale") is { } sc ? (int)Math.Round(sc) : null : null,
                PrecipRateMmH = precipitation is { } pr && Json.Get(pr, "intensity") is { } inten ? PrecipMmValue(inten) : null,
                PrecipType = precipitation is { } pt ? Json.Text(pt, "type") : null,
                Code = WeatherCodes.FromHeFeng(Json.Text(condition, "code")),
                // v1 不给昼夜标记，按城市当地时刻判断，国外城市才不会用本机时区算错。
                IsDay = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(offset)).Hour is >= 6 and < 18
            };
        }
        catch { return null; }
    }

    private static List<HourPoint>? ParseHoursV1(string? body, City city)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "hours") is not { } hoursBox || hoursBox.ValueKind != JsonValueKind.Array) return null;
            var hours = new List<HourPoint>();
            foreach (var item in hoursBox.EnumerateArray())
            {
                if (Stamp(Json.Text(item, "forecastTime"), city) is not { } stamp) continue;
                var wind = Json.Get(item, "wind");
                var direction = wind is { } w && Json.Get(w, "direction") is { } dir ? dir : (JsonElement?)null;
                var precipitation = Json.Get(item, "precipitation");
                hours.Add(new HourPoint
                {
                    Time = stamp.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    Temperature = TemperatureC(item, "temperature"),
                    FeelsLike = TemperatureC(item, "feelsLike"),
                    Humidity = PercentInt(item, "humidity"),
                    Code = WeatherCodes.FromHeFeng(Json.Get(item, "condition") is { } c ? Json.Text(c, "code") : null),
                    RainProb = precipitation is { } pp ? PercentInt(pp, "probability") : null,
                    PrecipMm = precipitation is { } pa && Json.Get(pa, "amount") is { } amount ? PrecipMmValue(amount) : null,
                    WindKmh = wind is { } windBox ? SpeedKmh(windBox, "speed") : null,
                    WindGustKmh = SpeedKmh(item, "windGust"),
                    WindDirDeg = direction is { } dirBox ? Json.Number(dirBox, "degree") : null,
                    PressureHpa = PressureHpa(item, "pressure"),
                    Uv = Json.Number(item, "uvIndex"),
                    DewPointC = TemperatureC(item, "dewPoint"),
                    CloudPercent = Percent(item, "cloudCover"),
                    VisibilityKm = DistanceKm(item, "visibility")
                });
            }
            return hours;
        }
        catch { return null; }
    }

    // v1 每日最多 10 天；第 11 天以后没有数据，界面显示 "-"。
    private static List<DayPoint>? ParseDaysV1(string? body, City city)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "days") is not { } daysBox || daysBox.ValueKind != JsonValueKind.Array) return null;
            var days = new List<DayPoint>();
            foreach (var item in daysBox.EnumerateArray())
            {
                if (Stamp(Json.Text(item, "forecastStartTime"), city) is not { } stamp) continue;
                var day = new DayPoint
                {
                    Date = stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    HighC = TemperatureC(item, "temperatureMax"),
                    LowC = TemperatureC(item, "temperatureMin"),
                    AvgTempC = TemperatureC(item, "temperatureAvg"),
                    UvMax = Json.Number(item, "uvIndexMax")
                };
                if (Json.Get(item, "daytime") is { } daytime)
                {
                    var condition = Json.Get(daytime, "condition");
                    day.Code = WeatherCodes.FromHeFeng(condition is { } c ? Json.Text(c, "code") : null);
                    day.Condition = condition is { } ct ? Json.Text(ct, "text") : null;
                    if (Json.Get(daytime, "wind") is { } wind)
                    {
                        day.WindMaxKmh = SpeedKmh(wind, "speed");
                        day.WindDirDeg = Json.Get(wind, "direction") is { } dir ? Json.Number(dir, "degree") : null;
                        day.WindDir = Json.Get(wind, "direction") is { } compassBox ? CompassText(Json.Text(compassBox, "compass")) : null;
                    }
                    day.GustMaxKmh = SpeedKmh(daytime, "windGustMax");
                    day.Humidity = PercentInt(daytime, "humidity");
                    if (Json.Get(daytime, "precipitation") is { } precipitation)
                    {
                        day.PrecipMm = Json.Get(precipitation, "amount") is { } amount ? PrecipMmValue(amount) : null;
                        day.RainProb = PercentInt(precipitation, "probability");
                    }
                    day.CloudPercent = Percent(daytime, "cloudCover");
                }
                // 夜间分段：夜间天气、湿度、风、降水、云量，与白天并列展示。
                if (Json.Get(item, "nighttime") is { } night)
                {
                    var nightCondition = Json.Get(night, "condition");
                    day.NightCode = WeatherCodes.FromHeFeng(nightCondition is { } nc ? Json.Text(nc, "code") : null);
                    day.NightCondition = nightCondition is { } nct ? Json.Text(nct, "text") : null;
                    day.NightHumidity = PercentInt(night, "humidity");
                    if (Json.Get(night, "wind") is { } nightWind)
                    {
                        day.NightWindMaxKmh = SpeedKmh(nightWind, "speed");
                        day.NightWindDirDeg = Json.Get(nightWind, "direction") is { } ndir ? Json.Number(ndir, "degree") : null;
                        day.NightWindDir = Json.Get(nightWind, "direction") is { } ncompass ? CompassText(Json.Text(ncompass, "compass")) : null;
                    }
                    day.NightGustMaxKmh = SpeedKmh(night, "windGustMax");
                    if (Json.Get(night, "precipitation") is { } nightPrecip)
                    {
                        day.NightPrecipMm = Json.Get(nightPrecip, "amount") is { } namount ? PrecipMmValue(namount) : null;
                        day.NightRainProb = PercentInt(nightPrecip, "probability");
                    }
                    day.NightCloudPercent = Percent(night, "cloudCover");
                }
                if (Json.Get(item, "astro") is { } astro)
                {
                    // 界面按 ISO 时刻取第 11–16 位显示，这里保持与 Open-Meteo 一致的完整格式。
                    day.Sunrise = Stamp(Json.Text(astro, "sunrise"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.Sunset = Stamp(Json.Text(astro, "sunset"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.MoonPhase = Json.Text(astro, "moonPhase");
                    day.Moonrise = Stamp(Json.Text(astro, "moonrise"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.Moonset = Stamp(Json.Text(astro, "moonset"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    // 天文轨道曲线的峰值：太阳正午、月过中天；其余 astro 时刻无视图消费，不解析。
                    day.SolarNoon = Stamp(Json.Text(astro, "solarNoon"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.MoonTransit = Stamp(Json.Text(astro, "moonTransit"), city)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                }
                days.Add(day);
            }
            return days;
        }
        catch { return null; }
    }

    // 实况与逐时预报共用同一套 AQI 标准优先级：中国城市取 cn-mee（QAQI 不适用于中国），其他地区退回 qaqi。
    private static JsonElement ChooseAirIndex(JsonElement[] indexes)
    {
        foreach (string code in new[] { "cn-mee", "cn-mee-1h", "qaqi" })
        {
            var found = indexes.FirstOrDefault(item => Json.Text(item, "code") == code);
            if (found.ValueKind == JsonValueKind.Object) return found;
        }
        return indexes.FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
    }

    // 一次返回当地标准与通用两套 AQI：中国城市取 cn-mee（QAQI 不适用于中国），其他地区退回 qaqi。
    private static AirPart? ParseAirV1(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (Json.Get(root, "indexes") is not { } indexesBox || indexesBox.ValueKind != JsonValueKind.Array) return null;
            var indexes = indexesBox.EnumerateArray().ToArray();
            var chosen = ChooseAirIndex(indexes);
            if (chosen.ValueKind != JsonValueKind.Object) return null;
            int? aqi = Json.Number(chosen, "aqi") is { } value ? (int)Math.Round(value) : null;
            string? level = Json.Text(chosen, "level");
            var primary = Json.Get(chosen, "primaryPollutant");
            var health = Json.Get(chosen, "health");
            var advice = health is { } h && Json.Get(h, "advice") is { } a ? a : (JsonElement?)null;
            // 通用国际标准（qaqi）：国内标准为主时并列展示，qaqi 为主时不重复存。
            int? aqiGlobal = Json.Text(chosen, "code") != "qaqi" && indexes.FirstOrDefault(item => Json.Text(item, "code") == "qaqi") is { } global
                ? Json.Number(global, "aqi") is { } g ? (int)Math.Round(g) : null : null;
            var air = new AirPart
            {
                Aqi = aqi,
                AqiGlobal = aqiGlobal,
                Level = int.TryParse(level, out var parsed) ? parsed : null,
                Text = Json.Text(chosen, "category"),
                Primary = primary is { } p ? Json.Text(p, "code") : null,
                PrimaryName = primary is { } pn ? Json.Text(pn, "name") : null,
                HealthEffect = health is { } he ? Json.Text(he, "effect") : null,
                HealthGeneral = advice is { } ga ? Json.Text(ga, "generalPopulation") : null,
                HealthSensitive = advice is { } sa ? Json.Text(sa, "sensitivePopulation") : null
            };
            if (Json.Get(root, "pollutants") is { } pollutants && pollutants.ValueKind == JsonValueKind.Array)
                foreach (var item in pollutants.EnumerateArray())
                {
                    string? code = Json.Text(item, "code");
                    if (code is null || Json.Get(item, "concentration") is not { } concentration) continue;
                    if (Json.Number(concentration, "value") is not { } amount) continue;
                    air.Pollutants[code] = amount;
                }
            if (air.Pollutants.TryGetValue("pm2p5", out var pm25)) air.Pm25 = (int)Math.Round(pm25);
            if (air.Pollutants.TryGetValue("pm10", out var pm10)) air.Pm10 = (int)Math.Round(pm10);
            return air.HasData ? air : null;
        }
        catch { return null; }
    }

    // 未来 24 小时逐时 AQI：按整点键存，稍后与天气逐时窗口对齐；只取 AQI，等级/颜色/污染物不在本模型里。
    private static Dictionary<string, int>? ParseAirHoursV1(string? body, City city)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "hours") is not { } hoursBox || hoursBox.ValueKind != JsonValueKind.Array) return null;
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in hoursBox.EnumerateArray())
            {
                if (Stamp(Json.Text(item, "forecastTime"), city) is not { } stamp) continue;
                if (Json.Get(item, "indexes") is not { } indexesBox || indexesBox.ValueKind != JsonValueKind.Array) continue;
                var chosen = ChooseAirIndex(indexesBox.EnumerateArray().ToArray());
                if (chosen.ValueKind != JsonValueKind.Object || Json.Number(chosen, "aqi") is not { } aqi) continue;
                values[stamp.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)] = (int)Math.Round(aqi);
            }
            return values.Count > 0 ? values : null;
        }
        catch { return null; }
    }

    // ---- v7 扩展域解析：非 200 或未订阅一律返回空，界面按缺失显示 "-" ----
    private static bool V7Ok(JsonElement root) => Json.Text(root, "code") is "200" or "0";

    // 分钟降水逐点与整段文字总结同处一份响应，只解析一次 JSON。
    private static (List<MinutePoint>? Minutes, string? Summary) ParseMinutelyV7(string? body)
    {
        if (body is null) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!V7Ok(root)) return (null, null);
            string? summary = Json.Text(root, "summary");
            if (Json.Get(root, "minutely") is not { ValueKind: JsonValueKind.Array } list) return (null, summary);
            var result = new List<MinutePoint>();
            foreach (var item in list.EnumerateArray())
            {
                string? time = Json.Text(item, "fxTime") ?? Json.Text(item, "time");
                if (time is null) continue;
                result.Add(new MinutePoint { Time = time, Precip = Json.Number(item, "precip") });
            }
            return (result.Count > 0 ? result : null, summary);
        }
        catch { return (null, null); }
    }

    private static List<WarningItem>? ParseWarningV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // /weatheralert/v1/current 成功响应不带 code 字段（{"metadata":…,"alerts":[…]}，2026-11-21 实测），
            // 不能用 v7 的 V7Ok 校验：只在显式给出错误 code 时判失败，alerts[] 存在即成功，否则预警永远解析不出来。
            string? code = Json.Text(root, "code");
            if (code is { Length: > 0 } && code is not ("200" or "0")) return null;
            if (Json.Get(root, "alerts") is not { ValueKind: JsonValueKind.Array } list) return null;
            var result = new List<WarningItem>();
            foreach (var item in list.EnumerateArray())
            {
                string type = Json.Text(item, "headline") ?? "";
                if (Json.Get(item, "eventType") is { } evt) type = Json.Text(evt, "name") ?? type;
                string level = Json.Text(item, "severity") ?? "";
                if (Json.Get(item, "color") is { } color) level = Json.Text(color, "code") ?? level;
                result.Add(new WarningItem
                {
                    Title = Json.Text(item, "headline") ?? "",
                    Text = Json.Text(item, "description") ?? "",
                    Level = level,
                    Type = type
                });
            }
            // 空数组是一次有效回答（当前无预警），不能折叠成 null，否则聚合器分不清「没预警」和「没请求到」。
            return result;
        }
        catch { return null; }
    }

    private static List<IndexItem>? ParseIndicesV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!V7Ok(root) || Json.Get(root, "daily") is not { ValueKind: JsonValueKind.Array } list) return null;
            var result = new List<IndexItem>();
            foreach (var item in list.EnumerateArray())
                result.Add(new IndexItem
                {
                    Type = Json.Text(item, "type") ?? "",
                    Name = Json.Text(item, "name") ?? "",
                    Level = Json.Text(item, "level") ?? "",
                    Category = Json.Text(item, "category") ?? "",
                    Text = Json.Text(item, "text") ?? ""
                });
            return result.Count > 0 ? result : null;
        }
        catch { return null; }
    }
}
