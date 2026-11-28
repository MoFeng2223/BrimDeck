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
    // 和风 expireTime：到点即失效；来源没给（null）时按仍有效处理。
    public DateTimeOffset? ExpireTime { get; set; }
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
    internal CurrentPart Copy() => (CurrentPart)MemberwiseClone();
    public DateTimeOffset? ObservedAt;
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
    public string? Standard { get; set; }         // AQI 标准代码（us_aqi / cn-mee / qaqi），文案与颜色不能混用
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
    public bool HasData() => Temperature.HasValue || FeelsLike.HasValue || Humidity.HasValue || Code.HasValue
        || RainProb.HasValue || PrecipMm.HasValue || WindKmh.HasValue || WindGustKmh.HasValue || WindDirDeg.HasValue
        || PressureHpa.HasValue || Uv.HasValue || DewPointC.HasValue || CloudPercent.HasValue || VisibilityKm.HasValue || Aqi.HasValue;
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
    // 太阳正午、月过中天（和风 astro 独有，Open-Meteo 无）：天文轨道曲线的峰值时刻。
    // 晨昏蒙影三级、太阳午夜、月下点无视图消费，已从契约中移除。
    public string? SolarNoon;
    public string? MoonTransit;
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
// 契约：快照直接持有来源层的结构化对象（CurrentPart / AirPart / HourPoint[] / DayPoint[] / MinutePoint[]）。
// 来源没给的字段是 null（数值/整数）或空串（文字），视图出口统一转成 --。没有 NaN/-1 哨兵、没有平行数组对齐、
// 没有「结构化 → 拍平 → 视图按下标还原」的转换层——新增字段只需在来源模型加一处。
internal sealed class WeatherSnapshot
{
    // Name 是城市的完整层级（洪山区 · 武汉市 · 湖北省 · 中国），与 City.Name 同源，自动定位城市名与设置页都用它。
    public string Name {get;set;}=""; public DateTimeOffset FetchedAt {get;set;} public string Today {get;set;}="";
    // 快照实际归属城市的历史键（city.ToString()）。快照一律按真实城市键存（自动定位也一样，
    // "auto" 只是 State.AutoKey 指向最新定位结果的映射），逐时历史与快照同键，手动选同一城市可复用。
    public string CityKey {get;set;}="";
    // 城市所在地的 UTC 偏移（分钟）：来源/IANA 时区都没给时按经度估算的结果，MinValue = 未知。
    public int UtcOffsetMinutes {get;set;} = int.MinValue;
    // 城市 IANA 时区：优先按当前时刻解析偏移，夏令时切换后离线快照也能认对「今天」。
    public string TimeZoneId {get;set;}="";

    // 结构化数据：与来源层同一个契约，聚合器直接赋值，不做拍平投影。
    public CurrentPart Current {get;set;} = new();
    public AirPart? Air {get;set;}
    public HourPoint[] Hours {get;set;}=[];
    public DayPoint[] Days {get;set;}=[];
    public MinutePoint[] Minutes {get;set;}=[];
    public string MinuteSummary {get;set;}="";
    public WarningItem[] Warnings {get;set;}=[];
    public IndexItem[] Indices {get;set;}=[];
    // 每个数据域实际贡献的来源，例如 Daily → "和风天气 + 中国天气网"；页脚主来源取这里的 Daily/Current。
    public Dictionary<string,string> Sources {get;set;}=[];

    // 当前实况的只读代理：视图沿用的平铺访问名，实际读结构化 Current，null 即缺失（出口显示 --）。
    // 零成本 —— 直接转发，不分配、不转换。FeelsLike 保留「体感缺失取气温」的既有语义。
    public double? Temperature => Current.Temperature;
    public double? FeelsLike => Current.FeelsLike ?? Current.Temperature;
    public int? HumidityPercent => Current.Humidity;
    public double? WindKmh => Current.WindKmh;
    public double? WindGustKmh => Current.WindGustKmh;
    public double? WindDirectionDeg => Current.WindDirDeg;
    public string WindDirection => Current.WindDir ?? "";
    public double? PressureHpa => Current.PressureHpa;
    public double? VisibilityKm => Current.VisibilityKm;
    public double? DewPointC => Current.DewPointC;
    public double? UvIndex => Current.Uv;
    public double? CloudPercent => Current.CloudPercent;
    public double? PrecipitationMm => Current.PrecipMm;
    public int? WindScale => Current.WindScale;
    public double? PrecipRateMmH => Current.PrecipRateMmH;
    public string PrecipType => Current.PrecipType ?? "";
    public int? WeatherCode => Current.Code;
    public bool IsDay => Current.IsDay ?? true;   // 昼夜兜底由聚合器按日出日落算好后写回 Current.IsDay。

    // 空气质量的只读代理，同理读结构化 Air。
    public int? Aqi => Air?.Aqi;
    public int? AqiGlobal => Air?.AqiGlobal;
    public int? Pm25 => Air?.Pm25;
    public int? Pm10 => Air?.Pm10;
    public int? AqiLevel => Air?.Level;
    public string AqiText => Air?.Text ?? "";
    public string AqiStandard => Air?.Standard ?? "";
    public string AirPrimary => Air?.PrimaryName ?? Air?.Primary ?? "";
    public string AirEffect => Air?.HealthEffect ?? "";
    public string AirAdvice => Air?.HealthGeneral ?? "";
    public string AirAdviceSensitive => Air?.HealthSensitive ?? "";

    // 只需替换扩展域时的浅拷贝：数组按引用共享，调用方只改写自己替换的属性。
    internal WeatherSnapshot ShallowCopy() => (WeatherSnapshot)MemberwiseClone();
}
