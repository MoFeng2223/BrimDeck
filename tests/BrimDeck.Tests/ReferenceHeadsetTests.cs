using BrimDeck.Core;

// Headset reads taken from myfinder-device-battery's facts where BrimDeck had no measurement of its own: the SteelSeries Arctis
// Pro Wireless and classic models, the HyperX charging states and the Cloud Alpha 2's collection, ATK's N9 V2 answers, and the
// pairs left out of ZhuAudio's C-Media list.
static class ReferenceHeadsetTests
{
    private static byte[] Hex(string hex, int length = 0)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        return bytes.Length >= length ? bytes : BrandFamilies.Frame(length, bytes);
    }
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, byte[]? outputIds = null)
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, 0, outputIds);
    private static string? Brand(HidShape shape) => BrandFamilies.Find(shape) is { } found && found.Ways.Count > 0 ? found.Family.Brand : null;
    private static readonly BatteryRead Asleep = new(null, Asleep: true);

    public static void Run(Action<string, bool> check)
    {
        ProWireless(check);
        ClassicSteelSeries(check);
        HyperX(check);
        Atk(check);
        Zhuaudio(check);
    }

    private static void ProWireless(Action<string, bool> check)
    {
        var shape = Shape(0x1038, 0x1290, 0xFF00, 1, 0, 32, 31, [0x40, 0x41]);
        check("Arctis Pro Wireless: a vendor collection on interface 0 (or an unnamed one) with 31-byte output reports; other interfaces, pages and shorter reports are not asked",
            Brand(shape) == "SteelSeries Headset" && Brand(shape with { Interface = -1 }) == "SteelSeries Headset" && Brand(shape with { UsagePage = 0xFFC0 }) == "SteelSeries Headset"
            && Brand(shape with { Interface = 3 }) is null && Brand(shape with { Interface = 5 }) is null
            && Brand(shape with { UsagePage = 0x000C }) is null && Brand(shape with { UsagePage = 0x00FF }) is null
            && Brand(shape with { OutputLength = 30 }) is null
            && Brand(Shape(0x1038, 0x1290, 0xFF00, 1, 0, 32, 31, [0x06])) is null);
        var way = BrandFamilies.Find(shape)!.Value.Ways.Single();
        check("Arctis Pro Wireless: the gate 41 AA and the level 40 AA, each 31 bytes with the report id; the report id 0 taken off the answers; within the limits",
            way.Name == "steelseries-headset.pro-wireless" && way.Transport == WayTransport.Output
            && way.Gate is { Length: 31 } gate && gate.SequenceEqual(Hex("41 AA", 31)) && way.Gated is not null
            && way.Request.Length == 31 && way.Request.SequenceEqual(Hex("40 AA", 31))
            && way.StripZero && way.Then is null && way.Attempts is >= 1 and <= 2 && way.TimeoutMs <= 1000
            && BrandFamilies.Find(shape)!.Value.Family.Kind(shape) == DeviceKind.Headphones);
        var on = way.Gated!;
        check("Arctis Pro Wireless: the gate's answer 04 is the headset on, 02 off (asleep); anything else is not its answer",
            on(Hex("04", 31)) == true && on(Hex("02", 31)) == false && on(Hex("00", 31)) is null && on(Hex("01", 31)) is null
            && on(Hex("41 AA", 31)) is null && on([]) is null
            && SteelSeriesHeadsets.ProWirelessOn(Hex("04")) == true && SteelSeriesHeadsets.ProWirelessGate().SequenceEqual(way.Gate!));
        check("Arctis Pro Wireless: the level 0 to 4 in the first byte, 25 % a step, no charging state; over 4 is none",
            way.Parse(Hex("00", 31)) == new BatteryRead(0, Accuracy: BatteryAccuracy.Steps)
            && way.Parse(Hex("01", 31)) == new BatteryRead(25, Accuracy: BatteryAccuracy.Steps)
            && way.Parse(Hex("03", 31)) == new BatteryRead(75, Accuracy: BatteryAccuracy.Steps)
            && way.Parse(Hex("04 01", 31)) == new BatteryRead(100, Accuracy: BatteryAccuracy.Steps)
            && way.Parse(Hex("05", 31)) is null && way.Parse(Hex("40 AA", 31)) is null && way.Parse([]) is null);
        check("Arctis Pro Wireless: not a Headsets model",
            Headsets.Find(0x1038, 0x1290) is null && SteelSeriesHeadsets.Find(0x1290) == SteelSeriesHeadsets.Exchange.ProWireless);
    }

    private static void ClassicSteelSeries(Action<string, bool> check)
    {
        check("SteelSeries classic models: 12AD asks the link first, 1260 and 1252 do not; 12C2 the Arctis 9, 12E0 and 12E5 the Nova Pro; the wired GameDAC 1280 is not read",
            Headsets.Find(0x1038, 0x12AD) == Headsets.Kind.Arctis7Linked && Headsets.Find(0x1038, 0x1260) == Headsets.Kind.Arctis7
            && Headsets.Find(0x1038, 0x1252) == Headsets.Kind.Arctis7 && Headsets.Find(0x1038, 0x12C2) == Headsets.Kind.Arctis9
            && Headsets.Find(0x1038, 0x12E0) == Headsets.Kind.NovaPro && Headsets.Find(0x1038, 0x12E5) == Headsets.Kind.NovaPro
            && Headsets.Find(0x1038, 0x1280) is null && SteelSeriesHeadsets.Find(0x1280) is null && SteelSeriesMice.Find(0x1038, 0x1280) is null
            && Headsets.Name(Headsets.Kind.Arctis7Linked) == "SteelSeries Arctis 7");
        check("SteelSeries classic models: only their interface's vendor collections are asked (Arctis 7 on 5, Arctis 9 on 0, Nova Pro on 3 and 4)",
            Headsets.Reads(Headsets.Kind.Arctis7Linked, 5, 0xFF00, 1) && Headsets.Reads(Headsets.Kind.Arctis7, 5, 0xFFC0, 1)
            && !Headsets.Reads(Headsets.Kind.Arctis7, 5, 0x000C, 1) && !Headsets.Reads(Headsets.Kind.Arctis7Linked, 3, 0xFF00, 1)
            && Headsets.Reads(Headsets.Kind.Arctis9, 0, 0xFF00, 1) && !Headsets.Reads(Headsets.Kind.Arctis9, 0, 0x0001, 6) && !Headsets.Reads(Headsets.Kind.Arctis9, 1, 0xFF00, 1)
            && Headsets.Reads(Headsets.Kind.NovaPro, 3, 0xFFC0, 1) && Headsets.Reads(Headsets.Kind.NovaPro, 4, 0xFF00, 1)
            && !Headsets.Reads(Headsets.Kind.NovaPro, 5, 0xFF00, 1) && !Headsets.Reads(Headsets.Kind.NovaPro, 4, 0x000C, 1) && !Headsets.Reads(Headsets.Kind.NovaPro, -1, 0xFF00, 1)
            && Headsets.AsksOne(Headsets.Kind.NovaPro) && !Headsets.AsksOne(Headsets.Kind.Arctis7) && !Headsets.AsksOne(Headsets.Kind.CloudIIIS));

        var arctis7 = Headsets.Kind.Arctis7; var linked = Headsets.Kind.Arctis7Linked;
        check("Arctis 7 (2018): 06 14 asked first, 03 at 2 linked, anything else not; then 06 18",
            Headsets.ConnectionRequest(linked) is { } ask && ask.SequenceEqual(Hex("06 14")) && Headsets.Request(linked).SequenceEqual(Hex("06 18"))
            && Headsets.Connected(linked, Hex("06 14 03 00")) == true && Headsets.Connected(linked, Hex("06 14 01 00")) == false
            && Headsets.Connected(linked, Hex("06 14 00 00")) == false && Headsets.Connected(linked, Hex("06 18 03 00")) is null
            && Headsets.Connected(linked, Hex("06 14 03")) is null && Headsets.ChargingRequest(linked) is null
            && Headsets.ConnectionRequest(arctis7) is null && Headsets.Connected(arctis7, Hex("06 14 03 00")) is null);
        check("Arctis 7 (2018): 06 18 <level>, over 100 counted as 100, 0 as 0 %; another report is none",
            Headsets.Battery(linked, Hex("06 18 50 00")) == new BatteryRead(80) && Headsets.Battery(linked, Hex("06 18 C8 00")) == new BatteryRead(100)
            && Headsets.Battery(linked, Hex("06 18 00 00")) == new BatteryRead(0)
            && Headsets.Battery(linked, Hex("06 14 50 00")) is null && Headsets.Battery(linked, Hex("06 18 50")) is null && Headsets.Battery(linked, Hex("07 18 50 00")) is null);
        check("Arctis 7 (1260, 1252): 06 18 <level>, 0 the headset off (asleep), over 100 counted as 100; the echo is required",
            Headsets.Request(arctis7).SequenceEqual(Hex("06 18"))
            && Headsets.Battery(arctis7, Hex("06 18 00 00")) == Asleep && Headsets.Battery(arctis7, Hex("06 18 40 00")) == new BatteryRead(64)
            && Headsets.Battery(arctis7, Hex("06 18 65 00")) == new BatteryRead(100) && Headsets.Battery(arctis7, Hex("06 12 40 00")) is null);

        var nine = Headsets.Kind.Arctis9;
        check("Arctis 9: 00 20; AA 01 <?> <raw> <charging>, the raw 0x64..0x9A clamped and rounded down; another AA or 55 answer the headset off; anything else none",
            Headsets.Request(nine).SequenceEqual(Hex("00 20"))
            && Headsets.Battery(nine, Hex("AA 01 00 80 00")) == new BatteryRead(51) && Headsets.Battery(nine, Hex("AA 01 00 80 01")) == new BatteryRead(51, true)
            && Headsets.Battery(nine, Hex("AA 01 00 9A 00")) == new BatteryRead(100) && Headsets.Battery(nine, Hex("AA 01 00 C0 00")) == new BatteryRead(100)
            && Headsets.Battery(nine, Hex("AA 01 00 64 00")) == new BatteryRead(0) && Headsets.Battery(nine, Hex("AA 01 00 10 00")) == new BatteryRead(0)
            && Headsets.Battery(nine, Hex("AA 00 00 80 00")) == Asleep && Headsets.Battery(nine, Hex("AA 03 00 80 00")) == Asleep
            && Headsets.Battery(nine, Hex("55 01 00 80 00")) == Asleep && Headsets.Battery(nine, Hex("55 00 00 00 00")) == Asleep
            && Headsets.Battery(nine, Hex("03 01 00 80 00")) is null && Headsets.Battery(nine, Hex("AA 01 00 80")) is null);

        var pro = Headsets.Kind.NovaPro;
        byte[] Pro(byte level, byte state, byte id = 0x06) { var r = new byte[31]; r[0] = id; r[1] = 0xB0; r[6] = level; r[15] = state; return r; }
        check("Nova Pro Wireless: 06 B0 (31 bytes); state 01 off (asleep), 02 charging, 08 on battery, the level a step of eight rounded down; other states and levels none",
            Headsets.Request(pro).Length == 31 && Headsets.Request(pro)[..2].SequenceEqual(Hex("06 B0"))
            && Headsets.Battery(pro, Pro(5, 0x08)) == new BatteryRead(62, Accuracy: BatteryAccuracy.Steps)
            && Headsets.Battery(pro, Pro(5, 0x02)) == new BatteryRead(62, true, BatteryAccuracy.Steps)
            && Headsets.Battery(pro, Pro(8, 0x08)) == new BatteryRead(100, Accuracy: BatteryAccuracy.Steps)
            && Headsets.Battery(pro, Pro(0, 0x08)) == new BatteryRead(0, Accuracy: BatteryAccuracy.Steps)
            && Headsets.Battery(pro, Pro(5, 0x01)) == Asleep && Headsets.Battery(pro, Pro(9, 0x01)) == Asleep
            && Headsets.Battery(pro, Pro(5, 0x04)) is null && Headsets.Battery(pro, Pro(5, 0x00)) is null && Headsets.Battery(pro, Pro(5, 0x03)) is null
            && Headsets.Battery(pro, Pro(9, 0x08)) is null && Headsets.Battery(pro, Pro(5, 0x08)[..15]) is null);
        check("Nova 7: the link byte 02 at 1 still the headset not connected",
            Headsets.Battery(Headsets.Kind.Nova7Percent, Hex("B0 02 50 03")) == Asleep && Headsets.Battery(Headsets.Kind.Nova7Steps, Hex("B0 02 03 01")) == Asleep
            && Headsets.Battery(Headsets.Kind.Nova7Percent, Hex("B0 03 50 03")) == new BatteryRead(80));
    }

    private static void HyperX(Action<string, bool> check)
    {
        var three = Headsets.Kind.CloudIII; var threeS = Headsets.Kind.CloudIIIS;
        check("HyperX Cloud III: the charging state 1 and 2 (full, still on the cable) charge, 0 does not, anything else says nothing",
            Headsets.Charging(three, Hex("66 8A 01 00")) == true && Headsets.Charging(three, Hex("66 8A 02 00")) == true && Headsets.Charging(three, Hex("66 0C 02 00")) == true
            && Headsets.Charging(three, Hex("66 8A 00 00")) == false && Headsets.Charging(three, Hex("66 8A 03 00")) is null && Headsets.Charging(three, Hex("66 89 02 00")) is null);
        check("HyperX Cloud III S: the charging state 1 and 2 charge, in its answer and in its own notice; 0 does not, anything else says nothing",
            Headsets.Charging(threeS, Hex("0C 02 03 01 00 48 02")) == true && Headsets.Charging(threeS, Hex("0C 02 03 01 00 48 01")) == true
            && Headsets.Charging(threeS, Hex("0C 02 03 01 00 48 00")) == false && Headsets.Charging(threeS, Hex("0C 02 03 01 00 48 03")) is null
            && Headsets.Charging(threeS, Hex("0D 00 00 00 0A 02")) == true && Headsets.Charging(threeS, Hex("0D 00 00 00 0A 00")) == false
            && Headsets.Charging(threeS, Hex("0D 00 00 00 0A 05")) is null);
        var alpha2 = Headsets.Kind.CloudAlpha2;
        check("HyperX Cloud Alpha 2: only its 0xFF13 usage 0xFF00 collection is asked",
            Headsets.Reads(alpha2, -1, 0xFF13, 0xFF00) && Headsets.Reads(alpha2, 3, 0xFF13, 0xFF00)
            && !Headsets.Reads(alpha2, -1, 0xFF13, 0x0001) && !Headsets.Reads(alpha2, -1, 0xFF00, 0xFF00) && !Headsets.Reads(alpha2, -1, 0x000C, 0xFF00)
            && Headsets.Reads(Headsets.Kind.CloudIII, -1, 0xFF13, 0x0001));
    }

    private static void Atk(Action<string, bool> check)
    {
        var shape = Shape(0x373B, 0x1262, 0xFF90, 1, -1, 22, 64, [0x55]);
        check("ATK N9 V2: input reports of 22 to 64 bytes are taken, shorter and longer are not; the output still 64",
            Brand(shape) == "ATK Headset" && Brand(shape with { InputLength = 64 }) == "ATK Headset" && Brand(shape with { InputLength = 40 }) == "ATK Headset"
            && Brand(shape with { InputLength = 21 }) is null && Brand(shape with { InputLength = 65 }) is null && Brand(shape with { OutputLength = 63 }) is null
            && BrandFamilies.Find(shape)!.Value.Ways.Single().Request.SequenceEqual(Hex("55 65 01 00")));
        check("ATK N9 V2: 02 on battery, 03 charging, 04 full with the reported percent and not charging; Voltide's measured state 02 answer stays 100 %",
            AtkHeadsets.StateBattery(Hex("55 65 64 02 02 00 00 04 00 00 00 00 01 02 01 00 05 02 00", 64)) == new BatteryRead(100)
            && AtkHeadsets.StateBattery(Hex("55 65 50 02", 22)) == new BatteryRead(80) && AtkHeadsets.StateBattery(Hex("55 65 32 03", 64)) == new BatteryRead(50, true)
            && AtkHeadsets.StateBattery(Hex("55 65 63 04", 64)) == new BatteryRead(99) && AtkHeadsets.StateBattery(Hex("55 65 50 04", 30)) == new BatteryRead(80)
            && AtkHeadsets.StateBattery(Hex("55 65 00 02", 64)) == new BatteryRead(0));
        check("ATK N9 V2: only 55 0A 01 is the headset disconnected; 1A the link lost; state 00, other states, over 100, other headers and lengths outside 22..64 are none",
            AtkHeadsets.StateBattery(Hex("55 0A 01", 64)) == Asleep && AtkHeadsets.StateBattery(Hex("55 0A 00", 64)) is null && AtkHeadsets.StateBattery(Hex("55 0A 02", 64)) is null
            && AtkHeadsets.StateBattery(Hex("55 65 40 1A", 64)) == Asleep
            && AtkHeadsets.StateBattery(Hex("55 65 01 00", 64)) is null && AtkHeadsets.StateBattery(Hex("55 65 40 05", 64)) is null && AtkHeadsets.StateBattery(Hex("55 65 40 01", 64)) is null
            && AtkHeadsets.StateBattery(Hex("55 65 65 02", 64)) is null && AtkHeadsets.StateBattery(Hex("55 AA 40 02", 64)) is null && AtkHeadsets.StateBattery(Hex("54 65 40 02", 64)) is null
            && AtkHeadsets.StateBattery(Hex("55 65 40 02", 21)) is null && AtkHeadsets.StateBattery(Hex("55 65 40 02", 65)) is null
            && AtkHeadsets.StateBattery(Hex("55 0A 01", 21)) is null);
        check("FIFINE and AULA A9 keep their own readings of the same frame (04 full: FIFINE 100 %, AULA A9 charging; 55 0A of any kind FIFINE's headset off)",
            Fifine.Battery(Hex("55 65 63 04", 64)) == new BatteryRead(100) && Fifine.Battery(Hex("55 65 32 03", 64)) == new BatteryRead(50, true)
            && Fifine.Battery(Hex("55 0A 00 00", 64)) == Asleep && Fifine.Battery(Hex("55 65 40 1A", 64)) == Asleep
            && Fifine.Battery(Hex("55 65 01 00", 64)) is null && Fifine.Battery(Hex("55 65 00 02", 64)) is null && Fifine.Battery(Hex("55 65 50 02", 4)) == new BatteryRead(80)
            && AulaMice.AulaA9(Hex("55 65 63 04", 64)) == new BatteryRead(99, true) && AulaMice.AulaA9(Hex("55 65 40 1A", 64)) == Asleep);
    }

    private static void Zhuaudio(Action<string, bool> check)
    {
        check("ZhuAudio C-Media: 0D8C:0334 (iKF V11 Max) and 0E6A:0110 (AULA G7) are no longer asked; the other INF pairs still are",
            !BrimDeck.Core.Zhuaudio.CmediaHeadsets.Contains((0x0D8C, 0x0334)) && !BrimDeck.Core.Zhuaudio.CmediaHeadsets.Contains((0x0E6A, 0x0110))
            && BrimDeck.Core.Zhuaudio.CmediaHeadsets.Length == 5
            && Brand(Shape(0x0D8C, 0x0334, 0xFF90, 1, -1, 64, 64, [0x55])) != "ZhuAudio C-Media"
            && Brand(Shape(0x0E6A, 0x0110, 0xFF90, 1, -1, 64, 64, [0x55])) != "ZhuAudio C-Media"
            && Brand(Shape(0x0D8C, 0x0334, 0xFF90, 1, -1, 64, 64, [])) != "ZhuAudio C-Media"
            && Brand(Shape(0x20F8, 0xA962, 0xFF90, 1, -1, 64, 64, [0x55])) == "ZhuAudio C-Media"
            && Brand(Shape(0x4C4A, 0x4255, 0xFF90, 1, -1, 64, 64, [0x55])) == "ZhuAudio C-Media");
    }
}
