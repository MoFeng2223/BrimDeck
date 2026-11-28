using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class OpenMeteoSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("openmeteo", "Open-Meteo",
        [WeatherDomain.Current, WeatherDomain.Hourly, WeatherDomain.Daily, WeatherDomain.Air, WeatherDomain.History, WeatherDomain.Minutely]);
    public bool CanServe(City city, WeatherSettings settings) => true;

    public async Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token)
    {
        bool Want(WeatherDomain domain) => need.Contains(domain);
        // 一份 forecast 响应同时覆盖实况、每日（含天文）与过去 10 天，三者共用一次请求。
        bool wantForecast = Want(WeatherDomain.Current) || Want(WeatherDomain.Daily) || Want(WeatherDomain.History);
        string location = FormattableString.Invariant($"latitude={city.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={city.Longitude.ToString(CultureInfo.InvariantCulture)}&timezone=auto");
        string site = FormattableString.Invariant($"{city.Latitude:0.00},{city.Longitude:0.00}");
        bool onlyCurrent = need.Count == 1 && Want(WeatherDomain.Current);
        var forecast = onlyCurrent ? Body("current|" + site, TimeSpan.FromMinutes(15),
            $"https://api.open-meteo.com/v1/forecast?{location}&current=temperature_2m,weather_code,is_day&timeformat=unixtime", token, allowStale: false) : wantForecast ? Body("forecast|" + site, TimeSpan.FromMinutes(15), $"https://api.open-meteo.com/v1/forecast?{location}&current=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,weather_code,pressure_msl,cloud_cover,wind_speed_10m,wind_direction_10m,wind_gusts_10m,visibility,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min,sunrise,sunset,moonrise,moonset,uv_index_max,precipitation_sum,precipitation_probability_max,wind_speed_10m_max,wind_gusts_10m_max,wind_direction_10m_dominant,relative_humidity_2m_mean,pressure_msl_mean&past_days=10&forecast_days=14", token) : Task.FromResult("");
        var hourly = Want(WeatherDomain.Hourly) ? Body("hourly|" + site, TimeSpan.FromMinutes(30), $"https://api.open-meteo.com/v1/forecast?{location}&hourly=temperature_2m,apparent_temperature,relative_humidity_2m,dew_point_2m,precipitation,precipitation_probability,weather_code,pressure_msl,cloud_cover,visibility,wind_speed_10m,wind_direction_10m,wind_gusts_10m,uv_index&forecast_hours=48", token) : Task.FromResult("");
        var air = Want(WeatherDomain.Air) ? Body("air|" + site, TimeSpan.FromMinutes(30), $"https://air-quality-api.open-meteo.com/v1/air-quality?{location}&current=pm2_5,pm10,us_aqi,european_aqi", token) : Task.FromResult("");
        // 分钟降水（未来 2 小时的 15 分钟级）独立缓存。
        var minute = Want(WeatherDomain.Minutely) ? Body("minute|" + site, TimeSpan.FromMinutes(15), $"https://api.open-meteo.com/v1/forecast?{location}&minutely_15=precipitation&forecast_minutely_15=8", token) : Task.FromResult("");
        await Task.WhenAll(forecast, hourly, air, minute);
        var part = new WeatherPart();
        try
        {
            using var doc = JsonDocument.Parse(forecast.Result);
            var root = doc.RootElement;
            part.TimeZone = Json.Text(root, "timezone");
            if (Json.Get(root, "current") is { } current)
                part.Current = new CurrentPart
                {
                    ObservedAt = Json.Get(current, "time") is { ValueKind: JsonValueKind.Number } time && time.TryGetInt64(out long unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix)
                        : DateTime.TryParse(Json.Text(current, "time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromSeconds(Json.Integer(root, "utc_offset_seconds") ?? 0)) : null,
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
                string[] names = ["weather_code", "temperature_2m_max", "temperature_2m_min", "apparent_temperature_max", "apparent_temperature_min", "sunrise", "sunset", "moonrise", "moonset", "uv_index_max", "precipitation_sum", "precipitation_probability_max", "wind_speed_10m_max", "wind_gusts_10m_max", "wind_direction_10m_dominant", "relative_humidity_2m_mean", "pressure_msl_mean"];
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
                        Sunrise = TextAt("sunrise", i), Sunset = TextAt("sunset", i),
                        // 月出/月落：Open-Meteo 返回 ISO 时刻；某天月不升/不落时该日为 null，合并层自然跳过。
                        Moonrise = TextAt("moonrise", i), Moonset = TextAt("moonset", i), UvMax = At("uv_index_max", i),
                        PrecipMm = At("precipitation_sum", i), RainProb = (int?)At("precipitation_probability_max", i),
                        WindMaxKmh = At("wind_speed_10m_max", i), GustMaxKmh = At("wind_gusts_10m_max", i),
                        WindDirDeg = At("wind_direction_10m_dominant", i), Humidity = (int?)At("relative_humidity_2m_mean", i),
                        PressureHpa = At("pressure_msl_mean", i)
                    });
                }
            }
        }
        catch { }
        if (onlyCurrent) return part.Current is null ? null : part;
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
                // 先按当地时间定位当前小时取 UV，序列里没有（过去两小时的旧点不算）再退回最近一条有 UV 的。
                if (part.Current is { Uv: null })
                {
                    // 优先城市时区 ID（带夏令时），其次拿响应里的 timezone，最后才按经度估算偏移。
                    string? zone = !string.IsNullOrWhiteSpace(city.TimeZoneId) ? city.TimeZoneId : part.TimeZone;
                    var now = zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var info)
                        ? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, info)
                        : DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(Geo.UtcOffsetHours(city)));
                    string nowKey = now.ToString("yyyy-MM-ddTHH:00", CultureInfo.InvariantCulture);
                    string nextKey = now.AddHours(1).ToString("yyyy-MM-ddTHH:00", CultureInfo.InvariantCulture);
                    // 当前整点没值时只认紧接着的下一小时；旧序列里第一条非空记录可能是几十分钟前的旧 UV。
                    var pick = part.Hours.FirstOrDefault(hour => hour.Time == nowKey && hour.Uv is not null)
                        ?? part.Hours.FirstOrDefault(hour => hour.Uv is not null
                            && string.CompareOrdinal(hour.Time, nowKey) >= 0 && string.CompareOrdinal(hour.Time, nextKey) <= 0);
                    if (pick is { Uv: { } uv }) part.Current.Uv = uv;
                }
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
                    Standard = "us_aqi",
                    Level = Json.Integer(current, "us_aqi") is { } aqi ? WeatherCodes.AqiBand(aqi) : null,
                    Text = Json.Integer(current, "us_aqi") is { } value ? WeatherCodes.UsAqiText(value) : null
                };
        }
        catch { }
        // 分钟降水：minutely_15.precipitation
        try
        {
            using var doc = JsonDocument.Parse(minute.Result);
            if (Json.Get(doc.RootElement, "minutely_15") is { } m && Json.Get(m, "time") is { } times)
            {
                int count = times.GetArrayLength();
                part.Minutes = [];
                for (int i = 0; i < count; i++)
                {
                    string? time = times[i].ValueKind == JsonValueKind.String ? times[i].GetString() : null;
                    if (time is null) continue;
                    double? precip = Json.Get(m, "precipitation") is { } arr && i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : null;
                    part.Minutes.Add(new MinutePoint { Time = time, Precip = precip, SpanMinutes = 15 });
                }
            }
        }
        catch { }
        return part.Current is null && part.Days is null && part.Hours is null && part.Air is null
            && part.Minutes is not { Count: > 0 } ? null : part;
    }

    // 响应体落盘：四份数据（预报/逐时/空气/分钟降水）各自过期，未过期直接读盘，重启后同样能复用，断网时退回上一份。
    private static async Task<string> Body(string key, TimeSpan ttl, string url, CancellationToken token, bool allowStale = true)
        => await WeatherCache.GetAsync<string>("openmeteo", key, ttl, async ct =>
        {
            var body = (await WeatherHttp.GetAsync("openmeteo", url, ct)).Body;
            if (body is null) return null;
            if (!allowStale)
            {
                using var json = JsonDocument.Parse(body);
                if (Json.Get(json.RootElement, "current") is not { } current || Json.Number(current, "temperature_2m") is null) return null;
            }
            return body;
        }, token, allowStale) ?? "";
}
