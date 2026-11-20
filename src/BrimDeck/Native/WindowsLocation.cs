using System.Threading;
using Windows.Devices.Geolocation;

namespace BrimDeck;
// Windows 系统定位服务：走系统位置提供程序（WiFi / 网络 / GPS）拿真实坐标，精度数百米级，
// 远高于 IP 库给的城市中心点（武汉的 IP 中心点就落在江汉区，反查到区县必然报错位置）。
// 系统未开「定位服务」或权限被拒时返回 null，调用方退回 IP 定位——那里只敢声称城市级。
internal static class WindowsLocation
{
    private static readonly object Lock = new();
    private static (DateTime At, double Lat, double Lon)? _last;
    // 同一位置几分钟内复用：天气自动刷新与设置页打开都会查定位，不必每次等一轮 WiFi 定位。
    public static async Task<(double Lat, double Lon)?> PositionAsync(CancellationToken token)
    {
        lock (Lock)
        {
            if (_last is { } kept && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(5)) return (kept.Lat, kept.Lon);
        }
        try
        {
            // RequestAccessAsync 要求前台窗口的 UI 线程；调用方多为 UI 事件，跨线程时借调度器回去。
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is { } d && !d.CheckAccess()) return await await d.InvokeAsync(() => ReadAsync(token));
            return await ReadAsync(token);
        }
        catch { return null; }
    }

    private static async Task<(double Lat, double Lon)?> ReadAsync(CancellationToken token)
    {
        try
        {
            if (await Geolocator.RequestAccessAsync().AsTask(token) != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)).AsTask(cts.Token);
            var point = position.Coordinate.Point.Position;
            lock (Lock) _last = (DateTime.UtcNow, point.Latitude, point.Longitude);
            return (point.Latitude, point.Longitude);
        }
        catch { return null; }
    }
}
