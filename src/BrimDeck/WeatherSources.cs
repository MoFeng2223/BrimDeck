﻿using BrimDeck.Core;

namespace BrimDeck;

// 来源注册表与能力接口。实现按主题拆分在同目录：
//   WeatherSources.Models.cs     传输模型（来源与聚合层的数据契约）
//   WeatherSources.Aggregator.cs 按域取数并合并成 WeatherSnapshot
//   WeatherSources.OpenMeteo.cs / WeatherSources.ComCn.cs / WeatherSources.HeFeng.cs
//   WeatherSources.Tools.cs      Geo / WeatherCodes / Json 纯函数
// WeatherDomain 与 WeatherSettings 定义在 BrimDeck.Core：它们是 settings.json 的一部分。

// 来源覆盖范围。国内源只在中国坐标附近可用，国外源全球可用。
internal enum WeatherRegion { Global, China }

// 一个来源的静态能力描述：能做哪些域、覆盖哪里。
internal sealed record WeatherSourceInfo(string Id, string Name, string Detail, WeatherDomain[] Domains, WeatherRegion Region);

internal interface IWeatherSource
{
    WeatherSourceInfo Info { get; }
    bool CanServe(City city, WeatherSettings settings);
    // need 是本次真正缺的域：来源只请求覆盖这些域的接口，不必为一个日出日落把逐时和空气一起拉回来。
    Task<WeatherPart?> FetchAsync(City city, WeatherSettings settings, IReadOnlyCollection<WeatherDomain> need, CancellationToken token);
}

// 来源集合与查询入口。
internal static class WeatherSources
{
    public static readonly IWeatherSource[] All =
    [
        new OpenMeteoSource(), new WeatherComCnSource(), new HeFengSource()
    ];
    public static IWeatherSource? Find(string id) => All.FirstOrDefault(s => s.Info.Id == id);
    public static string Name(string id) => Find(id)?.Info.Name ?? id;
    // 某个区域的候选来源：国内城市三家都能用（国内源只服务中国坐标），国外城市只有全球源。
    // 设置页按这个集合动态生成每个域的下拉选项，不写死名单。
    public static IEnumerable<IWeatherSource> Candidates(bool china)
        => All.Where(s => china || s.Info.Region == WeatherRegion.Global);
}
