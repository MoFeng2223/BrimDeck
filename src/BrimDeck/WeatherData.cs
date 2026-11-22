using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrimDeck.Core;
namespace BrimDeck;
// Data layer: weather fetching, city search, location, JSON parsing and snapshot persistence. No WPF controls or view lifetime.
// City and WeatherSettings live in BrimDeck.Core because they are saved in settings.json; this file keeps only downloaded
// snapshots in weather/snapshot/.
internal static class WeatherData
{
    // 配置全部来自 settings.json；本类只把其中的天气字段投影成视图使用的 State，并另存下载的快照。
    internal sealed class State
    {
        private readonly Func<DeckSettings> _settings;
        internal State(Func<DeckSettings> settings) { _settings = settings; }
        public List<City> Cities { get => _settings().WeatherCities; set => _settings().WeatherCities = value; }
        public int Selected { get => _settings().WeatherSelected; set => _settings().WeatherSelected = value; }
        public WeatherSettings Schedule { get => _settings().Weather; set => _settings().Weather = value; }
        public Dictionary<string, WeatherSnapshot> Cache { get; set; } = [];
        // 自动定位指针：最近一次定位到的实际城市键。快照一律按真实城市键存（定位映射选择的城市/区），
        // "auto" 不再是数据键，只保留这个指向，供重启后免定位直接命中缓存、设置页「当前位置」行取数。
        public string AutoKey { get; set; } = "";
    }
    private sealed class SnapshotFile { public string Key {get;set;}=""; public WeatherSnapshot? Value {get;set;} }
    private static readonly HttpClient Http=new() {Timeout=TimeSpan.FromSeconds(20)};
    // 快照用 NaN 表示来源未提供的数值；命名浮点字面量让缓存可以安全写入并还原，缩进让缓存可读。
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
    internal static State Load(Func<DeckSettings> settings)
    {
        Start();
        var state=new State(settings);
        foreach(var (key,snapshot) in ReadSnapshots())state.Cache[key]=snapshot;
        // 自动定位指针（weather/auto.json，与快照分开放，避免混进快照枚举）：
        // 快照都按真实城市键存，"auto" 只是映射，重启后靠它免定位命中缓存。
        try
        {
            string pointer=Path.Combine(WeatherFiles.Root,"weather","auto.json");
            if(File.Exists(pointer)&&JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(pointer),WeatherJson) is { Key.Length:>0 } entry)state.AutoKey=entry.Key;
        }
        catch { }
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
    // 只写下载快照与请求计数。用户在设置页改动的 cities/selected/schedule 属于 DeckSettings，
    // 由 SettingsWindow/App 的常规设置保存流程写回 settings.json，不再另存 weather.json。
    internal static void Save(State state)
    {
        lock(SnapLock)
            foreach(var (key,snapshot) in state.Cache)
            {
                // 版本检查在前：设置页驻留的旧快照（A）不得因配置保存把已被天气页新快照（B）覆盖的历史写回去。
                if(Written.TryGetValue(key,out var at)&&snapshot.FetchedAt<=at)continue;
                if(!WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Snapshots,WeatherFiles.Name(key,".json")),JsonSerializer.Serialize(new SnapshotFile{Key=key,Value=snapshot},WeatherJson)))continue;
                Written[key]=snapshot.FetchedAt;
                // 快照确实被接受后才并入本地历史；历史键=快照的真实城市键（CityKey，Merge 时写入）。
                WeatherHourHistory.Record(snapshot.CityKey,snapshot);
            }
        // 请求计数随保存一起落盘：重启后不会把当天额度重新算满。
        RequestGate.Flush();
        // 自动定位指针随快照一起落盘："auto" 只是映射，重启后靠它免定位命中缓存。
        if(state.AutoKey.Length>0)
            WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Root,"weather","auto.json"),JsonSerializer.Serialize(new SnapshotFile{Key=state.AutoKey},WeatherJson));
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

// 本地逐时历史：每次快照落盘时把其中的整点数据按「城市 + 整点」累积，供下一个窗口的过去小时使用。
// 和风逐小时只有未来、时光机不含今天，过去的小时只能靠本机历史；本地没有就留空，绝不联网补。
internal static class WeatherHourHistory
{
    // 保留最近 48 小时：足以覆盖「过去 10 小时」，也给隔天再打开留余量。
    private const int KeepHours = 48;
    private static readonly string Root = Path.Combine(WeatherFiles.Root, "weather", "hour-history");
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly object Lock = new();
    private static readonly Dictionary<string, Dictionary<string, HourPoint>> Memory = new(StringComparer.Ordinal);
    private static int _started;

