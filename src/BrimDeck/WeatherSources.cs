using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 数据域：来源调度的最小粒度。每个域可以由不同来源提供，聚合器再按下表的顺序拼接。
internal enum WeatherDomain { Current, Hourly, Daily, Sun, Air, History }

// 来源覆盖范围。国内源只在中国坐标附近可用，国外源全球可用。
internal enum WeatherRegion { Global, China }

// 一个来源的静态能力描述：能做哪些域、覆盖哪里、是否需要 key。
internal sealed record WeatherSourceInfo(string Id, string Name, string Detail, WeatherDomain[] Domains, WeatherRegion Region, bool NeedsKey);

// 当前实况：所有字段可空，来源只填自己知道的字段。用字段而不是属性，合并时可以 ref 写入。
internal sealed class CurrentPart
{
    public double? Temperature;
    public double? FeelsLike;
    public int? Humidity;
    public double? WindKmh;
    public double? WindGustKmh;
    public double? WindDirDeg;
    public string? WindDir;
    public double? PressureHpa;
    public double? VisibilityKm;
    public double? DewPointC;
    public double? Uv;
    public double? CloudPercent;
    public double? PrecipMm;
    public int? Code;
    public bool? IsDay;
    public string? Condition;
}

// 空气质量：AQI、首要污染物、健康建议与全部污染物浓度。
// Pollutants/Units 用污染物代码（pm2p5、o3…）作键，来源给几项就存几项，界面按需取用。
internal sealed class AirPart
{
    public int? Aqi { get; set; }
    public int? Pm25 { get; set; }
    public int? Pm10 { get; set; }
    public int? Level { get; set; }
    public string? Text { get; set; }
    public string? Primary { get; set; }          // 首要污染物代码
    public string? PrimaryName { get; set; }      // 首要污染物名称
    public string? HealthEffect { get; set; }     // 对健康的影响
    public string? HealthGeneral { get; set; }    // 一般人群建议
    public string? HealthSensitive { get; set; }  // 敏感人群建议
    public Dictionary<string, double> Pollutants { get; set; } = [];
    public Dictionary<string, string> Units { get; set; } = [];
    public bool HasData => Aqi is not null || Pm25 is not null || Pm10 is not null || Pollutants.Count > 0;
}

// 一个小时的观测/预报，时间统一为 yyyy-MM-ddTHH:mm。
internal sealed class HourPoint
{
    public string Time { get; set; } = "";
    public double? Temperature { get; set; }
    public double? FeelsLike { get; set; }
    public int? Humidity { get; set; }
    public int? Code { get; set; }
    public int? RainProb { get; set; }
    public double? PrecipMm { get; set; }
    public double? WindKmh { get; set; }
    public double? WindGustKmh { get; set; }
    public double? WindDirDeg { get; set; }
    public double? PressureHpa { get; set; }
    public double? Uv { get; set; }
    public double? DewPointC { get; set; }
    public double? CloudPercent { get; set; }
    public double? VisibilityKm { get; set; }
}

// 一天的预报/历史，日期统一为 yyyy-MM-dd。用字段而不是属性，合并时可以 ref 写入。
internal sealed class DayPoint
{
    public string Date = "";
    public int? Code;
    public string? Condition;
    public double? HighC;
    public double? LowC;
    public double? FeelsHighC;
    public double? FeelsLowC;
    public string? Sunrise;
    public string? Sunset;
    public int? RainProb;
    public double? PrecipMm;
    public double? UvMax;
    public double? WindMaxKmh;
    public double? GustMaxKmh;
    public double? WindDirDeg;
    public string? WindDir;
    public int? Humidity;
    public double? PressureHpa;
    public string? MoonPhase;
}

// 一个来源一次请求能拿到的全部数据；聚合器按域挑选。
internal sealed class WeatherPart
{
    public CurrentPart? Current { get; set; }
    public AirPart? Air { get; set; }
    public List<HourPoint>? Hours { get; set; }
    public List<DayPoint>? Days { get; set; }
    // 城市所在时区（Open-Meteo 的 timezone 字段），用于确定“当地今天”和小时窗口。
    public string? TimeZone { get; set; }
}

// 来源调度的持久化配置：每个域可选 auto 或具体来源 id；和风为唯一需要 key 的来源。
internal sealed class WeatherSettings
{
    public bool PreferDomestic { get; set; } = true;
    public Dictionary<string, string> Sources { get; set; } = [];
    public string HeFengKey { get; set; } = "";
    public string HeFengHost { get; set; } = "devapi.qweather.com";
    // 和风时光机：过去 10 天每天一次请求，默认关闭；开启后历史由和风提供，覆盖中国天气网的实测降水。
    public bool HeFengHistory { get; set; }
    public string Choice(WeatherDomain domain) => Sources.TryGetValue(domain.ToString(), out var value) && value.Length > 0 ? value : "auto";
    // 历史域：显式把「历史天气」指定为和风，或打开时光机开关，才走和风。默认关闭——10 天要 10 次请求。
    public bool WantsHeFengHistory() => HeFengHistory || Choice(WeatherDomain.History) == "hefeng";
}

// 来源集合与查询入口。
internal static class WeatherSources
{
    public static readonly IWeatherSource[] All =
    [
        new OpenMeteoSource(), new TencentSource(), new WeatherComCnSource(), new HeFengSource()
    ];
    public static IWeatherSource? Find(string id) => All.FirstOrDefault(s => s.Info.Id == id);
    public static string Name(string id) => Find(id)?.Info.Name ?? id;
    // 某域可用的来源，按“是否可用”过滤（国内源需要中文城市名，key 源需要有 key）。
    public static IEnumerable<IWeatherSource> For(WeatherDomain domain, City city, WeatherSettings settings)
        => All.Where(s => s.Info.Domains.Contains(domain) && s.CanServe(city, settings));
}

internal interface IWeatherSource
{
    WeatherSourceInfo Info { get; }
    bool CanServe(City city, WeatherSettings settings);
    Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token);
}

