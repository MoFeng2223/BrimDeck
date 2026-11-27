using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 单源聚合：每个域只请求用户选定的那一家；同一次刷新把多个域合并成尽量少的请求。
// 指定来源缺的字段就留空，不跨源补缺；唯一的例外是和风日预报在国内由中国天气网补充通道补齐缺口。
internal static class WeatherAggregator
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(15);
    // 时光机 10 次请求分批执行，耗时远超其它域：单独计时，不与预警/指数共用 15 秒预算。
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(25);

    // 两段式：首屏先取实况/预报/空气/分钟；时光机（10 次请求）与和风 v7 重域（预警/指数）放到第二段。
    // progress 在第一段结束后回调一次，界面先画不含历史的版本，历史到了再整体替换。
    public static async Task<WeatherSnapshot> FetchAsync(City city, WeatherSettings settings, string sourceId, CancellationToken token, Action<WeatherSnapshot>? progress = null)
    {
        // 每个域在本次刷新里的负责来源；null 表示这个域本次不取数（未选择、来源不支持或不能服务这座城）。
        var owners = Enum.GetValues<WeatherDomain>().ToDictionary(domain => domain, domain => Owner(domain, city, settings, sourceId));
        var parts = new Dictionary<string, WeatherPart>(StringComparer.Ordinal);

        await FetchRoundAsync(city, settings, owners, parts, deferred: false, token: token);
        if (progress is not null && owners.Any(owner => owner.Value is not null && IsDeferred(owner.Key)))
            progress(Merge(city, sourceId, owners, parts));
        var supplement = LoadDailySupplementAsync();
        await FetchRoundAsync(city, settings, owners, parts, deferred: true, token: token);
        return Merge(city, sourceId, owners, parts, await supplement);

        async Task<List<DayPoint>> LoadDailySupplementAsync()
        {
            if (!ChinaDailySupplement.Applies(sourceId, city)) return [];
            var today = LocalToday(city.TimeZoneId ?? "Asia/Shanghai", city);
            var days = parts.GetValueOrDefault(sourceId)?.Days;
            if (!ChinaDailySupplement.Missing(days, today)) return [];
            try { return await ChinaDailySupplement.FetchAsync(city, today, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return []; }
        }
    }

    // 时光机与 v7 重域推后到第二段：请求多、返回慢，不该拖住首屏。
    private static bool IsDeferred(WeatherDomain domain)
        => domain is WeatherDomain.History or WeatherDomain.Warning or WeatherDomain.Indices;

    // 某个域由哪一家来源负责：用户选定且这家支持该域、能服务这座城市。
    // 不按区域下探，也不跨源补缺——没配置或不可用时这个域就留空。
    private static IWeatherSource? Owner(WeatherDomain domain, City city, WeatherSettings settings, string sourceId)
    {
        var source = WeatherSources.Find(sourceId);
        return source is not null && source.Info.Domains.Contains(domain) && source.CanServe(city, settings) ? source : null;
    }

    // 一段取数：把本段的域按负责来源归并，每个来源只发一次请求。
    // 两段都落到同一来源时，后一段结果用 Absorb 按字段补齐，避免把前一段的逐时/空气覆盖掉。
    private static async Task FetchRoundAsync(City city, WeatherSettings settings,
        IReadOnlyDictionary<WeatherDomain, IWeatherSource?> owners, Dictionary<string, WeatherPart> parts,
        bool deferred, CancellationToken token)
    {
        // 历史域单独成组（同来源可能出现两次）：各组各自起算超时预算，历史的分批请求不侵占预警/指数的 15 秒。
        var groups = new Dictionary<(IWeatherSource Source, bool History, bool Air), List<WeatherDomain>>();
        foreach (var (domain, owner) in owners)
        {
            if (owner is null || IsDeferred(domain) != deferred) continue;
            if (domain == WeatherDomain.History && owner is OpenMeteoSource && parts.GetValueOrDefault(owner.Info.Id)?.Days is { Count: > 0 }) continue;
            var key = (owner, domain == WeatherDomain.History, domain == WeatherDomain.Air);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(domain);
        }
        if (groups.Count == 0) return;
        var results = await Task.WhenAll(groups.Select(async pair =>
            (Id: pair.Key.Source.Info.Id, Part: await TryAsync(pair.Key.Source, city, settings, pair.Value, token, pair.Key.History))));
        foreach (var (id, part) in results)
        {
            if (part is null) continue;
            if (parts.TryGetValue(id, out var existing)) Absorb(existing, part);
            else parts[id] = part;
        }
    }

    // 同一来源分多次返回时把结果并进同一份：能空位补齐的就补，日级数据按日期合并而不是互相覆盖。
    private static void Absorb(WeatherPart target, WeatherPart extra)
    {
        target.TimeZone ??= extra.TimeZone;
        target.Current ??= extra.Current;
        target.Air ??= extra.Air;
        target.Hours ??= extra.Hours;
        target.Minutes ??= extra.Minutes;
        target.MinuteSummary ??= extra.MinuteSummary;
        target.Warnings ??= extra.Warnings;
        target.Indices ??= extra.Indices;
        if (extra.Days is not { Count: > 0 } incoming) return;
        if (target.Days is not { Count: > 0 }) { target.Days = incoming; return; }
        var known = target.Days.Select(day => day.Date).ToHashSet(StringComparer.Ordinal);
        var merged = new List<DayPoint>(target.Days);
        merged.AddRange(incoming.Where(day => known.Add(day.Date)));
        target.Days = merged;
    }

    private static async Task<WeatherPart?> TryAsync(IWeatherSource source, City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token, bool history = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(history ? HistoryTimeout : SourceTimeout);
        try { return await source.FetchAsync(city, settings, need, cts.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // 合并：先建立以今天为中心的 24 天日期轴（历史 10 + 今天 + 未来 13），再按域把负责来源的数据叠上去。
    private static WeatherSnapshot Merge(City city, string sourceId, IReadOnlyDictionary<WeatherDomain, IWeatherSource?> owners, Dictionary<string, WeatherPart> parts, List<DayPoint>? supplement = null)
    {
        // 以城市当地日期为中心：查询国外城市时，“今天”不能按用户所在时区算。
        string? zone = city.TimeZoneId ?? parts.Values.Select(part => part.TimeZone).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var today = LocalToday(zone, city);
        var dates = Enumerable.Range(-10, 24).Select(offset => today.AddDays(offset)).ToArray();
        var dayMap = dates.ToDictionary(WeatherClock.DateKey, _ => new DayPoint());
        foreach (var (date, point) in dayMap) point.Date = date;
        var hours = new SortedDictionary<string, HourPoint>(StringComparer.Ordinal);
        var current = new CurrentPart();
        AirPart? air = null;
        var minutes = new List<MinutePoint>();
        var warnings = new List<WarningItem>();
        var indices = new List<IndexItem>();
        string? minuteSummary = null;
        var used = new Dictionary<WeatherDomain, List<string>>();
        void Use(WeatherDomain domain, IWeatherSource source)
        {
            if (!used.TryGetValue(domain, out var list)) used[domain] = list = [];
            if (!list.Contains(source.Info.Name)) list.Add(source.Info.Name);
        }

        foreach (var domain in Enum.GetValues<WeatherDomain>())
        {
            if (owners[domain] is not { } source) continue;
            if (!parts.TryGetValue(source.Info.Id, out var part)) continue;
            switch (domain)
            {
                case WeatherDomain.Current:
                    if (part.Current is { } c) { bool any = MergeCurrent(current, c); if (any) Use(domain, source); }
                    break;
                case WeatherDomain.Air:
                    // 空气质量按字段补齐：同一份返回里空位才填，单源聚合不做跨源补缺。
                    if (part.Air is { } a && a.HasData)
                    {
                        air ??= new AirPart();
                        bool any = false;
                        if (air.Aqi is null && a.Aqi is not null) { air.Aqi = a.Aqi; any = true; }
                        // 国际标准 AQI 独立合并：国内来源同给两套时各自落位，快照的 AqiGlobal 才有值。
                        if (air.AqiGlobal is null && a.AqiGlobal is not null) { air.AqiGlobal = a.AqiGlobal; any = true; }
                        if (air.Pm25 is null && a.Pm25 is not null) { air.Pm25 = a.Pm25; any = true; }
                        if (air.Pm10 is null && a.Pm10 is not null) { air.Pm10 = a.Pm10; any = true; }
                        if (air.Level is null && a.Level is not null) { air.Level = a.Level; any = true; }
                        if (air.Text is null && a.Text is not null) { air.Text = a.Text; any = true; }
                        if (air.Standard is null && a.Standard is not null) { air.Standard = a.Standard; any = true; }
                        if (air.Primary is null && a.Primary is not null) { air.Primary = a.Primary; any = true; }
                        if (air.PrimaryName is null && a.PrimaryName is not null) { air.PrimaryName = a.PrimaryName; any = true; }
                        if (air.HealthEffect is null && a.HealthEffect is not null) { air.HealthEffect = a.HealthEffect; any = true; }
                        if (air.HealthGeneral is null && a.HealthGeneral is not null) { air.HealthGeneral = a.HealthGeneral; any = true; }
                        if (air.HealthSensitive is null && a.HealthSensitive is not null) { air.HealthSensitive = a.HealthSensitive; any = true; }
                        // 污染物逐项补齐：排在前面的来源已经给出的项不再覆盖。
                        foreach (var (code, value) in a.Pollutants)
                        {
                            if (!air.Pollutants.TryAdd(code, value)) continue;
                            any = true;
                        }
                        // 空气质量逐时预报按整点键补齐；目前只有和风提供。
                        if (air.HourAqi.Count == 0 && a.HourAqi.Count > 0)
                        {
                            air.HourAqi = a.HourAqi;
                            any = true;
                        }
                        if (any) Use(domain, source);
                    }
                    break;
                case WeatherDomain.Hourly:
                    if (part.Hours is { Count: > 0 } hs) { bool any = false; foreach (var hour in hs) if (!hours.ContainsKey(hour.Time)) { hours[hour.Time] = hour; any = true; } if (any) Use(domain, source); }
                    break;
                case WeatherDomain.Minutely:
                    // 覆盖判定同时要求降水量有效：时间点齐全但全缺值（限流空壳）不算覆盖，让聚合器下探其它来源。
                    if (part.Minutes is { Count: > 0 } mins && mins.Any(m => m.Precip is not null)) { minutes.AddRange(mins.Where(m => !minutes.Any(x => x.Time == m.Time))); Use(domain, source); }
                    minuteSummary ??= part.MinuteSummary;
                    break;
                case WeatherDomain.Warning:
                    // 空列表也是一次有效回答（「当前无预警」），同样记来源。
                    if (part.Warnings is { } warns) { warnings.AddRange(warns); Use(domain, source); }
                    break;
                case WeatherDomain.Indices:
                    if (part.Indices is { Count: > 0 } idxs) { indices.AddRange(idxs); Use(domain, source); }
                    break;
                case WeatherDomain.Daily:
                case WeatherDomain.History:
                    if (part.Days is { Count: > 0 } ds)
                    {
                        bool any = false;
                        string todayKey = WeatherClock.DateKey(today);
                        foreach (var day in ds)
                        {
                            // 按日期归属：今天及以后算预报，之前算历史。
                            // 否则先遍历的 Daily 会把历史一起合并掉，History 域永远拿不到数据。
                            if ((string.CompareOrdinal(day.Date, todayKey) < 0) != (domain == WeatherDomain.History)) continue;
                            if (!dayMap.TryGetValue(day.Date, out var target)) continue;
                            if (MergeDay(target, day)) any = true;
                        }
                        if (any) Use(domain, source);
                    }
                    break;
            }
        }

        // 逐时窗口固定以「当前整点前 1 小时」为起点、共 24 条：过去 1 小时 + 当前 + 未来 22 小时。
        // 过去那 1 小时和风/各预报源都拿不到（和风从下一整点起、时光机不含今天），由本地历史补：
        // 每次成功刷新都把当时的整点数据存进 weather/hour-history，下一小时它就成了「过去」那一格。
        // 本地也没有就留空（NaN / -1），界面隐藏数值，不联网补、不冒充预报。
        if (supplement is not null)
        {
            bool contributed = false;
            foreach (var day in supplement)
                if (dayMap.TryGetValue(day.Date, out var target))
                    contributed |= MergeDay(target, day);
            if (contributed)
            {
                if (!used.TryGetValue(WeatherDomain.Daily, out var names)) used[WeatherDomain.Daily] = names = [];
                names.Add(ChinaDailySupplement.Name);
            }
        }
        var cityKey = WeatherData.CacheKey(city, sourceId);
        var local = WeatherHourHistory.Read(cityKey);
        foreach (var (time, point) in local)
            if (!hours.ContainsKey(time)) hours[time] = point;
        // 字典合并后顺序不保证有序（本地历史与预报源交错插入），先按整点键排好序再找锚点，窗口切片才可靠。
        var anchor = DateTimeOffset.UtcNow.ToOffset(ClockOffset(zone, city)).AddHours(-1);
        var hourList = Enumerable.Range(0, 24).Select(offset =>
        {
            string time = HourKey(anchor.AddHours(offset));
            return hours.GetValueOrDefault(time) ?? new HourPoint { Time = time };
        }).ToArray();
        if (air is not null && air.HourAqi.Count > 0)
            foreach (var hour in hourList)
                if (air.HourAqi.TryGetValue(hour.Time, out var aqi)) hour.Aqi = aqi;
        // 没有夜间分段的来源（Open-Meteo 等）从逐时窗口当晚 18–23 点补一个夜间天气码，
        foreach (var (date, point) in dayMap)
        {
            if (point.NightCode is not null) continue;
            var night = hourList.FirstOrDefault(hour => hour.Time.StartsWith(date, StringComparison.Ordinal)
                && hour.Code.HasValue && int.TryParse(hour.Time.Length >= 13 ? hour.Time.Substring(11, 2) : "", out int hourOfDay) && hourOfDay >= 18);
            if (night?.Code is { } code) point.NightCode = code;
        }
        var localNow = DateTimeOffset.UtcNow.ToOffset(ClockOffset(zone, city)).DateTime;
        bool daylight = current.IsDay ?? (localNow.Hour is >= 6 and < 18);
        if (dayMap.TryGetValue(WeatherClock.DateKey(localNow), out var localDay)
            && DateTime.TryParse(localDay.Sunrise, out var sunrise) && DateTime.TryParse(localDay.Sunset, out var sunset)
            && sunrise.Date == localNow.Date && sunset.Date == localNow.Date && sunrise < sunset)
            daylight = localNow >= sunrise && localNow < sunset;
        var snapshot = new WeatherSnapshot
        {
            Name = city.Name,
            // 快照一律按真实城市键存（自动定位也一样）：CityKey 供快照落盘与逐时历史使用，
            // 与下方 WeatherHourHistory.Read 的读取键严格一致；"auto" 只是 State.AutoKey 的映射。
            CityKey = cityKey,
            Today = WeatherClock.DateKey(today),
            FetchedAt = DateTimeOffset.Now,
            // 城市时钟：优先用 IANA 时区按当前时刻解析偏移，旧快照才靠固定偏移兜底。
            TimeZoneId = zone ?? "",
            UtcOffsetMinutes = (int)ClockOffset(zone, city).TotalMinutes,
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
            WindScale = current.WindScale ?? -1,
            PrecipRateMmH = current.PrecipRateMmH ?? double.NaN,
            PrecipType = current.PrecipType ?? "",
            WeatherCode = current.Code ?? -1,
            IsDay = daylight,
            Aqi = air?.Aqi ?? -1,
            AqiGlobal = air?.AqiGlobal ?? -1,
            Pm25 = air?.Pm25 ?? -1,
            Pm10 = air?.Pm10 ?? -1,
            AqiLevel = air?.Level ?? -1,
            AqiText = air?.Text ?? "",
            AqiStandard = air?.Standard ?? "",
            AirPrimary = air?.PrimaryName ?? air?.Primary ?? "",
            AirEffect = air?.HealthEffect ?? "",
            AirAdvice = air?.HealthGeneral ?? "",
            AirAdviceSensitive = air?.HealthSensitive ?? "",
            DayDates = dates.Select(WeatherClock.DateKey).ToArray(),
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
            MoonriseTimes = datePoints(dayMap, dates, point => point.Moonrise ?? ""),
            MoonsetTimes = datePoints(dayMap, dates, point => point.Moonset ?? ""),
            DayAvgC = datePoints(dayMap, dates, point => point.AvgTempC ?? double.NaN),
            DayCloudPercent = datePoints(dayMap, dates, point => point.CloudPercent ?? double.NaN),
            DayNightCodes = datePoints(dayMap, dates, point => point.NightCode ?? -1),
            DayNightConditions = datePoints(dayMap, dates, point => point.NightCondition ?? ""),
            DayNightHumidity = datePoints(dayMap, dates, point => point.NightHumidity ?? -1),
            DayNightWindMaxKmh = datePoints(dayMap, dates, point => point.NightWindMaxKmh ?? double.NaN),
            DayNightGustMaxKmh = datePoints(dayMap, dates, point => point.NightGustMaxKmh ?? double.NaN),
            DayNightWindDirDeg = datePoints(dayMap, dates, point => point.NightWindDirDeg ?? double.NaN),
            DayNightWindDir = datePoints(dayMap, dates, point => point.NightWindDir ?? ""),
            DayNightPrecipMm = datePoints(dayMap, dates, point => point.NightPrecipMm ?? double.NaN),
            DayNightRainProb = datePoints(dayMap, dates, point => point.NightRainProb ?? -1),
            DayNightCloudPercent = datePoints(dayMap, dates, point => point.NightCloudPercent ?? double.NaN),
            SolarNoonTimes = datePoints(dayMap, dates, point => point.SolarNoon ?? ""),
            MoonTransitTimes = datePoints(dayMap, dates, point => point.MoonTransit ?? ""),
            MinuteTimes = minutes.OrderBy(m => m.Time, StringComparer.Ordinal).Select(m => m.Time).ToArray(),
            MinutePrecip = minutes.OrderBy(m => m.Time, StringComparer.Ordinal).Select(m => m.Precip).ToArray(),
            MinuteSummary = minuteSummary ?? "",
            Warnings = [.. warnings],
            Indices = [.. indices],
            HourTimes = hourList.Select(hour => hour.Time).ToArray(),
            HourTempsC = hourList.Select(hour => hour.Temperature ?? double.NaN).ToArray(),
            HourFeelsLikeC = hourList.Select(hour => hour.FeelsLike ?? double.NaN).ToArray(),
            HourHumidity = hourList.Select(hour => hour.Humidity ?? -1).ToArray(),
            HourCodes = hourList.Select(hour => hour.Code ?? -1).ToArray(),
            HourRainProb = hourList.Select(hour => hour.RainProb ?? -1).ToArray(),
            HourAqi = hourList.Select(hour => hour.Aqi ?? -1).ToArray(),
            HourPrecipMm = hourList.Select(hour => hour.PrecipMm ?? double.NaN).ToArray(),
            HourWindKmh = hourList.Select(hour => hour.WindKmh ?? double.NaN).ToArray(),
            HourWindGustKmh = hourList.Select(hour => hour.WindGustKmh ?? double.NaN).ToArray(),
            HourWindDirDeg = hourList.Select(hour => hour.WindDirDeg ?? double.NaN).ToArray(),
            HourPressureHpa = hourList.Select(hour => hour.PressureHpa ?? double.NaN).ToArray(),
            HourUv = hourList.Select(hour => hour.Uv ?? double.NaN).ToArray(),
            HourDewPointC = hourList.Select(hour => hour.DewPointC ?? double.NaN).ToArray(),
            HourCloudPercent = hourList.Select(hour => hour.CloudPercent ?? double.NaN).ToArray(),
            HourVisibilityKm = hourList.Select(hour => hour.VisibilityKm ?? double.NaN).ToArray(),
            Sources = used.ToDictionary(pair => pair.Key.ToString(), pair => string.Join(" + ", pair.Value))
        };
        return snapshot;
    }

    private static T[] datePoints<T>(Dictionary<string, DayPoint> map, DateTime[] dates, Func<DayPoint, T> select)
        => dates.Select(date => select(map[WeatherClock.DateKey(date)])).ToArray();

    // 空位才写入：来源链靠前的那份数据优先，后面的来源只补空缺。
    private static bool Set<T>(ref T? field, T? value) where T : struct
    {
        if (field is null && value is not null) { field = value; return true; }
        return false;
    }

    // 字符串字段的空位补写，与 Set 同一语义（null 才算空，空串算已填）。
    private static bool FillString(ref string? field, string? value)
    {
        if (field is not null || value is null) return false;
        field = value; return true;
    }

    // 空位才写入：来源链靠前的那份数据优先，后面的来源只补空缺；返回是否补入了任何字段。
    private static bool MergeCurrent(CurrentPart target, CurrentPart source)
    {
        bool any = false;
        any |= Set(ref target.Temperature, source.Temperature);
        any |= Set(ref target.FeelsLike, source.FeelsLike);
        any |= Set(ref target.Humidity, source.Humidity);
        any |= Set(ref target.WindKmh, source.WindKmh);
        any |= Set(ref target.WindGustKmh, source.WindGustKmh);
        any |= Set(ref target.WindDirDeg, source.WindDirDeg);
        any |= Set(ref target.PressureHpa, source.PressureHpa);
        any |= Set(ref target.VisibilityKm, source.VisibilityKm);
        any |= Set(ref target.DewPointC, source.DewPointC);
        any |= Set(ref target.Uv, source.Uv);
        any |= Set(ref target.CloudPercent, source.CloudPercent);
        any |= Set(ref target.PrecipMm, source.PrecipMm);
        any |= Set(ref target.Code, source.Code);
        any |= Set(ref target.IsDay, source.IsDay);
        any |= Set<int>(ref target.WindScale, source.WindScale);
        any |= Set(ref target.PrecipRateMmH, source.PrecipRateMmH);
        any |= FillString(ref target.WindDir, source.WindDir);
        any |= FillString(ref target.PrecipType, source.PrecipType);
        return any;
    }

    private static bool MergeDay(DayPoint target, DayPoint source)
    {
        bool any = false;
        // 每条 `Set`/`FillString` 都要执行，所以用非短路的 `|` 串联：换成 `||` 会在第一个成功字段处
        // 短路，导致该来源后续的字段（如 solarNoon）补不进来。
        any |= Set<int>(ref target.Code, source.Code) | FillString(ref target.Condition, source.Condition);
        any |= Set<double>(ref target.HighC, source.HighC) | Set<double>(ref target.FeelsHighC, source.FeelsHighC);
        any |= Set<double>(ref target.LowC, source.LowC) | Set<double>(ref target.FeelsLowC, source.FeelsLowC);
        any |= Set<int>(ref target.RainProb, source.RainProb) | Set<double>(ref target.PrecipMm, source.PrecipMm);
        any |= Set<double>(ref target.UvMax, source.UvMax);
        any |= Set<double>(ref target.WindMaxKmh, source.WindMaxKmh) | Set<double>(ref target.GustMaxKmh, source.GustMaxKmh)
            | Set<double>(ref target.WindDirDeg, source.WindDirDeg) | FillString(ref target.WindDir, source.WindDir);
        any |= Set<int>(ref target.Humidity, source.Humidity);
        any |= Set<double>(ref target.PressureHpa, source.PressureHpa);
        // 天文（日升日落、月升月落、月相）随每日预报一起返回，是 Daily 的普通字段。
        any |= FillString(ref target.Sunrise, source.Sunrise) | FillString(ref target.Sunset, source.Sunset);
        // 太阳正午、月过中天：和风 astro 独有的天文轨道峰值，同为 Daily 字段。
        any |= FillString(ref target.SolarNoon, source.SolarNoon);
        any |= FillString(ref target.MoonTransit, source.MoonTransit);
        any |= FillString(ref target.Moonrise, source.Moonrise) | FillString(ref target.Moonset, source.Moonset)
            | FillString(ref target.MoonPhase, source.MoonPhase);
        // 日均温与白天云量：和风独有（Open-Meteo daily 不返回这两项）。
        any |= Set<double>(ref target.AvgTempC, source.AvgTempC);
        any |= Set<double>(ref target.CloudPercent, source.CloudPercent);
        // 夜间分段：夜间天气/湿度/风/降水/云量，缺哪项补哪项。
        any |= Set<int>(ref target.NightCode, source.NightCode) | FillString(ref target.NightCondition, source.NightCondition);
        any |= Set<int>(ref target.NightHumidity, source.NightHumidity);
        any |= Set<double>(ref target.NightWindMaxKmh, source.NightWindMaxKmh) | Set<double>(ref target.NightGustMaxKmh, source.NightGustMaxKmh)
            | Set<double>(ref target.NightWindDirDeg, source.NightWindDirDeg) | FillString(ref target.NightWindDir, source.NightWindDir);
        any |= Set<double>(ref target.NightPrecipMm, source.NightPrecipMm) | Set<int>(ref target.NightRainProb, source.NightRainProb);
        any |= Set<double>(ref target.NightCloudPercent, source.NightCloudPercent);
        return any;
    }

    // 「当地今天」：优先用来源给的 IANA 时区（能带夏令时）；和风不提供时区，退回按经度估算的固定偏移。
    // 否则查询跨日期的国外城市（例如洛杉矶清晨、北京已是次日）会把日期轴与时光机返回的日期错开一天。
    private static DateTime LocalToday(string? zone, City city)
    {
        if (!string.IsNullOrWhiteSpace(zone))
        {
            try { return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)).Date; }
            catch { }
        }
        return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(city))).Date;
    }
    // 城市当地的「现在」：逐时窗口锚点按当地时钟算，查询国外城市时不能用用户本地时间。
    private static TimeSpan ClockOffset(string? zone, City city)
        => zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var info)
            ? info.GetUtcOffset(DateTimeOffset.UtcNow) : TimeSpan.FromHours(Geo.UtcOffsetHours(city));
    // 整点键：与 HourPoint.Time 同一格式（yyyy-MM-ddTHH:mm，当地时钟），可直接做字符串比较。
    private static string HourKey(DateTimeOffset at)
        => new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Offset).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
}
