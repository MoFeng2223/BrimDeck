using BrimDeck.Core;

namespace BrimDeck;

// 分钟降水：每 5 分钟一个点（和风 /v7/minutely/5m、Open-Meteo minutely_15 折算）
internal sealed class MinutePoint
{
    public string Time { get; set; } = "";
    public double? Precip { get; set; }
}

// 气象预警：一条预警的标题、正文、等级与类型
internal sealed class WarningItem
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Level { get; set; } = "";
    public string Type { get; set; } = "";
}

// 生活指数：和风的 type ID、名称、等级、类别与说明
internal sealed class IndexItem
{
    public string Type { get; set; } = "";   // 和风指数 type ID，语言无关；用于排序与等级着色
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
    public string Text { get; set; } = "";
}

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
    // 风级（和风 wind.scale，蒲福级数；Open-Meteo 无，界面由风速换算兜底）。
    public int? WindScale;
    // 降水强度（mm/h）与降水类型（rain/snow/ice/mixed/none/unknown），和风 precipitation.intensity/type。
    public double? PrecipRateMmH;
    public string? PrecipType;
}

// 空气质量：AQI、首要污染物、健康建议与全部污染物浓度。
// Pollutants 用污染物代码（pm2p5、o3…）作键，只存原始数值；名称、单位与排版由视图决定。
internal sealed class AirPart
{
    public int? Aqi { get; set; }
    // 通用国际标准的 AQI（和风 qaqi）：国内来源同时返回中国标准（cn-mee）时并列展示。
    public int? AqiGlobal { get; set; }
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
    // 和风空气质量逐时预报：整点键（yyyy-MM-ddTHH:mm）→ 该小时 AQI，供快照按逐时时刻对齐。
    public Dictionary<string, int> HourAqi { get; set; } = [];
    public bool HasData => Aqi is not null || Pm25 is not null || Pm10 is not null || Pollutants.Count > 0 || HourAqi.Count > 0;
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
    // 该小时的 AQI，来自和风空气质量逐时预报；与天气逐时数据按时刻键合并。
    public int? Aqi { get; set; }
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
    public string? Moonrise;
    public string? Moonset;
    // 晨昏蒙影三级与太阳正午/午夜（和风 astro 独有，Open-Meteo 无）。
    public string? AstronomicalDawn;
    public string? NauticalDawn;
    public string? CivilDawn;
    public string? CivilDusk;
    public string? NauticalDusk;
    public string? AstronomicalDusk;
    public string? SolarNoon;
    public string? SolarMidnight;
    // 月过中天/月下点：月亮最高与最低时刻。
    public string? MoonTransit;
    public string? MoonUnderfoot;
    // 日均温（和风 temperatureAvg；Open-Meteo 无）。
    public double? AvgTempC;
    // 白天云量（和风 daytime.cloudCover）。
    public double? CloudPercent;
    // 夜间分段（和风 nighttime）：夜间天气、湿度、风、降水与云量，白天数据取上方字段。
    public int? NightCode;
    public string? NightCondition;
    public int? NightHumidity;
    public double? NightWindMaxKmh;
    public double? NightGustMaxKmh;
    public double? NightWindDirDeg;
    public string? NightWindDir;
    public double? NightPrecipMm;
    public int? NightRainProb;
    public double? NightCloudPercent;
}

// 一个来源一次请求能拿到的全部数据；聚合器按域挑选。
internal sealed class WeatherPart
{
    public CurrentPart? Current { get; set; }
    public AirPart? Air { get; set; }
    public List<HourPoint>? Hours { get; set; }
    public List<DayPoint>? Days { get; set; }
    // 扩展载荷：分钟/预警/指数；台风由预警域覆盖。
    public List<MinutePoint>? Minutes { get; set; }
    // 分钟降水的文字总结（如「未来两小时无降水」），和风 /v7/minutely/5m 顶层 summary。
    public string? MinuteSummary { get; set; }
    public List<WarningItem>? Warnings { get; set; }
    public List<IndexItem>? Indices { get; set; }
    // 城市所在时区（Open-Meteo 的 timezone 字段），用于确定“当地今天”和小时窗口。
    public string? TimeZone { get; set; }
}