// 多源调度与合并：每个域按一条来源链依次尝试，先到先得，缺失字段由后续来源补齐。
internal static class WeatherAggregator
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);

    public static async Task<WeatherSnapshot> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        var chains = Enum.GetValues<WeatherDomain>().ToDictionary(domain => domain, domain => Chain(domain, city, settings));
        var sources = chains.Values.SelectMany(chain => chain).DistinctBy(source => source.Info.Id).ToArray();
        var fetched = await Task.WhenAll(sources.Select(async source => (source.Info.Id, Part: await TryAsync(source, city, settings, token))));
        var parts = fetched.Where(item => item.Part is not null).ToDictionary(item => item.Id, item => item.Part!);
        if (parts.Count == 0) throw new InvalidOperationException("没有可用的天气来源");
        return Merge(city, chains, parts);
    }

    // 来源链：显式选择优先；默认国内城市用国内源，国外城市用全球源；Open-Meteo 永远兜底。
    private static List<IWeatherSource> Chain(WeatherDomain domain, City city, WeatherSettings settings)
    {
        var chain = new List<IWeatherSource>();
        void Add(IWeatherSource? source)
        {
            if (source is not null && source.Info.Domains.Contains(domain) && source.CanServe(city, settings) && !chain.Contains(source)) chain.Add(source);
        }
        // 手动指定的来源放在最前；它失败时仍会走完整回退链。
        string choice = settings.Choice(domain);
        if (choice != "auto") Add(WeatherSources.Find(choice));
        // 有 key 的和风天气质量最好，自动调度时优先（用户在单个域手动指定时不被覆盖）。
        // 历史域例外：时光机按天计费，只有用户显式打开开关或指定来源时才请求。
        if (settings.HeFengKey.Trim().Length > 0 && (domain != WeatherDomain.History || settings.WantsHeFengHistory())) Add(WeatherSources.Find("hefeng"));
        bool china = Geo.IsChina(city);
        if (china && settings.PreferDomestic)
        {
            switch (domain)
            {
                case WeatherDomain.Current: Add(WeatherSources.Find("tencent")); Add(WeatherSources.Find("weathercomcn")); break;
                case WeatherDomain.Daily: Add(WeatherSources.Find("weathercomcn")); Add(WeatherSources.Find("tencent")); break;
                case WeatherDomain.Air: Add(WeatherSources.Find("tencent")); Add(WeatherSources.Find("weathercomcn")); break;
                case WeatherDomain.Sun: break;                 // 国内源不提供日出日落，交给 Open-Meteo
                case WeatherDomain.Hourly: break;              // 国内源没有稳定的小时接口
            }
        }
        // Open-Meteo 全球可用，任何域都排在最后兜底。
        Add(WeatherSources.Find("openmeteo"));
        return chain;
    }

    private static async Task<WeatherPart?> TryAsync(IWeatherSource source, City city, WeatherSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(SourceTimeout);
        try { return await source.FetchAsync(city, settings, cts.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // 合并：先建立以今天为中心的 24 天日期轴（历史 10 + 今天 + 未来 13），再按域把来源链的数据叠上去。
    private static WeatherSnapshot Merge(City city, Dictionary<WeatherDomain, List<IWeatherSource>> chains, Dictionary<string, WeatherPart> parts)
    {
        // 以城市当地日期为中心：查询国外城市时，“今天”不能按用户所在时区算。
        string? zone = parts.Values.Select(part => part.TimeZone).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var today = LocalToday(zone);
        var dates = Enumerable.Range(-10, 24).Select(offset => today.AddDays(offset)).ToArray();
        var dayMap = dates.ToDictionary(date => date.ToString("yyyy-MM-dd"), _ => new DayPoint());
        foreach (var (date, point) in dayMap) point.Date = date;
        var hours = new SortedDictionary<string, HourPoint>(StringComparer.Ordinal);
        var current = new CurrentPart();
        AirPart? air = null;
        var used = new Dictionary<WeatherDomain, List<string>>();
        var provided = new Dictionary<string, bool>();
        var fieldSources = new Dictionary<string, string>();
        void Use(WeatherDomain domain, IWeatherSource source)
        {
            provided[source.Info.Id] = true;
            if (!used.TryGetValue(domain, out var list)) used[domain] = list = [];
            if (!list.Contains(source.Info.Name)) list.Add(source.Info.Name);
        }

        foreach (var domain in Enum.GetValues<WeatherDomain>())
        {
            foreach (var source in chains[domain])
            {
                if (!parts.TryGetValue(source.Info.Id, out var part)) continue;
                switch (domain)
                {
                    case WeatherDomain.Current:
                        if (part.Current is { } c) { bool any = MergeCurrent(current, c, source.Info.Id, fieldSources); if (any) Use(domain, source); }
                        break;
                    case WeatherDomain.Air:
                        // 空气质量按字段补齐：腾讯给 AQI，Open-Meteo 补 PM2.5/PM10，避免整块先到先得丢掉污染物浓度。
                        if (part.Air is { } a && a.HasData)
                        {
                            air ??= new AirPart();
                            bool any = false;
                            if (air.Aqi is null && a.Aqi is not null) { air.Aqi = a.Aqi; fieldSources["aqi"] = source.Info.Id; any = true; }
                            if (air.Pm25 is null && a.Pm25 is not null) { air.Pm25 = a.Pm25; fieldSources["pm25"] = source.Info.Id; any = true; }
                            if (air.Pm10 is null && a.Pm10 is not null) { air.Pm10 = a.Pm10; fieldSources["pm10"] = source.Info.Id; any = true; }
                            if (air.Level is null && a.Level is not null) { air.Level = a.Level; any = true; }
                            if (air.Text is null && a.Text is not null) { air.Text = a.Text; any = true; }
                            if (air.Primary is null && a.Primary is not null) { air.Primary = a.Primary; any = true; }
                            if (air.PrimaryName is null && a.PrimaryName is not null) { air.PrimaryName = a.PrimaryName; any = true; }
                            if (air.HealthEffect is null && a.HealthEffect is not null) { air.HealthEffect = a.HealthEffect; any = true; }
                            if (air.HealthGeneral is null && a.HealthGeneral is not null) { air.HealthGeneral = a.HealthGeneral; any = true; }
                            if (air.HealthSensitive is null && a.HealthSensitive is not null) { air.HealthSensitive = a.HealthSensitive; any = true; }
                            // 污染物逐项补齐：排在前面的来源已经给出的项不再覆盖。
                            foreach (var (code, value) in a.Pollutants)
                            {
                                if (!air.Pollutants.TryAdd(code, value)) continue;
                                if (a.Units.TryGetValue(code, out var unit)) air.Units[code] = unit;
                                fieldSources["air:" + code] = source.Info.Id;
                                any = true;
                            }
                            if (any) Use(domain, source);
                        }
                        break;
                    case WeatherDomain.Hourly:
                        if (part.Hours is { Count: > 0 } hs) { bool any = false; foreach (var hour in hs) if (!hours.ContainsKey(hour.Time)) { hours[hour.Time] = hour; fieldSources.TryAdd("h:" + hour.Time, source.Info.Id); any = true; } if (any) Use(domain, source); }
                        break;
                    case WeatherDomain.Daily:
                    case WeatherDomain.Sun:
                    case WeatherDomain.History:
                        if (part.Days is { Count: > 0 } ds)
                        {
                            bool any = false;
                            foreach (var day in ds)
                            {
                                if (!dayMap.TryGetValue(day.Date, out var target)) continue;
                                bool filled = domain == WeatherDomain.Sun ? MergeSun(target, day, source.Info.Id, fieldSources) : MergeDay(target, day, source.Info.Id, fieldSources);
                                if (filled) any = true;
                            }
                            if (any) Use(domain, source);
                            // 每日域里顺带填上的日出日落也要记到「日出日落」来源，详情页才能逐域列出。
                            if (domain == WeatherDomain.Daily && part.Days.Any(day => day.Sunrise is not null || day.Sunset is not null)) Use(WeatherDomain.Sun, source);
                        }
                        break;
                }
            }
        }

        // 小时来源本身已从当地当前整点开始返回，按时间正序取前 24 条即可，不再用用户本地时钟过滤。
        var hourList = hours.Values.Take(24).ToArray();
        var snapshot = new WeatherSnapshot
        {
            Name = city.Name,
            Today = today.ToString("yyyy-MM-dd"),
            FetchedAt = DateTimeOffset.Now,
            Temperature = current.Temperature ?? double.NaN,
            FeelsLike = current.FeelsLike ?? current.Temperature ?? double.NaN,
            HumidityPercent = current.Humidity ?? -1,
            WindKmh = current.WindKmh ?? double.NaN,
            WindGustKmh = current.WindGustKmh ?? double.NaN,
            WindDirectionDeg = current.WindDirDeg ?? double.NaN,
            WindDirection = current.WindDir ?? "",
            PressureHpa = current.PressureHpa ?? double.NaN,
            VisibilityKm = current.VisibilityKm ?? double.NaN,
            DewPointC = current.DewPointC ?? double.NaN,
            UvIndex = current.Uv ?? double.NaN,
            CloudPercent = current.CloudPercent ?? double.NaN,
            PrecipitationMm = current.PrecipMm ?? double.NaN,
            WeatherCode = current.Code ?? 0,
            IsDay = current.IsDay ?? (DateTime.Now.Hour is >= 6 and < 18),
            ConditionText = current.Condition ?? "",
            Aqi = air?.Aqi ?? -1,
            Pm25 = air?.Pm25 ?? -1,
            Pm10 = air?.Pm10 ?? -1,
            AqiLevel = air?.Level ?? -1,
            AqiText = air?.Text ?? "",
            AirPrimary = air?.PrimaryName ?? air?.Primary ?? "",
            AirEffect = air?.HealthEffect ?? "",
            AirAdvice = air?.HealthGeneral ?? "",
            AirAdviceSensitive = air?.HealthSensitive ?? "",
            AirPollutants = air is null ? [] : AirLines(air),
            DayDates = dates.Select(date => date.ToString("yyyy-MM-dd")).ToArray(),
            DayCodes = datePoints(dayMap, dates, point => point.Code ?? -1),
            DayConditions = datePoints(dayMap, dates, point => point.Condition ?? ""),
            DayHighC = datePoints(dayMap, dates, point => point.HighC ?? double.NaN),
            DayLowC = datePoints(dayMap, dates, point => point.LowC ?? double.NaN),
            DayFeelsHighC = datePoints(dayMap, dates, point => point.FeelsHighC ?? double.NaN),
            DayFeelsLowC = datePoints(dayMap, dates, point => point.FeelsLowC ?? double.NaN),
            SunriseTimes = datePoints(dayMap, dates, point => point.Sunrise ?? ""),
            SunsetTimes = datePoints(dayMap, dates, point => point.Sunset ?? ""),
            DayRainProb = datePoints(dayMap, dates, point => point.RainProb ?? -1),
            DayPrecipMm = datePoints(dayMap, dates, point => point.PrecipMm ?? double.NaN),
            DayUvMax = datePoints(dayMap, dates, point => point.UvMax ?? double.NaN),
            DayWindMaxKmh = datePoints(dayMap, dates, point => point.WindMaxKmh ?? double.NaN),
            DayGustMaxKmh = datePoints(dayMap, dates, point => point.GustMaxKmh ?? double.NaN),
            DayWindDirDeg = datePoints(dayMap, dates, point => point.WindDirDeg ?? double.NaN),
            DayWindDir = datePoints(dayMap, dates, point => point.WindDir ?? ""),
            DayHumidity = datePoints(dayMap, dates, point => point.Humidity ?? -1),
            DayPressureHpa = datePoints(dayMap, dates, point => point.PressureHpa ?? double.NaN),
            DayMoonPhase = datePoints(dayMap, dates, point => point.MoonPhase ?? ""),
            HourTimes = hourList.Select(hour => hour.Time).ToArray(),
            HourTempsC = hourList.Select(hour => hour.Temperature ?? double.NaN).ToArray(),
            HourFeelsLikeC = hourList.Select(hour => hour.FeelsLike ?? double.NaN).ToArray(),
            HourHumidity = hourList.Select(hour => hour.Humidity ?? -1).ToArray(),
            HourCodes = hourList.Select(hour => hour.Code ?? -1).ToArray(),
            HourRainProb = hourList.Select(hour => hour.RainProb ?? -1).ToArray(),
            HourPrecipMm = hourList.Select(hour => hour.PrecipMm ?? double.NaN).ToArray(),
            HourWindKmh = hourList.Select(hour => hour.WindKmh ?? double.NaN).ToArray(),
            HourWindGustKmh = hourList.Select(hour => hour.WindGustKmh ?? double.NaN).ToArray(),
            HourWindDirDeg = hourList.Select(hour => hour.WindDirDeg ?? double.NaN).ToArray(),
            HourPressureHpa = hourList.Select(hour => hour.PressureHpa ?? double.NaN).ToArray(),
            HourUv = hourList.Select(hour => hour.Uv ?? double.NaN).ToArray(),
            HourDewPointC = hourList.Select(hour => hour.DewPointC ?? double.NaN).ToArray(),
            HourCloudPercent = hourList.Select(hour => hour.CloudPercent ?? double.NaN).ToArray(),
            HourVisibilityKm = hourList.Select(hour => hour.VisibilityKm ?? double.NaN).ToArray(),
            Sources = used.ToDictionary(pair => pair.Key.ToString(), pair => string.Join(" + ", pair.Value)),
            FieldSources = fieldSources,
            // 每域列出所有声明支持的来源（含未提供数据的），保序去重；图例中未提供的以灰色圆点标注。
            DomainSources = Enum.GetValues<WeatherDomain>().ToDictionary(
                domain => domain.ToString(),
                domain => WeatherSources.All.Where(source => source.Info.Domains.Contains(domain)).Select(source => source.Info.Id).Distinct().ToArray()),
            SourceProvided = provided
        };
        return snapshot;
    }

    private static T[] datePoints<T>(Dictionary<string, DayPoint> map, DateTime[] dates, Func<DayPoint, T> select)
        => dates.Select(date => select(map[date.ToString("yyyy-MM-dd")])).ToArray();

    // 污染物按固定顺序输出为「名称 → 值 单位」，界面照着列即可，不必再关心代码与单位换算。
    private static readonly string[] PollutantOrder = ["pm2p5", "pm10", "o3", "no2", "so2", "co", "no", "nmhc"];
    private static Dictionary<string, string> AirLines(AirPart air)
    {
        var lines = new Dictionary<string, string>();
        // 痕量气体（O₃、CO）常在 1 以下，用两位小数，避免被舍成 0。
        void Add(string code, double value)
            => lines[PollutantName(code)] = $"{(value is > 0 and < 1 ? value.ToString("0.00", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture))} {PrettyUnit(air.Units.GetValueOrDefault(code) ?? "")}".Trim();
        foreach (var code in PollutantOrder) if (air.Pollutants.TryGetValue(code, out var value)) Add(code, value);
        foreach (var (code, value) in air.Pollutants) if (!PollutantOrder.Contains(code)) Add(code, value);
        return lines;
    }
    private static string PollutantName(string code) => code switch
    {
        "pm2p5" => "PM2.5", "pm10" => "PM10", "o3" => "O₃", "no2" => "NO₂",
        "so2" => "SO₂", "co" => "CO", "no" => "NO", "nmhc" => "NMHC", _ => code.ToUpperInvariant()
    };
    private static string PrettyUnit(string unit) => unit.Replace("m3", "m³");

    // 空位才写入：来源链靠前的那份数据优先，后面的来源只补空缺。
    private static bool Set<T>(ref T? field, T? value) where T : struct
    {
        if (field is null && value is not null) { field = value; return true; }
        return false;
    }

    private static bool MergeCurrent(CurrentPart target, CurrentPart source, string sourceId, Dictionary<string, string> fieldSources)
    {
        bool any = false;
        bool Mark(bool ok, string field) { if (ok) { fieldSources[field] = sourceId; return true; } return false; }
        any |= Mark(Set(ref target.Temperature, source.Temperature), "temp");
        any |= Mark(Set(ref target.FeelsLike, source.FeelsLike), "feels");
        any |= Mark(Set(ref target.Humidity, source.Humidity), "humidity");
        any |= Mark(Set(ref target.WindKmh, source.WindKmh), "wind");
        any |= Mark(Set(ref target.WindGustKmh, source.WindGustKmh), "gust");
        any |= Mark(Set(ref target.WindDirDeg, source.WindDirDeg), "wind_dir");
        any |= Mark(Set(ref target.PressureHpa, source.PressureHpa), "pressure");
        any |= Mark(Set(ref target.VisibilityKm, source.VisibilityKm), "visibility");
        any |= Mark(Set(ref target.DewPointC, source.DewPointC), "dew");
        any |= Mark(Set(ref target.Uv, source.Uv), "uv");
        any |= Mark(Set(ref target.CloudPercent, source.CloudPercent), "cloud");
        any |= Mark(Set(ref target.PrecipMm, source.PrecipMm), "precip");
        any |= Mark(Set(ref target.Code, source.Code), "code");
        any |= Mark(Set(ref target.IsDay, source.IsDay), "is_day");
        if (target.WindDir is null && source.WindDir is not null) { target.WindDir = source.WindDir; fieldSources["wind_dir_text"] = sourceId; any = true; }
        if (target.Condition is null && source.Condition is not null) { target.Condition = source.Condition; fieldSources["condition"] = sourceId; any = true; }
        return any;
    }

    private static bool MergeDay(DayPoint target, DayPoint source, string sourceId, Dictionary<string, string> fieldSources)
    {
        string key = "d:" + target.Date;
        bool Mark(bool ok, string field) { if (ok) { fieldSources.TryAdd(field + ":" + target.Date, sourceId); fieldSources.TryAdd(key, sourceId); return true; } return false; }
        bool any = false;
        any |= Mark(Set<int>(ref target.Code, source.Code) || (target.Condition is null && source.Condition is not null), "d_cond");
        if (target.Condition is null && source.Condition is not null) { target.Condition = source.Condition; }
        any |= Mark(Set<double>(ref target.HighC, source.HighC) || Set<double>(ref target.FeelsHighC, source.FeelsHighC), "d_high");
        any |= Mark(Set<double>(ref target.LowC, source.LowC) || Set<double>(ref target.FeelsLowC, source.FeelsLowC), "d_low");
        any |= Mark(Set<int>(ref target.RainProb, source.RainProb) || Set<double>(ref target.PrecipMm, source.PrecipMm), "d_rain");
        any |= Mark(Set<double>(ref target.UvMax, source.UvMax), "d_uv");
        any |= Mark(Set<double>(ref target.WindMaxKmh, source.WindMaxKmh) || Set<double>(ref target.GustMaxKmh, source.GustMaxKmh)
            || Set<double>(ref target.WindDirDeg, source.WindDirDeg) || (target.WindDir is null && source.WindDir is not null), "d_wind");
        if (target.WindDir is null && source.WindDir is not null) { target.WindDir = source.WindDir; }
        if (target.MoonPhase is null && source.MoonPhase is not null) { target.MoonPhase = source.MoonPhase; fieldSources.TryAdd("d_moon:" + target.Date, sourceId); fieldSources.TryAdd(key, sourceId); any = true; }
        any |= Mark(Set<int>(ref target.Humidity, source.Humidity), "d_humidity");
        any |= Mark(Set<double>(ref target.PressureHpa, source.PressureHpa), "d_pressure");
        return MergeSun(target, source, sourceId, fieldSources) || any;
    }

    private static bool MergeSun(DayPoint target, DayPoint source, string sourceId, Dictionary<string, string> fieldSources)
    {
        bool any = false;
        if (target.Sunrise is null && source.Sunrise is not null) { target.Sunrise = source.Sunrise; fieldSources.TryAdd("d_sun:" + target.Date, sourceId); any = true; }
        if (target.Sunset is null && source.Sunset is not null) { target.Sunset = source.Sunset; fieldSources.TryAdd("d_sun:" + target.Date, sourceId); any = true; }
        return any;
    }

    // 时区缺失或系统不认识时回退到用户本地日期。
    private static DateTime LocalToday(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone)) return DateTime.Today;
        try { return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).Date; }
        catch { return DateTime.Today; }
    }
}

