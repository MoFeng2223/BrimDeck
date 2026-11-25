using System.Globalization;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 国内/国外判断与统一的城市名拆分。
internal static class Geo
{
    // 中国范围框（含港澳台与南海诸岛）：决定国内来源能否服务这座城市，
    // 也用于判断和风分钟降水这类「仅中国坐标」的接口是否值得请求。
    public static bool IsChina(City city) => city.Latitude is >= 17 and <= 54 && city.Longitude is >= 73 and <= 136;
    public static bool HasChinese(string text) => text.Any(ch => ch is >= '\u4e00' and <= '\u9fff');
    // 城市级名称与省份：都从 City 唯一的完整名称里拆，区县城市取其所属地级市（国内源接口只认城市级）。
    public static (string City, string Province) Parts(City city) => City.GeoPartsOf(city.Name);
    // 中国统一 UTC+8；其他地区按经度估算，仅用于本地日升日落推算的兜底。
    public static double UtcOffsetHours(City city, DateTimeOffset? at = null) => city.TimeZoneId is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone.GetUtcOffset(at ?? DateTimeOffset.UtcNow).TotalHours : IsChina(city) ? 8 : (int)Math.Round(city.Longitude / 15.0);
}

// 各来源的天气代码统一成 Open-Meteo 使用的 WMO code，界面图标与配色只认这一套。
internal static class WeatherCodes
{
    // 中文天气文本（中国天气网）→ WMO。取“转”之前的第一种天气作为当日主天气。
    public static int FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 3;
        string value = text.Split('转')[0].Trim();
        if (value.Contains("雷")) return 95;
        if (value.Contains("暴雨")) return 65;
        if (value.Contains("大雨")) return 63;
        if (value.Contains("中雨")) return 63;
        if (value.Contains("阵雨")) return 80;
        if (value.Contains("雨")) return 61;
        if (value.Contains("暴雪") || value.Contains("大雪")) return 75;
        if (value.Contains("中雪")) return 73;
        if (value.Contains("阵雪")) return 85;
        if (value.Contains("雪")) return 71;
        if (value.Contains("雾")) return 45;
        if (value.Contains("霾") || value.Contains("浮尘") || value.Contains("扬沙") || value.Contains("沙尘")) return 45;
        if (value.Contains("阴")) return 3;
        if (value.Contains("多云")) return 2;
        if (value.Contains("晴")) return 0;
        return 3;
    }

    // 和风天气图标代码 → WMO。
    public static int FromHeFeng(string? code) => code switch
    {
        "100" => 0, "101" or "102" or "103" => 2, "104" => 3,
        "300" or "301" => 80, "302" => 95, "303" or "304" => 96, "305" or "309" => 61,
        "306" or "314" => 63, "307" or "315" => 65, "308" or "316" => 65, "310" or "317" => 65,
        "311" or "312" or "318" => 65, "313" => 66, "404" or "405" or "406" => 77,
        "400" or "408" => 71, "401" or "409" => 73, "402" or "410" or "403" => 75, "407" => 85,
        "500" or "501" or "509" or "510" or "514" or "515" => 45,
        "502" or "511" or "512" or "513" => 45, "503" or "504" or "507" or "508" => 45,
        "900" => 0, "901" => 0, "999" => 3, _ => 3
    };

    // 蒲福风级（"3-4" 级）→ 风速 km/h，取区间的中值。解析不出风级（串里没有数字）返回 null 视为缺失：
    // 非有限值一旦写进数据会占住空位，阻断后续来源补写，来源回退因此失效。
    public static double? FromWindPower(string? power)
    {
        if (string.IsNullOrWhiteSpace(power)) return null;
        var numbers = System.Text.RegularExpressions.Regex.Matches(power, "\\d+").Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length == 0) return null;
        double level = numbers.Average();
        // 蒲福风级对应的风速上界（m/s）：0→0.2, 1→1.5, 2→3.3, ... 12→32.6；取上界的 80% 作为代表值。
        double[] upper = [0.2, 1.5, 3.3, 5.4, 7.9, 10.7, 13.8, 17.1, 20.7, 24.4, 28.4, 32.6, 36.9];
        int index = Math.Clamp((int)Math.Round(level), 0, upper.Length - 1);
        return upper[index] * 0.8 * 3.6;
    }

    // 风速 km/h → 蒲福风级：按各级风速上界（m/s）反向查表，来源没给风级（Open-Meteo）时由界面调用兜底。
    public static int FromWindSpeed(double? kmh)
    {
        if (kmh is not { } speed || double.IsNaN(speed)) return -1;
        double ms = speed / 3.6;
        double[] upper = [0.2, 1.5, 3.3, 5.4, 7.9, 10.7, 13.8, 17.1, 20.7, 24.4, 28.4, 32.6, double.MaxValue];
        for (int i = 0; i < upper.Length; i++) if (ms <= upper[i]) return i;
        return 12;
    }

    // 风向度数 → 8 方位角度（45° 一档，0/45/…/315）。边界按远离零取整，和图标旋转共用同一档。
    public static double Compass8Angle(double deg)
    {
        if (double.IsNaN(deg)) return double.NaN;
        double d = (deg % 360 + 360) % 360;
        return Math.Round(d / 45, MidpointRounding.AwayFromZero) * 45 % 360;
    }

    // 风向文字优先的 8 方位角度：来源给了 compass/中文方位时按文字画箭头，缺失时才按度数分档。
    // 这样「西南风」一定指向左下，不会被 198° 这种 16 方位边界值重新量化成南。
    public static double Wind8Angle(string? text, double deg)
    {
        double fromText = DegreesFromText(text);
        return double.IsNaN(fromText) ? Compass8Angle(deg) : Compass8Angle(fromText);
    }

    // 风向度数 → 8 方位文字（仅 tooltip 等紧凑展示用）。
    public static string DegreeToCompass(double deg)
    {
        string[] names = [Loc.T("北", "N"), Loc.T("东北", "NE"), Loc.T("东", "E"), Loc.T("东南", "SE"),
                          Loc.T("南", "S"), Loc.T("西南", "SW"), Loc.T("西", "W"), Loc.T("西北", "NW")];
        double angle = Compass8Angle(deg);
        if (double.IsNaN(angle)) return "";
        return names[((int)angle / 45) % 8];
    }

    public static string Text(int code) => code switch
    {
        0 => Loc.T("晴", "Clear"), 1 or 2 => Loc.T("多云", "Cloudy"), 3 => Loc.T("阴", "Overcast"),
        45 or 48 => Loc.T("雾", "Fog"), >= 51 and <= 67 => Loc.T("雨", "Rain"), >= 71 and <= 77 => Loc.T("雪", "Snow"),
        >= 80 and <= 82 => Loc.T("阵雨", "Showers"), 85 or 86 => Loc.T("阵雪", "Snow showers"), >= 95 => Loc.T("雷雨", "Thunderstorm"),
        _ => Loc.T("多云", "Cloudy")
    };

    public static string AqiText(int aqi) => aqi switch
    {
        < 0 => "", <= 50 => Loc.T("优", "Good"), <= 100 => Loc.T("良", "Moderate"), <= 150 => Loc.T("轻度污染", "Light"),
        <= 200 => Loc.T("中度污染", "Moderate pollution"), <= 300 => Loc.T("重度污染", "Heavy"), _ => Loc.T("严重污染", "Severe")
    };

    public static string AqiColor(int aqi) => aqi switch
    {
        < 0 => "#9B9BA3", <= 50 => "#5CC85C", <= 100 => "#F2C94C", <= 150 => "#F2994A", <= 200 => "#EB5757", <= 300 => "#9B51E0", _ => "#B12E3A"
    };

    // 中文/英文风向 → 角度，供箭头与文字保持同一档。
    public static double DegreesFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;
        string lower = text.ToLowerInvariant();
        if (text.Contains("东北") || lower.Contains("ne")) return 45;
        if (text.Contains("东南") || lower.Contains("se")) return 135;
        if (text.Contains("西南") || lower.Contains("sw")) return 225;
        if (text.Contains("西北") || lower.Contains("nw")) return 315;
        if (text.Contains('北') || lower is "n" or "north") return 0;
        if (text.Contains('东') || lower is "e" or "east") return 90;
        if (text.Contains('南') || lower is "s" or "south") return 180;
        if (text.Contains('西') || lower is "w" or "west") return 270;
        return double.NaN;
    }
}

// JSON 小工具：容忍 number/string 混用，缺失返回 null。
internal static class Json
{
    public static JsonElement? Get(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;
    public static string? Text(JsonElement root, string name) => Get(root, name) is { } value && value.ValueKind switch { JsonValueKind.String => true, JsonValueKind.Number => true, _ => false } ? value.ToString() : null;
    public static double? Number(JsonElement root, string name)
    {
        if (Get(root, name) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        return double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    public static int? Integer(JsonElement root, string name) => Number(root, name) is { } value ? (int)Math.Round(value) : null;
    public static string Esc(string text) => Uri.EscapeDataString(text);
}
