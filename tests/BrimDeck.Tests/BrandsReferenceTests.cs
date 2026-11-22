using BrimDeck.Core;

// The models, reads and states added from myfinder-device-battery's facts: each new id goes to its documented collection alone,
// each reply is parsed as the source gives it (its users' frames where it quotes one), and what is not a reading stays none.
static class BrandsReferenceTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    private static byte[] Pad(int length, params byte[] head) => BrandFamilies.Frame(length, head);
    private static HidShape Shape(int vendor, int product, int page, int usage = 1, int iface = -1, int input = 64, int output = 64, int feature = 0,
        byte[]? outputIds = null, byte[]? featureIds = null, string name = "")
        => BrandFamilyTests.Shape(vendor, product, page, usage, iface, input, output, feature, outputIds, featureIds, name);
    private static string? Brand(HidShape shape) => BrandFamilies.Find(shape) is { } found && found.Ways.Count > 0 ? found.Family.Brand : null;
    private static readonly List<BrandWay> Seen = [];
    private static BrandWay Way(HidShape shape) { var way = BrandFamilies.Find(shape)!.Value.Ways[0]; Seen.Add(way); return way; }

    public static void Run(Action<string, bool> check)
    {
        Seen.Clear();
        NewReads(check);
        Ids(check);
        States(check);
        Evidence(check);
        check("Added reads: every one within the limits (1 to 2 attempts, at most 1000 ms)",
            Seen.Count >= 6 && Seen.All(way => way.Attempts is >= 1 and <= 2 && way.TimeoutMs <= 1000));
    }

    private static void NewReads(Action<string, bool> check)
    {
        var v9 = Shape(0x320F, 0x22BA, 0xFF1C, 0x0092, 1, outputIds: [0x04], name: "AULA-V9Pro+");
        check("AULA V9Pro+: its receiver's 0xFF1C collection under its own name takes 04 00 00 1A 00; another name, page or 320F product does not",
            Brand(v9) == "AULA V9Pro+" && Way(v9).Request.SequenceEqual(Pad(64, 0x04, 0x00, 0x00, 0x1A, 0x00))
            && Brand(v9 with { Name = "Wireless Receiver" }) is null && Brand(v9 with { UsagePage = 0xFF02 }) is null
            && Brand(v9 with { Product = 0x22BB }) is null && Dongles.Find(0x320F, 0x22BA, 0xFF1C, "AULA-V9Pro+", 0, 1, 0x0092, 64, 64) is null);
        check("AULA V9Pro+: 80 % off the cable and 82 % on it are read (charging at 9), the request read back, 0 % and an unknown 9 are none",
            Report04Status.Aula(Pad(64, 0x04, 0, 0, 0x1A, 0, 0, 0, 0, 80, 0)) == new BatteryRead(80)
            && Report04Status.Aula(Pad(64, 0x04, 0, 0, 0x1A, 0, 0, 0, 0, 82, 1)) == new BatteryRead(82, true)
            && Report04Status.Aula(Report04Status.AulaRequest()) is null && Report04Status.Aula(Pad(64, 0x04, 0, 0, 0x1A, 0, 0, 0, 0, 82, 2)) is null);

        var g3m = Shape(0x320F, 0x706E, 0xFF1C, 0x0092, outputIds: [0x04]);
        check("HECATE G3M PRO: 04 20 00 1A 06 (bytes 1 and 2 the sum of 3 to 63) on its 0xFF1C collection",
            Brand(g3m) == "HECATE G3M PRO" && Report04Status.SumRequest().SequenceEqual(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06)) && Way(g3m).Request.SequenceEqual(Report04Status.SumRequest()));
        check("HECATE G3M PRO: the percent at 8 is read; FE or FF at 7, the request read back and 0 % are none",
            Report04Status.Hecate(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0, 64)) == new BatteryRead(64)
            && Report04Status.Hecate(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0xFE, 64)) is null
            && Report04Status.Hecate(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0xFF, 64)) is null && Report04Status.Hecate(Report04Status.SumRequest()) is null);

        var cherry = Shape(0x046A, 0x01E0, 0xFF1C, 0x0092, 1, outputIds: [0x04], name: "MX 2.0S Pro Dongle");
        check("CHERRY MX 2.0S WL Pro: its dongle and its cable under their own names take the summed request; PIXIU75's ids stay Dongles'",
            Brand(cherry) == "CHERRY MX 2.0S" && Brand(cherry with { Product = 0x01DE, Name = "MX 2.0S WL Pro" }) == "CHERRY MX 2.0S"
            && Brand(cherry with { Name = "MX 2.0S WL Pro" }) is null && Brand(cherry with { Interface = 2 }) is null
            && Dongles.Find(0x046A, 0x01E4, 0xFF1C, "", 0, 1, 0x0092, 64, 64) == Dongles.Protocol.Cherry);
        check("CHERRY MX 2.0S WL Pro: 55 % and 58 % charging (the contributor's readings) are read; 7 not 0, 9 above 1 and another head are none",
            Report04Status.Cherry(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0, 55, 0)) == new BatteryRead(55)
            && Report04Status.Cherry(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0, 58, 1)) == new BatteryRead(58, true)
            && Report04Status.Cherry(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 1, 55, 0)) is null
            && Report04Status.Cherry(Pad(64, 0x04, 0x20, 0x00, 0x1A, 0x06, 0, 0, 0, 55, 2)) is null
            && Report04Status.Cherry(Pad(64, 0x04, 0x00, 0x00, 0x1A, 0x04, 0, 0, 0, 55, 0)) is null && Report04Status.Cherry(Report04Status.SumRequest()) is null);

        var df3 = Shape(0x391D, 0x1A01, 0xFF01, 0x0010, input: 65, output: 65, outputIds: [0x00]);
        check("VGN Dragonfly3 SE: 00 55 30 and zeros (65 bytes) on its 0xFF01 / 0x10 collection, read without the zero id",
            Brand(df3) == "VGN Dragonfly3 SE" && Way(df3).Request.SequenceEqual(Pad(65, 0x00, 0x55, 0x30)) && Way(df3).StripZero
            && Brand(df3 with { Product = 0x1A03 }) is null && Brand(df3 with { UsagePage = 0xFF05 }) is null);
        check("VGN Dragonfly3 SE: 30 at 1 and the percent at 8 are read; 1 at 9 (on the cable) and the request read back are none",
            VgnDragonfly3Se.Battery(Pad(64, 0xAA, 0x30, 0, 0, 0, 0, 0, 0, 72, 0)) == new BatteryRead(72)
            && VgnDragonfly3Se.Battery(Pad(64, 0xAA, 0x30, 0, 0, 0, 0, 0, 0, 72, 1)) is null && VgnDragonfly3Se.Battery(Pad(64, 0x55, 0x30)) is null);

        var m1 = Shape(0x088D, 0x052F, 0xFF00, 1, 1, input: 0, output: 0, feature: 33, featureIds: [0x0F], name: "Wireless-Receiver");
        var m1Frame = Bytes("0F 00 30 02 EC 01 10 4D 35 33 36 01 06 4C 58 11 11 01 41 00 FF 00 FF D0 00 0C 31 BE 00 5B 81 2C C8");
        check("LINGBAO M1 Pro: feature report 0x0F (33 bytes) only got back on its receiver, EWEADN's 088D cable untouched by it",
            Brand(m1) == "LINGBAO M1 Pro" && Way(m1) is { Transport: WayTransport.FeatureGet, FeatureLength: 33 } way && way.Request.SequenceEqual(new byte[] { 0x0F })
            && Brand(m1 with { FeatureLength = 65 }) is null && Brand(m1 with { Name = "2.4G Receiver" }) is null && Brand(m1 with { Product = 0x062E }) != "LINGBAO M1 Pro");
        var m1Cable = (byte[])m1Frame.Clone(); m1Cable[18] = 100; m1Cable[19] = 1;
        var m1Other = (byte[])m1Frame.Clone(); m1Other[9] = (byte)'4';
        check("LINGBAO M1 Pro: the user's frame reads 65 %; the cable's placeholder (flag 1) and another model's ids are none",
            LingbaoM1Pro.Battery(m1Frame) == new BatteryRead(65) && LingbaoM1Pro.Battery(m1Cable) is null && LingbaoM1Pro.Battery(m1Other) is null);

        var gm01 = Shape(0x35D8, 0x0133, 0x000C, 1, outputIds: [0x4B, 0x54]) with { InputIds = new HashSet<byte> { 0x4B, 0x54 } };
        check("MOONDROP GM01 Pro: 4B 80 27 00 to the consumer (or 0xFF02) collection that declares 0x4B both ways, and to no other",
            Brand(gm01) == "MOONDROP" && Way(gm01).Request.SequenceEqual(Pad(64, 0x4B, 0x80, 0x27, 0x00))
            && Brand(Shape(0x35D8, 0x0133, 0xFF02, 2, outputIds: [0x02]) with { InputIds = new HashSet<byte> { 0x02 } }) is null
            && Brand(gm01 with { InputIds = new HashSet<byte>() }) is null && Brand(gm01 with { UsagePage = 0x000B, Usage = 0x0005 }) is null
            && Brand(gm01 with { UsagePage = 0xFF02, Usage = 0x0002 }) == "MOONDROP");
        check("MOONDROP GM01 Pro: the user's answer reads 50 %; 0 at 8 (no level) and another command are none",
            Moondrop.Battery(Pad(64, 0x4B, 0x80, 0x27, 0x06, 0, 0, 0, 0, 0x01, 0x32)) == new BatteryRead(50)
            && Moondrop.Battery(Pad(64, 0x4B, 0x80, 0x27, 0x06, 0, 0, 0, 0, 0x00, 0x32)) is null
            && Moondrop.Battery(Pad(64, 0x4B, 0x80, 0x31, 0x06, 0, 0, 0, 0, 0x01, 0x32)) is null
            && Moondrop.Battery(Pad(64, 0x4B, 0x80, 0x27, 0x3D, 0, 0, 0, 0, 0x01, 0x32)) is null);
    }

    private static void Ids(Action<string, bool> check)
    {
        check("HyperX: the Cloud III Wireless on 0FCC takes the Cloud III read", Headsets.Find(0x03F0, 0x0FCC) == Headsets.Kind.CloudIII);
        check("Sony INZONE H5: its 0xFF04 collection as usage 1 too",
            Brand(Shape(0x054C, 0x0EBF, 0xFF04, 1, outputIds: [0x02])) == "Sony INZONE" && Brand(Shape(0x054C, 0x0EBF, 0xFF04, 2, outputIds: [0x02])) == "Sony INZONE"
            && Brand(Shape(0x054C, 0x0EBF, 0xFF04, 3, outputIds: [0x02])) is null);
        check("Razer: the BlackShark V2 Pro 2023's third id (0557) takes the 90-byte read (0x3F), not the PA read and its remote mode",
            Razer.Find(0x0557) is { Transaction: 0x3F, Charging: false, Kind: DeviceKind.Headphones } && Brand(Shape(0x1532, 0x0557, 0xFF00, 1, 3, outputIds: [0x02])) is null);
        check("MCHOSE: the rest of M HUB's keyboard codes name the keyboard behind the 3554:FA09 / 41E4:2001 receiver",
            Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x02, 0x1C)) == "MCHOSE G87"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x34)) == "MCHOSE G87 V2"
            && Dongles.AulaModel(Fa09(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0x15)) == "MCHOSE G75 Pro");
        check("MCHOSE: the V9 Turbo's light control (6008) is sent nothing; its dongle (600A) keeps the headset reads",
            Dongles.MchoseReads(0x6008, "V9 Turbo", 0xFF00, 1, 0, 64).Count == 0 && Dongles.MchoseKind(0x6008, "V9 Turbo") == DeviceKind.Other
            && Dongles.MchoseKind(0x600A, "") == DeviceKind.Headphones);
        check("Twins: the ATK A9 Air+ (1278 / 1293), EWEADN's receiver and cable, and the Cloud II Core's two ids show once",
            Dongles.Twins.Contains(("373B:1278", "373B:1293")) && Dongles.Twins.Contains(("089D:062F", "088D:062E")) && Dongles.Twins.Contains(("391D:1A04", "391D:1004"))
            && Dongles.CableWins.Contains(("03F0:0995", "03F0:0795")));
        check("Standard HID: the Battery System page's RelativeStateOfCharge (0x85 / 0x64) is a battery level", HidBattery.IsBattery(0x85, 0x64));
        check("Names: BlackShark and Barracuda are headphones, BlackWidow and DeathStalker keyboards",
            Devices.Named("Razer BlackShark V2 Pro") == DeviceKind.Headphones && Devices.Named("Razer Barracuda") == DeviceKind.Headphones
            && Devices.Named("Razer BlackWidow V3 Pro") == DeviceKind.Keyboard && Devices.Named("Razer DeathStalker V2 Pro") == DeviceKind.Keyboard
            && Devices.Named("Logitech G733") == DeviceKind.Headphones);
        check("Inphic IN9: its receiver on 248A:5C2F speaks the AJAZZ read and M910 names it",
            Dongles.IsAjazz(0x248A, 0x5C2F) && Dongles.IsAjazz(0x249A, 0x5C2F) && !Dongles.IsAjazz(0x248A, 0x5B2F) && Dongles.AjazzModel("M910") == "Inphic IN9"
            && Dongles.AjazzModel("M041") is null);
        var he80 = Shape(0x0C45, 0x8092, 0xFF68, 0x0061, 2, input: 65, output: 65, outputIds: [0x00]);
        check("Huafenda: the Epomaker HE80 (8092) and the TH108 JP on FEFE's keyboard page take the device-info read; FEFE's 0xFF60 stays EWEADN's, 05AC:024F's 0xFF68 untaken",
            Brand(he80) == "Huafenda keyboard" && Brand(he80 with { Product = 0xFEFE }) == "Huafenda keyboard"
            && Brand(Shape(0x0C45, 0xFEFE, 0xFF60, 0x0061, 3, input: 33, output: 33, outputIds: [0x00])) == "EWEADN"
            && Brand(he80 with { Vendor = 0x05AC, Product = 0x024F }) is null);
        var df3pm = Shape(0x391D, 0x1004, 0xFFFF, 2, input: 0, output: 0, feature: 65, featureIds: [0x00]);
        check("VGN: the Dragonfly3 Pro Max on its cable (1004) takes the receivers' status read as a mouse",
            Brand(df3pm) == "VGN" && BrandFamilies.Find(df3pm)!.Value.Family.Kind(df3pm) == DeviceKind.Mouse);
        var sc800 = Shape(0x38A6, 0x28E9, 0xFFA8, 2, input: 64, output: 64, outputIds: [0x0B]);
        check("AULA SC800 Max: its receiver's 0xFFA8 collection takes the L9 Ultra's read; its boot id 28ED is untaken",
            Brand(sc800) == "AULA mouse" && BrandFamilies.Find(sc800)!.Value.Family.DisplayName(sc800) == "AULA SC800 Max"
            && Brand(sc800 with { Product = 0x28ED }) is null);
    }

    private static void States(Action<string, bool> check)
    {
        check("VGN 5088: 04 00 00 FF 06 (the keyboard asleep or away) is asleep, the reply still read",
            Dongles.VgnBattery(Pad(64, 0x04, 0x00, 0x00, 0xFF, 0x06)) == new BatteryRead(null, Asleep: true)
            && Dongles.VgnBattery(Pad(64, 0x04, 0x00, 0x00, 0x1A, 0x06, 0, 0, 0, 93, 0)) == new BatteryRead(93)
            && Dongles.AtkKeyboardBattery(Pad(64, 0x04, 0x00, 0x00, 0xFF, 0x06)) is null);
        check("Inphic FA65: its web table's models (71, 90, 76 to 79) on events 40 and 41, named by the model",
            Pushes.Battery(Pushes.Kind.InphicZhen, [0x03, 0x71, 0x40, 0x00, 0x50]) == new BatteryRead(80)
            && Pushes.Battery(Pushes.Kind.InphicZhen, [0x03, 0x76, 0x41, 0x03, 0x50]) == new BatteryRead(80, true)
            && Pushes.Battery(Pushes.Kind.InphicZhen, [0x03, 0x93, 0x40, 0x00, 0x50]) is null && Pushes.Battery(Pushes.Kind.InphicZhen, [0x03, 0x71, 0x40, 0x00, 0x00]) is null
            && Pushes.Name(Pushes.Kind.InphicZhen, [0x03, 0x71, 0x40, 0x00, 0x50]) == "Inphic IN10 Pro" && Pushes.Name(Pushes.Kind.InphicZhen, null) == "Inphic ZHEN");
        check("Inphic IN10 (older): 03 93 40 on FA65's 0x000A collection is read and named; its states 2 and 3 are none; DELUX's frame unchanged",
            Pushes.Battery(Pushes.Kind.Delux, [0x03, 0x93, 0x40, 0x01, 0x50]) == new BatteryRead(80)
            && Pushes.Battery(Pushes.Kind.Delux, [0x03, 0x93, 0x40, 0x02, 0x64]) is null && Pushes.Battery(Pushes.Kind.Delux, [0x03, 0x93, 0x40, 0x03, 0x50]) is null
            && Pushes.Name(Pushes.Kind.Delux, [0x03, 0x93, 0x40, 0x01, 0x50]) == "Inphic IN10" && Pushes.Name(Pushes.Kind.Delux, [0x03, 0x30, 0x40, 0x01, 0x50]) == "DELUX M900 Pro"
            && Pushes.Battery(Pushes.Kind.Delux, [0x03, 0x30, 0x40, 0x02, 0x50]) == new BatteryRead(100));
        check("Beken: the AULA SC650's frames (03 8E 40 01 5B, 31) read 91 % and 49 %, and name it; Inphic's web models are named",
            Pushes.Battery(Pushes.Kind.Beken, [0x03, 0x8E, 0x40, 0x01, 0x5B]) == new BatteryRead(91) && Pushes.Battery(Pushes.Kind.Beken, [0x03, 0x8E, 0x40, 0x01, 0x31]) == new BatteryRead(49)
            && Pushes.Name(Pushes.Kind.Beken, [0x03, 0x8E, 0x40, 0x01, 0x5B]) == "AULA SC650" && Pushes.Name(Pushes.Kind.Beken, [0x03, 0x77, 0x40, 0x01, 0x5B]) == "Inphic IN9 PRO V2");
        check("AJAZZ / Inphic IN9: the passive 0xC0 frame's charging flag at 3",
            Dongles.AjazzParseStatus([0xC0, 1, 55, 1]) == (true, 55, true) && Dongles.AjazzParseStatus([0xC0, 1, 55, 0]) == (true, 55, false));
        var mouse = Asus.Catalog.First(model => model.Family == Asus.Family.Mouse && !model.Steps && !model.Buds);
        var stepped = Asus.Catalog.First(model => model.Family == Asus.Family.Mouse && model.Steps);
        byte[] AsusReply(Asus.Model model, byte level) { var r = new byte[65]; r[0] = model.ReportId; r[1] = 0x12; r[2] = 0x07; r[model.LevelAt] = level; return r; }
        var charging = AsusReply(mouse, 0); charging[10] = 1;
        check("ASUS: a mouse answering 0 off the cable is in standby (its last level stands), not at 0 %; on the cable, and other levels, read as before",
            Asus.Reply(mouse, AsusReply(mouse, 0)) is BatteryRead { Percent: null, Asleep: true } && Asus.Reply(stepped, AsusReply(stepped, 0)) is BatteryRead { Percent: null, Asleep: true }
            && Asus.Reply(mouse, charging) is BatteryRead { Percent: 0, Charging: true, Asleep: false }
            && Asus.Reply(mouse, AsusReply(mouse, 60)) is BatteryRead { Percent: 60, Asleep: false });
    }

    // Where BrimDeck's read had no hardware behind it and the reference's differs, the reference's way.
    private static void Evidence(Action<string, bool> check)
    {
        check("EWEADN S9 PRO 8K: the answer with 00 at 4 (a user's receiver, #315) reads as the one with 02",
            EweadnMice.Beiyin(Bytes("03 50 00 0B 00 01 4F 21 07 01 90 65 04 A5 5A 00")) == new BatteryRead(79)
            && EweadnMice.Beiyin(Bytes("03 50 00 0B 02 01 49")) == new BatteryRead(73) && EweadnMice.Beiyin(Bytes("03 50 00 0B 01 01 49")) is null);
        check("SteelSeries Nova 7: link 02 (the headset not connected, the dongle repeating its last level) is asleep",
            Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0x02, 0x50, 0x03]) == new BatteryRead(null, Asleep: true)
            && Headsets.Battery(Headsets.Kind.Nova7Percent, [0xB0, 0x03, 0x50, 0x03]) == new BatteryRead(80)
            && Headsets.Battery(Headsets.Kind.Nova7Steps, [0xB0, 0x02, 0x03, 0x01]) == new BatteryRead(null, Asleep: true));
        var elite = SteelSeriesHeadsets.Battery(SteelSeriesHeadsets.Exchange.Elite);
        byte[] Elite(byte level, byte power) { var r = new byte[64]; r[0] = 0x01; r[1] = 0xB0; r[6] = level; r[14] = power; return r; }
        check("SteelSeries Nova Elite: a level of 0 (the headset switched off) is asleep; 31 % still reads",
            elite(Elite(0, 0x08)) == new BatteryRead(null, Asleep: true) && elite(Elite(31, 0x08)) == new BatteryRead(31));
        var k99 = Dongles.MchoseReads(0x3033, "MCHOSE K99 V3 2.4G", 0x0001, 0x0000, 0, 65, new HashSet<byte> { 0x00 });
        check("MCHOSE K99 V3: its receiver under its own name takes command 03 alone; the K87S and G87 V2 keep command 05",
            k99.SequenceEqual([Dongles.MchoseRead.Battery03]) && Dongles.MchoseReads(0x3033, "MCHOSE K87S 2.4G", 0x0001, 0x0000, 0, 65, new HashSet<byte> { 0x00 }).SequenceEqual([Dongles.MchoseRead.Config])
            && Dongles.MchoseBattery03Request().SequenceEqual(Pad(65, 0x00, 0x55, 0x03, 0x00, 0x38, 0x38)));
        check("MCHOSE K99 V3: AA 03 with 40 at 8 reads 40 %; another command or 0 % is none",
            Dongles.MchoseBattery03(Pad(64, 0xAA, 0x03, 0, 0, 0, 0, 0, 0, 40)) == new BatteryRead(40)
            && Dongles.MchoseBattery03(Pad(64, 0xAA, 0x05, 0, 0, 0, 0, 0, 0, 40)) is null && Dongles.MchoseBattery03(Pad(64, 0xAA, 0x03)) is null);

        var buds = Shape(0x054C, 0x0EC2, 0xFF04, 1, outputIds: [0x02]);
        static byte[] Hci(params byte[] payload)
        {
            var r = new byte[64];
            int length = 12 + payload.Length;
            r[0] = 0x02; r[1] = (byte)length; r[2] = 0x04; r[3] = 0xFF; r[4] = (byte)(length - 3);
            new byte[] { 0x00, 0x96, 0xC3, 0x14, 0x04, 0x10, 0x02, 0x00 }.CopyTo(r, 5);
            payload.CopyTo(r, 13);
            int sum = 0; for (int i = 5; i <= length; i++) sum += r[i];
            r[length + 1] = (byte)sum;
            return r;
        }
        check("Sony INZONE Buds: their dongle's 0xFF04 usage 1 collection takes the GET, as earbuds",
            Brand(buds) == "Sony INZONE" && Way(buds).Name == "inzone.buds" && Way(buds).Request.SequenceEqual(SonyInzone.Request())
            && BrandFamilies.Find(buds)!.Value.Family.Kind(buds) == DeviceKind.Earbuds && Brand(buds with { Usage = 2 }) is null);
        check("Sony INZONE Buds: 100 %, 100 % (charging) and the case at 97 % give each part, the buds' lower level and charging",
            SonyInzone.BudsBattery(Hci(0x00, 100, 0x01, 100, 0x00, 97)) is { Percent: 100, Charging: true, Parts: { Count: 3 } parts }
            && parts[0] == new PartBattery(BatteryPart.Left, 100, false) && parts[1] == new PartBattery(BatteryPart.Right, 100, true)
            && parts[2] == new PartBattery(BatteryPart.Case, 97, false));
        check("Sony INZONE Buds: a left bud FF beside a right one at 100 % reads the right; both FF is the buds away; an H5 answer (one pair) is none",
            SonyInzone.BudsBattery(Hci(0x00, 0xFF, 0x00, 100, 0x00, 80)) is { Percent: 100, Parts: [{ Percent: null }, { Percent: 100 }, { Percent: 80 }] }
            && SonyInzone.BudsBattery(Hci(0x00, 0xFF, 0x00, 0xFF, 0x00, 80)) is { Percent: null, Asleep: true }
            && SonyInzone.BudsBattery(Hci(0x00, 100)) is null && SonyInzone.Battery(Hci(0x00, 0x64)) == new BatteryRead(100));

        var veekos = Shape(0x3151, 0x5026, 0xFFFF, 2, input: 0, output: 0, feature: 65, featureIds: [0x83], name: "Veekos 2.4G Wireless Keyboard");
        check("Veekos Shine 60: a 5026 receiver listing feature report 0x83 alone has it got back; one listing report 0 keeps the F7 read",
            BrandFamilies.Find(veekos) is { Ways: [{ Name: "monsgeek.page83", Transport: WayTransport.FeatureGet }] }
            && BrandFamilies.Find(veekos with { FeatureIds = new HashSet<byte> { 0x00 } }) is { Ways: [{ Name: "monsgeek.status" }] });
        check("Veekos Shine 60: 83 00 30 64 (the user's 48 %) reads; another id or 100 missing at 3 is none",
            RongyuanStatus.Page83(Pad(65, 0x83, 0x00, 0x30, 0x64)) == new BatteryRead(48)
            && RongyuanStatus.Page83(Pad(65, 0x00, 0x00, 0x30, 0x64)) is null && RongyuanStatus.Page83(Pad(65, 0x83, 0x00, 0x30, 0x32)) is null);
        var aj159 = Shape(0x3151, 0x5007, 0xFFFF, 2, input: 0, output: 0, feature: 65, featureIds: [0x00, 0x05]);
        check("AJAZZ AJ159 APEX (3151:5007): the status poll set as report 0, then report 0x05 got back; no read of report 0",
            BrandFamilies.Find(aj159) is { Ways: [{ Name: "rongyuan.report05", Transport: WayTransport.FeatureGet, GapMs: 30 } way] }
            && way.Before.Count == 1 && way.Before[0].SequenceEqual(RongyuanStatus.Request(false)));
        check("AJAZZ AJ159 APEX: 05 00 00 64 01 01 01 02 reads 100 %; a leftover (bytes 1, 2 not 0) and 0 (the link not up) are none",
            RongyuanStatus.Report05(Bytes("05 00 00 64 01 01 01 02")) == new BatteryRead(100)
            && RongyuanStatus.Report05(Bytes("05 00 01 64 01 01 01 02")) is null && RongyuanStatus.Report05(Bytes("05 00 00 00 01 01 01 02")) is null);
    }

    // A 3554:FA09 frame with its checksum (byte 19 the low byte of the sum of the nineteen before it).
    private static byte[] Fa09(params byte[] head) { var frame = new byte[20]; head.CopyTo(frame, 0); frame[19] = (byte)frame.Take(19).Sum(b => b); return frame; }
}