// Strongly-typed weather model; the only data contract between the data layer and the views. Views never touch JSON.
// 所有数组都以 DayDates / HourTimes 对齐；来源没有提供的数值用 NaN（数值）或 -1（整数）表示，界面自行隐藏。
// 这是来源与聚合契约的最终产物，和上面的来源分段模型放在一起，找契约不必再翻到 WeatherData.cs。
internal sealed class WeatherSnapshot
{
    // Name 是城市的完整层级（洪山区 · 武汉市 · 湖北省 · 中国），与 City.Name 同源，自动定位城市名与设置页都用它。
    public string Name {get;set;}=""; public DateTimeOffset FetchedAt {get;set;} public string Today {get;set;}="";
    // 快照实际归属城市的历史键（city.ToString()）。快照一律按真实城市键存（自动定位也一样，
    // "auto" 只是 State.AutoKey 指向最新定位结果的映射），逐时历史与快照同键，手动选同一城市可复用。
    public string CityKey {get;set;}="";
    // 城市所在地的 UTC 偏移（分钟）：逐时「现在」列要按城市时钟认，查国外城市时不能用本机时间。MinValue = 未知。
    public int UtcOffsetMinutes {get;set;} = int.MinValue;
    // 当前时刻
    public double Temperature {get;set;} public double FeelsLike {get;set;} public int HumidityPercent {get;set;} public double WindKmh {get;set;} public int WeatherCode {get;set;} public bool IsDay {get;set;}
    public double WindGustKmh {get;set;} public double WindDirectionDeg {get;set;} public string WindDirection {get;set;}="";
    public double PressureHpa {get;set;} public double VisibilityKm {get;set;} public double DewPointC {get;set;} public double UvIndex {get;set;}
    public double CloudPercent {get;set;} public double PrecipitationMm {get;set;}
    // 风级（和风 wind.scale；-1 表示无）、降水强度 mm/h 与降水类型（rain/snow/ice/mixed/none/unknown）。
    public int WindScale {get;set;}=-1; public double PrecipRateMmH {get;set;} public string PrecipType {get;set;}="";
    // 空气质量：-1 表示来源没有提供，界面据此隐藏；AqiText 是等级文字（优/良/…），界面按「等级 数值」展示。
    public int Aqi {get;set;}=-1; public int Pm25 {get;set;}=-1; public int Pm10 {get;set;}=-1; public int AqiLevel {get;set;}=-1; public string AqiText {get;set;}="";
    // 通用国际标准 AQI（qaqi，-1 表示无）：国内同时有中国标准时并列展示。
    public int AqiGlobal {get;set;}=-1;
    // 空气详情：首要污染物、健康影响与两类人群建议。快照只运原始数据，展示文案由视图自行排版。
    public string AirPrimary {get;set;}=""; public string AirEffect {get;set;}=""; public string AirAdvice {get;set;}=""; public string AirAdviceSensitive {get;set;}="";
    // 当日各时段（24 个）
    public string[] HourTimes {get;set;}=[]; public double[] HourTempsC {get;set;}=[]; public int[] HourHumidity {get;set;}=[]; public int[] HourCodes {get;set;}=[]; public int[] HourRainProb {get;set;}=[];
    public int[] HourAqi {get;set;}=[];   // 和风空气质量逐时预报，-1 表示该小时无数据；与 HourTimes 对齐
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
    // 扩展域：分钟降水 / 预警 / 生活指数
    public string[] MinuteTimes {get;set;}=[]; public double?[] MinutePrecip {get;set;}=[]; public string MinuteSummary {get;set;}="";
    public WarningItem[] Warnings {get;set;}=[];
    public IndexItem[] Indices {get;set;}=[];
    // 每个数据域实际贡献的来源，例如 Daily → "和风天气 + 中国天气网"；页脚主来源取这里的 Daily/Current。
    public Dictionary<string,string> Sources {get;set;}=[];
}
