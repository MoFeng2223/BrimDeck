namespace BrimDeck.Core;

// VGN's Dragonfly3 SE on its receiver 391D:1A01 (0x391D is shared: this product alone), its 0xFF01 usage 0x10 collection with
// report 0 of 64 bytes each way (65 with the id on Windows). VGN HUB's "yongjiaxin" driver writes 55 30 and zeros (command 0x30,
// GetBattery, no checksum) and reads the answer by its command byte: 30 at 1, the percent at 8, and 1 at 9 for the mouse on its
// cable, which gives no level then (and is not taken as charging). From VGN HUB's web bundle as myfinder-device-battery's
// vgn_dragonfly3.py and upstream-protocols.md give it (AGPL; facts only; no answer from a mouse is quoted). The request read back
// has 0 at 8 and is no reading. The Dragonfly receivers on 1A04 and the like are RongyuanStatus's.
[BrandFamilies]
public static class VgnDragonfly3Se
{
    public const int Vendor = 0x391D, Receiver = 0x1A01;

    public static byte[] Request() => BrandFamilies.Frame(65, 0x00, 0x55, 0x30);
    // The answer without its zero report id.
    public static BatteryRead? Battery(byte[] r) => r.Length >= 10 && r[1] == 0x30 && r[9] != 1 && r[8] is > 0 and <= 100 ? new(r[8]) : null;

    public static bool Takes(HidShape s) => s.Vendor == Vendor && s.Product == Receiver && MouseShapes.Collection(s, 0xFF01, 0x0010)
        && s.InputLength == 65 && s.OutputLength == 65;

    public static IEnumerable<BrandFamily> Families =>
    [
        new("VGN Dragonfly3 SE", Takes, _ => [new BrandWay("vgn.dragonfly3se", WayTransport.Output, Request(), Battery) { TimeoutMs = 700, StripZero = true }])
        { Kind = _ => DeviceKind.Mouse, Prefix = "vgn-df3se", DisplayName = _ => "VGN Dragonfly3 SE" },
    ];
}
