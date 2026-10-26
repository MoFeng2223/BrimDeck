using System.IO;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck.Island;

// A plugin folder's manifest: the manifest.json a plugin author writes, read once per scan. Everything on it is plain
// data from a local file — no code of any kind runs while loading a plugin, which is the whole point of the web model.
// MinWidth/MinHeight are the smallest page the plugin can show without losing content; zero means it asks for none.
internal sealed record ExtensionManifest(string Id, string TitleZh, string TitleEn, string Glyph, string Entry, double MinWidth, double MinHeight);

internal static class ExtensionManifests
{
    // Reads one plugin folder's manifest, or the reason it cannot be one. A folder without a manifest is not a plugin
    // and not an error either: the caller notes it as skipped so a stray folder is not silently invisible.
    public static (ExtensionManifest? Manifest, string? Error) Read(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 64 * 1024) return (null, "manifest.json: " + Loc.T("文件过大（上限 64 KB）", "is too large (64 KB max)"));
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            string Id(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
            // A page's floor is the author's claim about their own layout; a wild number is dropped rather than trusted.
            double Min(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && number >= 40 && number <= 4000 ? Math.Round(number) : 0;
            var id = Id("id");
            var titleZh = Id("titleZh");
            var titleEn = Id("titleEn");
            var glyph = Id("glyph");
            if (glyph.Length > 4096) glyph = "";
            var entry = Id("entry");
            if (id.Length == 0) return (null, "manifest.json: id " + Loc.T("缺失或为空", "is missing or empty"));
            if (titleZh.Length > 64) titleZh = titleZh[..64];
            if (titleEn.Length > 64) titleEn = titleEn[..64];
            if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
                return (null, "manifest.json: id " + Loc.T("只允许字母、数字、- 和 .（64 字符内）", "allows letters, digits, - and . only (64 chars max)"));
            if (titleZh.Length == 0 && titleEn.Length == 0) return (null, "manifest.json: titleZh/titleEn " + Loc.T("至少要有一个", "need at least one of the two"));
            if (entry.Length == 0) entry = "index.html";
            // The entry stays inside the plugin folder: a relative path with no parent walk and no scheme of its own.
            if (entry.Contains("..") || entry.Contains('\\') || Path.IsPathRooted(entry) || Uri.TryCreate(entry, UriKind.Absolute, out _))
                return (null, "manifest.json: entry " + Loc.T("必须是插件文件夹内的相对路径", "must be a relative path inside the plugin folder"));
            if (!File.Exists(Path.Combine(folder, entry))) return (null, Loc.T("入口文件不存在：", "The entry file is missing: ") + entry);
            return (new ExtensionManifest(id, titleZh.Length > 0 ? titleZh : titleEn, titleEn.Length > 0 ? titleEn : titleZh, glyph, entry,
                Min("minWidth"), Min("minHeight")), null);
        }
        catch (Exception ex)
        {
            return (null, "manifest.json: " + ex.Message.Split('\n')[0]);
        }
    }
}
