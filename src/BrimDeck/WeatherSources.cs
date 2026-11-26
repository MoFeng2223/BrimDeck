using BrimDeck.Core;

namespace BrimDeck;

// 来源注册表与能力接口。实现按主题拆分在同目录：
//   WeatherSources.Models.cs     传输模型（来源与聚合层的数据契约）
//   WeatherSources.Aggregator.cs 按域取数并合并成 WeatherSnapshot
//   WeatherSources.OpenMeteo.cs / WeatherSources.ComCn.cs / WeatherSources.HeFeng.cs
//   WeatherSources.Tools.cs      Geo / WeatherCodes / Json 纯函数
// WeatherDomain 与 WeatherSettings 定义在 BrimDeck.Core：它们是 settings.json 的一部分。

internal sealed record WeatherSourceInfo(string Id, string Name, WeatherDomain[] Domains);

internal interface IWeatherSource
{
    WeatherSourceInfo Info { get; }
    bool CanServe(City city, WeatherSettings settings);
    // need 是本次真正缺的域：来源只请求覆盖这些域的接口，不必为一个日升日落把逐时和空气一起拉回来。
    Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token);
}

// 来源集合与查询入口。
internal static class WeatherSources
{
    public static readonly IWeatherSource[] All =
    [
        new OpenMeteoSource(), new HeFengSource()
    ];
    public static IWeatherSource? Find(string id) => All.FirstOrDefault(s => s.Info.Id == id);
    public static string Name(string id) => Find(id)?.Info.Name ?? id;
}