// 国内/国外判断与统一的城市名拆分。
internal static class Geo
{
    // 中国范围框（含港澳台与南海诸岛），用于决定“国内源优先”还是“全球源优先”。
    public static bool IsChina(City city) => city.Latitude is >= 17 and <= 54 && city.Longitude is >= 73 and <= 136;
    public static bool HasChinese(string text) => text.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
    // City.Name 的格式是「城市 · 省/州 · 国家」，拆出前两段供国内接口使用。
    public static (string City, string Province) Parts(City city)
    {
        var parts = city.Name.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string name = parts.Length > 0 ? parts[0] : city.Name;
        string province = parts.Length > 1 ? parts[1] : "";
        if (province is "中国" or "China" or "中國") province = "";
        return (name, province);
    }
    // 中国统一 UTC+8；其他地区按经度估算，仅用于本地日出日落推算的兜底。
    public static int UtcOffsetHours(City city) => IsChina(city) ? 8 : (int)Math.Round(city.Longitude / 15.0);
}

// 各来源的天气代码统一成 Open-Meteo 使用的 WMO code，界面图标与配色只认这一套。
internal static class WeatherCodes
{
    public static int FromTencent(string? code) => code switch
    {
        "00" => 0, "01" => 2, "02" => 3,
        "03" => 80, "04" => 95, "05" => 96, "06" => 68,
        "07" => 61, "08" => 63, "09" => 65, "10" => 65, "11" => 65, "12" => 65,
        "13" => 85, "14" => 71, "15" => 73, "16" => 75, "17" => 75,
        "18" => 45, "19" => 66, "20" => 45, "21" => 61, "22" => 63, "23" => 65,
        "24" => 65, "25" => 65, "26" => 71, "27" => 73, "28" => 75, "29" => 45,
        "30" => 45, "31" => 45, "53" => 45, _ => 3
    };