    private sealed class FileFormat { public string Key { get; set; } = ""; public List<HourPoint> Hours { get; set; } = []; }

    private static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        WeatherFiles.Ensure(Root);
        string[] files;
        try { files = Directory.GetFiles(Root, "*.json"); } catch { return; }
        foreach (var file in files)
        {
            try
            {
                if (JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(file), Json) is not { } entry) continue;
                var dict = new Dictionary<string, HourPoint>(StringComparer.Ordinal);
                foreach (var hour in entry.Hours ?? []) if (!string.IsNullOrWhiteSpace(hour.Time)) dict[hour.Time] = hour;
                if (dict.Count > 0) Memory[entry.Key] = dict;
            }
            catch { }
        }
    }

    // 读出该城市已累积的整点数据（副本，调用方改不到内存库）。
    internal static Dictionary<string, HourPoint> Read(string key)
    {
        Start();
        lock (Lock) return Memory.TryGetValue(key, out var dict) ? new Dictionary<string, HourPoint>(dict, StringComparer.Ordinal) : [];
    }

    // 把一份快照里的整点数据并入历史；以最新整点为基准保留最近 KeepHours 小时，写盘失败不影响刷新。
    internal static void Record(string key, WeatherSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        Start();
        lock (Lock)
        {
            if (!Memory.TryGetValue(key, out var dict)) Memory[key] = dict = new Dictionary<string, HourPoint>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.HourTimes.Length; i++)
            {
                string time = snapshot.HourTimes[i];
                if (string.IsNullOrWhiteSpace(time)) continue;
                dict[time] = new HourPoint
                {
                    Time = time,
                    Temperature = At(snapshot.HourTempsC, i), FeelsLike = At(snapshot.HourFeelsLikeC, i),
                    Humidity = Int(snapshot.HourHumidity, i), Code = Int(snapshot.HourCodes, i), RainProb = Int(snapshot.HourRainProb, i),
                    PrecipMm = At(snapshot.HourPrecipMm, i), WindKmh = At(snapshot.HourWindKmh, i), WindGustKmh = At(snapshot.HourWindGustKmh, i),
                    WindDirDeg = At(snapshot.HourWindDirDeg, i), PressureHpa = At(snapshot.HourPressureHpa, i), Uv = At(snapshot.HourUv, i),
                    DewPointC = At(snapshot.HourDewPointC, i), CloudPercent = At(snapshot.HourCloudPercent, i), VisibilityKm = At(snapshot.HourVisibilityKm, i)
                };
            }
            if (dict.Count == 0) return;
            var newest = dict.Keys.Select(ParseTime).DefaultIfEmpty(DateTime.MinValue).Max();
            string cutoff = newest.AddHours(-KeepHours).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var old in dict.Keys.Where(t => string.CompareOrdinal(t, cutoff) < 0).ToList()) dict.Remove(old);
            try
            {
                WeatherFiles.WriteAtomic(Path.Combine(Root, WeatherFiles.Name(key, ".json")),
                    JsonSerializer.Serialize(new FileFormat { Key = key, Hours = [.. dict.Values.OrderBy(p => p.Time)] }, Json));
            }
            catch { }
        }
    }

    private static DateTime ParseTime(string t)
        => DateTime.TryParseExact(t, "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var value) ? value : DateTime.MinValue;
    private static double? At(double[] values, int i) => i >= 0 && i < values.Length && !double.IsNaN(values[i]) ? values[i] : null;
    private static int? Int(int[] values, int i) => i >= 0 && i < values.Length && values[i] >= 0 ? values[i] : null;
}
// Strongly-typed weather model; the only data contract between the data layer and the views. Views never touch JSON.
// 所有数组都以 DayDates / HourTimes 对齐；来源没有提供的数值用 NaN（数值）或 -1（整数）表示，界面自行隐藏。
internal sealed class WeatherSnapshot
{
    public string Name {get;set;}=""; public string FullName {get;set;}=""; public DateTimeOffset FetchedAt {get;set;} public string Today {get;set;}="";
    // 快照实际归属城市的历史键（city.ToString()）。快照一律按真实城市键存（自动定位也一样，
    // "auto" 只是 State.AutoKey 指向最新定位结果的映射），逐时历史与快照同键，手动选同一城市可复用。
    public string CityKey {get;set;}="";
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