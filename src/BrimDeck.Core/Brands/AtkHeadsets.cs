namespace BrimDeck.Core;

// ATK's N9 V2 Ultimate headset on its dongle (373B:1262, "ATK N9V2 ULTIMATE dongle"). 0x373B is shared with ATK's mice and
// keyboards (their BitMouse collection is usage page 0xFF05, which Dongles takes), so only this product, and only its 0xFF90
// collection, usage 1 (64-byte output, input of 22 to 64 bytes with the report id; the 0xFF82 twin refuses the report; the
// usage and the lengths as wushuai0802/myfinder-device-battery's atk_headset.py has them, AGPL, facts only): output report
// 0x55, 55 65 01 00 (the headset as the target), written as an output report; the answer 55 65 <percent> <state>, the state
// 02 on battery, 03 charging, 04 full (the percent as reported, not charging, as ATK HUB's parser has it:
// myfinder-device-battery's upstream-protocols.md), 1A the headset gone; 55 0A 01 is the headset disconnected. From Voltide's
// ATKN9V2UltimateProvider.cs (MIT; checked on the dongle against ATK HUB: 55 65 64 02 02 00 00 04 ..., 100 %). FIFINE's HT6
// answers the same frame (read by Fifine.Battery).
// The N9 Pro and N9 Ultra (receivers 373B:11CB and 1204, cables 11CA and 1203) are not read over HID: ATK HUB's Windows app
// speaks to them on the USB serial port the same device carries (9600 baud, 8 data bits, 1 stop bit, no parity), in 64-byte
// packets <length> <command> <subcommand> <data>: the battery is the AUTH group's GetBattery, 02 00 03, answered 64 bytes
// whose data's second byte (4 in the packet) is the level in its low seven bits and charging in bit 7 (Serial, below; the
// reader is HidBatteries.AtkSerial). The headset also sends packets of its own (command 0x80); only the answer with 00 03 is
// taken. Not measured by us.
[BrandFamilies]
public static class AtkHeadsets
{
    public const int Vendor = 0x373B, N9V2Ultimate = 0x1262;

    public static byte[] Request() => BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00);

    public static readonly (int Product, string Name)[] SerialProducts =
        [(0x11CB, "ATK N9 Pro"), (0x11CA, "ATK N9 Pro"), (0x1204, "ATK N9 Ultra"), (0x1203, "ATK N9 Ultra")];
    public const int SerialBaud = 9600, SerialPacket = 64;
    public static byte[] SerialRequest() => BrandFamilies.Frame(SerialPacket, 0x02, 0x00, 0x03);
    public static BatteryRead? SerialBattery(byte[] r)
    {
        if (r.Length < 5 || r[0] < 2 || r[1] != 0x00 || r[2] != 0x03) return null;
        int level = r[4] & 0x7F;
        return level is > 0 and <= 100 ? new(level, (r[4] & 0x80) != 0) : null;
    }

    // 55 65 <percent 0..100> <state 02, 03 or 04>, 03 charging; 1A the link lost (asleep); 55 0A 01 the headset disconnected
    // (asleep). Any other state (00 is the query read back), length or header is no reading.
    public const int InputMin = 22, InputMax = 64;
    public static BatteryRead? StateBattery(byte[] r)
    {
        if (r.Length is < InputMin or > InputMax || r[0] != 0x55) return null;
        if (r[1] == 0x0A) return r[2] == 0x01 ? new(null, Asleep: true) : null;
        if (r[1] != 0x65) return null;
        if (r[3] == 0x1A) return new(null, Asleep: true);
        return r[3] is 0x02 or 0x03 or 0x04 && r[2] <= 100 ? new(r[2], r[3] == 0x03) : null;
    }

    // The N9 V2 ULTRA's dongle (12D6), which ATK HUB (its "BOLUTEK" headsets) asks with the same frame as the Ultimate's; not
    // measured by us. The N9 V2 PRO's (12D4) is not asked: myfinder-device-battery leaves it out for want of its interface's
    // description (upstream-protocols.md; AGPL, facts only).
    public static readonly (int Product, string Name)[] Receivers = [(N9V2Ultimate, "ATK N9 V2 Ultimate"), (0x12D6, "ATK N9 V2 ULTRA")];

    private static bool Takes(HidShape s) => s.Vendor == Vendor && Receivers.Any(d => d.Product == s.Product) && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001
        && s.InputLength is >= InputMin and <= InputMax && s.OutputLength >= 64;

    public static IEnumerable<BrandFamily> Families =>
    [
        // Also where Windows lists report 0x55 as an input report alone, as on the MCHOSE V9 PRO's receiver, which also carries a
        // 0xFF82 and a 0xFF90 collection (BrandWay.Undeclared; not measured on these dongles).
        new("ATK Headset", Takes, _ => [new BrandWay("atk-headset.state", WayTransport.Output, Request(), StateBattery) { TimeoutMs = 1000, Undeclared = true }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "atk-headset", DisplayName = s => Receivers.First(d => d.Product == s.Product).Name },
    ];
}
