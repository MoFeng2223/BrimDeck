namespace BrimDeck.Core;

// FIFINE headsets on their 2.4 GHz dongles, vendor 0x3142 (FIFINE's own: every collection of the brand with the shape is
// asked). The vendor collection (usage page 0xFF90 usage 1) takes report 0x55 both ways, 64 bytes: the state query 55 65 01
// 00 is answered 55 65 <percent> <status> ..., status 03 charging and 1A the link to the headset lost; 55 0A ... says the
// headset is off or out of range. HeadsetControl's fifine_ht6.hpp (GPL; facts only: from a capture of FIFINE's SoundLink,
// verified on an HT6, 3142:00C3). The query read back (55 65 01 00) is no reading: its status is 0.
// The same Actions frame as ATK's N9 V2 Ultimate (AtkHeadsets); FIFINE's answer is read here, 04 (full) as 100 %.
[BrandFamilies]
public static class Fifine
{
    public const int Vendor = 0x3142;

    public static byte[] Request() => BrandFamilies.Frame(4, 0x55, 0x65, 0x01, 0x00);
    // 55 65 <percent> <state>: a state of 0 is the query read back; 1A the link lost (asleep). 55 0A: the headset is off.
    public static BatteryRead? Battery(byte[] r)
    {
        if (r.Length < 4 || r[0] != 0x55) return null;
        if (r[1] == 0x0A) return new(null, Asleep: true);
        if (r[1] != 0x65 || r[3] == 0x00) return null;
        if (r[3] == 0x1A) return new(null, Asleep: true);
        if (r[3] == 0x04 && r[2] <= 100) return new(100);
        return r[2] is > 0 and <= 100 ? new(r[2], r[3] == 0x03) : null;
    }

    private static bool Takes(HidShape s) => s.Vendor == Vendor && HeadsetPages.Is(s, 0xFF90) && s.Usage == 0x0001 && s.OutputLength >= 64 && s.InputLength >= 4;

    public static IEnumerable<BrandFamily> Families =>
    [
        // Also where Windows lists report 0x55 as an input report alone, as on the MCHOSE V9 PRO's receiver (BrandWay.Undeclared;
        // not measured on FIFINE's).
        new("FIFINE", Takes, _ => [new BrandWay("fifine.state", WayTransport.Output, Request(), Battery) { TimeoutMs = 1000, Undeclared = true }])
        { Kind = _ => DeviceKind.Headphones, Prefix = "fifine" },
    ];
}
