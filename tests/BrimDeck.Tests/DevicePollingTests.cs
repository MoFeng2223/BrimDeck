using System.Text.RegularExpressions;

// The rule of DevicePolling (src/BrimDeck/Native/DeviceBatteries.cs): a read that reaches a device on a timer is timed only by a
// DeviceAsk, which sends nothing while the user is away, so a keyboard or mouse can reach its own sleep. A brand added to the
// brand readers is asked in HidBatteries' pass and keeps the rule by itself; what is checked here is every source of readings
// (IDeviceSource), and that no DeviceAsk's interval is taken out to time something on its own.
static class DevicePollingTests
{
    private enum Reads { DeviceOnTimer, NoDevice }

    // Every source, and whether a read of its own on a timer reaches a device. A new source goes here: DeviceOnTimer if anything
    // it sends on a timer reaches a device (it must then use DevicePolling's DeviceAsk), NoDevice only if it never does.
    private static readonly Dictionary<string, (Reads Reads, string Why)> Sources = new()
    {
        ["HidBatteries"] = (Reads.DeviceOnTimer, "the brands' requests (Ask), HID++ (Logitech), and Corsair's receivers through its CorsairReader (Slow)"),
        ["BluetoothBatteries"] = (Reads.DeviceOnTimer, "the GATT levels whose devices send no notices (Gatt); Windows' cached levels reach no device"),
        ["GamingBatteries"] = (Reads.DeviceOnTimer, "Windows.Gaming.Input and XInput battery reports (Ask)"),
        ["PhoneLinkBatteries"] = (Reads.NoDevice, "Phone Link's card, a file on this computer"),
        ["AppleBatteries"] = (Reads.NoDevice, "a passive Bluetooth scan of the AirPods' advertisements; nothing is sent"),
        ["EncoDeckBatteries"] = (Reads.NoDevice, "asks the EncoDeck program over its pipe, which reads the device on its own terms"),
    };

    // The places a DeviceAsk's bare interval may be used, by file: none of them times a read of a device.
    private static readonly Dictionary<string, int> BareIntervals = new()
    {
        // The wait after a pass that threw, the longest wait between passes, and the time a silent device is taken to be asleep.
        ["HidBatteries.cs"] = 3,
        // DeviceAsk itself.
        ["DeviceBatteries.cs"] = 0,
    };

    public static void Run(Action<string, bool> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BrimDeck.sln"))) root = root.Parent;
        if (root is null) { check("Device polling: the source tree is found", false); return; }
        var files = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "BrimDeck"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToDictionary(file => file, File.ReadAllText);

        var declared = new Dictionary<string, string>();
        foreach (var (file, text) in files)
            foreach (Match match in Regex.Matches(text, @"\bclass\s+(\w+)[^{;]*?:[^{;]*\bIDeviceSource\b"))
                declared[match.Groups[1].Value] = text;

        var unknown = declared.Keys.Where(name => !Sources.ContainsKey(name)).ToList();
        check("Device polling: every source of readings is entered in DevicePollingTests" + (unknown.Count > 0
            ? $" ({string.Join(", ", unknown)}: a read it makes on a timer that reaches a device must be timed by DevicePolling's DeviceAsk, which pauses while the user is away; then enter it in Sources)"
            : ""), unknown.Count == 0);
        var gone = Sources.Keys.Where(name => !declared.ContainsKey(name)).ToList();
        check("Device polling: every source entered still exists" + (gone.Count > 0 ? " (" + string.Join(", ", gone) + ")" : ""), gone.Count == 0);

        var untimed = Sources.Where(source => source.Value.Reads == Reads.DeviceOnTimer && declared.TryGetValue(source.Key, out var text)
            && !Regex.IsMatch(text, @"DevicePolling\.\w+\.(Due|Left|Timer)\(")).Select(source => source.Key).ToList();
        check("Device polling: each source whose timed reads reach a device times them by a DeviceAsk" + (untimed.Count > 0 ? " (" + string.Join(", ", untimed) + ")" : ""),
            untimed.Count == 0);

        var bare = new List<string>();
        foreach (var (file, text) in files)
        {
            int count = Regex.Matches(text, @"DevicePolling\.(Ask|Logitech|Gatt|Slow)\.(Interval|Milliseconds)\b").Count;
            if (count > BareIntervals.GetValueOrDefault(Path.GetFileName(file))) bare.Add($"{Path.GetFileName(file)} ×{count}");
        }
        check("Device polling: no DeviceAsk's bare interval times a read of its own (use Due, Left or Timer)" + (bare.Count > 0 ? " (" + string.Join(", ", bare) + ")" : ""),
            bare.Count == 0);
    }
}
