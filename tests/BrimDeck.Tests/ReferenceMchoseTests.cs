using BrimDeck.Core;

// MCHOSE's reads where myfinder-device-battery's facts replace a way no measurement here backs: which 0x3837 ids get the
// RealTek exchange, the A5 V2 Ultra's receiver, the V3 0x4D answer, the Qhw sequence, the BY flags and the 41E4 identity,
// the charging of the 0x5253 answer, the V9 PRO, V9 Turbo+ and X9 states, and the G3 V2's online question. The measured
// points beside them (the 41E4 cable's charging, the V9 PRO's cable state, the Turbo+'s level on battery, the BY 0x10 on the
// cable, the 0x4D charge status 2) are checked to stay as they were.
static class ReferenceMchoseTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => Convert.ToByte(part, 16))];
    private static byte[] Inverted(byte reportId, byte[] payload, int length = 21)
        => [reportId, 0xF9, .. Enumerable.Range(0, length - 2).Select(i => (byte)((i < payload.Length ? payload[i] : 0) ^ 0xFF))];
    private static byte[] Summed(params byte[] head) { var frame = new byte[20]; head.CopyTo(frame, 0); frame[19] = (byte)frame.Take(19).Sum(b => b); return frame; }
    private static byte[] Frame4D(byte length, byte vendorLow, byte vendorHigh, byte link, byte charge, byte level)
    {
        var frame = new byte[64];
        frame[0] = 0x4D; frame[1] = 0x01; frame[2] = 0x01; frame[3] = length; frame[5] = 0x09;
        frame[8] = vendorLow; frame[9] = vendorHigh; frame[8 + 10] = link; frame[8 + 11] = charge; frame[8 + 12] = level;
        byte check = 0; for (int i = 2; i < 8 + length; i++) check ^= frame[i];
        frame[8 + length] = check;
        return frame;
    }

    public static void Run(Action<string, bool> check)
    {
        RealtekIds(check);
        Frames4D(check);
        A5V2(check);
        Qhw(check);
        By(check);
        RealtekCharging(check);
        V9Pro(check);
        Headsets(check);
        Identity(check);
        G3V2(check);
    }

    // 1. The RealTek exchange on 0x3837 only for mchose.py's list.
    private static void RealtekIds(Action<string, bool> check)
    {
        int[] listed = [0x100A, 0x100B, 0x1020, 0x4015, 0x4018, 0x4019, 0x4021, 0x4022, 0x4023];
        check("MCHOSE 0x3837 RealTek: the ids mchose.py lists get it, and those alone",
            Dongles.MchoseRealtekProducts.SetEquals(listed)
            && listed.All(product => Dongles.MchoseReads(product, "", 0xFF01, 1, 65, 65).Contains(Dongles.MchoseRead.Realtek)));
        check("MCHOSE 0x3837 RealTek: an id off the list (the V3 receivers, 5011, the G3 V2) gets the other reads but not this one",
            !Dongles.MchoseReads(0x1018, "", 0xFF01, 1, 65, 65).Contains(Dongles.MchoseRead.Realtek)
            && Dongles.MchoseReads(0x1018, "", 0xFF01, 1, 65, 65).Contains(Dongles.MchoseRead.Frame4D)
            && !Dongles.MchoseReads(0x1014, "MCHOSE A7 V3 Ultra+", 0xFF01, 1, 65, 65).Contains(Dongles.MchoseRead.Realtek)
            && !Dongles.MchoseReads(0x1016, "", 0xFF01, 1, 21, 64).Contains(Dongles.MchoseRead.Realtek)
            && !Dongles.MchoseReads(0x5011, "迈从无线鼠标", 0xFF01, 1, 65, 64).Contains(Dongles.MchoseRead.Realtek)
            && !Dongles.MchoseReads(0x4245, "MCHOSE G3 V2", 0xFF01, 1, 65, 65).Contains(Dongles.MchoseRead.Realtek));
        check("MCHOSE 0x5253 and the 41E4 cable keep the RealTek exchange whatever the product",
            Dongles.Find(0x5253, 0x1099, 0xFF01, "", 21) == Dongles.Protocol.MchoseRealtek && Dongles.Find(0x41E4, 0x1101, 0xFF01, "", 21) == Dongles.Protocol.MchoseRealtek);
    }

    // 2. The V3 receivers' 0x4D answer.
    private static void Frames4D(Action<string, bool> check)
    {
        check("MCHOSE 0x4D: a payload of 14 bytes with the vendor 3837 and a link is read; charge 1 charging, 2 (kmizmal: on the cable) not",
            Dongles.MchoseBattery(Frame4D(14, 0x37, 0x38, 2, 1, 70)) == new BatteryRead(70, true)
            && Dongles.MchoseBattery(Frame4D(14, 0x37, 0x38, 1, 2, 70)) == new BatteryRead(70));
        check("MCHOSE 0x4D: link 0 is no reading (not asleep), nor a payload of 13 bytes, nor another vendor",
            Dongles.MchoseBattery(Frame4D(14, 0x37, 0x38, 0, 0, 70)) is null
            && Dongles.MchoseBattery(Frame4D(13, 0x37, 0x38, 2, 0, 70)) is null
            && Dongles.MchoseBattery(Frame4D(14, 0x53, 0x52, 2, 0, 70)) is null);
    }

    // 3. The A5 V2 Ultra's receiver 3837:100C.
    private static void A5V2(Action<string, bool> check)
    {
        var shortRequest = Dongles.MchoseA5V2Request(21)!;
        var longRequest = Dongles.MchoseA5V2Request(65)!;
        check("MCHOSE A5 V2 (100C): 11 F9 and 19 FF, then zeros to the collection's 21 or 65 bytes; another length gets nothing",
            shortRequest.SequenceEqual(new byte[] { 0x11, 0xF9 }.Concat(Enumerable.Repeat((byte)0xFF, 19)))
            && longRequest.Length == 65 && longRequest[..21].SequenceEqual(shortRequest) && longRequest[21..].All(b => b == 0)
            && Dongles.MchoseA5V2Request(64) is null && Dongles.MchoseA5V2Request(20) is null);
        check("MCHOSE A5 V2 (100C): set, 90 ms, get, at most 20 rounds within 2 s",
            Dongles.MchoseA5V2Rounds == 20 && Dongles.MchoseA5V2WaitMs == 90 && Dongles.MchoseA5V2BudgetMs == 2000);
        check("MCHOSE A5 V2 (100C): its RealTek read alone, on 0xFF01 usage 1 with 21- or 65-byte feature reports that declare 0x11",
            Dongles.MchoseReads(0x100C, "MCHOSE A5 V2 Ultra", 0xFF01, 1, 21, 64).SequenceEqual([Dongles.MchoseRead.Realtek])
            && Dongles.MchoseReads(0x100C, "MCHOSE A5 V2 Ultra", 0xFF01, 1, 65, 64, new HashSet<byte> { 0x4D }, new HashSet<byte> { 0x11 }).SequenceEqual([Dongles.MchoseRead.Realtek])
            && Dongles.MchoseReads(0x100C, "MCHOSE A5 V2 Ultra", 0xFF01, 1, 64, 64).Count == 0
            && Dongles.MchoseReads(0x100C, "MCHOSE A5 V2 Ultra", 0xFF0B, 1, 21, 64).Count == 0
            && Dongles.MchoseReads(0x100C, "MCHOSE A5 V2 Ultra", 0xFF01, 1, 65, 64, new HashSet<byte>(), new HashSet<byte> { 0x12 }).Count == 0);
        byte[] fields = [0x37, 0x38, 0x21, 0x40, 1, 0, 0, 0, 0x09, 41, 0];
        var payload = Dongles.MchoseA5V2Payload(Inverted(0x11, fields));
        check("MCHOSE A5 V2 (100C): the first 11 decoded bytes (the tail not counted), any charge status but 0 charging, 0 % kept",
            payload is not null && payload.SequenceEqual(fields)
            && Dongles.MchoseA5V2Payload(Inverted(0x11, [.. fields, 0x55, 0x66], 65))!.SequenceEqual(fields)
            && Dongles.MchoseA5V2Battery(payload) == (new BatteryRead(41), 0x4021)
            && Dongles.MchoseA5V2Battery(Dongles.MchoseA5V2Payload(Inverted(0x11, [0x37, 0x38, 0x21, 0x40, 1, 0, 0, 0, 0x09, 41, 2]))!) == (new BatteryRead(41, true), 0x4021)
            && Dongles.MchoseA5V2Battery(Dongles.MchoseA5V2Payload(Inverted(0x11, [0x37, 0x38, 0x21, 0x40, 1, 0, 0, 0, 0x09, 0, 0]))!) == (new BatteryRead(0), 0x4021));
        check("MCHOSE A5 V2 (100C): the vendor must be 3837 (not 5253 or 41E4), the model not 0; all zero, report 0x12 or a level over 100 none",
            Dongles.MchoseA5V2Payload(Inverted(0x11, [0x53, 0x52, 0x21, 0x40, 1, 0, 0, 0, 0x09, 41, 0])) is null
            && Dongles.MchoseA5V2Payload(Inverted(0x11, [0xE4, 0x41, 0x21, 0x40, 1, 0, 0, 0, 0x09, 41, 0])) is null
            && Dongles.MchoseA5V2Payload(Inverted(0x11, [0x37, 0x38, 0x00, 0x00, 1, 0, 0, 0, 0x09, 41, 0])) is null
            && Dongles.MchoseA5V2Payload(Inverted(0x11, [])) is null && Dongles.MchoseA5V2Payload(shortRequest) is null
            && Dongles.MchoseA5V2Payload(Inverted(0x12, fields)) is null
            && Dongles.MchoseA5V2Payload(Inverted(0x11, [0x37, 0x38, 0x21, 0x40, 1, 0, 0, 0, 0x09, 101, 0])) is null);
    }

    // 4. The Qhw command's sequence.
    private static void Qhw(Action<string, bool> check)
    {
        static byte[] Answer(byte sequence, byte status, byte level, byte length = 2)
            => [0x4D, 0x01, 0x03, length, 0x04, 0x09, 0x00, sequence, status, level, 0x00];
        check("MCHOSE Qhw: the request carries the sequence and 0E XOR it",
            Dongles.MchoseQhwRequest(0).SequenceEqual(Bytes("4D 01 03 00 04 09 00 00 0E"))
            && Dongles.MchoseQhwRequest(5).SequenceEqual(Bytes("4D 01 03 00 04 09 00 05 0B"))
            && Dongles.MchoseQhwRequest(0xFF).SequenceEqual(Bytes("4D 01 03 00 04 09 00 FF F1")));
        check("MCHOSE Qhw: the answer with the request's sequence and a data length of 2 is read, 0 % included; another sequence or length none",
            Dongles.MchoseQhw(Answer(5, 1, 55), 5) == new BatteryRead(55, true) && Dongles.MchoseQhw(Answer(5, 0, 0), 5) == new BatteryRead(0)
            && Dongles.MchoseQhw(Answer(5, 2, 100), 5) == new BatteryRead(100)
            && Dongles.MchoseQhw(Answer(4, 0, 55), 5) is null && Dongles.MchoseQhw(Answer(5, 0, 55, length: 3), 5) is null
            && Dongles.MchoseQhw(Answer(5, 0, 55)[..10], 5) is null && Dongles.MchoseQhw(Dongles.MchoseQhwRequest(5), 5) is null);
    }

    // 5. The BY flags (3554:FA09, 41E4:2001, 3837:201B).
    private static void By(Action<string, bool> check)
    {
        check("BY flags: each nibble 0 or 1; charging only with the high nibble 1 below 100 % on a known model (0x10 on the cable kept)",
            Dongles.ByFlags(52, 0x10, true) == new BatteryRead(52, true) && Dongles.ByFlags(100, 0x10, true) == new BatteryRead(100)
            && Dongles.ByFlags(100, 0x11, true) == new BatteryRead(100) && Dongles.ByFlags(100, 0x01, true) == new BatteryRead(100)
            && Dongles.ByFlags(52, 0x00, true) == new BatteryRead(52) && Dongles.ByFlags(52, 0x10, false) == new BatteryRead(52));
        check("BY flags: any other nibble value and the whole frame is none",
            Dongles.ByFlags(52, 0x20, true) is null && Dongles.ByFlags(52, 0x02, true) is null && Dongles.ByFlags(52, 0xF0, false) is null && Dongles.ByFlags(52, 0x1F, true) is null);
        check("BY flags on the 41E4 and 201B answer: a known model charges, an unknown one does not, a bad nibble is none",
            Dongles.MchoseKeyboardBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x34, 0x10), true) == new BatteryRead(52, true)
            && Dongles.MchoseKeyboardBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x34, 0x10), false) == new BatteryRead(52)
            && Dongles.MchoseKeyboardBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x64, 0x10), true) == new BatteryRead(100)
            && Dongles.MchoseKeyboardBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x34, 0x30), true) is null);
        check("FA09: M HUB's flag rules for MCHOSE keyboards; the F99 Pro's measured charging; any other keyboard's level alone",
            Dongles.AulaBattery(Summed(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x0A, 0x10), "MCHOSE G75 Pro") == new BatteryRead(10, true)
            && Dongles.AulaBattery(Summed(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x0A, 0x10), null) == new BatteryRead(10)
            && Dongles.AulaBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x64, 0x10), "MCHOSE G75 Pro") == new BatteryRead(100)
            && Dongles.AulaBattery(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x55, 0x02), "MCHOSE G75 Pro") is null
            && Dongles.AulaBattery(Summed(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x55, 0x20), null) == new BatteryRead(0x55) && Dongles.AulaBattery(Summed(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x55, 0x10), "AULA F99 Pro") == new BatteryRead(0x55, true) && Dongles.AulaBattery(Summed(0x13, 0x0A, 0x01, 0x00, 0x04, 0x05, 0x55, 0x10), "LEOBOG Hi75") == new BatteryRead(0x55));
        check("BY on 201B: still its 13 4A read alone, on its 0xFF02 collection",
            Dongles.MchoseReads(0x201B, "MCHOSE 2.4G Wireless", 0xFF02, 2, 0, 20, new HashSet<byte> { 0x13 }, new HashSet<byte>()).SequenceEqual([Dongles.MchoseRead.By]));
    }

    // 6. The RealTek answer's charging.
    private static void RealtekCharging(Action<string, bool> check)
    {
        byte[] Status(byte charge, byte vendorLow = 0x53, byte vendorHigh = 0x52) => Inverted(0x11, [vendorLow, vendorHigh, 0x31, 0x00, 5, 2, 7, 0, 0x09, 80, charge]);
        check("MCHOSE RealTek: on 0x5253 (and 0x3837) any charge status but 0 is charging",
            Dongles.MchoseRealtekBattery(Status(1), 0x5253) == (new BatteryRead(80, true), 0x0031)
            && Dongles.MchoseRealtekBattery(Status(2), 0x5253) == (new BatteryRead(80, true), 0x0031)
            && Dongles.MchoseRealtekBattery(Status(0), 0x5253) == (new BatteryRead(80), 0x0031)
            && Dongles.MchoseRealtekBattery(Status(2, 0x37, 0x38), 0x3837) == (new BatteryRead(80, true), 0x0031));
        check("MCHOSE RealTek: on the 41E4 cable and receiver the charge status 1 alone, as measured",
            Dongles.MchoseRealtekBattery(Status(1, 0xE4, 0x41), 0x41E4) == (new BatteryRead(80, true), 0x0031)
            && Dongles.MchoseRealtekBattery(Status(2, 0xE4, 0x41), 0x41E4) == (new BatteryRead(80), 0x0031));
    }

    // 7. The V9 PRO's states.
    private static void V9Pro(Action<string, bool> check)
    {
        check("MCHOSE V9 PRO: 00 on the cable keeps its level and charging (HaloBattery); 03 hides the level, charging",
            MchoseV9Pro.Battery(Bytes("55 65 3C 00"), true) == new BatteryRead(60, true)
            && MchoseV9Pro.Battery(Bytes("55 65 55 03"), true) == new BatteryRead(null, true) && MchoseV9Pro.Battery(Bytes("55 65 55 03"), false) == new BatteryRead(null, true));
        check("MCHOSE V9 PRO: 04 only from a receiver named exactly MCHOSE V9 PRO, its level only at 100",
            MchoseV9Pro.Battery(Bytes("55 65 64 04"), true) == new BatteryRead(100) && MchoseV9Pro.Battery(Bytes("55 65 5A 04"), true) == new BatteryRead(null)
            && MchoseV9Pro.Battery(Bytes("55 65 64 04"), false) is null && MchoseV9Pro.G9Battery(Bytes("55 65 64 04")) is null
            && MchoseV9Pro.Original(" mchose v9 pro ") && !MchoseV9Pro.Original("MCHOSE V9 PRO 2.4G") && !MchoseV9Pro.Original("MCHOSE G9 PRO"));
        var named = BrandFamilyTests.Shape(0x291D, 0x385D, 0xFF90, 1, input: 64, output: 64, outputIds: [0x55], name: "MCHOSE V9 PRO");
        var longer = named with { Name = "MCHOSE V9 PRO Receiver" };
        static BrandWay Way(HidShape shape) => BrandFamilies.Find(shape)!.Value.Ways[0];
        check("MCHOSE V9 PRO: the read takes 04 by the receiver's product string",
            Way(named).Parse(Bytes("55 65 64 04")) == new BatteryRead(100) && Way(longer).Parse(Bytes("55 65 64 04")) is null
            && Way(longer).Parse(Bytes("55 65 55 03")) == new BatteryRead(null, true) && Way(named).TimeoutMs <= 1000 && Way(named).Attempts is >= 1 and <= 2);
    }

    // 8. The V9 Turbo+ (600A) and X9 (6045) on the status read.
    private static void Headsets(Action<string, bool> check)
    {
        check("MCHOSE V9 Turbo+ and X9: the status read with their own states",
            Dongles.MchoseStatusHeadsets.SetEquals([0x600A, 0x6045]) && !Dongles.MchoseStatusHeadsets.Contains(0x600D));
        check("MCHOSE V9 Turbo+ and X9: 02 the level (the Turbo+'s 80 %, #61), 00 and 03 outside power with no level, 1A asleep",
            Dongles.MchoseHeadsetStatus(Bytes("55 65 50 02")) == new BatteryRead(80)
            && Dongles.MchoseHeadsetStatus(Bytes("55 65 50 00")) == new BatteryRead(null, true) && Dongles.MchoseHeadsetStatus(Bytes("55 65 50 03")) == new BatteryRead(null, true)
            && Dongles.MchoseHeadsetStatus(Bytes("55 65 00 1A")) == new BatteryRead(null, Asleep: true));
        check("MCHOSE V9 Turbo+ and X9: 04 is no reading, nor the request read back, another state or a level over 100",
            Dongles.MchoseHeadsetStatus(Bytes("55 65 64 04")) is null && Dongles.MchoseHeadsetStatus(Dongles.MchoseStatusRequest()) is null
            && Dongles.MchoseHeadsetStatus(Bytes("55 65 50 01")) is null && Dongles.MchoseHeadsetStatus(Bytes("55 65 65 02")) is null
            && Dongles.MchoseHeadsetStatus(Bytes("55 66 50 02")) is null);
        check("MCHOSE status read for the other 0x3837 devices as before (03 charging with its level, 04 full)",
            Dongles.MchoseStatus([0x55, 0x65, 40, 3]) == new BatteryRead(40, true) && Dongles.MchoseStatus([0x55, 0x65, 100, 4]) == new BatteryRead(100));
    }

    // 9. The 41E4:2001 receiver names its keyboard as FA09's does.
    private static void Identity(Action<string, bool> check)
    {
        check("MCHOSE 41E4:2001: the identity request is FA09's 13 05 01 00, summed",
            Dongles.AulaRequest(0x05).SequenceEqual(Bytes("13 05 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 19")));
        check("MCHOSE 41E4:2001: the answer names the model by the six bytes at 5; a code not known is an answer without a name; the request read back is none",
            Dongles.AulaIdentity(Summed(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x02, 0x1C)) == ("MCHOSE G87", true)
            && Dongles.AulaIdentity(Summed(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01, 0x35)) == ("MCHOSE K87", true)
            && Dongles.AulaIdentity(Summed(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x09, 0x99)) == (null, false)
            && Dongles.AulaIdentity(Dongles.AulaRequest(0x05)) is null && Dongles.AulaIdentity(Summed(0x13, 0x4A, 0x01, 0x00, 0x02, 0x34, 0x10)) is null
            && Dongles.AulaModel(Summed(0x13, 0x05, 0x01, 0x00, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x09, 0x99)) is null);
    }

    // 10. The G3 V2's online question.
    private static void G3V2(Action<string, bool> check)
    {
        var request = Dongles.G3V2OnlineRequest();
        check("MCHOSE G3 V2: 00 55 ED, zeros to 65 bytes, only for 3837:4245",
            request.Length == 65 && request[..3].SequenceEqual(new byte[] { 0x00, 0x55, 0xED }) && request[3..].All(b => b == 0) && Dongles.MchoseG3V2 == 0x4245);
        check("MCHOSE G3 V2: the answer AA ED with the state at 8; another header, a short frame or the battery answer is none",
            Dongles.G3V2Online(Bytes("AA ED 00 00 00 00 00 00 02 00")) == 2 && Dongles.G3V2Online(Bytes("AA ED 00 00 00 00 00 00 01")) == 1
            && Dongles.G3V2Online(Bytes("00 AA ED 00 00 00 00 00 00 00")) == 0
            && Dongles.G3V2Online(Bytes("AA 30 a5 0b 0a 01 01 01 2e 00")) is null && Dongles.G3V2Online(Bytes("AA ED 00 00")) is null
            && Dongles.G3V2Online(request) is null);
        check("MCHOSE G3 V2: its battery answer still the raw level (vigil-watt)",
            Dongles.GmouseBattery(Bytes("aa 30 a5 0b 0a 01 01 01 2e 00 00 00")) == new BatteryRead(46)
            && Dongles.GmouseBattery(Bytes("aa 30 a5 0b 0a 01 01 01 2f 00 00 00")) == new BatteryRead(47));
    }
}
