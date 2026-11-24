namespace BrimDeck.Core;

// Per-device charge-notice state: a rise into "charging at or above the threshold" after the first reading briefly shows a notice
// (MainWindow.DetectDeviceAlerts). Each device's first look only seeds, so a device already charged when it is first seen does not alert.
public sealed class DeviceLevels
{
    public sealed record Rise(DeviceBattery Device, string Warning);

    private readonly Dictionary<string, bool> _ready = [];

    // Every device that crossed into charge-ready since the last call.
    public IReadOnlyList<Rise> Update(IReadOnlyList<DeviceBattery> devices, int threshold)
    {
        var risen = new List<Rise>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (device.Lowest is not int) continue;
            string key = device.Key;
            seen.Add(key);
            bool ready = Devices.ChargeReady(device.Lowest, device.Charging, threshold);
            bool known = _ready.ContainsKey(key);
            bool was = known && _ready[key];
            _ready[key] = ready;
            if (known && ready && !was)
                risen.Add(new Rise(device, Loc.T("电量充足", "Charged enough")));
        }
        foreach (var key in _ready.Keys.Where(key => !seen.Contains(key)).ToList())
            _ready.Remove(key);
        return risen;
    }
}
