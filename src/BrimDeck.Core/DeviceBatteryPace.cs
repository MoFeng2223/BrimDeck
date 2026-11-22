using System.Text.Json;

namespace BrimDeck.Core;

// Time left and time to full for a wireless device, worked out from how its percent moved while BrimDeck was reading it
// (DeviceBatteryPace). Null where there is not yet enough of a slope, or the reading is too coarse to trust.
public readonly record struct DeviceBatteryEstimate(TimeSpan? TimeLeft, TimeSpan? TimeToFull);

// Remembers recent charge readings per device while the setting is on, and turns a same-mode slope into an estimate. Fed from
// the devices' existing reports only: it never asks a device for anything further. Kept across runs in device-battery-pace.json
// (DeviceBatteryPaceStore), one file for every profile; turning the setting off keeps it, and only Clear in the settings erases it.
public sealed class DeviceBatteryPace
{
    public const int MaxSamples = 48;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    // A discharge that has barely moved is noise on a mouse that reports in whole percents; charging usually moves faster.
    public static readonly TimeSpan MinDischargeSpan = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinChargeSpan = TimeSpan.FromMinutes(2);
    // Keyboards and some mice last for weeks on a charge.
    public static readonly TimeSpan Longest = TimeSpan.FromDays(60);

    private sealed class SampleRow
    {
        public DateTimeOffset At { get; set; }
        public int Percent { get; set; }
        public bool Charging { get; set; }
    }
    private sealed class DeviceRow
    {
        public string Key { get; set; } = "";
        public List<SampleRow> Samples { get; set; } = [];
    }

    private readonly record struct Sample(DateTimeOffset At, int Percent, bool Charging);

    private readonly Dictionary<string, List<Sample>> _series = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // Set when the kept samples changed; cleared by Serialize.
    public bool Dirty { get; private set; }

    // Drops every kept reading (Clear readings in the settings); DeviceBatteryPaceStore.Erase removes the file.
    public void Clear()
    {
        lock (_gate)
        {
            _series.Clear();
            Dirty = false;
        }
    }

    // Replaces what is kept with a file's contents (Start), trimmed to what is still worth keeping.
    public void Load(string? json, DateTimeOffset now)
    {
        lock (_gate)
        {
            _series.Clear();
            foreach (var (key, list) in Parse(json, now)) _series[key] = list;
            Dirty = false;
        }
    }

