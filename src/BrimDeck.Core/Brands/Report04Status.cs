namespace BrimDeck.Core;

// Receivers that answer block 0x1A on the 0xFF1C / 0x0092 collection (report 0x04, 64 bytes each way), as the CHERRY PIXIU75 and
// HECATE G5M Pro do (Dongles.StatusBlock), but with a request of their own. 0x320F is Evision's id, shared by many makers, so only
// these products are taken, each on that collection alone:
// - AULA V9Pro+ (320F:22BA, product string "AULA-V9Pro+", interface 1): 04 00 00 1A 00, answered 04 <bytes 1 to 7 echoed, but
//   not the 00 at 4> <percent at 8> <charging at 9: 1 on the cable, 0 off it>. A contributor's capture of AULA's app and 27
//   readings over a charge (80 % to 82 %, 9 reading 0, 1, 0 as the cable went in and out).
// - HECATE G3M PRO (320F:706E): 04 20 00 1A 06, bytes 1 and 2 the sum of bytes 3 to 63, little endian. The answer has 1A at 3 and
//   the percent at 8; FE or FF at 7 is an error. No charging state is known. From BoBppy/g3mpro-macos and
//   BlueEve04/G3M-PRO-Controller (MIT; their author's notes say it was used on Windows), not measured by a user.
// - CHERRY MX 2.0S WL Pro (046A:01E0, "MX 2.0S Pro Dongle"; 046A:01DE, "MX 2.0S WL Pro" on its cable, interface 1): the same
//   request as the G3M PRO, answered with its first five bytes echoed, 0 at 7, the percent at 8 and 1 at 9 while charging.
//   zcmk123/cherry-battery-state (MIT) and a contributor's readings (55 %, 58 % charging, 63 % on the cable, a few points from
//   CHERRY's own app).
// All three as myfinder-device-battery gives them (aula_mouse.py, g3m.py, cherry.py and upstream-protocols.md; AGPL, facts only).
// The request read back has 0 at 8, which is no reading; nor is a level of 0.
[BrandFamilies]
public static class Report04Status
{
    public const int EvisionVendor = 0x320F, AulaV9ProPlus = 0x22BA, HecateG3m = 0x706E;
    public const int CherryVendor = 0x046A, CherryMx2Dongle = 0x01E0, CherryMx2Cable = 0x01DE;

    public static byte[] AulaRequest() => BrandFamilies.Frame(64, 0x04, 0x00, 0x00, 0x1A, 0x00);
    // 04 <sum of 3..63, little endian> 1A 06: 04 20 00 1A 06.
    public static byte[] SumRequest()
    {
        var frame = BrandFamilies.Frame(64, 0x04, 0x00, 0x00, 0x1A, 0x06);
        int sum = 0;
        for (int i = 3; i < 64; i++) sum += frame[i];
        frame[1] = (byte)sum; frame[2] = (byte)(sum >> 8);
        return frame;
    }

    public static BatteryRead? Aula(byte[] r)
        => r.Length >= 10 && r[0] == 0x04 && r[1] == 0x00 && r[2] == 0x00 && r[3] == 0x1A && r[8] is > 0 and <= 100 && r[9] <= 1
            ? new(r[8], r[9] == 1) : null;
    public static BatteryRead? Hecate(byte[] r)
        => r.Length >= 10 && r[0] == 0x04 && r[3] == 0x1A && r[7] is not (0xFE or 0xFF) && r[8] is > 0 and <= 100 ? new(r[8]) : null;
    public static BatteryRead? Cherry(byte[] r)
    {
        var head = SumRequest();
        if (r.Length < 10 || !r.AsSpan(0, 5).SequenceEqual(head.AsSpan(0, 5)) || r[7] != 0 || r[9] > 1) return null;
        return r[8] is > 0 and <= 100 ? new(r[8], r[9] == 1) : null;
    }

    private static bool Collection(HidShape s) => s.UsagePage == 0xFF1C && s.Usage == 0x0092 && s.InputLength == 64 && s.OutputLength == 64;
    private static bool Named(HidShape s, string name) => s.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase);

    public static bool TakesAula(HidShape s) => s.Vendor == EvisionVendor && s.Product == AulaV9ProPlus && Collection(s)
        && s.Interface is 1 or -1 && Named(s, "AULA-V9Pro+");
    public static bool TakesHecate(HidShape s) => s.Vendor == EvisionVendor && s.Product == HecateG3m && Collection(s);
    public static bool TakesCherry(HidShape s) => s.Vendor == CherryVendor && Collection(s) && s.Interface is 1 or -1 && s.Product switch
    {
        CherryMx2Dongle => Named(s, "MX 2.0S Pro Dongle"),
        CherryMx2Cable => Named(s, "MX 2.0S WL Pro"),
        _ => false
    };

    public static IEnumerable<BrandFamily> Families =>
    [
        new("AULA V9Pro+", TakesAula, _ => [new BrandWay("aula.v9proplus", WayTransport.Output, AulaRequest(), Aula) { TimeoutMs = 700 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "aula-v9", DisplayName = _ => "AULA V9Pro+" },
        new("HECATE G3M PRO", TakesHecate, _ => [new BrandWay("hecate.g3m", WayTransport.Output, SumRequest(), Hecate) { TimeoutMs = 700 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "hecate-g3m", DisplayName = _ => "HECATE G3M PRO" },
        new("CHERRY MX 2.0S", TakesCherry, _ => [new BrandWay("cherry.mx2s", WayTransport.Output, SumRequest(), Cherry) { TimeoutMs = 1000 }])
        { Kind = _ => DeviceKind.Keyboard, Prefix = "cherry-mx2s", DisplayName = _ => "CHERRY MX 2.0S WL Pro" },
    ];
}
