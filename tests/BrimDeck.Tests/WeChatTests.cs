using System.Buffers.Binary;
using BrimDeck.Core.Notices;

// WeChat's messages read from its chat list: finding the accessibility byte in an image built here (WeChatGateScan), reading a
// row's accessible name (WeChatRows; the names follow WeChat 4.2.0.13) and telling new messages between readings (WeChatWatch).
// Nothing here touches a running WeChat.
internal static class WeChatTests
{
    public static void Run(Action<string, bool> check)
    {
        Scan(check); Rows(check); Watch(check);
    }

    // A 64-bit image: a .text section at 0x1000 (file 0x400), .rdata at 0x200000 (file 0x400 + text) and .data at 0x300000.
    private const uint TextRva = 0x1000, RdataRva = 0x200000, DataRva = 0x300000;
    private static byte[] Image(int textSize, Action<byte[], uint> text, Action<byte[]>? rdata = null, bool pe32Plus = true)
    {
        const int headers = 0x400, dataSize = 0x200;
        var image = new byte[headers + textSize + dataSize * 2];
        image[0] = (byte)'M'; image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), (ushort)(pe32Plus ? 0x20B : 0x10B));
        void Section(int index, uint rva, int size, int at, uint flags)
        {
            var row = image.AsSpan(0x80 + 24 + 0xF0 + index * 40);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)size); BinaryPrimitives.WriteUInt32LittleEndian(row[12..], rva);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], (uint)size); BinaryPrimitives.WriteUInt32LittleEndian(row[20..], (uint)at);
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], flags);
        }
        Section(0, TextRva, textSize, headers, 0x60000020);
        Section(1, RdataRva, dataSize, headers + textSize, 0x40000040);
        Section(2, DataRva, dataSize, headers + textSize + dataSize, 0xC0000040);
        var code = new byte[textSize];
        text(code, TextRva);
        code.CopyTo(image, headers);
        var words = new byte[dataSize];
        (rdata ?? (bytes => "qt.accessibility.core"u8.CopyTo(bytes.AsSpan(0x10))))(words);
        words.CopyTo(image, headers + textSize);
        return image;
    }

    // The shape at an offset of the text section, reading the byte at an address.
    private static void Shape(byte[] code, int at, uint flagRva)
    {
        byte[] shape = [0x48, 0x85, 0xC9, 0x0F, 0x84, 1, 2, 3, 4, 0x80, 0x3D, 0, 0, 0, 0, 0x00, 0x0F, 0x84];
        shape.CopyTo(code, at);
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(at + 11), (int)(flagRva - (TextRva + (uint)at + 9 + 7)));
    }

    // lea rcx, [rip+disp] to the text in .rdata.
    private static void Use(byte[] code, int at)
    {
        code[at] = 0x48; code[at + 1] = 0x8D; code[at + 2] = 0x0D;
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(at + 3), (int)(RdataRva + 0x10 - (TextRva + (uint)at + 7)));
    }

    private static IReadOnlyList<WeChatGateSite> Sites(byte[] image) => WeChatGateScan.Sites(new MemoryStream(image));

    private static void Scan(Action<string, bool> check)
    {
        var one = Sites(Image(0x4000, (code, _) => { Use(code, 0x100); Shape(code, 0x140, DataRva + 0x20); }));
        check("WeChat scan: the shape near the text is found with its byte", one.Count == 1 && one[0] == new WeChatGateSite(TextRva + 0x140, DataRva + 0x20));

        byte[] code = [0x48, 0x85, 0xC9, 0x0F, 0x84, 0, 0, 0, 0, 0x80, 0x3D, 0x10, 0, 0, 0, 0x00, 0x0F, 0x84];
        check("WeChat scan: the shape's target is counted from the instruction after the cmp", WeChatGateScan.IsShape(code, out long target) && target == 9 + 7 + 0x10);
        code[15] = 1;
        check("WeChat scan: a compare with another value is not the shape", !WeChatGateScan.IsShape(code, out _));
        check("WeChat scan: too few bytes are not the shape", !WeChatGateScan.IsShape(code.AsSpan(0, 17), out _));

        var far = Sites(Image(0x60000, (text, _) => { Use(text, 0x100); Shape(text, 0x50000, DataRva + 0x30); Shape(text, 0x200, DataRva + 0x20); }));
        check("WeChat scan: of two shapes the one nearest the text's use comes first, the far one is left out", far.Count == 1 && far[0].Flag == DataRva + 0x20);

        var none = Sites(Image(0x4000, (text, _) => Shape(text, 0x140, DataRva + 0x20), rdata: _ => { }));
        check("WeChat scan: without the text every shape is a candidate", none.Count == 1 && none[0].Flag == DataRva + 0x20);

        check("WeChat scan: a byte in a read-only section is no candidate", Sites(Image(0x4000, (text, _) => { Use(text, 0x100); Shape(text, 0x140, RdataRva + 0x40); })).Count == 0);
        check("WeChat scan: a byte outside every section is no candidate", Sites(Image(0x4000, (text, _) => { Use(text, 0x100); Shape(text, 0x140, 0x900000); })).Count == 0);

        // A megabyte is one block of the scan: a shape and a use lying across the border of two blocks are still read whole.
        var crossing = Sites(Image(0x200000, (text, _) => { Use(text, 0x100000 - 3); Shape(text, 0x100000 - 9, DataRva + 0x20); Shape(text, 0x100000 + 0x40, DataRva + 0x28); }));
        check("WeChat scan: a shape across two blocks is found once", crossing.Count(site => site.Flag == DataRva + 0x20) == 1 && crossing.Any(site => site.Flag == DataRva + 0x28));

        var many = Sites(Image(0x4000, (text, _) => { Use(text, 0x100); for (int i = 0; i < 8; i++) Shape(text, 0x200 + i * 0x40, DataRva + 0x20 + (uint)i); }));
        check("WeChat scan: at most four places are handed out, nearest first", many.Count == WeChatGateScan.MaxSites && many[0].Flag == DataRva + 0x20);

        var twice = Sites(Image(0x4000, (text, _) => { Use(text, 0x100); Shape(text, 0x200, DataRva + 0x20); Shape(text, 0x300, DataRva + 0x20); }));
        check("WeChat scan: two reads of one byte are one place", twice.Count == 1);

        check("WeChat scan: a 32-bit image gives nothing", Sites(Image(0x4000, (text, _) => { Use(text, 0x100); Shape(text, 0x140, DataRva + 0x20); }, pe32Plus: false)).Count == 0);
        check("WeChat scan: a file that is no image gives nothing", Sites(new byte[5000]).Count == 0 && Sites([]).Count == 0 && Sites("MZ"u8.ToArray()).Count == 0);
    }

    private static WeChatRow? Row(string name, string text, bool selected = false) => WeChatRows.Parse(WeChatRows.IdPrefix + name, text, selected);

    private static void Rows(Action<string, bool> check)
    {
        check("WeChat row: name, message and time", Row("小林", "小林\n下午开会吗？\n12:41\n") is { Name: "小林", Unread: 0, Summary: "下午开会吗？", Time: "12:41", Muted: false, MentionsMe: false, Selected: false });
        check("WeChat row: the count before the message", Row("项目群", "项目群\n[12条] 老王: 收到\n09:05\n消息免打扰\n") is { Unread: 12, Summary: "老王: 收到", Time: "09:05", Muted: true });
        check("WeChat row: the count on a line of its own, a message over several lines",
            Row("新闻", "新闻\n[3条] \n第一行\n第二行\n第三行…\n18:30\n消息免打扰\n") is { Unread: 3, Summary: "第一行 第二行 第三行…", Time: "18:30", Muted: true });
        check("WeChat row: a pinned chat without messages", Row("微信团队", "微信团队\n已置顶\n\n\n") is { Name: "微信团队", Unread: 0, Summary: "", Time: "" });
        check("WeChat row: a date in place of the time", Row("文件传输助手", "文件传输助手\n[图片] \n11/20\n") is { Summary: "[图片]", Time: "11/20", Unread: 0 });
        check("WeChat row: yesterday and a weekday are times", Row("a", "a\nhi\n昨天\n") is { Time: "昨天", Summary: "hi" } && Row("a", "a\nhi\n星期三\n") is { Time: "星期三" } && Row("a", "a\nhi\n2025/12/31\n") is { Time: "2025/12/31" });
        check("WeChat row: a message that looks like a time stays the message", Row("a", "a\n12:30\n12:41\n") is { Summary: "12:30", Time: "12:41" });
        check("WeChat row: the count stops at its cap", Row("群", "群\n[99+条] 小林: 好\n10:00\n") is { Unread: WeChatRows.UnreadCap } && Row("群", "群\n[1500条] 好\n10:00\n") is { Unread: WeChatRows.UnreadCap });
        check("WeChat row: a mention is kept in the words", Row("群", "群\n[2条] [有人@我] 小林: 看一下\n10:00\n消息免打扰\n") is { Unread: 2, MentionsMe: true, Summary: "[有人@我] 小林: 看一下", Muted: true });
        check("WeChat row: a mention before the count", Row("群", "群\n[有人@我] [2条] 小林: 看一下\n10:00\n") is { Unread: 2, MentionsMe: true, Summary: "[有人@我] 小林: 看一下" });
        check("WeChat row: brackets that are no count stay", Row("a", "a\n[图片]\n10:00\n") is { Unread: 0, Summary: "[图片]" } && Row("a", "a\n[2条消息] x\n10:00\n") is { Unread: 0 });
        check("WeChat row: the selected row", Row("a", "a\nhi\n10:00\n", selected: true) is { Selected: true });
        check("WeChat row: the chat's name is the id's, whole", Row("两行\n名字", "两行\n名字\nhi\n10:00\n") is { Name: "两行\n名字" });
        check("WeChat row: the rows of many accounts are folders", Row("公众号", "公众号\n某号: 标题\n10:00\n") is { Folder: true } && Row("服务号", "服务号\nx\n10:00\n") is { Folder: true } && Row("小林", "小林\nx\n10:00\n") is { Folder: false });
        check("WeChat row: not a chat's row", WeChatRows.Parse("search_item_1", "x\ny\n", false) is null && WeChatRows.Parse(WeChatRows.IdPrefix + "a", "", false) is null && WeChatRows.Parse(null, "a\n", false) is null && WeChatRows.Parse(WeChatRows.IdPrefix, "\n", false) is null);

        var noon = new DateTime(2026, 11, 24, 12, 0, 30);
        check("WeChat row: a time of this minute or the last two is just now", WeChatRows.JustNow("12:00", noon) && WeChatRows.JustNow("11:58", noon) && WeChatRows.JustNow("12:02", noon) && !WeChatRows.JustNow("11:57", noon));
        check("WeChat row: just now across midnight", WeChatRows.JustNow("23:59", new DateTime(2026, 11, 24, 0, 0, 5)) && WeChatRows.JustNow("0:01", new DateTime(2026, 11, 24, 23, 59, 50)));
        check("WeChat row: a date or a weekday is never just now", !WeChatRows.JustNow("11/24", noon) && !WeChatRows.JustNow("昨天", noon) && !WeChatRows.JustNow("", noon));

        check("WeChat notice: a chat's notice names its chat", WeChatRows.ChatOf(new Notice { AppKey = WeChatRows.AppKey, InstanceKey = "wechat:1:0:小林", Title = "小林" }) == "小林");
        check("WeChat notice: the plain flash notice names none", WeChatRows.ChatOf(new Notice { AppKey = WeChatRows.AppKey, InstanceKey = "flash:wechat:1", Title = "微信" }) is null
            && WeChatRows.ChatOf(new Notice { AppKey = "windows:QQ", InstanceKey = "wechat:1:0:a", Title = "a" }) is null && WeChatRows.ChatOf(null) is null);
    }

    private static WeChatRow R(string name, int unread, string summary, string time = "12:00", bool muted = false, bool mention = false, bool selected = false)
        => new(name, unread, summary, time, muted, mention, selected);

    private static void Watch(Action<string, bool> check)
    {
        var at = new DateTime(2026, 11, 24, 12, 0, 0);
        WeChatWatch Primed(params WeChatRow[] rows) { var watch = new WeChatWatch("微信"); watch.Observe(rows, false, false, at); return watch; }

        var first = new WeChatWatch("微信").Observe([R("小林", 2, "在吗")], false, false, at);
        check("WeChat watch: the first reading is only remembered", first.Incoming.Count == 0 && first.Read.Count == 0);

        var grew = Primed(R("小林", 0, "好的")).Observe([R("小林", 1, "在吗")], false, false, at.AddSeconds(2));
        check("WeChat watch: a count that grew is a message, under the chat's name",
            grew.Incoming is [{ Title: "小林", Body: "在吗", AppKey: WeChatRows.AppKey, Origin: NoticeOrigin.Windows, Category: NoticeCategory.Message } told] && told.InstanceKey.StartsWith(WeChatRows.KeyPrefix, StringComparison.Ordinal) && told.InstanceKey.EndsWith(":小林", StringComparison.Ordinal));

        var watch = Primed(R("小林", 0, "好的"));
        var one = watch.Observe([R("小林", 1, "一")], false, false, at.AddSeconds(2));
        var two = watch.Observe([R("小林", 2, "二")], false, false, at.AddSeconds(4));
        check("WeChat watch: each message of a chat is a notice of its own with the same title", one.Incoming.Count == 1 && two.Incoming.Count == 1 && one.Incoming[0].InstanceKey != two.Incoming[0].InstanceKey && two.Incoming[0].Title == "小林");
        check("WeChat watch: an unchanged list tells nothing", watch.Observe([R("小林", 2, "二")], false, false, at.AddSeconds(6)) is { Incoming.Count: 0, Read.Count: 0 });
        var read = watch.Observe([R("小林", 0, "二")], false, false, at.AddSeconds(8));
        check("WeChat watch: a chat read in WeChat ends what was told about it", read.Incoming.Count == 0 && read.Read.Count == 2 && read.Read.Contains(one.Incoming[0].InstanceKey) && read.Read.Contains(two.Incoming[0].InstanceKey));
        check("WeChat watch: and only once", watch.Observe([R("小林", 0, "二")], false, false, at.AddSeconds(10)).Read.Count == 0);

        check("WeChat watch: my own message (no count) is not told", Primed(R("小林", 0, "好的")).Observe([R("小林", 0, "我发的", "12:01")], false, false, at.AddSeconds(2)).Incoming.Count == 0);
        check("WeChat watch: a muted chat is not told", Primed(R("群", 3, "a", muted: true)).Observe([R("群", 4, "b", muted: true)], false, false, at.AddSeconds(2)).Incoming.Count == 0);
        check("WeChat watch: a muted chat that mentions me is told", Primed(R("群", 3, "a", muted: true)).Observe([R("群", 4, "[有人@我] 小林: 看", muted: true, mention: true)], false, false, at.AddSeconds(2)).Incoming is [{ Body: "[有人@我] 小林: 看" }]);
        check("WeChat watch: a folder row is not told", Primed(R("公众号", 0, "a")).Observe([R("公众号", 1, "b")], false, false, at.AddSeconds(2)).Incoming.Count == 0);
        check("WeChat watch: nothing is told while the user is in WeChat", Primed(R("小林", 0, "好的")).Observe([R("小林", 1, "在吗")], false, true, at.AddSeconds(2)).Incoming.Count == 0);

        var inside = Primed(R("小林", 0, "好的"));
        inside.Observe([R("小林", 1, "在吗")], false, true, at.AddSeconds(2));
        check("WeChat watch: what arrived while in WeChat is not told afterwards either", inside.Observe([R("小林", 1, "在吗")], false, false, at.AddSeconds(4)).Incoming.Count == 0);

        check("WeChat watch: a chat that moved into view with a message of this minute is told", Primed(R("小林", 0, "好的")).Observe([R("老王", 1, "你好", "12:00"), R("小林", 0, "好的")], false, false, at.AddSeconds(2)).Incoming is [{ Title: "老王" }]);
        check("WeChat watch: a chat scrolled into view with older unread messages is not", Primed(R("小林", 0, "好的")).Observe([R("老王", 5, "你好", "09:30"), R("小林", 0, "好的")], false, false, at.AddSeconds(2)).Incoming.Count == 0);

        var scrolled = Primed(R("小林", 1, "一"), R("老王", 0, "x"));
        scrolled.Observe([R("老王", 0, "x")], false, false, at.AddSeconds(2));
        check("WeChat watch: a chat back in view is compared with what was last seen of it", scrolled.Observe([R("小林", 1, "一"), R("老王", 0, "x")], false, false, at.AddSeconds(4)).Incoming.Count == 0
            && scrolled.Observe([R("小林", 2, "二"), R("老王", 0, "x")], false, false, at.AddSeconds(6)).Incoming.Count == 1);

        check("WeChat watch: at the cap a changed message counts", Primed(R("群", WeChatRows.UnreadCap, "a")).Observe([R("群", WeChatRows.UnreadCap, "b")], false, false, at.AddSeconds(2)).Incoming.Count == 1
            && Primed(R("群", WeChatRows.UnreadCap, "a")).Observe([R("群", WeChatRows.UnreadCap, "a")], false, false, at.AddSeconds(2)).Incoming.Count == 0);
        check("WeChat watch: a message without words still tells", Primed(R("小林", 0, "")).Observe([R("小林", 1, "")], false, false, at.AddSeconds(2)).Incoming is [{ Body.Length: > 0 }]);

        // The open chat keeps no count: its change counts only together with a flash.
        check("WeChat watch: the open chat's change with a flash is told", Primed(R("小林", 0, "好的", selected: true)).Observe([R("小林", 0, "在吗", "12:01", selected: true)], true, false, at.AddSeconds(2)).Incoming is [{ Title: "小林", Body: "在吗" }]);
        check("WeChat watch: the open chat's change without a flash is not", Primed(R("小林", 0, "好的", selected: true)).Observe([R("小林", 0, "在吗", "12:01", selected: true)], false, false, at.AddSeconds(2)).Incoming.Count == 0);
        var late = Primed(R("小林", 0, "好的", selected: true));
        late.Observe([R("小林", 0, "在吗", "12:01", selected: true)], false, false, at.AddSeconds(2));
        var confirmed = late.Observe([R("小林", 0, "在吗", "12:01", selected: true)], true, false, at.AddSeconds(3));
        check("WeChat watch: a flash right after the open chat's change tells it, once", confirmed.Incoming is [{ Body: "在吗" }] && late.Observe([R("小林", 0, "在吗", "12:01", selected: true)], true, false, at.AddSeconds(4)).Incoming.Count == 0);
        var tooLate = Primed(R("小林", 0, "好的", selected: true));
        tooLate.Observe([R("小林", 0, "在吗", "12:01", selected: true)], false, false, at.AddSeconds(2));
        check("WeChat watch: a flash long after the change does not", tooLate.Observe([R("小林", 0, "在吗", "12:01", selected: true)], true, false, at.AddSeconds(2) + WeChatWatch.Confirm + TimeSpan.FromSeconds(1)).Incoming.Count == 0);
        var kept = Primed(R("小林", 0, "好的", selected: true));
        var open = kept.Observe([R("小林", 0, "在吗", "12:01", selected: true)], true, false, at.AddSeconds(2));
        check("WeChat watch: what was told of the open chat is not ended by its count of none", open.Incoming.Count == 1 && kept.Observe([R("小林", 0, "在吗", "12:01", selected: true)], false, false, at.AddSeconds(4)).Read.Count == 0);
        check("WeChat watch: a flash alone tells nothing of another chat's own message", Primed(R("小林", 0, "好的"), R("老王", 0, "x", selected: true)).Observe([R("小林", 0, "我发的", "12:01"), R("老王", 0, "x", selected: true)], true, false, at.AddSeconds(2)).Incoming.Count == 0);

        var gone = Primed(R("小林", 0, "好的"));
        gone.Forget();
        check("WeChat watch: after WeChat went away the next reading is only remembered", gone.Observe([R("小林", 3, "在吗")], false, false, at.AddSeconds(2)).Incoming.Count == 0
            && gone.Observe([R("小林", 4, "在")], false, false, at.AddSeconds(4)).Incoming.Count == 1);
    }
}
