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

// 空气质量：AQI 与主要污染物。
internal sealed class AirPart
{
    public int? Aqi { get; set; }
    public int? Pm25 { get; set; }
    public int? Pm10 { get; set; }
    public int? Level { get; set; }
    public string? Text { get; set; }
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

// 来源调度的持久化配置：每个域可选 auto 或具体来源 id；和风/高德为可选 key 来源。
internal sealed class WeatherSettings
{
    public bool PreferDomestic { get; set; } = true;
    public Dictionary<string, string> Sources { get; set; } = [];
    public string HeFengKey { get; set; } = "";
    public string HeFengHost { get; set; } = "devapi.qweather.com";
    public string AmapKey { get; set; } = "";
    public string Choice(WeatherDomain domain) => Sources.TryGetValue(domain.ToString(), out var value) && value.Length > 0 ? value : "auto";
}

// 来源集合与查询入口。
internal static class WeatherSources
{
    public static readonly IWeatherSource[] All =
    [
        new OpenMeteoSource(), new TencentSource(), new WeatherComCnSource(), new HeFengSource(), new AmapSource()
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
        if (settings.HeFengKey.Trim().Length > 0) Add(WeatherSources.Find("hefeng"));
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
        // 国外城市只使用 Open-Meteo。
        if (settings.AmapKey.Trim().Length > 0 && china && domain is WeatherDomain.Current or WeatherDomain.Daily) Add(WeatherSources.Find("amap"));
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
                        if (part.Air is { } a && (a.Aqi is not null || a.Pm25 is not null || a.Pm10 is not null))
                        {
                            air ??= new AirPart();
                            bool any = false;
                            if (air.Aqi is null && a.Aqi is not null) { air.Aqi = a.Aqi; fieldSources["aqi"] = source.Info.Id; any = true; }
                            if (air.Pm25 is null && a.Pm25 is not null) { air.Pm25 = a.Pm25; fieldSources["pm25"] = source.Info.Id; any = true; }
                            if (air.Pm10 is null && a.Pm10 is not null) { air.Pm10 = a.Pm10; fieldSources["pm10"] = source.Info.Id; any = true; }
                            if (air.Level is null && a.Level is not null) { air.Level = a.Level; any = true; }
                            if (air.Text is null && a.Text is not null) { air.Text = a.Text; any = true; }
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

// 和风天气：需要用户自己的 Key，覆盖当前、24 小时、15 天、日月与空气，质量最高，因此在自动调度中优先。
internal sealed class HeFengSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("hefeng", Loc.T("和风天气", "QWeather"), Loc.T("全维度 · 需 Key", "All domains · key required"),
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Sun, WeatherDomain.Air], WeatherRegion.Global, true);
    public bool CanServe(City city, WeatherSettings settings) => settings.HeFengKey.Trim().Length > 0;

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        string host = settings.HeFengHost.Trim().TrimEnd('/');
        if (host.Length == 0) host = "devapi.qweather.com";
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
        string location = FormattableString.Invariant($"{city.Longitude.ToString(CultureInfo.InvariantCulture)},{city.Latitude.ToString(CultureInfo.InvariantCulture)}");
        string key = Json.Esc(settings.HeFengKey.Trim());
        var tasks = new[]
        {
            TryGet($"{host}/v7/weather/now?location={location}&key={key}", token),
            TryGet($"{host}/v7/weather/24h?location={location}&key={key}", token),
            TryGet($"{host}/v7/weather/15d?location={location}&key={key}", token),
            TryGet($"{host}/v7/air/now?location={location}&key={key}", token)
        };
        var bodies = await Task.WhenAll(tasks);
        var part = new WeatherPart();
        if (ParseNow(bodies[0]) is { } now) part.Current = now;
        if (ParseHours(bodies[1]) is { Count: > 0 } hours) part.Hours = hours;
        if (ParseDays(bodies[2]) is { Count: > 0 } days) part.Days = days;
        if (ParseAir(bodies[3]) is { } air) part.Air = air;
        return part.Current is null && part.Hours is null && part.Days is null && part.Air is null ? null : part;
    }

    private static async Task<string?> TryGet(string url, CancellationToken token)
    {
        try { return await WeatherHttp.Client.GetStringAsync(url, token); } catch { return null; }
    }

