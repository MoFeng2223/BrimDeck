using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class TencentSource : IWeatherSource
{
    public WeatherSourceInfo Info { get; } = new("tencent", Loc.T("腾讯天气", "Tencent Weather"), Loc.T("国内实况与 8 天预报", "China current & 8-day"),
        [WeatherDomain.Current, WeatherDomain.Daily, WeatherDomain.Air], WeatherRegion.China);

    public bool CanServe(City city, WeatherSettings settings)
    {
        var (name, _) = Geo.Parts(city);
        // 接口只认中文城市名：英文环境下搜出的城市名不含汉字，这家来源不可用，
        // 由设置页/调度层直接显示为空，不做自动换源。
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
