using BrimDeck.Core;

// Reads moved to myfinder-device-battery's facts where BrimDeck had no measurement of its own, the measured points kept: VGN's
// 5088 and beiying receivers, its ryMs mouse receivers, WEIKAV's NUT75, the 0C45:FEFE receiver, BETOP's Kunpeng 20 Elite,
// XIBERIA, the EWEADN X23 Pro and S9 PRO 8K, Keychron and its partners, and Flydigi.
static class ReferenceKeyboardTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    private static byte[] Pad(int length, params byte[] head) => BrandFamilies.Frame(length, head);
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, feature, outputIds, featureIds, name);
    private static string? Brand(HidShape shape) => BrandFamilies.Find(shape) is { } found && found.Ways.Count > 0 ? found.Family.Brand : null;
    private static BrandWay? Way(HidShape shape, string name) => BrandFamilies.Find(shape) is { } found ? found.Ways.FirstOrDefault(way => way.Name == name) : null;

    public static void Run(Action<string, bool> check)
    {
        Vgn5088(check);
        Nut75(check);
        Fefe(check);
        Beiying(check);
        RyMs(check);
        Betop(check);
        Xiberia(check);
        X23(check);
        S9Pro(check);
        Keychron(check);
        Flydigi(check);
    }

    private static void Vgn5088(Action<string, bool> check)
    {
        check("VGN 5088: waited on for 2500 ms (the receiver answers after about 1.03 s); ATK's keyboards keep their 500 ms",
            Dongles.VgnWaitMs == 2500);
        check("VGN 5088: 04 00 00 1A 06 .. 5D reads 93 %, byte 9 not read as charging; 06 at 4 is required; the request read back is none",
            Dongles.VgnBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x06, 0, 0, 0, 0x5D)) == new BatteryRead(93)
            && Dongles.VgnBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x06, 0, 0, 0, 0x5D, 0x01)) == new BatteryRead(93)
            && Dongles.VgnBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x00, 0, 0, 0, 0x5D)) is null
            && Dongles.VgnBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x06, 0, 0, 0, 0x65)) is null
            && Dongles.VgnBattery(Dongles.VgnRequest()) is null);
        check("VGN 5088: the asleep frame 04 00 00 FF 06 is still asleep",
            Dongles.VgnBattery(Pad(64, 0x04, 0, 0, 0xFF, 0x06)) == new BatteryRead(null, Asleep: true));
        check("ATK keyboards: the Z87 Pro's read and the HUB's receivers keep charging at 9 and need no 06 at 4",
            Dongles.EvisionKeyboardBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x06, 0, 0, 0, 0x5C, 0x01)) == new BatteryRead(92, true)
            && Dongles.EvisionKeyboardBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x00, 0, 0, 0, 0x5C)) == new BatteryRead(92)
            && Dongles.AtkKeyboardBattery(Pad(64, 0x04, 0, 0, 0x1A, 0x06, 0, 0, 0, 0x5C, 0x02)) == new BatteryRead(92, true)
            && Dongles.Find(0x320F, 0x5088, 0xFF1C, "", 0) == Dongles.Protocol.Vgn);
    }

    private static void Nut75(Action<string, bool> check)
    {
        var nut = Shape(0x0C45, 0xFEF9, 0xFF60, 0x61, 2, 65, 65, outputIds: [0x00], name: "NUT75 2.4G");
        var request = Way(nut, "huafenda.info")?.Request;
        check("WEIKAV NUT75: its FF60 / 0x61 65-byte collection is asked on any interface, with 0x30 as the length byte",
            Brand(nut) == "Huafenda keyboard" && request is { Length: 65 } && request.Take(9).SequenceEqual(Bytes("00 AA 10 30 00 00 00 01 00")) && request.Skip(9).All(b => b == 0)
            && Brand(nut with { Interface = 3 }) == "Huafenda keyboard" && Brand(nut with { Interface = 0 }) == "Huafenda keyboard"
            && Brand(nut with { Interface = 4 }) is null);
        check("WEIKAV NUT75: a 33-byte collection under that name, or FEF9 under another name off interface 3, is not asked",
            Brand(nut with { InputLength = 33, OutputLength = 33 }) is null && Brand(nut with { Name = "2.4G Receiver" }) is null);
        var other = nut with { Interface = 3, Name = "2.4G Receiver" };
        check("FEF9 under another name: interface 3 keeps the 0x38 read",
            Brand(other) == "Huafenda keyboard" && Way(other, "huafenda.info")!.Request.Take(4).SequenceEqual(Bytes("00 AA 10 38")));
        var answer = Pad(64, 0x55, 0x10, 0x30); answer[25] = 77;
        check("WEIKAV NUT75: 55 10 .. with 77 at 25 reads 77 %",
            Way(nut, "huafenda.info")!.Parse(answer) == new BatteryRead(77));
    }

    private static void Fefe(Action<string, bool> check)
    {
        var fefe = Shape(0x0C45, 0xFEFE, 0xFF60, 0x61, 3, 33, 33, outputIds: [0x00]);
        // The AULA F87 Pro V2D's answer (myfinder-device-battery's contributor): 48 % at 25; a 1 put at 26 to show it is not read.
        var answer = Bytes("55 10 18 00 00 00 00 00 00 00 00 12 A6 38 11 27 66 01 00 00 66 01 00 00 02 30 01 00 00 00 00 00");
        check("0C45:FEFE: the Huafenda read gives the level alone, the charge byte not read",
            Brand(fefe) == "EWEADN" && Way(fefe, "huafenda.info")!.Parse(answer) == new BatteryRead(48)
            && Way(fefe, "huafenda.info")!.Request.Take(9).SequenceEqual(Bytes("00 AA 10 18 00 00 00 00 00")));
        var epomaker = Shape(0x0C45, 0x8006, 0xFF68, 0x61, -1, 33, 33, outputIds: [0x00]);
        check("Other Huafenda devices keep reading charging at 26",
            Way(epomaker, "huafenda.info")!.Parse(answer) == new BatteryRead(48, true));
    }

    // 13 4A 01 00 <metadata> <percent> <state>, zeros, the low byte of the sum of bytes 0 to 18 at 19.
    private static byte[] Beiying(byte metadata, byte percent, byte state)
    {
        var frame = Pad(20, 0x13, 0x4A, 0x01, 0x00, metadata, percent, state);
        int sum = 0; for (int i = 0; i < 19; i++) sum += frame[i];
        frame[19] = (byte)sum;
        return frame;
    }

    private static void Beiying(Action<string, bool> check)
    {
        var v98 = Way(Shape(0x258A, 0x024F, 0xFF02, 0x0002, -1, 20, 20, outputIds: [0x13]), "vgn.13")!;
        var v87 = Way(Shape(0x258A, 0x027B, 0xFF02, 0x0002, -1, 20, 20, outputIds: [0x13]), "vgn.13")!;
        var n75 = Way(Shape(0x391D, 0x2A01, 0xFF02, 0x0002, -1, 20, 20, outputIds: [0x13]), "vgn.13")!;
        var v108 = Way(Shape(0x391D, 0x2A02, 0xFF02, 0x0002, 1, 25, 20, outputIds: [0x13]), "vgn-v108.battery")!;
        check("VGN beiying (#190 on 258A:024F): 99 % with bit 0x10 of 6 charging, without it not; byte 4 not checked",
            v98.Parse(Beiying(0x02, 99, 0x10)) == new BatteryRead(99, true) && v98.Parse(Beiying(0x02, 99, 0x00)) == new BatteryRead(99)
            && v98.Parse(Beiying(0x00, 99, 0x00)) == new BatteryRead(99) && v98.Parse(Beiying(0x07, 99, 0x10)) == new BatteryRead(99, true));
        check("VGN beiying: only bit 0x10 is charging (0x20 is not), 0 to 100 is read, 101, a wrong sum and the request read back are none",
            v98.Parse(Beiying(0x02, 50, 0x20)) == new BatteryRead(50) && v98.Parse(Beiying(0x02, 0, 0x00)) == new BatteryRead(0)
            && v98.Parse(Beiying(0x02, 101, 0x00)) is null && v98.Parse([.. Beiying(0x02, 50, 0x00)[..^1], 0]) is null
            && v98.Parse(VgnV108.Request()) is null && v98.Parse(Pad(20, 0x13, 0x4A, 0x01, 0x01, 0x02, 50)) is null);
        check("VGN beiying: the other receivers give the level alone",
            v87.Parse(Beiying(0x02, 80, 0x10)) == new BatteryRead(80) && n75.Parse(Beiying(0x02, 80, 0x10)) == new BatteryRead(80));
        check("VGN V108 (2A02): pull request #278's 100 % answer reads as before; charging not shown",
            v108.Parse(Bytes("13 4A 01 00 02 64 01 00 00 00 00 00 00 00 00 00 00 00 00 C5")) == new BatteryRead(100)
            && v108.Parse(Beiying(0x02, 50, 0x10)) == new BatteryRead(50));
    }

    private static void RyMs(Action<string, bool> check)
    {
        static HidShape Receiver(int vendor, int product) => Shape(vendor, product, 0xFFFF, 0x0002, 1, 0, 0, 65, featureIds: [0x00]);
        var ryms = new[] { 0x1A04, 0x1A08, 0x1A13, 0x1A25, 0x1A2B, 0x1A41 }.Select(p => Way(Receiver(0x391D, p), "vgn.status")).ToList();
        check("VGN ryMs receivers: the status read back 60 ms after it is set, F7 with Rongyuan's checksum",
            ryms.All(way => way is { GapMs: 60, FeatureLength: 65 } && way.Request.Take(9).SequenceEqual(Bytes("00 F7 00 00 00 00 00 00 08"))));
        var way = ryms[1]!;
        check("VGN ryMs: 0 at 1 required, the pairing byte not looked at, the level at 3 and sleep at 5",
            way.Parse(Pad(65, 0x00, 0x00, 0x00, 92, 0x00, 0x00, 0x00, 0x00)) == new BatteryRead(92)
            && way.Parse(Pad(65, 0x00, 0x00, 0x00, 92, 0x00, 0x00, 0x00, 0x02)) == new BatteryRead(92)
            && way.Parse(Pad(65, 0x00, 0x01, 0x00, 92, 0x00, 0x00, 0x00, 0x02)) is null
            && way.Parse(Pad(65, 0x00, 0x00, 0x00, 92, 0x00, 0x01, 0x00, 0x02)) == new BatteryRead(null, Asleep: true));
        check("VGN ryMs: 0 to 100 read, 101, a sleep byte of 2, zeros and the request read back none",
            way.Parse(Pad(65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02)) == new BatteryRead(0)
            && way.Parse(Pad(65, 0x00, 0x00, 0x00, 101)) is null && way.Parse(Pad(65, 0x00, 0x00, 0x00, 50, 0x00, 0x02)) is null
            && way.Parse(new byte[65]) is null && way.Parse(RongyuanStatus.Request(true)) is null);
        var monsgeek = Way(Receiver(0x3151, 0x5020), "rongyuan.status");
        var cable = Way(Receiver(0x391D, 0x1004), "vgn.status");
        check("Other mouse receivers (MonsGeek's 5020, VGN's cable 1004) keep their 100 ms and the pairing check",
            monsgeek is { GapMs: 100 } && cable is { GapMs: 100 }
            && monsgeek.Parse(Pad(65, 0x00, 0x01, 0x00, 92, 0x00, 0x00, 0x00, 0x02)) == new BatteryRead(92)
            && monsgeek.Parse(Pad(65, 0x00, 0x00, 0x00, 92, 0x00, 0x00, 0x00, 0x01)) is null);
    }

    private static void Betop(Action<string, bool> check)
    {
        check("BETOP: the Kunpeng 20 Elite's receiver (20BC:5191) is not asked; other 20BC receivers still are",
            Brand(Shape(0x20BC, 0x5191, 0x00FF, 0x0003, -1, 64, 64, outputIds: [0x02])) is null
            && Brand(Shape(0x20BC, 0x5128, 0x00FF, 0x0003, -1, 64, 64, outputIds: [0x02])) == "BETOP");
    }

    private static void Xiberia(Action<string, bool> check)
    {
        var t7 = Shape(0x36F9, 0xF008, 0xFF90, 1, outputIds: [0x55]);
        check("XIBERIA: only 0xFF90 / 1 and 0xFF82 / 1 are asked; other vendor pages and usages are not",
            Brand(t7) == "XIBERIA" && Brand(t7 with { UsagePage = 0xFF82 }) == "XIBERIA"
            && Brand(t7 with { UsagePage = 0xFF00 }) != "XIBERIA" && Brand(t7 with { UsagePage = 0x00FF }) != "XIBERIA" && Brand(t7 with { Usage = 2 }) != "XIBERIA");
        check("XIBERIA: nothing is asked while XIBERIA HUB runs",
            BrandFamilies.Find(t7)!.Value.Family.StandAsideFor.SequenceEqual(["XIBERIA HUB.exe"]));
    }

    private static void X23(Action<string, bool> check)
    {
        var x23 = Shape(0x04F3, 0x026F, 0x000A, 0x00, 1, 32, 0, name: "X23 Pro Receiver") with { InputIds = new HashSet<byte> { 0x04 } };
        var listen = BrandFamilies.Find(x23)!.Value.Ways;
        check("EWEADN X23 Pro: its receiver is listened to for 1200 ms, once (its report comes about every 5 s), nothing sent",
            listen.Count == 1 && listen[0] is { Name: "ryunix.x23", Transport: WayTransport.Listen, TimeoutMs: 1200, Attempts: 1, Request.Length: 0 });
        check("EWEADN X23 Pro: its report's id first and not all zeros, the level at 4 alone (0 to 100), byte 5 not read",
            listen[0].Parse(Bytes("04 01 02 04 54 01 02")) == new BatteryRead(84) && listen[0].Parse(Bytes("04 00 02 04 54 00 02")) == new BatteryRead(84)
            && listen[0].Parse(Bytes("05 01 02 04 54 01 02")) is null && listen[0].Parse(Bytes("04 00 00 00 00 00 00")) is null && listen[0].Parse(Bytes("04 01 02 04 65 01 02")) is null
            && BrandFamilies.Find(x23 with { InputLength = 33 })!.Value.Ways.Count == 0);
        var ryunix = BrandFamilies.Find(x23 with { Name = "" })!.Value.Ways;
        check("Ryunix's own (the XS Flow 2 capture) keep 600 ms and charging at 5",
            ryunix.Count == 1 && ryunix[0] is { Name: "ryunix.listen", TimeoutMs: 600 } && ryunix[0].Parse(Bytes("04 01 02 04 54 01 02")) == new BatteryRead(84, true));
    }

    private static void S9Pro(Action<string, bool> check)
    {
        var way = Way(Shape(0x372E, 0x1026, 0xFF00, 0x01, -1, 0, 0, 64, featureIds: [0x03]), "eweadn.beiyin")!;
        check("EWEADN S9 PRO 8K: read back 100 ms after it is set; the request keeps its sequence 02",
            way is { GapMs: 100, Transport: WayTransport.Feature } && way.Request[4] == 0x02);
        check("EWEADN S9 PRO 8K: a level of 0 is no reading (not asleep); [4] 02 or 00 is read",
            way.Parse(Pad(64, Bytes("03 50 00 0B 02 01 00"))) is null && way.Parse(Pad(64, Bytes("03 50 00 0B 02 01 80"))) is null
            && way.Parse(Pad(64, Bytes("03 50 00 0B 02 01 49"))) == new BatteryRead(73) && way.Parse(Pad(64, Bytes("03 50 00 0B 00 01 C9"))) == new BatteryRead(73, true));
    }

    private static void Keychron(Action<string, bool> check)
    {
        static byte[] Reply(byte level) { var r = Pad(64, 0xB4, 0x06); r[20] = level; return r; }
        var parses = new[]
        {
            Way(Shape(0x3434, 0xD060, 0xFFC1, 1, outputIds: [0xB3], featureIds: [0xB3]), "keychron.status"),
            Way(Shape(0x3434, 0xD060, 0xFFC1, 1, outputIds: [0xB3], featureIds: [0xB3]), "keychron.status-feature"),
            Way(Shape(0x362D, 0xD107, 0x008C, 1, outputIds: [0xB3]), "jingzao.status"),
            Way(Shape(0x0BDA, 0xFFE6, 0xFFC1, 1, outputIds: [0xB3]), "machenike.status"),
            Way(Shape(0x391D, 0x1A20, 0xFFC1, 1, outputIds: [0xB3]), "vgn.b3"),
        };
        check("Keychron and the brands on its platform: B3 06 on the 0xFFC1 collection, the M6's output report kept",
            parses.All(way => way is not null && way.Request.SequenceEqual(new byte[] { 0xB3, 0x06 })) && parses[0]!.Transport == WayTransport.Output);
        // The charging bit stays: the JZM5's receiver shows it flipping with the cable (LANMIN-X/Jingzao-JZM5-Battery, measured).
        check("Keychron and the brands on its platform: the low 7 bits of 20 the percent, bit 7 charging (measured on the JZM5)",
            parses.All(way => way!.Parse(Reply(85)) == new BatteryRead(85) && way.Parse(Reply(100)) == new BatteryRead(100)
                && way.Parse(Reply(0xD5)) == new BatteryRead(85, true) && way.Parse(Reply(101)) is null && way.Parse(Pad(64, 0xB3, 0x06)) is null));
    }

    private static void Flydigi(Action<string, bool> check)
    {
        static byte[] Info(byte battery) => [0x04, .. BrandFamilies.Frame(31, 0x5A, 0xA5, 0x01, 0x01, 0x00, 0x80, 0x02, 0, 0, 0, 0, battery)];
        var apex5 = BrandFamilies.Find(Shape(0x37D7, 0x2501, 0xFFA0, 1, 2, 33, 33, outputIds: [0x03]))!.Value.Ways[0];
        check("Flydigi: the request's last byte is the sum of the two before it (03 5A A5 01 02 03)",
            apex5.Request.SequenceEqual(Bytes("03 5A A5 01 02 03"))
            && BrandFamilies.Find(Shape(0x37D7, 0x2401, 0xFFA0, 1, 1, 33, 33, outputIds: [0x00]))!.Value.Ways[0].Request.SequenceEqual(Bytes("00 5A A5 01 02 03")));
        check("Flydigi: high nibble 1 is charging without a level, 2 is none, 0 the step × 20",
            apex5.Parse(Info(0x13)) == new BatteryRead(null, true) && apex5.Parse(Info(0x25)) is null && apex5.Parse(Info(0x20)) is null
            && apex5.Parse(Info(0x04)) == new BatteryRead(80, Accuracy: BatteryAccuracy.Steps) && apex5.Parse(Info(0x06)) is null);
        check("Flydigi: the brand's other pads on 37D7 are still read",
            Brand(Shape(0x37D7, 0x2401, 0xFFA0, 1, 1, 33, 33, outputIds: [0x00])) == "Flydigi" && Brand(Shape(0x37D7, 0x2601, 0xFFA0, 1, 1, 33, 33, outputIds: [0x00])) == "Flydigi");
    }
}
