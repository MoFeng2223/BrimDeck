using BrimDeck.Core;

// Logitech HID++ as myfinder-device-battery's logitech.py reads it: the battery replies' rules and their edges, the HID++ 1.0
// registers' levels and charging bits, the G535's collection and the requests sent on it, the ping's error codes, the timeouts and
// the headsets' names.
static class ReferenceLogitechTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    private static byte[] Padded(string hex) { var head = Bytes(hex); var frame = new byte[Hidpp.LongLength]; head.CopyTo(frame, 0); return frame; }

    public static void Run(Action<string, bool> check)
    {
        Unified(check);
        Status(check);
        Voltages(check);
        Registers(check);
        G535(check);
        Errors(check);
        check("HID++ timeouts: a request 600 ms, a ping 2 s, a silent slot's ping 600 ms; the software id stays 0x0E",
            Hidpp.RequestTimeoutMs == 600 && Hidpp.PingTimeoutMs == 2000 && Hidpp.PingTimeout(false) == 2000 && Hidpp.PingTimeout(true) == 600
            && Hidpp.SoftwareId == 0x0E && (Hidpp.Request(0xFF, 0x00, 1)[3] & 0x0F) == 0x0E);
        check("Logitech headsets: each model's name by product when it reports none; any other product has none",
            Hidpp.HeadsetName(0x0A66) == "Logitech G533" && Hidpp.HeadsetName(0x0AC4) == "Logitech G535" && Hidpp.HeadsetName(0x0A5C) == "Logitech G633"
            && Hidpp.HeadsetName(0x0A89) == "Logitech G635" && Hidpp.HeadsetName(0x0A5B) == "Logitech G933" && Hidpp.HeadsetName(0x0A87) == "Logitech G935"
            && new[] { 0x0AB5, 0x0AFE, 0x0B1F }.All(product => Hidpp.HeadsetName(product) == "Logitech G733") && Hidpp.HeadsetName(0x0AA7) == "Logitech G PRO"
            && new[] { 0x0AAA, 0x0ABA }.All(product => Hidpp.HeadsetName(product) == "Logitech G PRO X")
            && new[] { 0x0AFB, 0x0AFC }.All(product => Hidpp.HeadsetName(product) == "Logitech G PRO X 2")
            && Hidpp.HeadsetName(0xC52B) is null && Hidpp.HeadsetName(0x0AF7) is null && Hidpp.HeadsetName(0x0B1C) is null);
    }

    private static void Unified(Action<string, bool> check)
    {
        check("HID++ 0x1004: a state of charge 1 to 100 is exact; above 100 is none (the flags are not read then)",
            Hidpp.Unified([1, 0, 0]) == new BatteryRead(1) && Hidpp.Unified([100, 0, 0]) == new BatteryRead(100)
            && Hidpp.Unified([101, 8, 0]) is null && Hidpp.Unified([0xFF, 8, 0]) is null);
        check("HID++ 0x1004: at 0, the level flags 8, 4, 2, 1 give 90, 50, 20, 5 in steps",
            Hidpp.Unified([0, 8, 0]) == new BatteryRead(90, false, BatteryAccuracy.Steps) && Hidpp.Unified([0, 4, 0]) == new BatteryRead(50, false, BatteryAccuracy.Steps)
            && Hidpp.Unified([0, 2, 0]) == new BatteryRead(20, false, BatteryAccuracy.Steps) && Hidpp.Unified([0, 1, 0]) == new BatteryRead(5, false, BatteryAccuracy.Steps));
        check("HID++ 0x1004: at 0, no flag or flags together are none",
            Hidpp.Unified([0, 0, 0]) is null && Hidpp.Unified([0, 0x0C, 0]) is null && Hidpp.Unified([0, 3, 1]) is null && Hidpp.Unified([0, 0x10, 0]) is null);
        check("HID++ 0x1004: status 1 to 4 is charging, 0 and 5 on are not, with a percentage or a step",
            new byte[] { 1, 2, 3, 4 }.All(status => Hidpp.Unified([60, 0, status]) is { Percent: 60, Charging: true } && Hidpp.Unified([0, 4, status]) is { Percent: 50, Charging: true })
            && Hidpp.Unified([60, 0, 0]) is { Charging: false } && Hidpp.Unified([60, 0, 5]) is { Charging: false } && Hidpp.Unified([60, 0, 6]) is { Charging: false });
    }

    private static void Status(Action<string, bool> check)
    {
        check("HID++ 0x1000: the level 1 to 100 as given, also at status 3 (full); 0 and above 100 are none",
            Hidpp.Status([1, 0, 0]) == new BatteryRead(1) && Hidpp.Status([100, 0, 0]) == new BatteryRead(100) && Hidpp.Status([95, 0, 3]) == new BatteryRead(95, true)
            && Hidpp.Status([0, 0, 3]) is null && Hidpp.Status([0, 0, 0]) is null && Hidpp.Status([101, 0, 3]) is null);
        check("HID++ 0x1000: status 1 to 4 is charging, 0 and 5 are not",
            new byte[] { 1, 2, 3, 4 }.All(status => Hidpp.Status([40, 0, status]) is { Charging: true }) && Hidpp.Status([40, 0, 0]) is { Charging: false } && Hidpp.Status([40, 0, 5]) is { Charging: false });
    }

    private static void Voltages(Action<string, bool> check)
    {
        check("HID++ 0x1001: 2500 mV and up on the curve, below 2500 none; bit 7 charging",
            Hidpp.Voltage([0x09, 0xC3, 0x00]) is null && Hidpp.Voltage([0x09, 0xC4, 0x00]) == new BatteryRead(0, false, BatteryAccuracy.Voltage)
            && Hidpp.Voltage([0x0E, 0xE3, 0x80]) == new BatteryRead(50, true, BatteryAccuracy.Voltage) && Hidpp.Voltage([0x10, 0x5A, 0x7F]) is { Percent: 100, Charging: false }
            && Hidpp.Voltage([0, 0, 0x80]) is null);
        check("HID++ 0x1F20: bit 0 the headset connected (without it none), bit 1 charging; 0x07 is the curve's level, not 100",
            Hidpp.Adc([0x0F, 0xA0, 0x07]) == new BatteryRead(81, true, BatteryAccuracy.Voltage) && Hidpp.Adc([0x0F, 0xA0, 0x01]) == new BatteryRead(81, false, BatteryAccuracy.Voltage)
            && Hidpp.Adc([0x10, 0x5A, 0x03]) is { Percent: 100, Charging: true } && Hidpp.Adc([0x0F, 0xA0, 0x02]) is null && Hidpp.Adc([0x0F, 0xA0, 0x00]) is null
            && Hidpp.Adc([0x0F, 0xA0, 0x80]) is null);
        check("HID++ 0x1F20: below 2500 mV none, 2500 mV the curve's 0",
            Hidpp.Adc([0x09, 0xC3, 0x03]) is null && Hidpp.Adc([0, 0, 0x01]) is null && Hidpp.Adc([0x09, 0xC4, 0x03]) == new BatteryRead(0, true, BatteryAccuracy.Voltage));
    }

    private static void Registers(Action<string, bool> check)
    {
        check("HID++ 1.0 register 0x0D: the percentage 1 to 100, 0 and above none; charging by the top nibble 0x5 or 0x9 only",
            Hidpp.Mileage([1, 0, 0x30]) == new BatteryRead(1) && Hidpp.Mileage([100, 0, 0x90]) == new BatteryRead(100, true) && Hidpp.Mileage([50, 0, 0x50]) == new BatteryRead(50, true)
            && Hidpp.Mileage([50, 0, 0x5F]) is { Charging: true } && Hidpp.Mileage([50, 0, 0x9A]) is { Charging: true }
            && Hidpp.Mileage([50, 0, 0x40]) is { Charging: false } && Hidpp.Mileage([50, 0, 0x80]) is { Charging: false } && Hidpp.Mileage([50, 0, 0xA0]) is { Charging: false }
            && Hidpp.Mileage([50, 0, 0x05]) is { Charging: false }
            && Hidpp.Mileage([0, 0, 0x50]) is null && Hidpp.Mileage([101, 0, 0x30]) is null);
        check("HID++ 1.0 register 0x07: levels 7, 5, 3, 1 give 90, 50, 20, 5 in steps; any other level is none",
            Hidpp.Level([7, 0]) == new BatteryRead(90, false, BatteryAccuracy.Steps) && Hidpp.Level([5, 0]) == new BatteryRead(50, false, BatteryAccuracy.Steps)
            && Hidpp.Level([3, 0]) == new BatteryRead(20, false, BatteryAccuracy.Steps) && Hidpp.Level([1, 0]) == new BatteryRead(5, false, BatteryAccuracy.Steps)
            && new byte[] { 0, 2, 4, 6, 8, 0xFF }.All(level => Hidpp.Level([level, 0x21]) is null));
        check("HID++ 1.0 register 0x07: charging when the byte holds the bits 0x21 or 0x22",
            new byte[] { 0x21, 0x22, 0x23, 0x25, 0x26, 0x27, 0x63, 0xA1 }.All(state => Hidpp.Level([5, state]) is { Charging: true })
            && new byte[] { 0x00, 0x01, 0x02, 0x20, 0x24, 0x03, 0x40 }.All(state => Hidpp.Level([5, state]) is { Charging: false }));
    }

    private static void G535(Action<string, bool> check)
    {
        var long11 = new HashSet<byte> { 0x10, 0x11 };
        check("Logitech G535: its HID++ is the consumer collection 0x000C / 0x0001 with the long report; not its 0xFF43 / 0x0202 one",
            Hidpp.HeadsetCollection(Hidpp.G535, 0x000C, 0x0001, 20, long11) && Hidpp.HeadsetCollection(Hidpp.G535, 0x000C, 0x0001, 64, new HashSet<byte> { 0x11 })
            && !Hidpp.HeadsetCollection(Hidpp.G535, 0xFF43, 0x0202, 20, long11) && !Hidpp.HeadsetCollection(Hidpp.G535, 0x43FF, 0x0202, 20, long11));
        check("Logitech G535: the consumer collection needs the long report and the exact page and usage",
            !Hidpp.HeadsetCollection(Hidpp.G535, 0x000C, 0x0001, 7, long11) && !Hidpp.HeadsetCollection(Hidpp.G535, 0x000C, 0x0001, 20, new HashSet<byte> { 0x10 })
            && !Hidpp.HeadsetCollection(Hidpp.G535, 0x0C00, 0x0001, 20, long11) && !Hidpp.HeadsetCollection(Hidpp.G535, 0x000C, 0x0002, 20, long11)
            && !Hidpp.HeadsetCollection(Hidpp.G535, 0xFF00, 0x0002, 20, long11));
        check("Logitech headsets: another product's consumer collection is never HID++'s",
            !Hidpp.HeadsetCollection(0x0A87, 0x000C, 0x0001, 20, long11) && !Hidpp.HeadsetCollection(0x0AFB, 0x000C, 0x0001, 20, long11) && Hidpp.HeadsetCollection(0x0A87, 0xFF43, 0x0202, 20, long11));
        // The G535 is read as any HID++ 2.0 device on its own dongle (slot 0xFF): the ping, the 0x1F20 feature's index, its function 0.
        check("Logitech G535: the ping, the 0x1F20 lookup and its read, each a long read request at slot 0xFF",
            Hidpp.Request(0xFF, 0x00, 1, 0, 0, 0x5A).SequenceEqual(Padded("11 FF 00 1E 00 00 5A"))
            && Hidpp.Request(0xFF, 0x00, 0, Hidpp.FeatureParameters(Hidpp.AdcMeasurement)).SequenceEqual(Padded("11 FF 00 0E 1F 20"))
            && Hidpp.Request(0xFF, 0x07, 0).SequenceEqual(Padded("11 FF 07 0E")));
        check("Logitech G535: the 0x1F20 reply on the curve with its charging bit; an error reply is the headset connected but not active",
            Hidpp.Match(Padded("11 FF 00 0E 07"), 0xFF, 0x00, 0).Reply is [0x07, ..]
            && Hidpp.Match(Padded("11 FF 07 0E 0F A0 03"), 0xFF, 0x07, 0).Reply is { } reply && Hidpp.Adc(reply) == new BatteryRead(81, true, BatteryAccuracy.Voltage)
            && Hidpp.Match(Padded("11 FF FF 07 0E 05"), 0xFF, 0x07, 0) is { Reply: null, Error: 0x05 });
    }

    private static void Errors(Action<string, bool> check)
    {
        check("HID++ ping errors: 0x08 an empty slot, 0x09 and 0x04 a device off or out of range, 0x01 HID++ 1.0, any other code other",
            Hidpp.Ping(0x08) == Hidpp.PingError.Empty && Hidpp.Ping(0x09) == Hidpp.PingError.Off && Hidpp.Ping(0x04) == Hidpp.PingError.Off
            && Hidpp.Ping(0x01) == Hidpp.PingError.Legacy && new[] { 0x00, 0x02, 0x03, 0x05, 0x06, 0x07, 0x0A, 0xFF }.All(code => Hidpp.Ping(code) == Hidpp.PingError.Other));
        check("HID++ ping errors: a receiver's short 1.0 error and a long 2.0 one carry the code",
            Hidpp.Match(Bytes("10 03 8F 00 1E 08 00"), 3, 0x00, 1) is { Error: Hidpp.UnknownDevice }
            && Hidpp.Match(Bytes("10 03 8F 00 1E 09 00"), 3, 0x00, 1) is { Error: Hidpp.ResourceError }
            && Hidpp.Match(Padded("11 03 FF 00 1E 04"), 3, 0x00, 1) is { Error: Hidpp.ConnectFail }
            && Hidpp.Match(Bytes("10 03 8F 00 1E 08 00"), 4, 0x00, 1) == (null, null));
    }
}
