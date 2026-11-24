using BrimDeck.Core;

// Razer's reads as myfinder-device-battery's razer.py, razer_profiles.py and razer_v4.py give them: the level rounded, the
// charging question for every catalog model but the Viper V4 Pro, the Viper V4 Pro's one collection and reply, the Bluetooth
// Orochi V2 left to Windows, and the devices not in the catalog read by name with each transaction id in turn.
static class ReferenceRazerTests
{
    private static readonly HashSet<byte> Unnumbered = [0];

    public static void Run(Action<string, bool> check)
    {
        check("Razer: the level rounded to a percentage (0, 1, 2, 3, 5, 127, 128, 184, 254, 255)",
            Razer.Percent(0) == 0 && Razer.Percent(1) == 0 && Razer.Percent(2) == 1 && Razer.Percent(3) == 1 && Razer.Percent(5) == 2
            && Razer.Percent(127) == 50 && Razer.Percent(128) == 50 && Razer.Percent(184) == 72 && Razer.Percent(254) == 100 && Razer.Percent(255) == 100);
        check("Razer: the rounding is round(raw / 255 * 100) and (raw * 100 + 127) // 255 for every raw",
            Enumerable.Range(0, 256).All(raw => Razer.Percent((byte)raw) == (int)Math.Round(raw / 255.0 * 100, MidpointRounding.ToEven)
                && Razer.Percent((byte)raw) == (raw * 100 + 127) / 255));

        check("Razer: the Orochi V2 over Bluetooth (0095) is not read; its receivers (0094, 00E0) are",
            Razer.Find(0x0095) is null && !Razer.Unlisted(0x0095, "Razer Orochi V2 Wireless")
            && Razer.Find(0x0094) is { Transaction: 0x1F } && Razer.Find(0x00E0) is { Transaction: 0x1F });

        check("Razer: every catalog model is asked the charging state but the Viper V4 Pro (00E5, 00E6)",
            Razer.Catalog.All(model => Razer.AsksCharging(model) == model.Product is not (0x00E5 or 0x00E6))
            && Razer.Catalog.Count(model => !Razer.AsksCharging(model)) == 2
            && Razer.Find(0x0557) is { } blackShark && Razer.AsksCharging(blackShark) && Razer.Find(0x027B) is { } proType && Razer.AsksCharging(proType));
        check("Razer: the charging request (0x07 0x84) and its checksum",
            Razer.Request(0x1F, Razer.ChargingState) is var charge && charge[1] == 0x1F && charge[5] == 0x02 && charge[6] == 0x07 && charge[7] == 0x84 && charge[88] == 0x81);

        check("Razer Viper V4 Pro: interface 3, usage 000C:0001, one unnumbered 91-byte feature report; any other collection is not asked",
            Razer.ViperV4Collection(3, 0x000C, 0x0001, 91, Unnumbered)
            && !Razer.ViperV4Collection(2, 0x000C, 0x0001, 91, Unnumbered) && !Razer.ViperV4Collection(-1, 0x000C, 0x0001, 91, Unnumbered)
            && !Razer.ViperV4Collection(3, 0xFF00, 0x0001, 91, Unnumbered) && !Razer.ViperV4Collection(3, 0x0001, 0x0002, 91, Unnumbered)
            && !Razer.ViperV4Collection(3, 0x000C, 0x0002, 91, Unnumbered) && !Razer.ViperV4Collection(3, 0x000C, 0x0001, 90, Unnumbered)
            && !Razer.ViperV4Collection(3, 0x000C, 0x0001, 92, Unnumbered) && !Razer.ViperV4Collection(3, 0x000C, 0x0001, 91, new HashSet<byte> { 0, 1 })
            && !Razer.ViperV4Collection(3, 0x000C, 0x0001, 91, new HashSet<byte> { 5 }));
        check("Razer Viper V4 Pro: 65 ms before the first get, at most three gets 10 ms apart, transaction 0x1F on interface 3",
            Razer.Find(0x00E5) is { Transaction: 0x1F, Interface: 3, WaitMicroseconds: 65_000 } && Razer.Find(0x00E6) is { Transaction: 0x1F, Interface: 3, WaitMicroseconds: 65_000 }
            && Razer.IsViperV4(0x00E5) && Razer.IsViperV4(0x00E6) && !Razer.IsViperV4(0x00E2) && Razer.ViperV4Gets == 3 && Razer.ViperV4GapMs == 10);
        var v4 = ViperReply(184);
        check("Razer Viper V4 Pro: raw 184 (its issue's receiver) is 72 %; raw 0, another status, transaction, size, command or length is none",
            Razer.ViperV4Level(v4) == 72 && Razer.ViperV4Level(ViperReply(255)) == 100 && Razer.ViperV4Level(ViperReply(1)) == 0
            && Razer.ViperV4Level(ViperReply(0)) is null
            && Razer.ViperV4Level(With(v4, 1, Razer.Busy)) is null && Razer.ViperV4Level(With(v4, 1, Razer.NoResponse)) is null
            && Razer.ViperV4Level(With(v4, 2, 0x3F)) is null && Razer.ViperV4Level(With(v4, 6, 0x03)) is null
            && Razer.ViperV4Level(With(v4, 8, 0x84)) is null && Razer.ViperV4Level(With(v4, 0, 0x01)) is null && Razer.ViperV4Level(v4[..90]) is null);

        check("Razer, not in the catalog: each of the names' words is enough, in any case",
            Razer.UnlistedWords.All(word => Razer.Unlisted(0x0FFE, "Razer " + word.ToUpperInvariant() + " Thing"))
            && Razer.Unlisted(0x00FE, "Razer HyperSpeed Wireless Mouse Dongle") && Razer.Unlisted(0x0A01, "Razer BlackShark V2 X")
            && Razer.Unlisted(0x0A02, "Razer Barracuda X") && Razer.Unlisted(0x0A03, "Razer Nari Ultimate") && Razer.Unlisted(0x0A04, "Razer Mouse Dock Chroma"));
        check("Razer, not in the catalog: the Huntsman keyboards' names and other wired names are not read",
            new[] { "Razer Huntsman", "Razer Huntsman V2", "Razer Huntsman V2 Tenkeyless", "Razer Huntsman V2 Analog", "Razer Huntsman Mini",
                "Razer Huntsman Mini Analog", "Razer Huntsman Elite", "Razer Huntsman Tournament Edition", "Razer Huntsman V3 Pro",
                "Razer Huntsman V3 Pro TKL", "Razer Huntsman V3 Pro Mini", "Razer BlackWidow V4", "Razer DeathAdder Essential", "" }
                .All(name => !Razer.Unlisted(0x0FFE, name)));
        check("Razer, not in the catalog: the catalog's ids, the headset families' ids, 052E, 053A, 0095, 00CB and 0552 (Airoha RACE) are kept out",
            Razer.Catalog.All(model => !Razer.Unlisted(model.Product, "Razer Wireless Receiver"))
            && new[] { 0x0528, 0x051C, 0x0555, 0x0556, 0x0583, 0x053A, 0x056C, 0x0565, 0x056E, 0x0568, 0x0567, 0x0588, 0x0587, 0x057D, 0x0A5B,
                0x057A, 0x0579, 0x0577, 0x0576, 0x052E, 0x0095, 0x00CB, 0x0552 }.All(pid => !Razer.Unlisted(pid, "Razer BlackShark Wireless Receiver"))
            && Razer.KeptOut.SequenceEqual(new[] { 0x052E, 0x053A, 0x0095, 0x00CB, 0x0552 }));
        check("Razer, not in the catalog: the collections of page 0x0001 or 0xFF00 first, then the others, each by interface, none last",
            new[] { (0x000C, 1), (0xFF01, -1), (0xFF00, 2), (0x0001, 0), (0xFF00, -1), (0x0001, 3), (0x000C, 0) }
                .OrderBy(c => Razer.UnlistedOrder(c.Item1, c.Item2))
                .SequenceEqual(new[] { (0x0001, 0), (0xFF00, 2), (0x0001, 3), (0xFF00, -1), (0x000C, 0), (0x000C, 1), (0xFF01, -1) }));
        check("Razer, not in the catalog: the transaction ids 1F, 3F, FF, 9F, 08 in that order, each read under a stable name of its own",
            Razer.UnlistedTransactions.SequenceEqual(new byte[] { 0x1F, 0x3F, 0xFF, 0x9F, 0x08 })
            && Razer.UnlistedTransactions.Select(Razer.UnlistedWay).SequenceEqual(new[] { "razer.tid-1f", "razer.tid-3f", "razer.tid-ff", "razer.tid-9f", "razer.tid-08" }));
        check("Razer, not in the catalog: each transaction id's battery request (00 tid 00 00 00 02 07 80 ..., checksum 85 at 88)",
            Razer.UnlistedTransactions.All(tid => Razer.Request(tid, Razer.Battery) is var request && request.Length == 90 && request[0] == 0 && request[1] == tid
                && request[2] == 0 && request[3] == 0 && request[4] == 0 && request[5] == 0x02 && request[6] == 0x07 && request[7] == 0x80
                && request.Skip(8).Take(80).All(b => b == 0) && request[88] == 0x85 && request[89] == 0
                && Razer.Request(tid, Razer.ChargingState) is var charging && charging[1] == tid && charging[7] == 0x84 && charging[88] == 0x81));
        var reply = Razer.Request(0x08, Razer.Battery); reply[0] = Razer.Success; reply[9] = 0x80; reply[88] = Razer.Checksum(reply);
        check("Razer, not in the catalog: a reply on transaction 08 is read as on the others (raw 128 is 50 %)",
            Razer.Reply(reply, Razer.Battery) is { Status: Razer.Success, Value: 0x80 } && Razer.Percent(0x80) == 50);
    }

    // A Viper V4 Pro reply as got back (the report id first): status 02, transaction 1F, size 02, class 07, command 80, the level.
    private static byte[] ViperReply(byte raw)
    {
        var data = Razer.Request(0x1F, Razer.Battery);
        data[0] = Razer.Success; data[9] = raw; data[88] = Razer.Checksum(data);
        return [0x00, .. data];
    }
    private static byte[] With(byte[] bytes, int at, byte value) { var copy = (byte[])bytes.Clone(); copy[at] = value; return copy; }
}
