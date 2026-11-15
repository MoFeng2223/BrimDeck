namespace BrimDeck.Core;

// Metapanda's (迈塔猩) NearLink mice on their 2.4 GHz receiver 369B:00B3 (MPMouseNearLink), usage page 0xFFA0 usage 2, 64-byte
// input and output reports. Output report 0xA0 with the one-byte status key 0x50 — the key the vendor SDK's gettlv defaults
// to, and the one its get_wireless_status polls — answered within a fraction of a second on input report 0xA1:
// A1 50 01 03 <length> <TLV...>, the level at 8. 188 while charging and 189 when charged stand in for the level (the vendor's
// own window keeps the last percent and shows a bolt); a status with nothing in it (0 at 8) is a heartbeat, no reading. The
// key that SDK's get_battery_led builds (A0 08 03 F0) is not answered by this firmware at all: what looked like a reply to it
// was a push arriving on its own, which is why reading appeared to need the vendor's driver open.
// Measured on one receiver on Windows: the level matched the vendor's window at the same moment (31 % and 0x1F), and the
// request was answered 4 times out of 4 over four rounds 2 s apart. Thunderobot's ML903 (369B:0234) shares the vendor id and
// the page but answers a different command (A0 02, tag fields), so the product is named here as well.
[BrandFamilies]
public static class MetapandaMice
{
    public const int Vendor = 0x369B, Mouse = 0x00B3, Page = 0xFFA0;
    public const byte Status = 0xA1, ReportId = 0xA0, Ask = 0x50, Charging = 188, Charged = 189;

    public static byte[] Request() => BrandFamilies.Frame(64, ReportId, Ask);

    public static BatteryRead? Battery(byte[] r)
    {
        // The second byte is the command answered: the receiver also sends A1 reports for other keys on this collection, and a
        // parse that takes one of them ends the read early with a level that was never asked for.
        if (r.Length < 9 || r[0] != Status || r[1] != Ask) return null;
        if (r[8] == Charging) return new(null, Charging: true);
        // 189 is the charged state, and the firmware never sends a level with it: 100 is what it means, and a null level would
        // read as "not reported" in the panel.
        if (r[8] == Charged) return new(100);
        return r[8] is > 0 and <= 100 ? new(r[8]) : null;
    }

    public static bool Takes(HidShape shape) => shape.Vendor == Vendor && shape.Product == Mouse
        && MouseShapes.Collection(shape, Page, 0x02) && shape.InputLength >= 9 && shape.OutputLength >= 2;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("Metapanda", Takes, _ => [new BrandWay("metapanda.status", WayTransport.Output, Request(), Battery) { TimeoutMs = 600 }])
        {
            Kind = _ => DeviceKind.Mouse, Prefix = "metapanda", DisplayName = _ => "Metapanda NearLink Mouse",
        },
    ];
}