    // 中文天气文本（中国天气网/高德/腾讯文本）→ WMO。取“转”之前的第一种天气作为当日主天气。
    public static int FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 3;
        string value = text.Split('转')[0].Trim();
        if (value.Contains("雷")) return 95;
        if (value.Contains("暴雨")) return 65;
        if (value.Contains("大雨")) return 63;
        if (value.Contains("中雨")) return 63;
        if (value.Contains("小雨") || value.Contains("雨")) return 61;
        if (value.Contains("阵雨")) return 80;
        if (value.Contains("暴雪")) return 75;
        if (value.Contains("大雪")) return 75;
        if (value.Contains("中雪")) return 73;
        if (value.Contains("小雪") || value.Contains("雪")) return 71;
        if (value.Contains("雾")) return 45;
        if (value.Contains("霾") || value.Contains("浮尘") || value.Contains("扬沙") || value.Contains("沙尘")) return 45;
        if (value.Contains("阴")) return 3;
        if (value.Contains("多云")) return 2;
        if (value.Contains("晴")) return 0;
        return 3;
    }

    // 和风天气图标代码 → WMO。
    public static int FromHeFeng(string? code) => code switch
    {
        "100" => 0, "101" or "102" or "103" => 2, "104" => 3,
        "300" or "301" => 80, "302" => 95, "303" or "304" => 96, "305" or "309" => 61,
        "306" or "314" => 63, "307" or "315" => 65, "308" or "316" => 65, "310" or "317" => 65,
        "311" or "312" or "318" => 65, "313" => 66, "404" or "405" or "406" => 68,
        "400" or "408" => 71, "401" or "409" => 73, "402" or "410" => 75, "403" or "407" => 75,
        "500" or "501" or "509" or "510" or "514" or "515" => 45,
        "502" or "511" or "512" or "513" => 45, "503" or "504" or "507" or "508" => 45,
        "900" => 0, "901" => 0, "999" => 3, _ => 3
    };

    // 蒲福风级（"3-4" 级）→ 风速 km/h，取区间的中值。
    public static double FromWindPower(string? power)
    {
        if (string.IsNullOrWhiteSpace(power)) return double.NaN;
        var numbers = System.Text.RegularExpressions.Regex.Matches(power, "\\d+").Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length == 0) return double.NaN;
        double level = numbers.Average();
        // 蒲福风级对应的风速上界（m/s）：0→0.2, 1→1.5, 2→3.3, ... 12→32.6；取上界的 80% 作为代表值。
        double[] upper = [0.2, 1.5, 3.3, 5.4, 7.9, 10.7, 13.8, 17.1, 20.7, 24.4, 28.4, 32.6, 36.9];
        int index = Math.Clamp((int)Math.Round(level), 0, upper.Length - 1);
        return upper[index] * 0.8 * 3.6;
    }

    public static string Text(int code) => code switch
    {
        0 => Loc.T("晴", "Clear"), 1 or 2 => Loc.T("多云", "Cloudy"), 3 => Loc.T("阴", "Overcast"),
        45 or 48 => Loc.T("雾", "Fog"), >= 51 and <= 67 => Loc.T("雨", "Rain"), >= 71 and <= 77 => Loc.T("雪", "Snow"),
        >= 80 and <= 82 => Loc.T("阵雨", "Showers"), 85 or 86 => Loc.T("阵雪", "Snow showers"), >= 95 => Loc.T("雷雨", "Thunderstorm"),
        _ => Loc.T("多云", "Cloudy")
    };

    public static string AqiText(int aqi) => aqi switch
    {
        < 0 => "", <= 50 => Loc.T("优", "Good"), <= 100 => Loc.T("良", "Moderate"), <= 150 => Loc.T("轻度污染", "Light"),
        <= 200 => Loc.T("中度污染", "Moderate pollution"), <= 300 => Loc.T("重度污染", "Heavy"), _ => Loc.T("严重污染", "Severe")
    };

    public static string AqiColor(int aqi) => aqi switch
    {
        < 0 => "#9B9BA3", <= 50 => "#5CC85C", <= 100 => "#F2C94C", <= 150 => "#F2994A", <= 200 => "#EB5757", <= 300 => "#9B51E0", _ => "#B12E3A"
    };

    // 中文风向 → 角度，供详情页画风向箭头。
    public static double DegreesFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        if (text.Contains("东北")) return 45; if (text.Contains("东南")) return 135;
        if (text.Contains("西南")) return 225; if (text.Contains("西北")) return 315;
        if (text.Contains('北')) return 0; if (text.Contains('东')) return 90;
        if (text.Contains('南')) return 180; if (text.Contains('西')) return 270;
        return double.NaN;
    }
}

// 统一的网络入口：显式解压、固定 User-Agent，避免个别接口因压缩或 UA 被拒。
internal static class WeatherHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    static WeatherHttp()
    {
        // 浏览器 UA 加应用标识：国内站点对非浏览器 UA 偶尔会长时间不响应，MET Norway 则要求 UA 可识别。
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 BrimDeck/1.4");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }
}