    private static CurrentPart? ParseNow(string? body)
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

    private static List<HourPoint>? ParseHours(string? body)
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

    private static List<DayPoint>? ParseDays(string? body)
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
                    Sunrise = Json.Text(item, "sunrise"), Sunset = Json.Text(item, "sunset"),
                    PrecipMm = Json.Number(item, "precip"), RainProb = null, UvMax = Json.Number(item, "uvIndex"),
                    WindMaxKmh = Json.Number(item, "windSpeedDay"), WindDirDeg = Json.Number(item, "wind360Day"),
                    Humidity = Json.Integer(item, "humidity"), PressureHpa = Json.Number(item, "pressure")
                });
            }
            return days;
        }
        catch { return null; }
    }

    private static AirPart? ParseAir(string? body)
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
}

// 高德天气：需要用户自己的 Key，仅提供实况与 4 天预报，作为国内链的补充。
internal sealed class AmapSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("amap", Loc.T("高德天气", "Amap Weather"), Loc.T("国内实况与 4 天预报 · 需 Key", "China current & 4-day · key required"),
        [WeatherDomain.Current, WeatherDomain.Daily], WeatherRegion.China, true);
    public bool CanServe(City city, WeatherSettings settings) => settings.AmapKey.Trim().Length > 0 && Geo.IsChina(city);

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, CancellationToken token)
    {
        string key = settings.AmapKey.Trim();
        string location = FormattableString.Invariant($"{city.Longitude.ToString(CultureInfo.InvariantCulture)},{city.Latitude.ToString(CultureInfo.InvariantCulture)}");
        using var geoDoc = JsonDocument.Parse(await WeatherHttp.Client.GetStringAsync($"https://restapi.amap.com/v3/geocode/regeo?location={location}&key={Json.Esc(key)}", token));
        if (Json.Get(geoDoc.RootElement, "regeocode") is not { } regeocode || Json.Get(regeocode, "addressComponent") is not { } component) return null;
        string? adcode = Json.Text(component, "adcode");
        if (string.IsNullOrWhiteSpace(adcode)) return null;
        using var doc = JsonDocument.Parse(await WeatherHttp.Client.GetStringAsync($"https://restapi.amap.com/v3/weather/weatherInfo?city={Json.Esc(adcode)}&extensions=all&key={Json.Esc(key)}", token));
        var root = doc.RootElement;
        var part = new WeatherPart();
        if (Json.Get(root, "lives") is { } lives && lives.ValueKind == JsonValueKind.Array && lives.GetArrayLength() > 0)
        {
            var live = lives[0];
            string? text = Json.Text(live, "weather");
            string? windDir = Json.Text(live, "winddirection");
            part.Current = new CurrentPart
            {
                Temperature = Json.Number(live, "temperature"), Humidity = Json.Integer(live, "humidity"), Code = WeatherCodes.FromText(text),
                Condition = text, WindDir = windDir, WindDirDeg = WeatherCodes.DegreesFromText(windDir),
                WindKmh = WeatherCodes.FromWindPower(Json.Text(live, "windpower")), IsDay = DateTime.Now.Hour is >= 6 and < 18
            };
        }
        if (Json.Get(root, "forecasts") is { } forecasts && forecasts.ValueKind == JsonValueKind.Array && forecasts.GetArrayLength() > 0
            && Json.Get(forecasts[0], "casts") is { } casts && casts.ValueKind == JsonValueKind.Array)
        {
            part.Days = [];
            foreach (var item in casts.EnumerateArray())
            {
                string? date = Json.Text(item, "date"); if (date is null) continue;
                string? text = Json.Text(item, "dayweather");
                string? windDir = Json.Text(item, "daywind");
                part.Days.Add(new DayPoint
                {
                    Date = date[..10], Code = WeatherCodes.FromText(text), Condition = text,
                    HighC = Json.Number(item, "daytemp"), LowC = Json.Number(item, "nighttemp"),
                    WindDir = windDir, WindDirDeg = WeatherCodes.DegreesFromText(windDir),
                    WindMaxKmh = WeatherCodes.FromWindPower(Json.Text(item, "daypower"))
                });
            }
        }
        return part.Current is null && part.Days is null ? null : part;
    }
}