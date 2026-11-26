using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrimDeck.Core;
namespace BrimDeck;
// 数据层：天气取数入口、城市搜索、自动定位、快照落盘与逐时本地历史，不包含任何 WPF 控件或视图生命周期。
// City 与 WeatherSettings 由于要存进 settings.json，定义在 BrimDeck.Core；本文件只把下载到的快照放在 weather/snapshot/。
internal static class WeatherData
{
    internal sealed class State
    {
        private readonly Func<DeckSettings> _settings;
        internal State(Func<DeckSettings> settings) { _settings = settings; }
        public List<City> Cities { get => _settings().WeatherCities; set => _settings().WeatherCities = value; }
        public int Selected { get => _settings().WeatherSelected; set => _settings().WeatherSelected = value; }
        public WeatherSettings Schedule { get => _settings().Weather; set => _settings().Weather = value; }
        public IReadOnlyDictionary<string, WeatherSnapshot> Cache => Snapshots;
        // 自动定位指针：最近一次定位到的实际城市键。快照一律按真实城市键存（定位映射选择的城市/区），
        // "auto" 只作指向，供重启后免定位直接命中缓存、设置页「当前位置」行取数。

        public string AutoKey { get => _autoKey.EndsWith("|" + Schedule.SourceFor(null), StringComparison.Ordinal) ? _autoKey : ""; }
        public string KeyFor(City city) => CacheKey(city, Schedule.SourceFor(city));
    }
    private sealed class SnapshotFile { public string Key {get;set;}=""; public WeatherSnapshot? Value {get;set;} }
    private static readonly HttpClient Http=new() {Timeout=TimeSpan.FromSeconds(20)};
    // 快照用 NaN 表示来源未提供的数值；命名浮点字面量让缓存可以安全写入并还原，缩进让缓存可读。
    private static readonly JsonSerializerOptions WeatherJson=new() {WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull};
    // 已落盘的快照时间：同一条数据没有更新就不再重复写盘，刷新一座城只碰一个文件。
    private static readonly object SnapLock=new();
    private static readonly Dictionary<string,DateTimeOffset> Written=new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WeatherSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static string _autoKey = "";
    internal static event Action? SnapshotChanged;
    private static int _started;
    // 进程内只做一次：清理过期缓存条目、恢复当日请求计数。
    private static void Start()
    {
        if(Interlocked.Exchange(ref _started,1)==1)return;
        WeatherFiles.Ensure(WeatherFiles.Snapshots);
        WeatherCache.Prune(TimeSpan.FromDays(14));
        RequestGate.Restore();
        foreach(var (key,snapshot) in ReadSnapshots())Snapshots[key]=snapshot;
        // 自动定位指针（weather/auto.json，与快照分开放，避免混进快照枚举）：
        // 快照都按真实城市键存，"auto" 只是映射，重启后靠它免定位命中缓存。
        try
        {
            string pointer=Path.Combine(WeatherFiles.Root,"weather","auto.json");
            if(File.Exists(pointer)&&JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(pointer),WeatherJson) is { Key.Length:>0 } entry)_autoKey=entry.Key;
        }
        catch { }
    }
    internal static State Load(Func<DeckSettings> settings)
    {
        Start();
        return new State(settings);
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
    internal static void SaveSnapshot(string key,WeatherSnapshot snapshot,string? autoKey=null)
    {
        lock(SnapLock)
        {
            if (!Snapshots.TryGetValue(key, out var previous) || snapshot.FetchedAt >= previous.FetchedAt)
                Snapshots[key] = snapshot;
            if(!(Written.TryGetValue(key,out var at)&&snapshot.FetchedAt<=at))
            {
                if(WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Snapshots,WeatherFiles.Name(key,".json")),JsonSerializer.Serialize(new SnapshotFile{Key=key,Value=snapshot},WeatherJson)))
                {
                    Written[key]=snapshot.FetchedAt;
                    // 快照确实被接受后才并入本地历史；历史键=快照的真实城市键（CityKey，Merge 时写入）。
                    WeatherHourHistory.Record(snapshot.CityKey,snapshot);
                }
            }
            if(autoKey is {Length:>0})
            {
                _autoKey = autoKey;
                WeatherFiles.WriteAtomic(Path.Combine(WeatherFiles.Root,"weather","auto.json"),JsonSerializer.Serialize(new SnapshotFile{Key=autoKey},WeatherJson));
            }
        }
        RequestGate.Flush();
        SnapshotChanged?.Invoke();
    }
    // 城市搜索：配置了和风 Key/Host 就只用和风 GeoAPI——能精确命中区县（搜「洪山区」得「洪山区 · 武汉市 · 湖北省」），
    // 返回的 LocationID 与和风时光机同源。请求失败不退回 Open-Meteo，避免同一次搜索混用两套层级和精度。
    // 未配置和风时才用 Open-Meteo（全球地名库，中文覆盖一般）。
    // 拼音里的分隔撇号（chang'feng）会被和风当成分词符，先试去掉撇号的写法，失败再用原串。
    internal static async Task<List<City>> Search(string query, WeatherSettings settings, CancellationToken token)
    {
        string raw = query.Trim();
        var attempts = new List<string> { raw };
        if (raw.Any(ch => ch is '\'' or '’') && raw.All(ch => char.IsAsciiLetter(ch) || ch is '\'' or '’' or '-'))
        {
            string plain = new(raw.Where(ch => ch is not ('\'' or '’')).ToArray());
            if (plain.Length > 0 && plain != raw) attempts.Insert(0, plain);
        }
        if (settings.HeFengKey.Trim().Length > 0 && settings.HeFengHost.Trim().Length > 0)
        {
            bool failed = false;
            foreach (var attempt in attempts)
            {
                try
                {
                    var found = await SearchHeFeng(attempt, settings, token);
                    if (found.Count > 0) return found;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { failed = true; }
            }
            // 区分「确实没有这个城市」与「这次请求失败」：两者都返回空列表会让界面把超时/限流
            // 显示成「未找到」，看起来就是搜索偶尔失灵。失败交给界面提示重试。
            if (failed) throw new HttpRequestException("QWeather search unavailable");
            return [];
        }
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=10&language=" + (Loc.IsEnglish ? "en" : "zh") + "&name=" + Uri.EscapeDataString(raw), token));
        var cities = new List<City>();
        if (doc.RootElement.TryGetProperty("results", out var list))
            foreach (var c in list.EnumerateArray())
                cities.Add(new City(
                    City.Compose(Json.Text(c, "name"), Json.Text(c, "admin2"), Json.Text(c, "admin1"), Json.Text(c, "country")),
                    c.GetProperty("latitude").GetDouble(), c.GetProperty("longitude").GetDouble()) { TimeZoneId = Json.Text(c, "timezone") });
        return cities;
    }
    // 和风 GeoAPI v2 city/lookup：按地名或「经度,纬度」坐标反查最多 10 个，结构化解析省/市/国家/城市 id。
    // 和风明确禁止缓存 GeoAPI 数据，因此这里不做任何落盘/内存缓存：防抖后的每一次查询都直接请求。
    // 错误响应（HTTP 非 2xx 或 code != 200）直接当作空结果，调用方据此区分「没有这个城市」与请求失败。
    private static async Task<List<City>> SearchHeFeng(string query, WeatherSettings settings, CancellationToken token)
    {
        string host = settings.HeFengHost.Trim().TrimEnd('/');
        // 与天气请求同一策略：Key 走请求头，强制 HTTPS，绝不落明文。
        host = host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + host[7..]
             : host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host;
        string lang = Loc.IsEnglish ? "en" : "zh";
        string body = "";
        // 搜索用独立闸门通道：天气取数（尤其时光机一轮 10 次请求）触发 429/失败熔断时，
        // 不该把城市搜索一起停掉——否则用户搜新词会全部落空，看起来像搜索坏了。
        // 超时 8 秒：搜索是交互操作，不该让输入框等满 20 秒。
        var result = await WeatherHttp.GetAsync("hefeng", $"{host}/geo/v2/city/lookup?location={Uri.EscapeDataString(query)}&number=10&lang={lang}", token,
            request => request.Headers.TryAddWithoutValidation("X-QW-Api-Key", settings.HeFengKey.Trim()), 8, settings);
        if (result.Body is { Length: > 0 } raw)
        {
            using var probe = JsonDocument.Parse(raw);
            if (!(Json.Text(probe.RootElement, "code") is { Length: > 0 } code && code != "200")) body = raw;
        }
        var cities = new List<City>();
        if (body.Length == 0) return cities;
        using var doc = JsonDocument.Parse(body);
        if (Json.Get(doc.RootElement, "location") is not { ValueKind: JsonValueKind.Array } list) return cities;
        foreach (var item in list.EnumerateArray())
        {
            double? lat = Json.Number(item, "lat"), lon = Json.Number(item, "lon");
            if (Json.Text(item, "name") is not { Length: > 0 } name || lat is null || lon is null) continue;
            cities.Add(new City(
                City.Compose(name, Json.Text(item, "adm2"), Json.Text(item, "adm1"), Json.Text(item, "country")),
                lat.Value, lon.Value, Json.Text(item, "id") ?? "") { TimeZoneId = Json.Text(item, "tz") });
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
        bool hefeng=settings.HeFengKey.Trim().Length>0&&settings.HeFengHost.Trim().Length>0;
        // 阶段一（Windows 定位 → 和风反查区县）与阶段二（IP 定位）分开 try/catch：
        // 反查抛异常时不清空已取得的系统坐标，流程落到 IP 阶段继续——只丢「区县级」精度，不丢定位本身。
        (double Lat,double Lon)? precise=null;
        try {
            if(hefeng)precise=await WindowsLocation.PositionAsync(token);
            if(precise is { } coords)
            {
                try
                {
                    var found=await SearchHeFeng(Math.Round(coords.Lon,3)+","+Math.Round(coords.Lat,3),settings,token);
                    if(found.Count>0)return found[0];
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }
        } catch { }
        try {
            using var doc=JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang="+(Loc.IsEnglish?"en":"zh-CN"),token));
            var root=doc.RootElement;
            if(root.TryGetProperty("success",out var ok)&&ok.ValueKind==JsonValueKind.False)return null;
            if(!root.TryGetProperty("latitude",out var la)||!root.TryGetProperty("longitude",out var lo))return null;
            double lat=la.GetDouble(),lon=lo.GetDouble();
            // 系统坐标在手（反查失败落下来的）不再对 IP 坐标做反查：坐标已是数百米级，
            // 只需要 IP 补城市名；返回时坐标仍用系统定位的精确值。
            if(hefeng&&precise is null)
            {
                var found=await SearchHeFeng(Math.Round(lon,3)+","+Math.Round(lat,3),settings,token);
                if(found.Count>0)
                {
                    // IP 坐标只有城市精度：丢掉反查结果里的区县段与区县 id，不冒充精确到区。
                    return new City(City.WithoutDistrict(found[0].Name), lat, lon) { TimeZoneId = found[0].TimeZoneId };
                }
            }
            if(root.TryGetProperty("city",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} query) {
                var found=await Search(query,settings,token);
                if(found.Count>0)
                {
                    var best=found.OrderBy(c=>Math.Abs(c.Latitude-(precise?.Lat??lat))+Math.Abs(c.Longitude-(precise?.Lon??lon))).First();
                    // 有系统坐标时搜索只用来拿规范的名称与 LocationId，坐标以系统定位为准。
                    return precise is { } coords ? best with { Latitude=coords.Lat,Longitude=coords.Lon } : best;
                }
            }
            var parts=new List<string>();
            if(root.TryGetProperty("city",out name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0} s)parts.Add(s);
            if(root.TryGetProperty("region",out var region)&&region.ValueKind==JsonValueKind.String&&region.GetString() is {Length:>0} r)parts.Add(r);
            if(root.TryGetProperty("country",out var country)&&country.ValueKind==JsonValueKind.String&&country.GetString() is {Length:>0} c&&!parts.Contains(c))parts.Add(c);
            return new City(parts.Count>0?City.Compose([.. parts]):"当前位置",precise?.Lat??lat,precise?.Lon??lon);
        } catch { return null; }
    }
    // 天气聚合入口：当前、24 小时、24 天（历史 10 + 今天 + 未来 13）、日升日落与空气质量由 WeatherAggregator 统一拼装。
    // 历史单独一轮，progress 让界面先画不含历史的版本，历史到了再用返回值整体替换。
    internal static string CacheKey(City city, string sourceId) => city.ToString() + "|" + sourceId;
    internal static async Task<WeatherSnapshot> CurrentAsync(City city, WeatherSettings schedule, string sourceId, CancellationToken token, Action<WeatherSnapshot>? progress = null)
    {
        var snapshot = await WeatherAggregator.FetchAsync(city, schedule, sourceId, token, progress);
        token.ThrowIfCancellationRequested();
        if (snapshot.Sources.Count > 0 || double.IsFinite(snapshot.Temperature)) return snapshot;
        if (Snapshots.TryGetValue(CacheKey(city, sourceId), out var cached)) return cached;
        return snapshot;
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
                    DewPointC = At(snapshot.HourDewPointC, i), CloudPercent = At(snapshot.HourCloudPercent, i), VisibilityKm = At(snapshot.HourVisibilityKm, i),
                    Aqi = Int(snapshot.HourAqi, i)
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
