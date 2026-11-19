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
    internal sealed class State { public List<City> Cities {get;set;}=[]; public int Selected {get;set;}=-1; public Dictionary<string,WeatherSnapshot> Cache {get;set;}=[]; public WeatherSettings Schedule {get;set;}=new(); }
    // 设置文件只放城市与调度：快照按城市各存一份，刷新一座城不会重写整个文件，也不会和别的窗口互相覆盖。
    private sealed class SettingsFile
    {
        public List<City> Cities { get; set; } = [];
        public int Selected { get; set; } = -1;
        public WeatherSettings Schedule { get; set; } = new();
    }
    private sealed class SnapshotFile { public string Key {get;set;}=""; public WeatherSnapshot? Value {get;set;} }
    private static readonly string FilePath=Path.Combine(WeatherFiles.Root,"weather.json");
    private static readonly HttpClient Http=new() {Timeout=TimeSpan.FromSeconds(20)};
    // 快照用 NaN 表示来源未提供的数值；命名浮点字面量让缓存可以安全写入并还原，缩进让 weather.json 可读。
    private static readonly JsonSerializerOptions WeatherJson=new() {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    // 已落盘的快照时间：同一条数据没有更新就不再重复写盘，刷新一座城只碰一个文件。
    private static readonly object SnapLock=new();
    private static readonly Dictionary<string,DateTimeOffset> Written=new(StringComparer.Ordinal);
    private static int _started;
    // 进程内只做一次：清理过期缓存条目、恢复当日请求计数。
    private static void Start()
    {
        if(Interlocked.Exchange(ref _started,1)==1)return;
        WeatherFiles.Ensure(WeatherFiles.Snapshots);
        WeatherCache.Prune(TimeSpan.FromDays(14));
        RequestGate.Restore();
    }
    internal static State Load()
    {
        Start();
        var state=new State();
        try
        {
            string text=File.Exists(FilePath)?File.ReadAllText(FilePath):"";
            if(text.Length>0&&JsonSerializer.Deserialize<SettingsFile>(text,WeatherJson) is { } file)
            {
                state.Cities=file.Cities??[];
                state.Selected=file.Selected;
                state.Schedule=file.Schedule??new();
            }
        }
        catch { }
        foreach(var (key,snapshot) in ReadSnapshots())state.Cache[key]=snapshot;
        return state;
    }
    private static IEnumerable<(string Key,WeatherSnapshot Value)> ReadSnapshots()
    {
        string[] files;
        try { files=Directory.Exists(WeatherFiles.Snapshots)?Directory.GetFiles(WeatherFiles.Snapshots,"*.json"):[]; }
        catch { yield break; }
        foreach(var file in files)
        {
            string key="";WeatherSnapshot? snapshot=null;
            try
            {
                if(JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(file),WeatherJson) is { Value:{ } value } entry){key=entry.Key;snapshot=value;}
            }
            catch { }
            if(snapshot is null||key.Length==0)continue;
            // 记下已落盘的时间，接下来的保存就不会把它再写一遍。
            lock(SnapLock)Written[key]=snapshot.FetchedAt;
            yield return (key,snapshot);
        }
    }
    // 设置与快照分开写：任一文件损坏都只影响自己，不会连带把城市列表一起弄丢。
    internal static void Save(State state)
    {
        WeatherFiles.WriteAtomic(FilePath,JsonSerializer.Serialize(new SettingsFile{Cities=state.Cities,Selected=state.Selected,Schedule=state.Schedule},WeatherJson));
        lock(SnapLock)
            foreach(var (key,snapshot) in state.Cache)
            {
                if(Written.TryGetValue(key,out var at)&&snapshot.FetchedAt<=at)continue;
                if(!WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Snapshots,WeatherFiles.Name(key,".json")),JsonSerializer.Serialize(new SnapshotFile{Key=key,Value=snapshot},WeatherJson)))continue;
                Written[key]=snapshot.FetchedAt;
            }
        // 请求计数随保存一起落盘：重启后不会把当天额度重新算满。
        RequestGate.Flush();
    }
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
    // 多源调度入口：当前、24 小时、24 天（历史 10 + 今天 + 未来 13）、日出日落与空气质量由 WeatherAggregator 统一拼装。
    // 历史单独一轮，progress 让界面先画不含历史的版本，历史到了再用返回值整体替换。
    internal static async Task<WeatherSnapshot> CurrentAsync(City city, WeatherSettings schedule, CancellationToken token, Action<WeatherSnapshot>? progress = null)
        => await WeatherAggregator.FetchAsync(city, schedule, token, progress);
    // 在 daily 正序数组中定位“今天”的索引；Today 是聚合时写下的城市当地日期，找不到时回退 0。
    internal static int TodayIndex(WeatherSnapshot s)
    {
        string today = s.Today;
        for (int i = 0; i < s.DayDates.Length; i++) if (s.DayDates[i].StartsWith(today)) return i;
        return 0;
    }
}
// Strongly-typed weather model; the only data contract between the data layer and the views. Views never touch JSON.
// 所有数组都以 DayDates / HourTimes 对齐；来源没有提供的数值用 NaN（数值）或 -1（整数）表示，界面自行隐藏。
internal sealed class WeatherSnapshot
{
    public string Name {get;set;}=""; public DateTimeOffset FetchedAt {get;set;} public string Today {get;set;}="";
    // 当前时刻
    public double Temperature {get;set;} public double FeelsLike {get;set;} public int HumidityPercent {get;set;} public double WindKmh {get;set;} public int WeatherCode {get;set;} public bool IsDay {get;set;}
    public double WindGustKmh {get;set;} public double WindDirectionDeg {get;set;} public string WindDirection {get;set;}="";
    public double PressureHpa {get;set;} public double VisibilityKm {get;set;} public double DewPointC {get;set;} public double UvIndex {get;set;}
    public double CloudPercent {get;set;} public double PrecipitationMm {get;set;} public string ConditionText {get;set;}="";
    // 空气质量：-1 表示来源没有提供，界面据此隐藏。
    public int Aqi {get;set;}=-1; public int Pm25 {get;set;}=-1; public int Pm10 {get;set;}=-1; public int AqiLevel {get;set;}=-1; public string AqiText {get;set;}="";
    // 空气详情：首要污染物、健康影响与两类人群建议；AirPollutants 是「污染物名称 → 值 单位」的展示用列表。
    public string AirPrimary {get;set;}=""; public string AirEffect {get;set;}=""; public string AirAdvice {get;set;}=""; public string AirAdviceSensitive {get;set;}="";
    public Dictionary<string,string> AirPollutants {get;set;}=[];
    // 当日各时段（24 个）
    public string[] HourTimes {get;set;}=[]; public double[] HourTempsC {get;set;}=[]; public int[] HourHumidity {get;set;}=[]; public int[] HourCodes {get;set;}=[]; public int[] HourRainProb {get;set;}=[];
    public double[] HourFeelsLikeC {get;set;}=[]; public double[] HourPrecipMm {get;set;}=[]; public double[] HourWindKmh {get;set;}=[]; public double[] HourWindGustKmh {get;set;}=[];
    public double[] HourWindDirDeg {get;set;}=[]; public double[] HourPressureHpa {get;set;}=[]; public double[] HourUv {get;set;}=[]; public double[] HourDewPointC {get;set;}=[];
    public double[] HourCloudPercent {get;set;}=[]; public double[] HourVisibilityKm {get;set;}=[];
    // 全 24 天 = 过去 10 天 + 今天 + 未来 13 天（按日期正序）
    public string[] DayDates {get;set;}=[]; public int[] DayCodes {get;set;}=[]; public double[] DayHighC {get;set;}=[]; public double[] DayLowC {get;set;}=[]; public string[] SunriseTimes {get;set;}=[]; public string[] SunsetTimes {get;set;}=[]; public int[] DayRainProb {get;set;}=[];
    public string[] DayConditions {get;set;}=[]; public double[] DayFeelsHighC {get;set;}=[]; public double[] DayFeelsLowC {get;set;}=[];
    public double[] DayPrecipMm {get;set;}=[]; public double[] DayUvMax {get;set;}=[]; public double[] DayWindMaxKmh {get;set;}=[]; public double[] DayGustMaxKmh {get;set;}=[];
    public double[] DayWindDirDeg {get;set;}=[]; public string[] DayWindDir {get;set;}=[]; public int[] DayHumidity {get;set;}=[]; public double[] DayPressureHpa {get;set;}=[]; public string[] DayMoonPhase {get;set;}=[];
    // 每个数据域实际贡献的来源，例如 Daily → "中国天气网 + Open-Meteo"。
    public Dictionary<string,string> Sources {get;set;}=[];
    // 当前读数逐字段的来源（字段名 → 来源 id），供详情页逐项着色标注；来源未给出时为空字典，界面不标注。
    public Dictionary<string,string> FieldSources {get;set;}=[];
    // 每个域参与调度的全部来源（含未提供数据的，保序去重），供详情页把每一家来源都标注出来。
    public Dictionary<string,string[]> DomainSources {get;set;}=[];
    // 某来源本次是否实际提供了数据（任一域），未提供的在详情页以灰色标注"未提供数据"。
    public Dictionary<string,bool> SourceProvided {get;set;}=[];
}