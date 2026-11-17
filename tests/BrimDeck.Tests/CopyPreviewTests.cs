using System.Text.Json;
using BrimDeck.Core;

internal static class CopyPreviewTests
{
    public static void Run(Action<string, bool> check)
    {
        check("copy preview flattens whitespace", CopyPreviewText.Summarize("  hello\r\n\tworld  ") == "hello world");
        check("copy preview removes direction controls", CopyPreviewText.Summarize("a\u202eb\u0000c") == "abc");
        check("copy preview preserves emoji clusters", CopyPreviewText.Summarize("你好 👨‍👩‍👧‍👦 e\u0301") == "你好 👨‍👩‍👧‍👦 e\u0301");
        check("copy preview bounds long input", CopyPreviewText.Summarize(new string('字', 1000000)) == new string('字', 20) + "…");
        check("copy preview bounds whitespace scans", CopyPreviewText.Summarize(new string(' ', 1000000)).Length <= 1);
        check("copy preview empty input", CopyPreviewText.Summarize("") == "");
        check("copy preview bounds combining sequences", CopyPreviewText.Summarize("e" + new string('\u0301', 10000)) == "…");
        check("copy preview exact limit", CopyPreviewText.Summarize(new string('a', 20)) == new string('a', 20));
        var settings = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { ClipboardPreview = false, ClipboardPreviewContent = false }))!;
        check("copy preview persists opt out", !settings.ClipboardPreview && !settings.ClipboardPreviewContent);
        check("copy preview short user example", CopyPreviewText.Summarize("已做好新版：", 10) == "已做好新版：");
        check("copy preview configurable length", CopyPreviewText.Summarize("一二三四五六七八九十一二三四五", 10) == "一二三四五六七八九十…");
        check("copy preview counts spaces", CopyPreviewText.Summarize("12345678 abcd", 10) == "12345678 a…");
        check("copy preview ignores trailing space at limit", CopyPreviewText.Summarize("1234567890  ", 10) == "1234567890");
        check("copy preview keeps whole emoji at limit", CopyPreviewText.Summarize("123456789👨‍👩‍👧‍👦x", 10) == "123456789👨‍👩‍👧‍👦…");
        check("copy preview clamps invalid limit", CopyPreviewText.Summarize("1234567890abcdef", -1) == "1234567890…");
        check("copy preview permits 30 characters", CopyPreviewText.Summarize(new string('a', 31), 30) == new string('a', 30) + "…");
        var old = new DeckSettings { ClipboardPreviewMaxLength = 72 }; old.Normalize();
        check("copy preview migrates previous maximum", old.ClipboardPreviewMaxLength == 30);
        var saved = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(new DeckSettings { ClipboardPreviewMaxLength = 24 }))!;
        check("copy preview persists maximum", saved.ClipboardPreviewMaxLength == 24);
        check("copy preview old settings retain default", JsonSerializer.Deserialize<DeckSettings>("{}")!.ClipboardPreviewMaxLength == 20);
        var monitor = new DipRect(0, 0, 320, 720);
        foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch })
        {
            var shape = CollapsedGeometry.Resolve(settings, style, monitor, monitor, contentWanted: 1000);
            check($"copy preview stays inside narrow monitor {style}", shape.Surface.X >= 0 && shape.Surface.Right <= 320);
        }
    }
}
