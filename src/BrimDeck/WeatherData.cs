using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using BrimDeck.Core;
namespace BrimDeck;
// Generic geographic place: a named city with coordinates. Lives in the data layer because it is used by
// search, favourite storage and automatic location alike.
internal sealed record City(string Name, double Latitude, double Longitude);
// Data layer: weather fetching, city search, location, JSON parsing and persistence. No WPF controls or view lifetime.
internal static class WeatherData
{
    internal sealed class State { public List<City> Cities {get;set;}=[]; public int Selected {get;set;}=-1; public Dictionary<string,string> Cache {get;set;}=[]; }
    private static readonly string FilePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BrimDeck","weather.json");
    private static readonly HttpClient Http=new() {Timeout=TimeSpan.FromSeconds(20)};
    internal static State Load() {try {return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath))??new();}catch{return new();}}
    // Merges local cache into the persisted one so an in-flight refresh never wipes another view's result.
    internal static void Save(State state) {var latest=Load();foreach(var (key,value) in state.Cache)latest.Cache[key]=value;state.Cache=latest.Cache;Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);File.WriteAllText(FilePath,JsonSerializer.Serialize(state));}
    internal static async Task<List<City>> Search(string query,CancellationToken token)
    {
        using var doc=JsonDocument.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=10&language="+(Loc.IsEnglish?"en":"zh")+"&name="+Uri.EscapeDataString(query),token));
        var cities=new List<City>();if(doc.RootElement.TryGetProperty("results",out var list))foreach(var c in list.EnumerateArray())cities.Add(new City(string.Join(" · ",new[]{c.GetProperty("name").GetString(),c.TryGetProperty("admin1",out var a)?a.GetString():null,c.TryGetProperty("country",out var country)?country.GetString():null}.Where(x=>!string.IsNullOrWhiteSpace(x))),c.GetProperty("latitude").GetDouble(),c.GetProperty("longitude").GetDouble()));return cities;
    }
    // 自动定位：IP 反查得到城市名后，复用与搜索相同的地理编码源解析标准名与坐标，保证名称格式与手动添加完全一致。
    internal static async Task<City?> LocateAsync(CancellationToken token)
    {
        try {
            using var doc=JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang="+(Loc.IsEnglish?"en":"zh-CN"),token));
            var root=doc.RootElement;
            if(root.TryGetProperty("success",out var ok)&&ok.ValueKind==JsonValueKind.False)return null;
            if(!root.TryGetProperty("latitude",out var la)||!root.TryGetProperty("longitude",out var lo))return null;
            double lat=la.GetDouble(),lon=lo.GetDouble();
            if(root.TryGetProperty("city",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} query) {
                var found=await Search(query,token);
                if(found.Count>0)return found.OrderBy(c=>Math.Abs(c.Latitude-lat)+Math.Abs(c.Longitude-lon)).First();
            }
            var parts=new List<string>();
            if(root.TryGetProperty("city",out name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} s)parts.Add(s);
            if(root.TryGetProperty("region",out var region)&&region.ValueKind==JsonValueKind.String&&region.GetString() is {Length:>0} r)parts.Add(r);
            if(root.TryGetProperty("country",out var country)&&country.ValueKind==JsonValueKind.String&&country.GetString() is {Length:>0} c&&!parts.Contains(c))parts.Add(c);
            return new City(parts.Count>0?string.Join(" · ",parts):"当前位置",lat,lon);
        } catch { return null; }
    }
    // Fetches current + 13-day forecast + 10-day history + 24 hourly readings from Open-Meteo, parses them into a strongly-typed snapshot. No JSON knowledge leaks to the views.
    internal static async Task<WeatherSnapshot> CurrentAsync(City city,CancellationToken token)
    {
        string url=FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={city.Latitude}&longitude={city.Longitude}&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&hourly=temperature_2m,relative_humidity_2m,weather_code,precipitation_probability&forecast_hours=24&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max&timezone=auto&past_days=10&forecast_days=14");
        using var doc=JsonDocument.Parse(await Http.GetStringAsync(url,token));
        return Parse(doc.RootElement,city.Name);
    }
    // 从缓存字符串还原快照；与 Encode 严格对称，仅支持当前版本格式，旧版缓存数据视为损坏丢弃。
    internal static WeatherSnapshot Decode(string json)
    {
        var s = JsonSerializer.Deserialize<WeatherSnapshot>(json) ?? throw new InvalidDataException("缓存数据损坏");
        if (s.DayHighC.Length == 0 || s.HourTimes.Length == 0) throw new InvalidDataException("缓存数据损坏");
        return s;
    }
    internal static string Encode(WeatherSnapshot s) => JsonSerializer.Serialize(s);
    // 在 daily 正序数组中定位“今天”的索引；直接按日期匹配，兼容任意缓存天数，找不到时回退 0。
    internal static int TodayIndex(WeatherSnapshot s)
    {
        string today = s.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd");
        for (int i = 0; i < s.DayDates.Length; i++) if (s.DayDates[i].StartsWith(today)) return i;
        return 0;
    }
    private static WeatherSnapshot Parse(JsonElement root,string name)
    {
        var c=root.GetProperty("current");var h=root.GetProperty("hourly");var d=root.GetProperty("daily");
        return new WeatherSnapshot {
            Name=name,FetchedAt=DateTimeOffset.Now,
            Temperature=c.GetProperty("temperature_2m").GetDouble(),FeelsLike=c.GetProperty("apparent_temperature").GetDouble(),
            HumidityPercent=c.GetProperty("relative_humidity_2m").GetInt32(),WindKmh=c.GetProperty("wind_speed_10m").GetDouble(),
            WeatherCode=c.GetProperty("weather_code").GetInt32(),IsDay=c.GetProperty("is_day").GetInt32()!=0,
            HourTimes=h.GetProperty("time").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
            HourTempsC=h.GetProperty("temperature_2m").EnumerateArray().Select(x=>x.GetDouble()).ToArray(),
            HourHumidity=h.GetProperty("relative_humidity_2m").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
            HourCodes=h.GetProperty("weather_code").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
            HourRainProb=h.GetProperty("precipitation_probability").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
            DayDates=d.GetProperty("time").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
            DayCodes=d.GetProperty("weather_code").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),
            DayHighC=d.GetProperty("temperature_2m_max").EnumerateArray().Select(x=>x.GetDouble()).ToArray(),
            DayLowC=d.GetProperty("temperature_2m_min").EnumerateArray().Select(x=>x.GetDouble()).ToArray(),
            SunriseTimes=d.GetProperty("sunrise").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
            SunsetTimes=d.GetProperty("sunset").EnumerateArray().Select(x=>x.GetString()!).ToArray(),
            DayRainProb=d.GetProperty("precipitation_probability_max").EnumerateArray().Select(x=>x.GetInt32()).ToArray()
        };
    }
}
// Strongly-typed weather model; the only data contract between the data layer and the views. Views never touch JSON.
internal sealed class WeatherSnapshot
{
    public string Name {get;set;}=""; public DateTimeOffset FetchedAt {get;set;}
    // 当前时刻
    public double Temperature {get;set;} public double FeelsLike {get;set;} public int HumidityPercent {get;set;} public double WindKmh {get;set;} public int WeatherCode {get;set;} public bool IsDay {get;set;}
    // 当日各时段（24 个）
    public string[] HourTimes {get;set;}=[]; public double[] HourTempsC {get;set;}=[]; public int[] HourHumidity {get;set;}=[]; public int[] HourCodes {get;set;}=[]; public int[] HourRainProb {get;set;}=[];
    // 全 24 天 = 过去 10 天 + 今天 + 未来 13 天（Open-Meteo daily 正序返回）
    public string[] DayDates {get;set;}=[]; public int[] DayCodes {get;set;}=[]; public double[] DayHighC {get;set;}=[]; public double[] DayLowC {get;set;}=[]; public string[] SunriseTimes {get;set;}=[]; public string[] SunsetTimes {get;set;}=[]; public int[] DayRainProb {get;set;}=[];
}