namespace BrimDeck.Core;

// LINGBAO's M1 Pro on its receiver 088D:052F ("Wireless-Receiver"; 0x088D is also EWEADN's, so this product alone), its 0xFF00
// usage 1 collection. Nothing is written: feature report 0x0F (33 bytes) is only got back. It holds the mouse's ids, "M536" at 7
// to 10 and "LX" at 13 and 14, the percent at 18 and a flag at 19: 0 a reading, 1 the mouse on its cable, when 18 is always 100
// (a placeholder, no reading). No charging or link state is known. A user's two frames in myfinder-device-battery #171
// (0F 00 30 02 EC 01 10 4D 35 33 36 01 06 4C 58 11 11 01 41 00 FF 00 FF D0 00 0C 31 BE 00 5B 81 2C C8: 65 %), as its lingbao.py
// and docs/lingbao-m1pro.md give them (AGPL; facts only). LINGBAO's M5PRO Max (3151:402D) is read elsewhere (Dongles).
[BrandFamilies]
public static class LingbaoM1Pro
{
    public const int Vendor = 0x088D, Receiver = 0x052F;
    private const byte Report = 0x0F;

    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 20 || r[0] != Report || r[7] != (byte)'M' || r[8] != (byte)'5' || r[9] != (byte)'3' || r[10] != (byte)'6'
            || r[13] != (byte)'L' || r[14] != (byte)'X') return null;
        return r[19] == 0 && r[18] is > 0 and <= 100 ? new(r[18]) : null;
    }

    public static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Receiver && MouseShapes.Collection(s, 0xFF00, 0x0001)
        && s.FeatureLength == 33 && s.Interface is 1 or -1 && s.Name.Trim().Equals("Wireless-Receiver", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<BrandFamily> Families =>
    [
        new("LINGBAO M1 Pro", Takes, _ => [new BrandWay("lingbao.m1pro", WayTransport.FeatureGet, [Report], Battery) { FeatureLength = 33, Attempts = 1 }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "lingbao-m1", DisplayName = _ => "LINGBAO M1 Pro" },
    ];
}
