namespace BrimDeck.Core;

// MOONDROP's GM01 Pro headset on its receiver 35D8:0133 (MOONDROP's own id; only this product). The battery is asked on the
// receiver's collection that declares report 0x4B both ways (a user found it on a 0x000C usage 1 collection, 64 bytes with
// the id): output 4B 80 27 00 and zeros (read, command 0x27, no payload), answered on input report 0x4B with 27 at 2, the
// payload's length at 3 (6 or more), and, from 4 on, the payload: at 8 (its byte 4) 0 when there is no level, else the percent
// at 9 (its byte 5). No charging state is known. MOONDROP Hub's web code as myfinder-device-battery's moondrop.py and
// upstream-protocols.md give it (AGPL; facts only), and a user's answer there (#127): 4B 80 27 06 00 00 00 00 01 32, 50 %.
// The Hub's reachability check first (80 31 00) is not sent; nor anything else it sends (flash, EQ, firmware).
// The one family that writes to a consumer collection (0x000C usage 1): only there, or on 0xFF02 usage 2 where a firmware
// declares 0x4B on it, and only where report 0x4B is declared both ways, as the user's receiver has it (its other 0x000C
// collection, the volume keys', declares no output and is never asked).
[BrandFamilies]
public static class Moondrop
{
    public const int Vendor = 0x35D8, Gm01Pro = 0x0133;
    private const byte Report = 0x4B;

    public static byte[] Request(int length) => BrandFamilies.Frame(Math.Max(4, length), Report, 0x80, 0x27, 0x00);
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 10 || r[0] != Report || r[2] != 0x27 || r[3] < 6 || r[3] > r.Length - 4 || r[8] == 0) return null;
        return r[9] is > 0 and <= 100 ? new(r[9]) : null;
    }

    public static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Gm01Pro && s.InputLength >= 10 && s.OutputLength >= 4
        && (s.UsagePage, s.Usage) is (0x000C, 0x0001) or (0xFF02, 0x0002) && s.OutputIds.Contains(Report) && s.InputIds.Contains(Report);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("MOONDROP", Takes, s => [new BrandWay("moondrop.battery", WayTransport.Output, Request(s.OutputLength), Battery) { TimeoutMs = 1000, Attempts = 1 }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "moondrop", DisplayName = _ => "MOONDROP GM01 Pro" },
    ];
}
