namespace BrimDeck.Core;

// Sony's INZONE headsets on their 2.4 GHz dongles, vendor 0x054C (shared with the PlayStation controllers and the PULSE 3D,
// which other readers take), so only these products: the INZONE H5 (0EBF, usage page 0xFF04 usage 2, or usage 1 as a user's
// Windows listed it in myfinder-device-battery #813, AGPL; facts only) and H9 II (0FA8, usage
// page 0xFF04 usage 1, interface 5), Sony's vendor HCI over HID on report 02. The battery GET is 02 0C 01 00 FC 08 96 C3 41
// 04 01 <transaction, little endian> <sum of bytes 6 to 12>; the transaction is 2 (the dongle's own notices carry 1). The
// answer is an HCI event: 02 <length> 04 FF <param length> 00 96 C3 <address, high nibble 1 = to the PC> 04 <10 = RET>
// <transaction> <charger> <percent>, the sum of bytes 5 to <length> at <length> + 1; the percent 0xFF is the headset offline.
// HeadsetControl's sony_inzone_protocol.hpp (GPL; facts only). No answer from a device is quoted there; the H5 and H9 II are
// listed as supported.
// The INZONE Buds' dongle (0EC2, usage page 0xFF04 usage 1) answers the same GET with three pairs from 13: the left bud, the
// right bud and the case, each <charger> <percent> (0xFF a bud not available); the case's charger is not known. From
// kinako19/inzone-buds-battery-tray's Windows captures (100 %, 100 %, 97 %; a left bud FF beside a right one at 100 %) and
// orkun-soylu/inzone-buds-gnome's descriptor, as myfinder-device-battery's sony_inzone.py and upstream-protocols.md give
// them (AGPL; facts only). (HeadsetControl's pull request #505 says the Buds also send their battery on their own.)
// Not read: the INZONE H9 (0E53) speaks the same commands over a serial port. The PULSE Explore is SonyPulse.
[BrandFamilies]
public static class SonyInzone
{
    public const int Vendor = 0x054C, H5 = 0x0EBF, H9II = 0x0FA8, Buds = 0x0EC2;
    private const byte Transaction = 0x02;

    public static byte[] Request()
    {
        var frame = BrandFamilies.Frame(14, 0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, Transaction, 0x00);
        int sum = 0;
        for (int i = 6; i <= 12; i++) sum += frame[i];
        frame[13] = (byte)sum;
        return frame;
    }

    // Whether a report is the answer to the GET, with at least the given length (the payload from 13 to it).
    private static bool Answer(byte[] r, int least)
    {
        if (r.Length < 15 || r[0] != 0x02 || r[2] != 0x04 || r[3] != 0xFF || r[5] != 0x00 || r[6] != 0x96 || r[7] != 0xC3 || r[8] >> 4 != 0x1) return false;
        int length = r[1];
        if (length < least || length > 62 || length + 1 >= r.Length) return false;
        int sum = 0;
        for (int i = 5; i <= length; i++) sum += r[i];
        if ((byte)sum != r[length + 1]) return false;
        return r[9] == 0x04 && r[10] is 0x10 or 0xA0 && r[11] == Transaction && r[12] == 0x00;
    }
    public static BatteryRead? Battery(byte[] r)
    {
        if (!Answer(r, 14)) return null;
        if (r[14] == 0xFF) return new(null, Asleep: true);
        return r[14] is > 0 and <= 100 ? new(r[14], r[13] != 0) : null;
    }
    // The Buds: the lower of the buds' levels, charging while either bud charges; neither bud available is the buds away.
    public static BatteryRead? BudsBattery(byte[] r)
    {
        if (!Answer(r, 18)) return null;
        int? Level(byte value) => value <= 100 ? value : null;
        bool Known(byte value) => value <= 100 || value == 0xFF;
        if (!Known(r[14]) || !Known(r[16]) || !Known(r[18])) return null;
        var parts = new List<PartBattery>
        {
            new(BatteryPart.Left, Level(r[14]), r[13] == 1 && r[14] <= 100), new(BatteryPart.Right, Level(r[16]), r[15] == 1 && r[16] <= 100),
            new(BatteryPart.Case, Level(r[18]), false),
        };
        var buds = parts.Where(part => part.Part != BatteryPart.Case && part.Percent is not null).ToList();
        if (buds.Count == 0) return new(null, Asleep: true) { Parts = parts };
        return new(buds.Min(part => part.Percent), buds.Any(part => part.Charging)) { Parts = parts };
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF04) && s.InputLength >= 64 && s.OutputLength >= 64 && s.Product switch
    {
        H5 => s.Usage is 0x0002 or 0x0001,
        H9II => s.Usage == 0x0001 && s.Interface is 5 or -1,
        Buds => s.Usage == 0x0001,
        _ => false
    };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Sony INZONE", Takes, s => [s.Product == Buds
            ? new BrandWay("inzone.buds", WayTransport.Output, Request(), BudsBattery) { TimeoutMs = 1000 }
            : new BrandWay("inzone.battery", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000 }])
        {
            Kind = s => s.Product == Buds ? DeviceKind.Earbuds : DeviceKind.Headphones, Prefix = "inzone",
            DisplayName = s => s.Product == Buds && s.Name.Length == 0 ? "INZONE Buds" : null,
        },
    ];
}
