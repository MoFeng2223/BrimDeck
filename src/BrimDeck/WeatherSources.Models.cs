using BrimDeck.Core;

namespace BrimDeck;

// 分钟降水：每 5 分钟一个点（和风 /v7/minutely/5m、Open-Meteo minutely_15 折算）
internal sealed class MinutePoint
{
    public string Time { get; set; } = "";
    public double? Precip { get; set; }
}

// 气象预警：一条预警的标题、正文、等级、类型与生效区间
internal sealed class WarningItem
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Level { get; set; } = "";
    public string Type { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
}

// 生活指数：名称、等级、类别与说明
internal sealed class IndexItem
{
    public string Date { get; set; } = "";
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
    public string? Condition;
    // 风级（和风 wind.scale，蒲福级数；Open-Meteo 无，界面由风速换算兜底）。
    public int? WindScale;
    // 降水强度（mm/h）与降水类型（rain/snow/ice/mixed/none/unknown），和风 precipitation.intensity/type。
    public double? PrecipRateMmH;
    public string? PrecipType;
}

// 空气质量：AQI、首要污染物、健康建议与全部污染物浓度。
// Pollutants/Units 用污染物代码（pm2p5、o3…）作键，来源给几项就存几项，界面按需取用。
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
    public Dictionary<string, string> Units { get; set; } = [];
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
    public int? WindScale { get; set; }
    public double? PrecipRateMmH { get; set; }
    public string? PrecipType { get; set; }
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