// JSON 小工具：容忍 number/string 混用，缺失返回 null。
internal static class Json
{
    public static JsonElement? Get(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;
    public static string? Text(JsonElement root, string name) => Get(root, name) is { } value && value.ValueKind switch { JsonValueKind.String => true, JsonValueKind.Number => true, _ => false } ? value.ToString() : null;
    public static double? Number(JsonElement root, string name)
    {
        if (Get(root, name) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        return double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    public static int? Integer(JsonElement root, string name) => Number(root, name) is { } value ? (int)Math.Round(value) : null;
    public static string Esc(string text) => Uri.EscapeDataString(text);
}

internal sealed class OpenMeteoSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("openmeteo", "Open-Meteo", Loc.T("全球数值预报 · 免 Key · CC BY 4.0", "Global forecast · no key · CC BY 4.0"),
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Sun, WeatherDomain.Air, WeatherDomain.History], WeatherRegion.Global, false);
    public bool CanServe(City city, WeatherSettings settings) => true;

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        string location = FormattableString.Invariant($"latitude={city.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={city.Longitude.ToString(CultureInfo.InvariantCulture)}&timezone=auto");
        var forecast = GetStringAsync($"https://api.open-meteo.com/v1/forecast?{location}&current=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,weather_code,pressure_msl,cloud_cover,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,uv_index_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,relative_humidity_2m_mean,pressure_msl_mean&past_days=10&forecast_days=14", token);
        var hourly = GetStringAsync($"https://api.open-meteo.com/v1/forecast?{location}&hourly=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,precipitation_probability,weather_code,pressure_msl,cloud_cover,visibility,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index&forecast_hours=24", token);
        var air = GetStringAsync($"https://air-quality-api.open-meteo.com/v1/air-quality?{location}&current=pm2_5,pm10,us_aqi,european_aqi", token);
        await Task.WhenAll(forecast, hourly, air);
        var part = new WeatherPart();
        try
        {
            using var doc = JsonDocument.Parse(forecast.Result);
            var root = doc.RootElement;
            part.TimeZone = Json.Text(root, "timezone");
            if (Json.Get(root, "current") is { } current)
                part.Current = new CurrentPart
                {
                    Temperature = Json.Number(current, "temperature_2m"), FeelsLike = Json.Number(current, "apparent_temperature"),
                    Humidity = Json.Integer(current, "relative_humidity_2m"), DewPointC = Json.Number(current, "dew_point_2m"),
                    PrecipMm = Json.Number(current, "precipitation"), Code = Json.Integer(current, "weather_code"),
                    PressureHpa = Json.Number(current, "pressure_msl"), CloudPercent = Json.Number(current, "cloud_cover"),
                    WindKmh = Json.Number(current, "wind_speed_10m"), WindDirDeg = Json.Number(current, "wind_direction_10m"),
                    WindGustKmh = Json.Number(current, "wind_gusts_10m"), VisibilityKm = Json.Number(current, "visibility") is { } visibility ? visibility / 1000 : null,
                    IsDay = Json.Integer(current, "is_day") is { } day ? day != 0 : null
                };
            if (Json.Get(root, "daily") is { } daily && Json.Get(daily, "time") is { } dates)
            {
                int count = dates.GetArrayLength();
                string[] names = ["weather_code", "temperature_2m_max", "temperature_2m_min", "apparent_temperature_max", "apparent_temperature_min", "sunrise", "sunset", "uv_index_max", "precipitation_sum", "precipitation_probability_max", "wind_speed_10m_max", "wind_gusts_10m_max", "wind_direction_10m_dominant", "relative_humidity_2m_mean", "pressure_msl_mean"];
                var arrays = names.ToDictionary(name => name, name => Json.Get(daily, name));
                double? At(string name, int index) => arrays[name] is { } array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number ? array[index].GetDouble() : null;
                string? TextAt(string name, int index) => arrays[name] is { } array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.String ? array[index].GetString() : null;
                string? DateAt(int index) => index < count && dates[index].ValueKind == JsonValueKind.String ? dates[index].GetString() : null;
                part.Days = [];
                for (int i = 0; i < count; i++)
                {
                    string? date = DateAt(i); if (date is null) continue;
                    part.Days.Add(new DayPoint
                    {
                        Date = date[..10], Code = (int?)(At("weather_code", i)), HighC = At("temperature_2m_max", i), LowC = At("temperature_2m_min", i),
                        FeelsHighC = At("apparent_temperature_max", i), FeelsLowC = At("apparent_temperature_min", i),
                        Sunrise = TextAt("sunrise", i), Sunset = TextAt("sunset", i), UvMax = At("uv_index_max", i),
                        PrecipMm = At("precipitation_sum", i), RainProb = (int?)At("precipitation_probability_max", i),
                        WindMaxKmh = At("wind_speed_10m_max", i), GustMaxKmh = At("wind_gusts_10m_max", i),
                        WindDirDeg = At("wind_direction_10m_dominant", i), Humidity = (int?)At("relative_humidity_2m_mean", i),
                        PressureHpa = At("pressure_msl_mean", i)
                    });
                }
            }
        }
        catch { }
        try
        {
            using var doc = JsonDocument.Parse(hourly.Result);
            if (Json.Get(doc.RootElement, "hourly") is { } h && Json.Get(h, "time") is { } times)
            {
                int count = times.GetArrayLength();
                string[] names = ["temperature_2m", "apparent_temperature", "relative_humidity_2m", "dew_point_2m", "precipitation", "precipitation_probability", "weather_code", "pressure_msl", "cloud_cover", "visibility", "wind_speed_10m", "wind_direction_10m", "wind_gusts_10m", "uv_index"];
                var arrays = names.ToDictionary(name => name, name => Json.Get(h, name));
                double? At(string name, int index) => arrays[name] is { } array && index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number ? array[index].GetDouble() : null;
                string? TextAt(int index) => index < count && times[index].ValueKind == JsonValueKind.String ? times[index].GetString() : null;
                part.Hours = [];
                for (int i = 0; i < count; i++)
                {
                    string? time = TextAt(i); if (time is null) continue;
                    part.Hours.Add(new HourPoint
                    {
                        Time = time, Temperature = At("temperature_2m", i), FeelsLike = At("apparent_temperature", i),
                        Humidity = (int?)At("relative_humidity_2m", i), DewPointC = At("dew_point_2m", i), PrecipMm = At("precipitation", i),
                        RainProb = (int?)At("precipitation_probability", i), Code = (int?)At("weather_code", i), PressureHpa = At("pressure_msl", i),
                        CloudPercent = At("cloud_cover", i), VisibilityKm = At("visibility", i) is { } visibility ? visibility / 1000 : null,
                        WindKmh = At("wind_speed_10m", i), WindDirDeg = At("wind_direction_10m", i), WindGustKmh = At("wind_gusts_10m", i), Uv = At("uv_index", i)
                    });
                }
                // 当前 UV 从小时序列补齐：current 接口不返回 uv_index。
                if (part.Current is { Uv: null } && part.Hours.FirstOrDefault(hour => hour.Temperature is not null) is { Uv: { } uv }) part.Current.Uv = uv;
            }
        }
        catch { }
        try
        {
            using var doc = JsonDocument.Parse(air.Result);
            if (Json.Get(doc.RootElement, "current") is { } current
                && (Json.Integer(current, "us_aqi") is not null || Json.Integer(current, "pm2_5") is not null || Json.Integer(current, "pm10") is not null))
                part.Air = new AirPart
                {
                    Aqi = Json.Integer(current, "us_aqi"), Pm25 = Json.Integer(current, "pm2_5"), Pm10 = Json.Integer(current, "pm10"),
                    Level = Json.Integer(current, "us_aqi") is { } aqi ? AqiLevel(aqi) : null,
                    Text = Json.Integer(current, "us_aqi") is { } value ? WeatherCodes.AqiText(value) : null
                };
        }
        catch { }
        return part.Current is null && part.Days is null && part.Hours is null && part.Air is null ? null : part;
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken token) => await WeatherHttp.Client.GetStringAsync(url, token);
    private static int AqiLevel(int aqi) => aqi <= 50 ? 1 : aqi <= 100 ? 2 : aqi <= 150 ? 3 : aqi <= 200 ? 4 : aqi <= 300 ? 5 : 6;
}