    // Records a sample when a device's percent or charging state changed. Earlier / asleep / stepped readings are skipped.
    public void Observe(IReadOnlyList<DeviceBattery> devices, DateTimeOffset now)
    {
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var device in devices)
            {
                if (!Eligible(device)) continue;
                string key = device.Key;
                seen.Add(key);
                int percent = Devices.Clamp(device.Lowest!.Value);
                bool charging = device.Charging;
                if (!_series.TryGetValue(key, out var list)) _series[key] = list = [];
                if (Trim(list, now)) Dirty = true;
                if (list.Count > 0)
                {
                    var last = list[^1];
                    if (last.Percent == percent && last.Charging == charging) continue;
                }
                list.Add(new Sample(now, percent, charging));
                if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
                Dirty = true;
            }
            foreach (var key in _series.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                if (Trim(_series[key], now)) Dirty = true;
                if (_series[key].Count == 0) { _series.Remove(key); Dirty = true; }
            }
        }
    }

    public DeviceBatteryEstimate Of(DeviceBattery device, DateTimeOffset now) => Estimate(device, now).Estimate;

    // Collecting: the readings in the device's present mode are still too few or too short for a slope. A full battery on its
    // charger, an empty one, or a slope too slow or out of range gives no estimate and is not collecting either.
    private (DeviceBatteryEstimate Estimate, bool Collecting) Estimate(DeviceBattery device, DateTimeOffset now)
    {
        if (!Eligible(device)) return (default, false);
        bool charging = device.Charging;
        int percent = Devices.Clamp(device.Lowest!.Value);
        if (charging ? percent >= 100 : percent <= 0) return (default, false);
        lock (_gate)
        {
            if (!_series.TryGetValue(device.Key, out var list)) return (default, true);
            Trim(list, now);
            // The latest run of samples in the device's present mode: a plug or unplug starts a new slope, and so does a rise while
            // not charging (a device that reports no charging state, charged on its cable, or charged while BrimDeck was not reading).
            int from = list.Count;
            while (from > 0 && list[from - 1].Charging == charging && (charging || from == list.Count || list[from - 1].Percent >= list[from].Percent))
                from--;
            if (list.Count - from < 2) return (default, true);
            var last = list[^1];
            int differs = from;
            while (differs < list.Count && list[differs].Percent == last.Percent) differs++;
            if (differs == list.Count) return (default, true);
            var start = list[differs];
            var span = last.At - start.At;
            if (span < (charging ? MinChargeSpan : MinDischargeSpan)) return (default, true);
            double rate = (last.Percent - start.Percent) / span.TotalHours;
            if (charging)
                return (rate < 0.5 ? default : new(null, Bounded(TimeSpan.FromHours((100 - percent) / rate))), false);
            return (rate > -0.05 ? default : new(Bounded(TimeSpan.FromHours(percent / -rate)), null), false);
        }
    }

    // The detail line's estimate, or that readings are still being collected; null when there is nothing to say (the device cannot
    // be estimated, or the readings give no estimate).
    public string? Status(DeviceBattery device, DateTimeOffset now)
    {
        var (estimate, collecting) = Estimate(device, now);
        if (estimate.TimeToFull is { } full)
            return Loc.T("约 " + Span(full) + " 充满", "~" + Span(full) + " to full");
        if (estimate.TimeLeft is { } left)
            return Loc.T("约 " + Span(left) + " 可用", "~" + Span(left) + " left");
        return collecting ? Loc.T("正在收集电量数据…", "Collecting battery data…") : null;
    }

    // Under a day as the power page shows it ("3 小时 12 分"); longer in days and hours, as an estimate runs up to Longest.
    private static string Span(TimeSpan time)
    {
        if (time < TimeSpan.FromDays(1)) return PowerFormat.Duration(time);
        int hours = (int)Math.Round(time.TotalHours), days = hours / 24, rest = hours % 24;
        return rest == 0 ? Loc.T($"{days} 天", $"{days} d") : Loc.T($"{days} 天 {rest} 小时", $"{days} d {rest} h");
    }

    public string Serialize()
    {
        lock (_gate)
        {
            Dirty = false;
            var rows = _series.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DeviceRow
                {
                    Key = pair.Key,
                    Samples = [.. pair.Value.Select(sample => new SampleRow { At = sample.At, Percent = sample.Percent, Charging = sample.Charging })]
                }).ToList();
            return JsonSerializer.Serialize(rows, Options);
        }
    }

    public static DeviceBatteryPace FromFile(string? json, DateTimeOffset now)
    {
        var pace = new DeviceBatteryPace();
        pace.Load(json, now);
        return pace;
    }

    private static Dictionary<string, List<Sample>> Parse(string? json, DateTimeOffset now)
    {
        var series = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return series;
        try
        {
            var rows = JsonSerializer.Deserialize<List<DeviceRow>>(json, Options) ?? [];
            foreach (var row in rows.Where(row => row is { Key.Length: > 0 }).Take(256))
            {
                var list = new List<Sample>();
                // A file edited by hand or cut short can hold null samples; they are skipped, never thrown on at start.
                foreach (var sample in (row.Samples ?? []).Where(sample => sample is { Percent: >= 0 and <= 100 }).Take(MaxSamples))
                    list.Add(new Sample(sample.At, sample.Percent, sample.Charging));
                list.Sort((a, b) => a.At.CompareTo(b.At));
                Trim(list, now);
                if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
                if (list.Count > 0) series[row.Key] = list;
            }
        }
        catch (JsonException) { }
        return series;
    }

    private static bool Eligible(DeviceBattery device)
        => device.Lowest is not null && !device.Earlier && !device.Asleep && device.Accuracy != BatteryAccuracy.Steps;

    private static bool Trim(List<Sample> list, DateTimeOffset now)
    {
        var keep = now - KeepFor;
        int drop = 0;
        while (drop < list.Count && list[drop].At < keep) drop++;
        if (drop == 0) return false;
        list.RemoveRange(0, drop);
        return true;
    }

    private static TimeSpan? Bounded(TimeSpan time) => time > TimeSpan.Zero && time <= Longest ? time : null;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
