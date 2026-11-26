using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// 和风可用性监测：只认「实况」这一条真实请求的结果。缓存命中、闸门拦截、请求级 400 都不算，
// 避免把国外坐标必失败的分钟降水、或另一座城市的并发请求误判成来源不可用。
// 分类依赖和风的 HTTP 语义，所以他和来源实现放同一层；只把文件独立出来，不让来源反向依赖设置页。
internal enum HeFengState { Untested, Connected, Failed }

internal sealed record HeFengCheck(HeFengState State, string Detail, DateTimeOffset At, TimeSpan Elapsed);

internal static class HeFengMonitor
{
    private static readonly object Sync = new();
    private static HeFengCheck _current = new(HeFengState.Untested,
        Loc.T("等待下一次真实刷新", "Waiting for the next live refresh"), default, default);
    private static int _generation;

    public static HeFengCheck Current { get { lock (Sync) return _current; } }
    public static event Action<HeFengCheck>? Changed;

    // Host / Key 改动后旧结果作废；在途请求带着旧代次回来也不会覆盖新配置的状态。
    public static void Invalidate()
    {
        HeFengCheck fresh;
        lock (Sync)
        {
            _generation++;
            _current = new HeFengCheck(HeFengState.Untested,
                Loc.T("配置已更改，下次刷新会重新检测", "Configuration changed; the next refresh will check again"), DateTimeOffset.Now, default);
            fresh = _current;
        }
        Changed?.Invoke(fresh);
    }

    // 由实况请求在收到 HTTP 结果后直接回报，generation 与发起时一致才写入。
    internal static void Report(int generation, int status, string? body, TimeSpan elapsed)
    {
        var (state, detail) = Classify(status, body);
        if (state is null) return;   // 闸门未放行 / 请求级错误：不判来源可用性
        var check = new HeFengCheck(state.Value, detail!, DateTimeOffset.Now, elapsed);
        lock (Sync)
        {
            if (generation != _generation) return;
            _current = check;
        }
        Changed?.Invoke(check);
    }

    public static int Generation { get { lock (Sync) return _generation; } }

    private static (HeFengState? State, string? Detail) Classify(int status, string? body)
    {
        if (status == 0) return (null, null);   // 请求闸门拦下，没有真实发出
        if (status is 401 or 403) return (HeFengState.Failed, Loc.T("API Key 无效或无权限", "API Key invalid or unauthorized"));
        if (status == 402) return (HeFengState.Failed, Loc.T("账户额度不足", "Account quota exhausted"));
        if (status == 429) return (HeFengState.Failed, Loc.T("请求过于频繁", "Rate limited"));
        if (status is 408 or -1) return (HeFengState.Failed, Loc.T("网络异常或超时", "Network error or timeout"));
        if (status is 400 or 404 or 422) return (null, null);   // 参数/地点问题只影响该请求，不代表来源不可用
        if (status is < 200 or >= 300) return (HeFengState.Failed, Loc.T($"HTTP {status}", $"HTTP {status}"));
        if (body is null) return (HeFengState.Failed, Loc.T("响应为空", "Empty response"));
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // v1 成功响应带 condition；错误响应只有 code/message 或 error，不能只看 HTTP 200。
            if (Json.Get(root, "condition") is { ValueKind: JsonValueKind.Object })
                return (HeFengState.Connected, Loc.T("实况接口响应正常", "Current-weather endpoint responded"));
            string? code = Json.Text(root, "code");
            string? message = Json.Text(root, "message");
            if (message is null && Json.Get(root, "error") is { } error)
                message = Json.Text(error, "message") ?? Json.Text(error, "description");
            string detail = string.Join(" · ", new[] { code is null ? null : Loc.T($"错误码 {code}", $"Code {code}"), message }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            return (HeFengState.Failed, detail.Length > 0 ? detail : Loc.T("响应中没有实况数据", "No current-weather data in response"));
        }
        catch { return (HeFengState.Failed, Loc.T("返回内容不是有效 JSON", "Response is not valid JSON")); }
    }
}