internal sealed class TencentSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("tencent", Loc.T("腾讯天气", "Tencent Weather"), Loc.T("国内实况与 8 天预报 · 免 Key", "China current & 8-day · no key"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.Air], WeatherRegion.China, false);

    public bool CanServe(City city, WeatherSettings settings)
    {
        var (name, _) = Geo.Parts(city);
        // 接口只认中文城市名；英文环境下搜索出的城市改用 Open-Meteo。
        return Geo.IsChina(city) && name.Length > 0 && (Geo.HasChinese(name) || name.All(char.IsLetter));
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        var (name, province) = Geo.Parts(city);
        string url = $"https://wis.qq.com/weather/common?source=pc&weather_type=observe%7Cforecast_24h&province={Json.Esc(province)}&city={Json.Esc(name)}";
        string body = await WeatherHttp.Client.GetStringAsync(url, token);
        using var doc = JsonDocument.Parse(body);
        if (Json.Get(doc.RootElement, "data") is not { } data) return null;
        var part = new WeatherPart();
        if (Json.Get(data, "observe") is { } observe)
        {
            double? wind = Json.Text(observe, "wind_power") is { } power ? WeatherCodes.FromWindPower(power) : null;
            int? code = WeatherCodes.FromTencent(Json.Text(observe, "weather_code"));
            string? condition = Json.Text(observe, "weather");
            string? windDir = Json.Text(observe, "wind_direction_name");
            part.Current = new CurrentPart
            {
                Temperature = Json.Number(observe, "degree"), Humidity = Json.Integer(observe, "humidity"),
                PrecipMm = Json.Number(observe, "precipitation"), PressureHpa = Json.Number(observe, "pressure"),
                Code = code, Condition = condition, WindDir = windDir,
                WindDirDeg = WeatherCodes.DegreesFromText(windDir), WindKmh = wind,
                IsDay = DateTime.Now.Hour is >= 6 and < 18
            };
        }
        if (Json.Get(data, "forecast_24h") is { } forecast && forecast.ValueKind == JsonValueKind.Object)
        {
            part.Days = [];
            int todayAqi = -1;
            foreach (var entry in forecast.EnumerateObject())
            {
                var item = entry.Value;
                string? date = Json.Text(item, "time"); if (date is null) continue;
                int? code = WeatherCodes.FromTencent(Json.Text(item, "day_weather_code"));
                string? condition = Json.Text(item, "day_weather");
                part.Days.Add(new DayPoint
                {
                    Date = date[..10], Code = code, Condition = condition,
                    HighC = Json.Number(item, "max_degree"), LowC = Json.Number(item, "min_degree"),
                    WindDir = Json.Text(item, "day_wind_direction"),
                    WindDirDeg = WeatherCodes.DegreesFromText(Json.Text(item, "day_wind_direction")),
                    WindMaxKmh = Json.Text(item, "day_wind_power") is { } dayPower ? WeatherCodes.FromWindPower(dayPower) : null
                });
                if (date[..10] == DateTime.Today.ToString("yyyy-MM-dd") && Json.Integer(item, "aqi") is { } aqi && aqi > 0) todayAqi = aqi;
            }
            if (todayAqi >= 0) part.Air = new AirPart { Aqi = todayAqi, Level = todayAqi <= 50 ? 1 : todayAqi <= 100 ? 2 : todayAqi <= 150 ? 3 : 4, Text = WeatherCodes.AqiText(todayAqi) };
        }
        return part;
    }
}

internal sealed class WeatherComCnSource : IWeatherSource
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Ids = new();
    public WeatherSourceInfo Info { get; } = new("weathercomcn", Loc.T("中国天气网", "China Weather"), Loc.T("国内实况、15 天预报与历史降水 · 免 Key", "China current, 15-day & history · no key"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.History, WeatherDomain.Air], WeatherRegion.China, false);

    public bool CanServe(City city, WeatherSettings settings)
    {
        var (name, _) = Geo.Parts(city);
        return Geo.IsChina(city) && name.Length > 0 && Geo.HasChinese(name);
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        var (name, _) = Geo.Parts(city);
        string? id = await ResolveIdAsync(name, token);
        if (id is null) return null;
        var part = new WeatherPart();
        // 每个请求独立超时且失败返回 null：实况偶尔很慢，不能让它拖住已经拿到的每日预报。
        var currentTask = TryGetAsync($"http://d1.weather.com.cn/sk_2d/{id}.html", token, 4);
        // 24 天窗口可能跨月，按起止日期枚举涉及的每个月份。
        var months = new List<Task<string?>>();
        var start = DateTime.Today.AddDays(-10); var end = DateTime.Today.AddDays(13);
        for (var month = new DateTime(start.Year, start.Month, 1); month <= end; month = month.AddMonths(1))
        {
            string stamp = month.ToString("yyyyMM");
            months.Add(TryGetAsync($"http://d1.weather.com.cn/calendar_new/{stamp[..4]}/{id}_{stamp}.html", token, 6));
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
        if (Ids.TryGetValue(name, out var cached)) return cached;
        try
        {
            string? body = await TryGetAsync($"http://toy1.weather.com.cn/search?cityname={Json.Esc(name)}", token, 3);
            if (body is null) return null;
            string json = body.Trim().TrimStart('(').TrimEnd(')');
            if (ExtractArray(json) is not { } array) return null;
            using var doc = JsonDocument.Parse(array);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                string? reference = Json.Text(item, "ref");
                string? id = reference?.Split('~')[0];
                if (id is { Length: >= 9 } && id.All(char.IsDigit) && id.StartsWith("101")) { Ids[name] = id; return id; }
            }
        }
        catch { }
        return null;
    }

    private static async Task<string?> TryGetAsync(string url, CancellationToken token, int seconds)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("http://www.weather.com.cn/");
            using var response = await WeatherHttp.Client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
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

