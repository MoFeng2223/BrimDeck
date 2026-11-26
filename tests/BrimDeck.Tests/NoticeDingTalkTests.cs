using System.Security.Cryptography;
using System.Text;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Reading DingTalk's own store (DingTalkStore): the key, the write-ahead log, and the notices its rows stand for. Nothing
// here touches a real DingTalk; the log is built and encrypted by the test itself.
internal static class NoticeDingTalkTests
{
    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try { Key(check); Log(check); Rows(check); }
        finally { Loc.Use(language); }
    }

    private static void Key(Action<string, bool> check)
    {
        // The derivation DingTalk itself uses, checked against a value worked out apart from this code.
        check("DingTalk: the key is the account's id and salt through PBKDF2 and MD5",
            Encoding.ASCII.GetString(DingTalkStore.Key("123456789012", "abc123")) == "39f1dd89ed89bab7");
        check("DingTalk: another account has another key", !DingTalkStore.Key("123456789013", "abc123").SequenceEqual(DingTalkStore.Key("123456789012", "abc123")));
        string config = Convert.ToBase64String("""{"salt":"QQQQ","salt_md5":"zz"}"""u8.ToArray());
        check("DingTalk: the salt comes from the account's user_config", DingTalkStore.Salt(config) == "QQQQ" && DingTalkStore.Salt("  " + config + "\n") == "QQQQ");
        check("DingTalk: a user_config that is not one says nothing", DingTalkStore.Salt("") is null && DingTalkStore.Salt("not base64 at all!") is null
            && DingTalkStore.Salt(Convert.ToBase64String("{}"u8.ToArray())) is null && DingTalkStore.Salt(Convert.ToBase64String("{\"salt\":\"\"}"u8.ToArray())) is null);
        check("DingTalk: the account's own id comes from its logs", DingTalkStore.AccountId("x real_uid=2133349068 y") == "2133349068"
            && DingTalkStore.AccountId("myOpenId=987654321098;") == "987654321098" && DingTalkStore.AccountId("&uid=2133349068") is null && DingTalkStore.AccountId("") is null);
    }

    // A log of two frames, encrypted the way DingTalk encrypts it: the frame checksums are over the pages as stored, so the
    // reading has to write them again after decrypting, and a frame that does not add up ends the log.
    private static void Log(Action<string, bool> check)
    {
        var key = DingTalkStore.Key("123456789012", "abc123");
        const int size = 512;
        var pages = new byte[2][];
        for (int frame = 0; frame < 2; frame++)
        {
            pages[frame] = new byte[size];
            for (int at = 0; at < size; at++) pages[frame][at] = (byte)(frame * 7 + at);
        }
        var log = Build(pages, size, key);
        var plain = DingTalkStore.DecryptLog((byte[])log.Clone(), key);
        check("DingTalk: the log keeps its length and header", plain.Length == log.Length && plain.AsSpan(0, 24).SequenceEqual(log.AsSpan(0, 24)));
        check("DingTalk: every frame's page comes back as it was", plain.AsSpan(32 + 24, size).SequenceEqual(pages[0]) && plain.AsSpan(32 + 24 + size + 24 + 24 - 24, size).SequenceEqual(pages[1]));
        check("DingTalk: the frames' checksums are written again over what they now hold", Checked(plain, size));
        var torn = (byte[])log.Clone();
        torn[32 + 24 + size + 24 + 10] ^= 0xFF; // a frame DingTalk is still writing
        var cut = DingTalkStore.DecryptLog(torn, key);
        check("DingTalk: a frame that does not add up ends the log, and the whole frame before it is kept", cut.Length == 32 + 24 + size && Checked(cut, size));
        var broken = (byte[])log.Clone();
        broken[24] ^= 0xFF;
        check("DingTalk: a header that does not add up leaves no frame at all", DingTalkStore.DecryptLog(broken, key).Length == 32);
        check("DingTalk: a log too short to hold a header is left alone", DingTalkStore.DecryptLog([1, 2, 3], key).Length == 3);
    }

    // The log as SQLite writes one, with every page encrypted and the checksums over the encrypted pages.
    private static byte[] Build(byte[][] pages, int size, byte[] key)
    {
        var log = new byte[32 + pages.Length * (24 + size)];
        DingTalkStore.WriteBE(log, 0, 0x377f0683); // big-endian magic
        DingTalkStore.WriteBE(log, 4, 3007000);
        DingTalkStore.WriteBE(log, 8, (uint)size);
        DingTalkStore.WriteBE(log, 12, 1);
        DingTalkStore.WriteBE(log, 16, 0x11111111);
        DingTalkStore.WriteBE(log, 20, 0x22222222);
        uint s0 = 0, s1 = 0;
        Sum(log.AsSpan(0, 24), ref s0, ref s1);
        DingTalkStore.WriteBE(log, 24, s0); DingTalkStore.WriteBE(log, 28, s1);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        for (int frame = 0; frame < pages.Length; frame++)
        {
            int at = 32 + frame * (24 + size);
            DingTalkStore.WriteBE(log, at, (uint)(frame + 1));
            DingTalkStore.WriteBE(log, at + 4, (uint)(frame == pages.Length - 1 ? pages.Length : 0));
            DingTalkStore.WriteBE(log, at + 8, 0x11111111);
            DingTalkStore.WriteBE(log, at + 12, 0x22222222);
            aes.EncryptEcb(pages[frame], log.AsSpan(at + 24, size), PaddingMode.None);
            Sum(log.AsSpan(at, 8), ref s0, ref s1);
            Sum(log.AsSpan(at + 24, size), ref s0, ref s1);
            DingTalkStore.WriteBE(log, at + 16, s0); DingTalkStore.WriteBE(log, at + 20, s1);
        }
        return log;
    }

    // Whether every frame's checksum holds over the log as it now is.
    private static bool Checked(byte[] log, int size)
    {
        uint s0 = 0, s1 = 0;
        Sum(log.AsSpan(0, 24), ref s0, ref s1);
        if (DingTalkStore.ReadBE(log, 24) != s0 || DingTalkStore.ReadBE(log, 28) != s1) return false;
        for (int at = 32; at + 24 + size <= log.Length; at += 24 + size)
        {
            Sum(log.AsSpan(at, 8), ref s0, ref s1);
            Sum(log.AsSpan(at + 24, size), ref s0, ref s1);
            if (DingTalkStore.ReadBE(log, at + 16) != s0 || DingTalkStore.ReadBE(log, at + 20) != s1) return false;
        }
        return true;
    }

    private static void Sum(ReadOnlySpan<byte> data, ref uint s0, ref uint s1)
    {
        for (int at = 0; at + 8 <= data.Length; at += 8)
        {
            s0 += DingTalkStore.ReadBE(data, at) + s1;
            s1 += DingTalkStore.ReadBE(data, at + 4) + s0;
        }
    }

    private const string Me = "2133349068", Them = "7140511142";
    private const long T0 = 1764165000000;

    private static string?[] Row(string cid, string chat, string sender, string contentType, string content, long at, int unread = 1, string atIds = "", string type = "1", string? senderId = null)
        => [cid, chat, sender, contentType, content, at.ToString(), unread.ToString(), atIds, type, senderId ?? Them];

    private static void Rows(Action<string, bool> check)
    {
        var direct = DingTalkStore.Parse(Row("c1", "小林", "小林", "1", """{"text":" 下班了 ","contentType":1}""", T0), Me);
        check("DingTalk: a one-to-one row is the chat, the sender and the words", direct is { Cid: "c1", Chat: "小林", Sender: "小林", Text: "下班了", At: T0, Group: false, Mine: false, Mentioned: false, Unread: 1 });
        var notice = DingTalkStore.Notice(direct!, 0);
        check("DingTalk: its notice names the chat and says the words", notice is { AppKey: DingTalkStore.AppKey, AppName: "钉钉", Title: "小林", Body: "下班了", Call: false, Category: NoticeCategory.Message }
            && notice!.ReplaceKey is null && notice.InstanceKey == $"dingtalk:msg:c1:{T0}" && notice.CreatedAt == DateTimeOffset.FromUnixTimeMilliseconds(T0));
        var again = DingTalkStore.Notice(DingTalkStore.Parse(Row("c1", "小林", "小林", "1", """{"text":"下班了"}""", T0 + 1000), Me)!, T0);
        check("DingTalk: the same words sent again are another notice, not an update of the first",
            again is not null && again!.InstanceKey != notice.InstanceKey && again.ReplaceKey is null && again.Body == notice.Body);

        var group = DingTalkStore.Parse(Row("c2", "三组", "小林", "1", """{"text":"收到"}""", T0, type: "2"), Me);
        check("DingTalk: a group's message carries who sent it", DingTalkStore.Notice(group!, 0) is { Title: "三组", Body: "小林: 收到" });
        var mention = DingTalkStore.Parse(Row("c2", "三组", "小林", "1", """{"text":"看下"}""", T0, atIds: $"[\"{Me}\"]", type: "2"), Me);
        check("DingTalk: a group's message that names the user is marked", mention is { Mentioned: true } && DingTalkStore.Notice(mention!, 0) is { Body: "[有人@你] 小林: 看下" });
        var all = DingTalkStore.Parse(Row("c2", "三组", "小林", "1", """{"text":"全体"}""", T0, atIds: "-1", type: "2"), Me);
        check("DingTalk: an everyone mention counts as naming the user", all is { Mentioned: true });

        check("DingTalk: what the user sent themselves is never told back",
            DingTalkStore.Notice(DingTalkStore.Parse(Row("c1", "小林", "我", "1", """{"text":"好"}""", T0, senderId: Me), Me)!, 0) is null);
        check("DingTalk: a message no newer than the mark is not told again",
            DingTalkStore.Notice(direct!, T0) is null && DingTalkStore.Notice(direct!, T0 + 1) is null && DingTalkStore.Notice(direct!, T0 - 1) is not null);

        check("DingTalk: a row without a chat or a time says nothing",
            DingTalkStore.Parse(Row("", "小林", "小林", "1", """{"text":"x"}""", T0), Me) is null
            && DingTalkStore.Parse(Row("c1", "小林", "小林", "1", """{"text":"x"}""", 0), Me) is null
            && DingTalkStore.Parse(["c1"], Me) is null);
        check("DingTalk: a chat with no name falls back to the sender's",
            DingTalkStore.Notice(DingTalkStore.Parse(Row("c3", "", "小林", "1", """{"text":"x"}""", T0), Me)!, 0) is { Title: "小林" });
        check("DingTalk: a one-to-one chat named after the other account's number shows their name instead",
            DingTalkStore.Parse(Row("c4", "7140511142", "小林", "1", """{"text":"x"}""", T0), Me) is { Chat: "小林" }
            && DingTalkStore.Parse(Row("c5", "三组", "小林", "1", """{"text":"x"}""", T0, type: "2"), Me) is { Chat: "三组" }
            && DingTalkStore.Notice(DingTalkStore.Parse(Row("c6", "164902", "", "1", """{"text":"x"}""", T0), Me)!, 0) is { Title: "钉钉" });

        check("DingTalk: what is not text is named by its kind", DingTalkStore.Text("2", "{}") == "[图片]" && DingTalkStore.Text("3", "{}") == "[文件]"
            && DingTalkStore.Text("4", "{}") == "[语音]" && DingTalkStore.Text("500", "{}") == "[DING]" && DingTalkStore.Text("2950", "{}") == "[消息]");
        check("DingTalk: text that cannot be read is no message at all", DingTalkStore.Text("1", "not json") == "" && DingTalkStore.Text("1", """{"text":""}""") == ""
            && DingTalkStore.Parse(Row("c1", "小林", "小林", "1", "not json", T0), Me) is null
            && DingTalkStore.Text("1101", "{}") == "");
        Loc.Use(Loc.English);
        bool english = DingTalkStore.Text("2", "{}") == "[Picture]" && DingTalkStore.Notice(mention!, 0)!.Body.StartsWith("[Mentioned you]", StringComparison.Ordinal);
        Loc.Use(Loc.Chinese);
        check("DingTalk: the words follow the language", english);
    }
}
