using BrimDeck.Core;

// The reads moved to myfinder-device-battery's way where BrimDeck had no capture of its own: the Ninjutso Sora V2's legacy
// settings read, Rapoo's report 07 states, the FA6x passive frames, the Pulsar and ATK NANO receivers, ASUS's mouse
// collection, the 3151:402D receiver's name and XInput's levels. What was measured here stays as it was.
static class ReferenceMouseTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    private static byte[] Pad(int length, params byte[] head) { var frame = new byte[length]; head.CopyTo(frame, 0); return frame; }

    public static void Run(Action<string, bool> check)
    {
        Ninjutso(check);
        Rapoo(check);
        Fa6x(check);
        Compx(check);
        Asus(check);
        Lingbao(check);
        XInput(check);
    }

    // A legacy settings reply: the level at 16, charging at 17, the sum of bytes 0 to 700 at 702 and 703.
    private static byte[] Settings(byte level, byte charging, byte at701 = 0)
    {
        var reply = new byte[704];
        reply[0] = 0x04; reply[1] = 0x26; reply[16] = level; reply[17] = charging; reply[100] = 0xFF; reply[500] = 0x80; reply[701] = at701;
        int sum = 0;
        for (int i = 0; i < 701; i++) sum += reply[i];
        reply[702] = (byte)sum; reply[703] = (byte)(sum >> 8);
        return reply;
    }

    private static void Ninjutso(Action<string, bool> check)
    {
        var sizes = new Dictionary<byte, int> { [0x04] = 704, [0x05] = 32 };
        check("Ninjutso AE1C: the legacy read only for \"Ninjutso Sora V2\" on usage 1 with feature reports of 704 at most, 4 of 704 and 5 of 32",
            Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, sizes)
            && Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, new Dictionary<byte, int> { [0x04] = 704, [0x05] = 32, [0x06] = 64 })
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2 8K", 1, 704, sizes) && !Dongles.NinjutsoLegacy(0xAE1C, "", 1, 704, sizes)
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 2, 704, sizes) && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 32, sizes)
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, null) && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, new Dictionary<byte, int>())
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, new Dictionary<byte, int> { [0x04] = 704, [0x05] = 33 })
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, new Dictionary<byte, int> { [0x05] = 32, [0x06] = 704 })
            && !Dongles.NinjutsoLegacy(0xAE1C, "Ninjutso Sora V2", 1, 704, new Dictionary<byte, int> { [0x04] = 704, [0x05] = 32, [0x06] = 1 }));
        check("Ninjutso AE11, AE8A and AE8C never take the legacy read; they and AE1C keep the 05 15 read on 0xFFA0",
            !Dongles.NinjutsoLegacy(0xAE11, "Ninjutso Sora V2", 1, 704, sizes) && !Dongles.NinjutsoLegacy(0xAE8A, "Ninjutso Sora V2", 1, 704, sizes)
            && !Dongles.NinjutsoLegacy(0xAE8C, "Ninjutso Sora V2", 1, 704, sizes)
            && new[] { 0xAE1C, 0xAE11, 0xAE8A, 0xAE8C }.All(product => Dongles.Find(0x1915, product, 0xFFA0, "", 32) == Dongles.Protocol.Nordic)
            && Dongles.NordicRequest(32)[..5].SequenceEqual(new byte[] { 0x05, 0x15, 0, 0, 0x01 }));
        check("Ninjutso legacy: the profile request 05 0D 00 00 01 00 00 16 in 32 bytes, the settings request 04 26 00 00 01 00 <profile> in 704",
            Dongles.NinjutsoProfileRequest().SequenceEqual(Pad(32, 0x05, 0x0D, 0, 0, 0x01, 0, 0, 0x16))
            && Dongles.NinjutsoSettingsRequest(3).SequenceEqual(Pad(704, 0x04, 0x26, 0, 0, 0x01, 0, 3))
            && Dongles.NinjutsoSettingsRequest(255)[6] == 255);
        var profile = Pad(32, 0x05); profile[9] = 2;
        var none = Pad(32, 0x05);
        check("Ninjutso legacy: the profile at 9 of a 32-byte report 5; 0 there (asked once more); another id or length nothing",
            Dongles.NinjutsoProfile(profile) == 2 && Dongles.NinjutsoProfile(none) == 0
            && Dongles.NinjutsoProfile(Pad(32, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 2)) is null && Dongles.NinjutsoProfile(Pad(33, 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 2)) is null);
        var wrongSum = Settings(80, 0); wrongSum[702] ^= 0x01;
        var wrongId = Settings(80, 0); wrongId[0] = 0x05;
        check("Ninjutso legacy: the level at 16 and charging at 17 when bytes 0 to 700 sum to 702 | 703 << 8, 701 not counted",
            Dongles.NinjutsoSettingsBattery(Settings(80, 0)) == new BatteryRead(80) && Dongles.NinjutsoSettingsBattery(Settings(57, 1)) == new BatteryRead(57, true)
            && Dongles.NinjutsoSettingsBattery(Settings(57, 1, at701: 0x33)) == new BatteryRead(57, true) && Dongles.NinjutsoSettingsBattery(Settings(0, 0)) == new BatteryRead(0)
            && Dongles.NinjutsoSettingsBattery(wrongSum) is null && Dongles.NinjutsoSettingsBattery(wrongId) is null
            && Dongles.NinjutsoSettingsBattery(Settings(101, 0)) is null && Dongles.NinjutsoSettingsBattery(Settings(80, 2)) is null
            && Dongles.NinjutsoSettingsBattery(Settings(80, 0)[..703]) is null);
    }

    private static void Rapoo(Action<string, bool> check)
    {
        byte[] Report(byte state, byte level, byte link = 0x20, byte radio = 0xD2) { var r = new byte[19]; r[0] = 0x07; r[1] = link; r[7] = state; r[8] = level; r[18] = radio; return r; }
        check("Rapoo report 07: states 2 and 3 charging, 1 and 0 not; the level 2 to 100 (0 and 1 come as the cable goes in), 101 none",
            Pushes.Battery(Pushes.Kind.Rapoo, Report(2, 60)) == new BatteryRead(60, true) && Pushes.Battery(Pushes.Kind.Rapoo, Report(3, 100)) == new BatteryRead(100, true)
            && Pushes.Battery(Pushes.Kind.Rapoo, Report(1, 60)) == new BatteryRead(60) && Pushes.Battery(Pushes.Kind.Rapoo, Report(0, 60)) == new BatteryRead(60)
            && Pushes.Battery(Pushes.Kind.Rapoo, Report(1, 0)) is null && Pushes.Battery(Pushes.Kind.Rapoo, Report(2, 1)) is null
            && Pushes.Battery(Pushes.Kind.Rapoo, Report(1, 101)) is null);
        check("Rapoo report 07: still 19 bytes at least, and the receiver's radio down still asleep",
            Pushes.Battery(Pushes.Kind.Rapoo, Report(1, 60)[..18]) is null
            && Pushes.Battery(Pushes.Kind.Rapoo, Report(3, 60, link: 0x20, radio: 0x52)) == new BatteryRead(null, Asleep: true)
            && Pushes.Apply(Pushes.Kind.Rapoo, new BatteryRead(70, true), Report(2, 60, radio: 0x00)) == new BatteryRead(70, Asleep: true)
            && Pushes.Battery(Pushes.Kind.RapooKeyboard, Report(3, 40)) == new BatteryRead(40, true));
    }

    private static void Fa6x(Action<string, bool> check)
    {
        check("FA60 / FA62 passive frames: a level of 0 is none, and the state byte is never charging (02, 03, 80 alike)",
            Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 01 00")) is null && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 85 40 01 00")) is null
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 02 50")) == new BatteryRead(80) && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 03 50")) == new BatteryRead(80)
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 41 80 50")) == new BatteryRead(80) && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 01 65")) is null);
        check("FA60 passive frames: misaka310's X1 frame and the tenths models as before",
            Pushes.Battery(Pushes.Kind.Beken, Bytes("03 B1 40 01 5A")) == new BatteryRead(90)
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 10 40 03 09")) == new BatteryRead(90, Accuracy: BatteryAccuracy.Steps)
            && Pushes.Battery(Pushes.Kind.Beken, Bytes("03 85 40 02 0A")) == new BatteryRead(100, Accuracy: BatteryAccuracy.Steps));
        check("FA65 Inphic frames: the level 1 to 100, no charging from the state; DELUX (0x30) and the older IN10 (0x93) unchanged",
            Pushes.Battery(Pushes.Kind.InphicZhen, Bytes("03 76 41 03 54")) == new BatteryRead(84) && Pushes.Battery(Pushes.Kind.InphicZhen, Bytes("03 71 40 02 54")) == new BatteryRead(84)
            && Pushes.Battery(Pushes.Kind.InphicZhen, Bytes("03 76 41 01 00")) is null
            && Pushes.Battery(Pushes.Kind.Delux, Bytes("03 30 40 03 59")) == new BatteryRead(89, true) && Pushes.Battery(Pushes.Kind.Delux, Bytes("03 30 40 02 59")) == new BatteryRead(100)
            && Pushes.Battery(Pushes.Kind.Delux, Bytes("03 93 40 01 50")) == new BatteryRead(80) && Pushes.Battery(Pushes.Kind.Delux, Bytes("03 93 40 03 50")) is null);
    }

    private static void Compx(Action<string, bool> check)
    {
        int[] dropped = [0x5502, 0x5504, 0x5407, 0x5601, 0x5603];
        check("Pulsar: 3710:5502, 5504, 5407, 5601 and 5603 are not in Compx's official list nor sent the Compx frame; 5501 and 5503 still are",
            dropped.All(product => !Dongles.IsCompxOfficial(0x3710, product) && Dongles.Find(0x3710, product, 0xFF02, "", 0, 1, 0x0002, 17, 17) is null)
            && Dongles.IsCompxOfficial(0x3710, 0x5501) && Dongles.IsCompxOfficial(0x3710, 0x5503)
            && Dongles.Find(0x3710, 0x5501, 0xFF02, "", 0, 1, 0x0002, 17, 17) == Dongles.Protocol.CompxFramed);
        check("ATK NANO 373B:1216: BitMouse only on 0xFF05 usage 1 with a 64-byte output report 8; other 373B products as before",
            Dongles.Find(0x373B, 0x1216, 0xFF05, "", 0, 1, 0x0001, 64, 64, -1, new HashSet<byte> { 0x08 }) == Dongles.Protocol.BitMouse
            && Dongles.Find(0x373B, 0x1216, 0xFF05, "", 0, 1, 0x0001, 64, 64) == Dongles.Protocol.BitMouse
            && Dongles.Find(0x373B, 0x1216, 0xFF05, "", 0, 1, 0x0002, 64, 64) is null && Dongles.Find(0x373B, 0x1216, 0xFF05, "", 0, 1, 0x0001, 64, 65) is null
            && Dongles.Find(0x373B, 0x1216, 0xFF05, "", 0, 1, 0x0001, 64, 64, -1, new HashSet<byte> { 0x09 }) is null
            && Dongles.Find(0x373B, 0x9999, 0xFF05, "", 0) == Dongles.Protocol.BitMouse);
        check("ATK NANO: the reply's id is the collection's one input report (8 to 64 bytes); two ids, or none, nothing",
            Dongles.AtkNanoInput(64, new HashSet<byte> { 0x08 }) == 0x08 && Dongles.AtkNanoInput(64, new HashSet<byte> { 0x00 }) == 0x00
            && Dongles.AtkNanoInput(64, new HashSet<byte> { 0x08, 0x09 }) is null && Dongles.AtkNanoInput(64, new HashSet<byte>()) is null
            && Dongles.AtkNanoInput(7, new HashSet<byte> { 0x08 }) is null && Dongles.AtkNanoInput(65, new HashSet<byte> { 0x08 }) is null
            && Dongles.BitMouseRequest(0x07, 1)[..8].SequenceEqual(new byte[] { 0x08, 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 }));
        check("ATK NANO: status 0, command 07 and a fitting data length; no 0x72 marker needed; charging unknown; the old marker still read",
            Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 77), 0x08) == new BatteryRead(77)
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x72, 0x00, 0x00, 0x01, 0x07, 0x03, 0, 0x10, 0x0F), 0x08) == new BatteryRead(0)
            && Dongles.AtkNanoBattery(Pad(64, 0x05, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 77), 0x05) == new BatteryRead(77)
            && Dongles.AtkNanoBattery(Pad(8, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 77), 0x08) == new BatteryRead(77)
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 77), 0x05) is null
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x02, 0x00, 0x01, 0x07, 0x01, 77), 0x08) is null
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x0F, 0x01, 1), 0x08) is null
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x00, 77), 0x08) is null
            && Dongles.AtkNanoBattery(Pad(8, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x02, 77), 0x08) is null
            && Dongles.AtkNanoBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 101), 0x08) is null
            && Dongles.AtkNanoBattery(Pad(65, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x01, 77), 0x08) is null
            && Dongles.BitMouseBattery(Pad(64, 0x08, 0x72, 0x00, 0x00, 0x01, 0x07, 0x02, 77, 0x10, 0x0F)) == new BatteryRead(77)
            && Dongles.BitMouseBattery(Pad(64, 0x08, 0x00, 0x00, 0x00, 0x01, 0x07, 0x02, 77, 0x10, 0x0F)) is null);
    }

    private static void Asus(Action<string, bool> check)
    {
        var gladius = BrimDeck.Core.Asus.Find(0x0B05, 0x197F)!;
        var carry = BrimDeck.Core.Asus.Find(0x0B05, 0x18B4)!;
        var azoth = BrimDeck.Core.Asus.Find(0x0B05, 0x1A83)!;
        var scope = BrimDeck.Core.Asus.Find(0x0B05, 0x1A07)!;
        var delta = BrimDeck.Core.Asus.Find(0x0B05, 0x1AFA)!;
        check("ASUS mice: those on interface 0 only on usage page 0xFF00 or above; the models on another interface (the Strix Carry, the Harpe II Ace receiver) keep their collection",
            BrimDeck.Core.Asus.Collection(gladius, 0, -1, 0xFF00, 65) && BrimDeck.Core.Asus.Collection(gladius, 0, -1, 0xFF01, 65)
            && !BrimDeck.Core.Asus.Collection(gladius, 0, -1, 0x00FF, 65) && !BrimDeck.Core.Asus.Collection(gladius, 0, -1, 0x0001, 65)
            && !BrimDeck.Core.Asus.Collection(gladius, 1, -1, 0xFF00, 65) && BrimDeck.Core.Asus.Collection(carry, 1, -1, 0xFF00, 65) && BrimDeck.Core.Asus.Collection(carry, 1, -1, 0x0001, 65)
            && Dongles.Find(0x0B05, 0x197F, 0xFF00, "", 0, 0, 1, 65, 65) == Dongles.Protocol.Asus && Dongles.Find(0x0B05, 0x197F, 0x0001, "", 0, 0, 2, 65, 65) is null);
        check("ASUS keyboards and headsets keep their collections",
            BrimDeck.Core.Asus.Collection(azoth, 1, -1, 0x0001, 65) && BrimDeck.Core.Asus.Collection(scope, 2, -1, 0x00FF, 65)
            && BrimDeck.Core.Asus.Collection(delta, 3, -1, 0xFF00, 65) && !BrimDeck.Core.Asus.Collection(delta, 3, -1, 0x00FF, 65));
    }

    private static void Lingbao(Action<string, bool> check)
    {
        check("3151:402D: the receiver's neutral name where it gives no product string, its read unchanged",
            Dongles.LingbaoFallbackName == "2.4G Wireless Receiver" && !Dongles.LingbaoFallbackName.Contains("LINGBAO", StringComparison.OrdinalIgnoreCase)
            && Dongles.Find(0x3151, 0x402D, 0xFFFF, "", 65, 2, 0x0002) == Dongles.Protocol.Lingbao
            && Dongles.LingbaoBattery(Bytes("00 00 00 5f 01 00 01 02")) == new BatteryRead(95));
    }

    private static void XInput(Action<string, bool> check)
    {
        check("XInput: alkaline and NiMH levels as 5, 30, 65 and 100 %; wired and unknown no level and charging unknown; disconnected nothing",
            XInputBattery.Battery(0x02, 0) == new BatteryRead(5, Accuracy: BatteryAccuracy.Steps) && XInputBattery.Battery(0x03, 1) == new BatteryRead(30, Accuracy: BatteryAccuracy.Steps)
            && XInputBattery.Battery(0x02, 2) == new BatteryRead(65, Accuracy: BatteryAccuracy.Steps) && XInputBattery.Battery(0x03, 3) == new BatteryRead(100, Accuracy: BatteryAccuracy.Steps)
            && XInputBattery.Battery(0x01, 3) == new BatteryRead(null) && XInputBattery.Battery(0x01, 0) is { Percent: null, Charging: false }
            && XInputBattery.Battery(0xFF, 2) == new BatteryRead(null) && XInputBattery.Battery(0x04, 2) == new BatteryRead(null)
            && XInputBattery.Battery(0x02, 4) == new BatteryRead(null) && XInputBattery.Battery(0x00, 3) is null);
    }
}
