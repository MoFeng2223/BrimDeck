using System.Globalization;
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
    // 控制台分配的项目 Host：公共 devapi 域名已停止服务，不再提供默认值，留空即不启用和风。
    public string HeFengHost { get; set; } = "";
    public string Choice(WeatherDomain domain) => Sources.TryGetValue(domain.ToString(), out var value) && value.Length > 0 ? value : "auto";
    // 历史域由用户在来源调度里显式选「和风天气」才走时光机：10 天 = 10 次请求，不做隐式默认。
    public bool PicksHeFengForHistory() => Choice(WeatherDomain.History) == "hefeng";
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
    // need 是本次真正缺的域：来源只请求覆盖这些域的接口，不必为一个日出日落把逐时和空气一起拉回来。
    Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token);
}

// 多源调度：每个域一条来源链，逐轮按需下探——第 1 轮只请求各域的首选来源，
// 合并后检查覆盖情况，只对仍缺口的数据域才去请求链上的下一家。
// 这样既保留「先到先得、后续补缺」的合并语义，又不会为了一个字段把整条链全打一遍。
internal static class WeatherAggregator
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);

    // 两段式：先取实况与预报让首屏立刻出数，历史（10 次请求）再单独补一轮。
    // progress 在第一段结束时回调一次，界面可以先画不含历史的版本，历史到了再整体替换。
    public static async Task<WeatherSnapshot> FetchAsync(City city, WeatherSettings settings, CancellationToken token, Action<WeatherSnapshot>? progress = null)
    {
        var chains = Enum.GetValues<WeatherDomain>().ToDictionary(domain => domain, domain => Chain(domain, city, settings));
        var parts = new Dictionary<string, WeatherPart>(StringComparer.Ordinal);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var served = new Dictionary<string, HashSet<WeatherDomain>>(StringComparer.Ordinal);
        var live = Enum.GetValues<WeatherDomain>().Where(domain => domain != WeatherDomain.History).ToArray();
        await RoundsAsync(city, settings, chains, parts, tried, served, live, token);
        if (parts.Count == 0) throw new InvalidOperationException("没有可用的天气来源");
        // 第一段结果先交给界面；历史补上后返回的快照会覆盖它。
        if (progress is not null && chains[WeatherDomain.History].Count > 0) progress(Merge(city, chains, parts));
        await RoundsAsync(city, settings, chains, parts, tried, served, [WeatherDomain.History], token);
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
        if (settings.HeFengKey.Trim().Length > 0 && (domain != WeatherDomain.History || settings.PicksHeFengForHistory())) Add(WeatherSources.Find("hefeng"));
        bool china = Geo.IsChina(city);
        if (china && settings.PreferDomestic)
        {
            switch (domain)
            {
                case WeatherDomain.Current: Add(WeatherSources.Find("tencent")); Add(WeatherSources.Find("weathercomcn")); break;
                case WeatherDomain.Daily: Add(WeatherSources.Find("weathercomcn")); Add(WeatherSources.Find("tencent")); break;
                case WeatherDomain.Air: Add(WeatherSources.Find("tencent")); Add(WeatherSources.Find("weathercomcn")); break;
                // 中国天气网的月历带「过去 10 天的实测降水」，是免费历史的首选，排在和风时光机之后。
                case WeatherDomain.History: Add(WeatherSources.Find("weathercomcn")); break;
                case WeatherDomain.Sun: break;                 // 国内源不提供日出日落，交给 Open-Meteo
                case WeatherDomain.Hourly: break;              // 国内源没有稳定的小时接口
            }
        }
        // Open-Meteo 全球可用，任何域都排在最后兜底。
        Add(WeatherSources.Find("openmeteo"));
        return chain;
    }

    // 逐轮下探：每轮把「同一来源的多个域」合并成一次请求，取完再看缺口。
    private static async Task RoundsAsync(City city, WeatherSettings settings, Dictionary<WeatherDomain, List<IWeatherSource>> chains,
        Dictionary<string, WeatherPart> parts, HashSet<string> tried, Dictionary<string, HashSet<WeatherDomain>> served,
        IReadOnlyCollection<WeatherDomain> domains, CancellationToken token)
    {
        var pending = domains.ToHashSet();
        int max = chains.Values.Select(chain => chain.Count).DefaultIfEmpty(0).Max();
        for (int round = 0; round < max && pending.Count > 0; round++)
        {
            var wanted = new Dictionary<IWeatherSource, List<WeatherDomain>>();
            foreach (var domain in pending)
            {
                var chain = chains[domain];
                int index = round;
                // 跳过这条链上已经为同一域试过的来源，避免失败后在下一轮重复请求。
                while (index < chain.Count && tried.Contains(chain[index].Info.Id + "|" + domain)) index++;
                if (index >= chain.Count) continue;
                var source = chain[index];
                if (!wanted.TryGetValue(source, out var list)) wanted[source] = list = [];
                list.Add(domain);
                tried.Add(source.Info.Id + "|" + domain);
            }
            if (wanted.Count == 0) break;
            // 该来源本轮要补的域；已经取过且确实覆盖这些域的来源不再请求，直接进入合并。
            var calls = wanted.Where(pair => !Covers(served, pair.Key.Info.Id, pair.Value)).ToArray();
            if (calls.Length > 0)
            {
                var results = await Task.WhenAll(calls.Select(async pair =>
                {
                    var part = await TryAsync(pair.Key, city, settings, pair.Value, token);
                    return (Id: pair.Key.Info.Id, Domains: (IReadOnlyCollection<WeatherDomain>)pair.Value, Part: part);
                }));
                foreach (var (id, covered, part) in results)
                {
                    if (!served.TryGetValue(id, out var set)) served[id] = set = [];
                    foreach (var domain in covered) set.Add(domain);
                    if (part is null) continue;
                    // 记下响应里实际带了哪些域：一个接口常顺带给多个域，下一轮就不必为这些域再请求一次。
                    if (part.Current is not null) set.Add(WeatherDomain.Current);
                    if (part.Hours is { Count: > 0 }) set.Add(WeatherDomain.Hourly);
                    if (part.Air is not null) set.Add(WeatherDomain.Air);
                    if (part.Days is { Count: > 0 } provided)
                    {
                        set.Add(WeatherDomain.Daily);
                        if (provided.Any(day => day.Sunrise is not null || day.Sunset is not null)) set.Add(WeatherDomain.Sun);
                    }
                    // 同一个来源可能在两段里各被请求一次（例如和风先给预报、第二轮单独补历史），
                    // 后一段只拿到部分数据，直接覆盖会把前一段的逐时/空气弄丢，所以按字段补齐。
                    if (parts.TryGetValue(id, out var existing)) Absorb(existing, part);
                    else parts[id] = part;
                }
            }
            var snapshot = Merge(city, chains, parts);
            pending.RemoveWhere(domain => Satisfied(domain, snapshot));
        }
    }

    private static bool Covers(Dictionary<string, HashSet<WeatherDomain>> served, string id, IReadOnlyCollection<WeatherDomain> domains)
        => served.TryGetValue(id, out var set) && domains.All(set.Contains);

    // 同一来源分多次返回时把结果并进同一份：能空位补齐的就补，日级数据按日期合并而不是互相覆盖。
    private static void Absorb(WeatherPart target, WeatherPart extra)
    {
        target.TimeZone ??= extra.TimeZone;
        target.Current ??= extra.Current;
        target.Air ??= extra.Air;
        target.Hours ??= extra.Hours;
        if (extra.Days is not { Count: > 0 } incoming) return;
        if (target.Days is not { Count: > 0 }) { target.Days = incoming; return; }
        var known = target.Days.Select(day => day.Date).ToHashSet(StringComparer.Ordinal);
        var merged = new List<DayPoint>(target.Days);
        merged.AddRange(incoming.Where(day => known.Add(day.Date)));
        target.Days = merged;
    }

    // 覆盖判定：只认界面真正要画的字段，够用就停手，不追求「所有字段都齐」。
    private static bool Satisfied(WeatherDomain domain, WeatherSnapshot snapshot)
    {
        int today = Array.IndexOf(snapshot.DayDates, snapshot.Today);
        if (today < 0) today = Math.Min(10, Math.Max(0, snapshot.DayDates.Length - 1));
        bool Has(double[] values, int index) => index >= 0 && index < values.Length && !double.IsNaN(values[index]);
        bool Day(int offset)
        {
            int index = today + offset;
            return index >= 0 && index < snapshot.DayDates.Length && Has(snapshot.DayHighC, index) && Has(snapshot.DayLowC, index);
        }
        switch (domain)
        {
            case WeatherDomain.Current:
                // 温度、湿度、风、气压到手就能画完今日天气；能见度、露点、紫外线不单独触发一轮请求。
                return !double.IsNaN(snapshot.Temperature) && snapshot.HumidityPercent >= 0
                    && !double.IsNaN(snapshot.WindKmh) && !double.IsNaN(snapshot.PressureHpa);
            case WeatherDomain.Hourly:
                // 逐时窗口是 24 小时，允许头部少量缺测。
                int hours = snapshot.HourTimes.Length, filled = 0;
                for (int i = 0; i < hours; i++) if (!double.IsNaN(snapshot.HourTempsC[i])) filled++;
                return hours >= 20 && filled >= 20;
            case WeatherDomain.Daily:
                // 今天起 13 天里至少 11 天有高低温；和风 v1 只有 10 天，最后 3 天由下一家补。
                int days = 0; for (int offset = 0; offset <= 12; offset++) if (Day(offset)) days++;
                return days >= 11;
            case WeatherDomain.Sun:
                int sun = 0;
                for (int offset = 0; offset <= 13; offset++)
                {
                    int index = today + offset;
                    if (index >= 0 && index < snapshot.SunriseTimes.Length && snapshot.SunriseTimes[index].Length >= 16) sun++;
                }
                return sun >= 10;
            case WeatherDomain.Air:
                return snapshot.Aqi >= 0 || snapshot.Pm25 >= 0;
            case WeatherDomain.History:
                int past = 0; for (int offset = -10; offset <= -1; offset++) if (Day(offset)) past++;
                return past >= 9;
            default:
                return true;
        }
    }

    private static async Task<WeatherPart?> TryAsync(IWeatherSource source, City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(SourceTimeout);
        try { return await source.FetchAsync(city, settings, need, cts.Token); }
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

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        bool Want(WeatherDomain domain) => need.Contains(domain);
        // 一份 forecast 响应同时覆盖实况、每日、日出日落与过去 10 天，这四个域共用一次请求。
        bool wantForecast = Want(WeatherDomain.Current) || Want(WeatherDomain.Daily) || Want(WeatherDomain.Sun) || Want(WeatherDomain.History);
        string location = FormattableString.Invariant($"latitude={city.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={city.Longitude.ToString(CultureInfo.InvariantCulture)}&timezone=auto");
        string site = FormattableString.Invariant($"{city.Latitude:0.00},{city.Longitude:0.00}");
        var forecast = wantForecast ? Body("forecast|" + site, TimeSpan.FromMinutes(15), $"https://api.open-meteo.com/v1/forecast?{location}&current=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,weather_code,pressure_msl,cloud_cover,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,uv_index_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,relative_humidity_2m_mean,pressure_msl_mean&past_days=10&forecast_days=14", token) : Task.FromResult("");
        var hourly = Want(WeatherDomain.Hourly) ? Body("hourly|" + site, TimeSpan.FromMinutes(30), $"https://api.open-meteo.com/v1/forecast?{location}&hourly=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,precipitation_probability,weather_code,pressure_msl,cloud_cover,visibility,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index&forecast_hours=24", token) : Task.FromResult("");
        var air = Want(WeatherDomain.Air) ? Body("air|" + site, TimeSpan.FromMinutes(30), $"https://air-quality-api.open-meteo.com/v1/air-quality?{location}&current=pm2_5,pm10,us_aqi,european_aqi", token) : Task.FromResult("");
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

    // 响应体落盘：三份数据各自过期，未过期直接读盘，重启后同样能复用，断网时退回上一份。
    private static async Task<string> Body(string key, TimeSpan ttl, string url, CancellationToken token)
        => await WeatherCache.GetAsync<string>("openmeteo", key, ttl, async ct => (await WeatherHttp.GetAsync("openmeteo", url, ct)).Body, token) ?? "";
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

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        var (name, province) = Geo.Parts(city);
        // 一个接口同时给实况、8 天预报与今日空气，分不出更细的粒度，所以按最短的那个周期缓存（15 分钟）。
        string url = $"https://wis.qq.com/weather/common?source=pc&weather_type=observe%7Cforecast_24h&province={Json.Esc(province)}&city={Json.Esc(name)}";
        string body = await WeatherCache.GetAsync<string>("tencent", $"all|{name}|{province}", TimeSpan.FromMinutes(15),
            async ct => (await WeatherHttp.GetAsync("tencent", url, ct)).Body, token) ?? "";
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
    public WeatherSourceInfo Info { get; } = new("weathercomcn", Loc.T("中国天气网", "China Weather"), Loc.T("国内实况、15 天预报与历史降水 · 免 Key", "China current, 15-day & history · no key"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.History, WeatherDomain.Air], WeatherRegion.China, false);

    public bool CanServe(City city, WeatherSettings settings)
    {
        var (name, _) = Geo.Parts(city);
        return Geo.IsChina(city) && name.Length > 0 && Geo.HasChinese(name);
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        var (name, _) = Geo.Parts(city);
        string? id = await ResolveIdAsync(name, token);
        if (id is null) return null;
        var part = new WeatherPart();
        bool wantCurrent = need.Contains(WeatherDomain.Current) || need.Contains(WeatherDomain.Air);
        bool wantDays = need.Contains(WeatherDomain.Daily) || need.Contains(WeatherDomain.Sun) || need.Contains(WeatherDomain.History);
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

// 和风天气：需要用户自己的 Key，走 v1 接口覆盖实况、逐时、每日、日月与空气，质量最高，因此在自动调度中优先。
// 只用 v1：v7 城市天气 2027-06 停服，不再保留旧接口的退路。
internal sealed class HeFengSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("hefeng", Loc.T("和风天气", "QWeather"), Loc.T("全维度 · v1 + 时光机 · 需 Key", "All domains (v1 + time machine) · key required"),
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Sun, WeatherDomain.Air, WeatherDomain.History], WeatherRegion.Global, true);
    public bool CanServe(City city, WeatherSettings settings) => settings.HeFengKey.Trim().Length > 0 && settings.HeFengHost.Trim().Length > 0;

    // 一次请求要用到的公共参数：坐标只保留两位小数（v1 只认这个精度）。
    private sealed record Context(string Host, string Key, City City)
    {
        public string Lat => City.Latitude.ToString("0.00", CultureInfo.InvariantCulture);
        public string Lon => City.Longitude.ToString("0.00", CultureInfo.InvariantCulture);
        public string Lang => Loc.IsEnglish ? "en" : "zh";
        public string Site => $"{Lat},{Lon}|{Lang}";
        // 城市当地日期：跨过当地零点后逐时/逐天窗口整体前移，缓存键带上它，旧响应自动作废（文档专门警告过「跨天」）。
        public string Day => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(City))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        string host = settings.HeFengHost.Trim().TrimEnd('/');
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
        var context = new Context(host, settings.HeFengKey.Trim(), city);
        // 历史单独占一段：时光机只认 LocationID、一天一次请求，不能挂在首屏链路上。
        // 调度器把它放在独立的第二轮，这里只负责那一段。
        if (need.All(domain => domain == WeatherDomain.History))
            return await FetchHistory(context, token) is { Count: > 0 } past ? new WeatherPart { Days = past } : null;
        return await FetchV1(context, need, token);
    }

    // v1：坐标走路径，数值包在 { value, unit } 里，湿度/云量/降水概率是 0..1 的比例。
    private static async Task<WeatherPart?> FetchV1(Context context, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        bool Want(WeatherDomain domain) => need.Contains(domain);
        string host = context.Host;
        // localTime=true：v1 默认返回 UTC，而界面和“今天”都按城市当地时刻算，不换算会整体错一天。
        string query = $"localTime=true&lang={context.Lang}";
        // 四个接口各自过期：实况最短、逐天最长，互不牵连（官方推荐 10–30 分钟 / 30–60 分钟 / 1–6 小时）。
        var current = Want(WeatherDomain.Current) ? Body(context, "current|" + context.Site, TimeSpan.FromMinutes(10), $"{host}/weather/v1/current/{context.Lat}/{context.Lon}?{query}", token) : Task.FromResult<string?>(null);
        var hours = Want(WeatherDomain.Hourly) ? Body(context, "hourly|" + context.Site + "|" + context.Day, TimeSpan.FromMinutes(30), $"{host}/weather/v1/hourly/{context.Lat}/{context.Lon}?hours=24&{query}", token) : Task.FromResult<string?>(null);
        var days = Want(WeatherDomain.Daily) || Want(WeatherDomain.Sun) ? Body(context, "daily|" + context.Site + "|" + context.Day, TimeSpan.FromHours(2), $"{host}/weather/v1/daily/{context.Lat}/{context.Lon}?days=10&{query}", token) : Task.FromResult<string?>(null);
        var air = Want(WeatherDomain.Air) ? Body(context, "air|" + context.Site, TimeSpan.FromMinutes(30), $"{host}/airquality/v1/current/{context.Lat}/{context.Lon}?lang={context.Lang}", token) : Task.FromResult<string?>(null);
        await Task.WhenAll(current, hours, days, air);
        var part = new WeatherPart();
        if (ParseCurrentV1(current.Result, context.City) is { } now) part.Current = now;
        if (ParseHoursV1(hours.Result, context.City) is { Count: > 0 } list) part.Hours = list;
        if (ParseDaysV1(days.Result, context.City) is { Count: > 0 } forecast) part.Days = forecast;
        if (ParseAirV1(air.Result) is { } quality) part.Air = quality;
        return part.Current is null && part.Hours is null && part.Days is null && part.Air is null ? null : part;
    }

    private static Task<string?> Body(Context context, string key, TimeSpan ttl, string url, CancellationToken token)
        => WeatherCache.GetAsync<string>("hefeng", key, ttl, ct => SendAsync(context, url, ct), token);

    // Key 走 X-QW-Api-Key 请求头：比放在查询字符串里安全，也避免同时使用两种认证方式。
    private static async Task<string?> SendAsync(Context context, string url, CancellationToken token)
    {
        var result = await WeatherHttp.GetAsync("hefeng", url, token, request => request.Headers.TryAddWithoutValidation("X-QW-Api-Key", context.Key));
        return result.Body;
    }

    // ---- 时光机：/v7/historical/weather 只认 LocationID、单日一次请求、最多回溯 10 天（不含今天）----

    private static async Task<List<DayPoint>?> FetchHistory(Context context, CancellationToken token)
    {
        string? id = await LocationId(context, token);
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
                var body = await Body(context, $"history|{id}|{stamp}", ttl, $"{context.Host}/v7/historical/weather?location={id}&date={stamp.Replace("-", "")}", token);
                return ParseHistory(body, stamp);
            }
            finally { gate.Release(); }
        }));
        return [.. days.Where(day => day is not null).Select(day => day!).OrderBy(day => day.Date, StringComparer.Ordinal)];
    }

    // 时光机只接受 LocationID，用与本城相同的坐标反查一次并落盘 30 天；失败就整个历史域跳过，不影响其它数据。
    private static Task<string?> LocationId(Context context, CancellationToken token)
        => WeatherCache.GetAsync<string>("hefeng", $"place|{context.Lat},{context.Lon}", TimeSpan.FromDays(30), async ct =>
        {
            var body = await SendAsync(context, $"{context.Host}/geo/v2/city/lookup?location={context.Lon},{context.Lat}&number=1", ct);
            if (body is null) return null;
            using var doc = JsonDocument.Parse(body);
            if (Json.Get(doc.RootElement, "location") is { ValueKind: JsonValueKind.Array } locations)
                foreach (var item in locations.EnumerateArray())
                    if (Json.Text(item, "id") is { Length: > 0 } id) return id;
            return null;
        }, token);

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

    // v1 每日最多 10 天，第 11 天以后由调度链上的下一家来源补齐。
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
