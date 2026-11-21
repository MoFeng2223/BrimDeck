using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrimDeck.Core;
namespace BrimDeck;
// 通用地理地点：带坐标的命名城市。放在数据层，因为搜索、收藏与自动定位都用它。
// 层级拆成结构化字段：Name 是地名主体（区县/城市本身，如「洪山区」），CityName/Province/Country 是所属
// 地级市/省州/国家，LocationId 是和风城市 id（与中国天气网代码同源，中国天气网与时光机可直接使用）。
// 旧格式（Name 内嵌「城市 · 省 · 国家」三段）不再写入，读取时保留兜底拆段。
internal sealed record City(string Name, double Latitude, double Longitude, string Province = "", string CityName = "", string Country = "", string LocationId = "")
{
    // 完整层级展示名：「洪山区 · 武汉市 · 湖北省」。跳过空段与重复段，国内不再缀「中国」；
    // 旧格式 Name 自带分隔，原样返回。设置页城市行、搜索下拉与详情页标题用它区分同名区县。
    [JsonIgnore] public string Full => Name.Contains(" · ")
        ? Name
        : string.Join(" · ", new[] { Name, CityName, Province }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Concat(Country is "" or "中国" or "China" or "中國" ? Array.Empty<string>() : new[] { Country }));
}
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
    // 城市搜索：配置了和风 Key 时优先走和风 GeoAPI——能精确命中区县（搜「洪山区」得「洪山区 · 武汉市 · 湖北省」），
    // 返回的 id 与中国天气网城市代码同源；未配置或查询失败时退回 Open-Meteo（全球地名库，中文覆盖一般）。
    internal static async Task<List<City>> Search(string query, WeatherSettings settings, CancellationToken token)
    {
        if (settings.HeFengKey.Trim().Length > 0 && settings.HeFengHost.Trim().Length > 0)
        {
            try
            {
                var found = await SearchHeFeng(query, settings, token);
                if (found.Count > 0) return found;
            }
            catch { /* 网络/额度失败不阻塞：退回 Open-Meteo 继续 */ }
        }
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=10&language=" + (Loc.IsEnglish ? "en" : "zh") + "&name=" + Uri.EscapeDataString(query), token));
        var cities = new List<City>();
        if (doc.RootElement.TryGetProperty("results", out var list))
            foreach (var c in list.EnumerateArray())
                cities.Add(new City(
                    Json.Text(c, "name") ?? "",
                    c.GetProperty("latitude").GetDouble(), c.GetProperty("longitude").GetDouble(),
                    Json.Text(c, "admin1") ?? "", Json.Text(c, "admin2") ?? "", Json.Text(c, "country") ?? ""));
        return cities;
    }
    // 和风 GeoAPI v2 city/lookup：按地名或「经度,纬度」坐标反查最多 10 个，结构化解析省/市/国家/城市 id。
    // 结果落盘一天：输入框防抖后的每一次查询不重复烧免费额度。
    private static async Task<List<City>> SearchHeFeng(string query, WeatherSettings settings, CancellationToken token)
    {
        string host = settings.HeFengHost.Trim().TrimEnd('/');
        // 与天气请求同一策略：Key 走请求头，强制 HTTPS，绝不落明文。
        host = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + host[7..]
             : host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host;
        string lang = Loc.IsEnglish ? "en" : "zh";
        string body = await WeatherCache.GetAsync<string>("hefeng", "search|" + lang + "|" + query, TimeSpan.FromDays(1), async ct =>
        {
            var result = await WeatherHttp.GetAsync("hefeng", $"{host}/geo/v2/city/lookup?location={Uri.EscapeDataString(query)}&number=10&lang={lang}", ct,
                request => request.Headers.TryAddWithoutValidation("X-QW-Api-Key", settings.HeFengKey.Trim()));
            return result.Body;
        }, token) ?? "";
        var cities = new List<City>();
        if (body.Length == 0) return cities;
        using var doc = JsonDocument.Parse(body);
        if (Json.Get(doc.RootElement, "location") is not { ValueKind: JsonValueKind.Array } list) return cities;
        foreach (var item in list.EnumerateArray())
        {
            double? lat = Json.Number(item, "lat"), lon = Json.Number(item, "lon");
            if (Json.Text(item, "name") is not { Length: > 0 } name || lat is null || lon is null) continue;
            cities.Add(new City(name, lat.Value, lon.Value, Json.Text(item, "adm1") ?? "", Json.Text(item, "adm2") ?? "", Json.Text(item, "country") ?? "", Json.Text(item, "id") ?? ""));
        }
        return cities;
    }
    // 自动定位，精度分层，不冒充：
    // 1) Windows 系统定位（WiFi/网络，数百米级）→ 和风反查到区县，「洪山区 · 武汉市 · 湖北省」才真正可信；
    // 2) IP 定位（城市级精度，坐标常是数据库里的城市中心点——武汉的中心点恰好落在江汉区，
    //    反查到区县只会给出错的位置）→ 反查结果只取城市与省两级，诚实显示「武汉市 · 湖北省」；
    // 3) 无和风 Key：退回按 IP 城市名搜索或直接用 IP 返回的地名拼装。
    internal static async Task<City?> LocateAsync(WeatherSettings settings, CancellationToken token)
    {
        try {
            bool hefeng=settings.HeFengKey.Trim().Length>0&&settings.HeFengHost.Trim().Length>0;
            if(hefeng&&await WindowsLocation.PositionAsync(token) is { } precise)
            {
                var found=await SearchHeFeng(Math.Round(precise.Lon,3)+","+Math.Round(precise.Lat,3),settings,token);
                if(found.Count>0)return found[0];
            }
            using var doc=JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang="+(Loc.IsEnglish?"en":"zh-CN"),token));
            var root=doc.RootElement;
            if(root.TryGetProperty("success",out var ok)&&ok.ValueKind==JsonValueKind.False)return null;
            if(!root.TryGetProperty("latitude",out var la)||!root.TryGetProperty("longitude",out var lo))return null;
            double lat=la.GetDouble(),lon=lo.GetDouble();
            if(hefeng)
            {
                var found=await SearchHeFeng(Math.Round(lon,3)+","+Math.Round(lat,3),settings,token);
                if(found.Count>0)
                {
                    // IP 坐标只有城市精度：只保留城市与省两级；区县名与区县 id 都不可信，一并不带。
                    var city=found[0];
                    string cityName=city.CityName.Length>0?city.CityName:city.Name;
                    return new City(cityName,lat,lon,city.Province,cityName,city.Country,"");
                }
            }
            if(root.TryGetProperty("city",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} query) {
                var found=await Search(query,settings,token);
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
    public string Name {get;set;}=""; public string FullName {get;set;}=""; public DateTimeOffset FetchedAt {get;set;} public string Today {get;set;}="";
    // FullName 是城市完整层级（洪山区 · 武汉市 · 湖北省），详情页标题与设置预览用它；空时退回 Name。
    // 城市所在地的 UTC 偏移（分钟）：逐时「现在」列要按城市时钟认，查国外城市时不能用本机时间。MinValue = 未知。
    public int UtcOffsetMinutes {get;set;} = int.MinValue;
    // 当前时刻
    public double Temperature {get;set;} public double FeelsLike {get;set;} public int HumidityPercent {get;set;} public double WindKmh {get;set;} public int WeatherCode {get;set;} public bool IsDay {get;set;}
    public double WindGustKmh {get;set;} public double WindDirectionDeg {get;set;} public string WindDirection {get;set;}="";
    public double PressureHpa {get;set;} public double VisibilityKm {get;set;} public double DewPointC {get;set;} public double UvIndex {get;set;}
    public double CloudPercent {get;set;} public double PrecipitationMm {get;set;} public string ConditionText {get;set;}="";
    // 风级（和风 wind.scale；-1 表示无）、降水强度 mm/h 与降水类型（rain/snow/none）。
    public int WindScale {get;set;}=-1; public double PrecipRateMmH {get;set;} public string PrecipType {get;set;}="";
    // 通用国际标准 AQI（qaqi，-1 表示无）：国内同时有中国标准时并列展示。
    // 空气质量：-1 表示来源没有提供，界面据此隐藏。
    public int Aqi {get;set;}=-1; public int Pm25 {get;set;}=-1; public int Pm10 {get;set;}=-1; public int AqiLevel {get;set;}=-1; public string AqiText {get;set;}="";
    public int AqiGlobal {get;set;}=-1;
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
    public string[] MoonriseTimes {get;set;}=[]; public string[] MoonsetTimes {get;set;}=[];
    // 晨昏蒙影三级（天文/航海/民用）、太阳正午/午夜、月过中天/月下点：和风 astro 独有，Open-Meteo 无此数据。
    public string[] AstronomicalDawnTimes {get;set;}=[]; public string[] NauticalDawnTimes {get;set;}=[]; public string[] CivilDawnTimes {get;set;}=[]; public string[] CivilDuskTimes {get;set;}=[];
    public string[] NauticalDuskTimes {get;set;}=[]; public string[] AstronomicalDuskTimes {get;set;}=[]; public string[] SolarNoonTimes {get;set;}=[]; public string[] SolarMidnightTimes {get;set;}=[];
    public string[] MoonTransitTimes {get;set;}=[]; public string[] MoonUnderfootTimes {get;set;}=[];
    // 日均温与白天云量（和风独有）；夜间分段（夜间天气/湿度/风/降水/云量）。
    public double[] DayAvgC {get;set;}=[]; public double[] DayCloudPercent {get;set;}=[];
    public int[] DayNightCodes {get;set;}=[]; public string[] DayNightConditions {get;set;}=[]; public int[] DayNightHumidity {get;set;}=[];
    public double[] DayNightWindMaxKmh {get;set;}=[]; public double[] DayNightGustMaxKmh {get;set;}=[]; public double[] DayNightWindDirDeg {get;set;}=[]; public string[] DayNightWindDir {get;set;}=[];
    public double[] DayNightPrecipMm {get;set;}=[]; public int[] DayNightRainProb {get;set;}=[]; public double[] DayNightCloudPercent {get;set;}=[];
    // 扩展域：分钟降水 / 预警 / 生活指数。台风来袭由「预警」域覆盖，不再单列台风/辐照/海洋。
    public string[] MinuteTimes {get;set;}=[]; public double[] MinutePrecip {get;set;}=[]; public string MinuteSummary {get;set;}="";
    public WarningItem[] Warnings {get;set;}=[];
    public IndexItem[] Indices {get;set;}=[];
    // 每个数据域实际贡献的来源，例如 Daily → "中国天气网 + Open-Meteo"。
    public Dictionary<string,string> Sources {get;set;}=[];
    // 当前读数逐字段的来源（字段名 → 来源 id），供详情页逐项着色标注；来源未给出时为空字典，界面不标注。
    public Dictionary<string,string> FieldSources {get;set;}=[];
    // 每个域参与调度的全部来源（含未提供数据的，保序去重），供详情页把每一家来源都标注出来。
    public Dictionary<string,string[]> DomainSources {get;set;}=[];
    // 某来源本次是否实际提供了数据（任一域），未提供的在详情页以灰色标注"未提供数据"。
    public Dictionary<string,bool> SourceProvided {get;set;}=[];
}