// 和风天气：需要用户自己的 Key，走 v1 接口覆盖实况、逐时、每日、日月与空气，质量最高，因此在自动调度中优先。
// v1 每日最多 10 天；v7 的 15 天仅作为旧 key 的退路保留，2027-06 停服后可整段删除。
internal sealed class HeFengSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("hefeng", Loc.T("和风天气", "QWeather"), Loc.T("全维度 · v1 + 时光机 · 需 Key", "All domains (v1 + time machine) · key required"),
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Sun, WeatherDomain.Air, WeatherDomain.History], WeatherRegion.Global, true);
    public bool CanServe(City city, WeatherSettings settings) => settings.HeFengKey.Trim().Length > 0;

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        string host = settings.HeFengHost.Trim().TrimEnd('/');
        if (host.Length == 0) host = "devapi.qweather.com";
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
        string key = settings.HeFengKey.Trim();
        // 两代接口都只接受小数点后两位的坐标，先取整再拼路径，避免长小数被拒。
        string lat = city.Latitude.ToString("0.00", CultureInfo.InvariantCulture);
        string lon = city.Longitude.ToString("0.00", CultureInfo.InvariantCulture);
        string lang = Loc.IsEnglish ? "en" : "zh";
        // v1 是现行接口（v7 城市天气 2027-06-01 停服）。旧版 key 绑定在公共 Host 上时 v1 可能整个不存在，
        // 因此 v1 一条都取不到时才回退 v7，两代结果不混用。
        // 时光机本身只有 v7 实现，与主请求并行发出；开关关闭时不请求（10 天 = 10 次调用）。
        var history = settings.WantsHeFengHistory() ? FetchHistory(host, key, lat, lon, city, token) : null;
        var part = await FetchV1(host, key, lat, lon, lang, city, token) ?? await FetchV7(host, key, lat, lon, lang, token);
        if (part is not null && history is not null && await history is { Count: > 0 } past)
        {
            // 历史日并入 Days：Daily 链里和风排在第一位，过去 10 天会先于中国天气网的实测降水被采用。
            part.Days ??= [];
            var known = part.Days.Select(day => day.Date).ToHashSet(StringComparer.Ordinal);
            part.Days.AddRange(past.Where(day => known.Add(day.Date)));
        }
        return part;
    }

    // v1：坐标走路径，数值包在 { value, unit } 里，湿度/云量/降水概率是 0..1 的比例。
    private static async Task<WeatherPart?> FetchV1(string host, string key, string lat, string lon, string lang, City city, CancellationToken token)
    {
        // localTime=true：v1 默认返回 UTC，而界面和“今天”都按城市当地时刻算，不换算会整体错一天。
        string query = $"localTime=true&lang={lang}";
        // 缓存键带上城市当地日期：跨过当地零点后逐时/逐天窗口会整体前移，旧响应必须作废（文档专门警告过「跨天」）。
        string day = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(city))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string site = $"{lat},{lon}|{lang}|{day}";
        var bodies = await Task.WhenAll(
            Get($"{host}/weather/v1/current/{lat}/{lon}?{query}", key, token),
            Get($"{host}/weather/v1/hourly/{lat}/{lon}?hours=24&{query}", key, token, TimeSpan.FromMinutes(30), "hourly|" + site),
            Get($"{host}/weather/v1/daily/{lat}/{lon}?days=10&{query}", key, token, TimeSpan.FromHours(2), "daily|" + site),
            Get($"{host}/airquality/v1/current/{lat}/{lon}?lang={lang}", key, token, TimeSpan.FromMinutes(30), "air|" + site));
        var part = new WeatherPart();
        if (ParseCurrentV1(bodies[0], city) is { } now) part.Current = now;
        if (ParseHoursV1(bodies[1], city) is { Count: > 0 } hours) part.Hours = hours;
        if (ParseDaysV1(bodies[2], city) is { Count: > 0 } days) part.Days = days;
        if (ParseAirV1(bodies[3]) is { } air) part.Air = air;
        return part.Current is null && part.Hours is null && part.Days is null && part.Air is null ? null : part;
    }

    // v7：迁移完成前给旧 key 留的退路，2027-06 停服后可整段删除。
    private static async Task<WeatherPart?> FetchV7(string host, string key, string lat, string lon, string lang, CancellationToken token)
    {
        string location = $"{lon},{lat}";
        var bodies = await Task.WhenAll(
            Get($"{host}/v7/weather/now?location={location}&lang={lang}", key, token),
            Get($"{host}/v7/weather/24h?location={location}&lang={lang}", key, token),
            Get($"{host}/v7/weather/15d?location={location}&lang={lang}", key, token),
            Get($"{host}/v7/air/now?location={location}&lang={lang}", key, token));
        var part = new WeatherPart();
        if (ParseNowV7(bodies[0]) is { } now) part.Current = now;
        if (ParseHoursV7(bodies[1]) is { Count: > 0 } hours) part.Hours = hours;
        if (ParseDaysV7(bodies[2]) is { Count: > 0 } days) part.Days = days;
        if (ParseAirV7(bodies[3]) is { } air) part.Air = air;
        return part.Current is null && part.Hours is null && part.Days is null && part.Air is null ? null : part;
    }

    // 请求体缓存：和风文档的推荐缓存是「实况 10–30 分钟、逐小时 30–60 分钟、逐天 1–6 小时、实时空气 30–60 分钟」。
    // 界面每 15 分钟刷新一次，逐时/逐天/空气不缓存等于把请求量放大 2–16 倍。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Body)> BodyCache = new();

    // Key 走 X-QW-Api-Key 请求头：比放在查询字符串里安全，也避免同时使用两种认证方式。
    // life + cacheKey 同时给出时才缓存；cacheKey 里带上城市当地日期，跨过当地零点旧响应自动作废。
    private static async Task<string?> Get(string url, string key, CancellationToken token, TimeSpan? life = null, string? cacheKey = null)
    {
        if (life is { } ttl && cacheKey is not null && BodyCache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.Now - hit.At < ttl) return hit.Body;
        string? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-QW-Api-Key", key);
            using var response = await WeatherHttp.Client.SendAsync(request, token);
            // 老账号的 key 可能只认查询参数，按文档允许的第二种形式再试一次，避免整个来源失效。
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                body = await WeatherHttp.Client.GetStringAsync($"{url}{(url.Contains('?') ? "&" : "?")}key={Uri.EscapeDataString(key)}", token);
            else if (!response.IsSuccessStatusCode) return null;
            else body = await response.Content.ReadAsStringAsync(token);
        }
        catch { return null; }
        if (body is not null && life is not null && cacheKey is not null) BodyCache[cacheKey] = (DateTimeOffset.Now, body);
        return body;
    }

    // ---- 时光机：/v7/historical/weather 只认 LocationID、单日一次请求、最多回溯 10 天（不含今天）----
    // 缓存 12 小时：历史是再分析数据，不会分钟级变化，避免 15 分钟一次的刷新把请求量放大近 100 倍。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, DayPoint Day)> HistoryCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> HistoryIds = new();

    private static async Task<List<DayPoint>?> FetchHistory(string host, string key, string lat, string lon, City city, CancellationToken token)
    {
        string? id = await LocationId(host, key, lat, lon, token);
        if (id is null) return null;
        // 按城市当地日期回溯：跨时区时用本机日期会整体错一天。
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(city))).Date;
        using var gate = new SemaphoreSlim(4);
        var days = await Task.WhenAll(Enumerable.Range(1, 10).Select(async offset =>
        {
            string stamp = today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string cacheKey = id + ":" + stamp;
            // 历史是定稿数据：昨天那条还可能被再分析修订，留 24 小时；更早的日期基本不再变，缓存 7 天。
            // 于是稳态下每天只补 1 个新日期，而不是每次刷新重拉 10 天。
            var life = offset == 1 ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7);
            if (HistoryCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.Now - cached.At < life) return cached.Day;
            await gate.WaitAsync(token);
            try
            {
                string? body = await Get($"{host}/v7/historical/weather?location={id}&date={stamp.Replace("-", "")}", key, token);
                if (ParseHistory(body, stamp) is not { } day) return null;
                HistoryCache[cacheKey] = (DateTimeOffset.Now, day);
                return day;
            }
            finally { gate.Release(); }
        }));
        return days.Where(day => day is not null).Select(day => day!).OrderBy(day => day.Date, StringComparer.Ordinal).ToList();
    }

    // 时光机只接受 LocationID，用与本城相同的坐标反查一次并记住；失败就整个历史域跳过，不影响其它数据。
    private static async Task<string?> LocationId(string host, string key, string lat, string lon, CancellationToken token)
    {
        string cacheKey = lat + "," + lon;
        if (HistoryIds.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            string? body = await Get($"{host}/geo/v2/city/lookup?location={lon},{lat}&number=1", key, token);
            if (body is null) return null;
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "location") is { ValueKind: JsonValueKind.Array } locations)
                foreach (var item in locations.EnumerateArray())
                {
                    if (Json.Text(item, "id") is not { Length: > 0 } id) continue;
                    HistoryIds[cacheKey] = id;
                    return id;
                }
        }
        catch { }
        return null;
    }

    // 时光机返回 weatherDaily（高低温、降水、气压、日月相）+ weatherHourly（没有日级天气现象）。
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

    // 0..1 的比例转百分比；若来源已经给百分数（>1.5）则不再放大。
    private static double? Percent(JsonElement root, string name) => Json.Number(root, name) is { } value
        ? Math.Round(value <= 1.5 ? value * 100 : value, 1) : null;

    private static int? PercentInt(JsonElement root, string name)
        => Percent(root, name) is { } value ? (int)Math.Round(value) : null;

    // v1 时间默认是 UTC；带 Z 的按城市时区折算，已带偏移（localTime=true）的直接用。
    private static DateTimeOffset? Stamp(string? raw, int offsetHours)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)) return null;
        bool utc = raw.EndsWith('Z') || raw.EndsWith("+00:00", StringComparison.Ordinal);
        return utc ? stamp.ToOffset(TimeSpan.FromHours(offsetHours)) : stamp;
    }

    // v1 只给 compass 代码（sw/nne…），转成界面惯用的风向文字；精度保留在 WindDirDeg 里。
    private static string? CompassText(string? compass) => compass?.ToLowerInvariant() switch
    {
        "n" => Loc.T("北风", "N"), "nne" => Loc.T("东北偏北风", "NNE"), "ne" => Loc.T("东北风", "NE"),
        "ene" => Loc.T("东北偏东风", "ENE"), "e" => Loc.T("东风", "E"), "ese" => Loc.T("东南偏东风", "ESE"),
        "se" => Loc.T("东南风", "SE"), "sse" => Loc.T("东南偏南风", "SSE"), "s" => Loc.T("南风", "S"),
        "ssw" => Loc.T("西南偏南风", "SSW"), "sw" => Loc.T("西南风", "SW"), "wsw" => Loc.T("西南偏西风", "WSW"),
        "w" => Loc.T("西风", "W"), "wnw" => Loc.T("西北偏西风", "WNW"), "nw" => Loc.T("西北风", "NW"),
        "nnw" => Loc.T("西北偏北风", "NNW"), _ => null
    };


    private static CurrentPart? ParseNowV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "now") is not { } now || Json.Text(doc.RootElement, "code") is not "200") return null;
            string? text = Json.Text(now, "text");
            return new CurrentPart
            {
                Temperature = Json.Number(now, "temp"), FeelsLike = Json.Number(now, "feelsLike"), Humidity = Json.Integer(now, "humidity"),
                WindKmh = Json.Number(now, "windSpeed"), WindDirDeg = Json.Number(now, "wind360"), WindDir = Json.Text(now, "windDir"),
                PressureHpa = Json.Number(now, "pressure"), VisibilityKm = Json.Number(now, "vis"), CloudPercent = Json.Number(now, "cloud"),
                DewPointC = Json.Number(now, "dew"), PrecipMm = Json.Number(now, "precip"), Code = WeatherCodes.FromHeFeng(Json.Text(now, "icon")),
                Condition = text, IsDay = DateTime.Now.Hour is >= 6 and < 18
            };
        }
        catch { return null; }
    }

    private static List<HourPoint>? ParseHoursV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "hourly") is not { } hourly || hourly.ValueKind != JsonValueKind.Array) return null;
            var hours = new List<HourPoint>();
            foreach (var item in hourly.EnumerateArray())
            {
                string? time = Json.Text(item, "fxTime"); if (time is null) continue;
                hours.Add(new HourPoint
                {
                    Time = time.Length >= 16 ? time[..16] : time, Temperature = Json.Number(item, "temp"), Humidity = Json.Integer(item, "humidity"),
                    Code = WeatherCodes.FromHeFeng(Json.Text(item, "icon")), RainProb = Json.Integer(item, "pop"), PrecipMm = Json.Number(item, "precip"),
                    WindKmh = Json.Number(item, "windSpeed"), WindDirDeg = Json.Number(item, "wind360"), PressureHpa = Json.Number(item, "pressure"),
                    CloudPercent = Json.Number(item, "cloud"), DewPointC = Json.Number(item, "dew"), FeelsLike = Json.Number(item, "feelsLike")
                });
            }
            return hours;
        }
        catch { return null; }
    }

    private static List<DayPoint>? ParseDaysV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "daily") is not { } daily || daily.ValueKind != JsonValueKind.Array) return null;
            var days = new List<DayPoint>();
            foreach (var item in daily.EnumerateArray())
            {
                string? date = Json.Text(item, "fxDate"); if (date is null) continue;
                days.Add(new DayPoint
                {
                    Date = date[..10], Code = WeatherCodes.FromHeFeng(Json.Text(item, "iconDay")), Condition = Json.Text(item, "textDay"),
                    HighC = Json.Number(item, "tempMax"), LowC = Json.Number(item, "tempMin"),
                    // v7 的日出日落只有 "06:58"，而界面按 ISO 时间取第 11–16 位，这里补上日期成为完整时刻。
                    Sunrise = Clock(date[..10], Json.Text(item, "sunrise")), Sunset = Clock(date[..10], Json.Text(item, "sunset")),
                    PrecipMm = Json.Number(item, "precip"), RainProb = null, UvMax = Json.Number(item, "uvIndex"),
                    WindMaxKmh = Json.Number(item, "windSpeedDay"), WindDirDeg = Json.Number(item, "wind360Day"),
                    Humidity = Json.Integer(item, "humidity"), PressureHpa = Json.Number(item, "pressure"),
                    MoonPhase = Json.Text(item, "moonPhase")
                });
            }
            return days;
        }
        catch { return null; }
    }

    // 把 "06:58" 与日期拼成 "2026-11-19T06:58"；已经带日期或为空时原样返回。
    private static string? Clock(string date, string? time)
        => string.IsNullOrWhiteSpace(time) || time.Length < 5 ? null : time.Length >= 16 ? time[..16] : $"{date}T{time[..5]}";

    private static AirPart? ParseAirV7(string? body)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "now") is not { } now) return null;
            int? aqi = Json.Integer(now, "aqi");
            if (aqi is null) return null;
            return new AirPart { Aqi = aqi, Pm25 = Json.Integer(now, "pm2p5"), Pm10 = Json.Integer(now, "pm10"), Level = aqi <= 50 ? 1 : aqi <= 100 ? 2 : aqi <= 150 ? 3 : aqi <= 200 ? 4 : 5, Text = Json.Text(now, "category") };
        }
        catch { return null; }
    }

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
            int offset = Geo.UtcOffsetHours(city);
            return new CurrentPart
            {
                Temperature = Value(root, "temperature"),
                FeelsLike = Value(root, "feelsLike"),
                Humidity = PercentInt(root, "humidity"),
                WindKmh = wind is { } windBox ? SpeedKmh(windBox, "speed") : null,
                WindGustKmh = SpeedKmh(root, "windGust"),
                WindDirDeg = direction is { } dirBox ? Json.Number(dirBox, "degree") : null,
                WindDir = direction is { } compassBox ? CompassText(Json.Text(compassBox, "compass")) : null,
                PressureHpa = Value(root, "pressure"),
                VisibilityKm = DistanceKm(root, "visibility"),
                DewPointC = Value(root, "dewPoint"),
                Uv = Json.Number(root, "uvIndex"),
                CloudPercent = Percent(root, "cloudCover"),
                PrecipMm = precipitation is { } p && Json.Get(p, "amount") is { } amount ? Json.Number(amount, "value") : null,
                Code = WeatherCodes.FromHeFeng(Json.Text(condition, "code")),
                Condition = Json.Text(condition, "text"),
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
            int offset = Geo.UtcOffsetHours(city);
            var hours = new List<HourPoint>();
            foreach (var item in hoursBox.EnumerateArray())
            {
                if (Stamp(Json.Text(item, "forecastTime"), offset) is not { } stamp) continue;
                var wind = Json.Get(item, "wind");
                var direction = wind is { } w && Json.Get(w, "direction") is { } dir ? dir : (JsonElement?)null;
                var precipitation = Json.Get(item, "precipitation");
                hours.Add(new HourPoint
                {
                    Time = stamp.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    Temperature = Value(item, "temperature"),
                    FeelsLike = Value(item, "feelsLike"),
                    Humidity = PercentInt(item, "humidity"),
                    Code = WeatherCodes.FromHeFeng(Json.Get(item, "condition") is { } c ? Json.Text(c, "code") : null),
                    RainProb = precipitation is { } pp ? PercentInt(pp, "probability") : null,
                    PrecipMm = precipitation is { } pa && Json.Get(pa, "amount") is { } amount ? Json.Number(amount, "value") : null,
                    WindKmh = wind is { } windBox ? SpeedKmh(windBox, "speed") : null,
                    WindGustKmh = SpeedKmh(item, "windGust"),
                    WindDirDeg = direction is { } dirBox ? Json.Number(dirBox, "degree") : null,
                    PressureHpa = Value(item, "pressure"),
                    Uv = Json.Number(item, "uvIndex"),
                    DewPointC = Value(item, "dewPoint"),
                    CloudPercent = Percent(item, "cloudCover"),
                    VisibilityKm = DistanceKm(item, "visibility")
                });
            }
            return hours;
        }
        catch { return null; }
    }

    // v1 每日最多 10 天（v7 的 15d 提供 15 天），第 11 天以后由调度链上的下一家来源补齐。
    private static List<DayPoint>? ParseDaysV1(string? body, City city)
    {
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "days") is not { } daysBox || daysBox.ValueKind != JsonValueKind.Array) return null;
            int offset = Geo.UtcOffsetHours(city);
            var days = new List<DayPoint>();
            foreach (var item in daysBox.EnumerateArray())
            {
                if (Stamp(Json.Text(item, "forecastStartTime"), offset) is not { } stamp) continue;
                var day = new DayPoint
                {
                    Date = stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    HighC = Value(item, "temperatureMax"),
                    LowC = Value(item, "temperatureMin"),
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
                        day.PrecipMm = Json.Get(precipitation, "amount") is { } amount ? Json.Number(amount, "value") : null;
                        day.RainProb = PercentInt(precipitation, "probability");
                    }
                }
                if (Json.Get(item, "astro") is { } astro)
                {
                    // 界面按 ISO 时刻取第 11–16 位显示，这里保持与 Open-Meteo 一致的完整格式。
                    day.Sunrise = Stamp(Json.Text(astro, "sunrise"), offset)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.Sunset = Stamp(Json.Text(astro, "sunset"), offset)?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    day.MoonPhase = Json.Text(astro, "moonPhase");
                }
                days.Add(day);
            }
            return days;
        }
        catch { return null; }
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
            var chosen = new[] { "cn-mee", "cn-mee-1h", "qaqi" }
                .Select(code => indexes.FirstOrDefault(item => Json.Text(item, "code") == code))
                .FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
            if (chosen.ValueKind != JsonValueKind.Object && indexes.Length > 0) chosen = indexes[0];
            if (chosen.ValueKind != JsonValueKind.Object) return null;
            int? aqi = Json.Number(chosen, "aqi") is { } value ? (int)Math.Round(value) : null;
            string? level = Json.Text(chosen, "level");
            var primary = Json.Get(chosen, "primaryPollutant");
            var health = Json.Get(chosen, "health");
            var advice = health is { } h && Json.Get(h, "advice") is { } a ? a : (JsonElement?)null;
            var air = new AirPart
            {
                Aqi = aqi,
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
                    air.Units[code] = Json.Text(concentration, "unit") ?? "";
                }
            if (air.Pollutants.TryGetValue("pm2p5", out var pm25)) air.Pm25 = (int)Math.Round(pm25);
            if (air.Pollutants.TryGetValue("pm10", out var pm10)) air.Pm10 = (int)Math.Round(pm10);
            return air.HasData ? air : null;
        }
        catch { return null; }
    }
}
