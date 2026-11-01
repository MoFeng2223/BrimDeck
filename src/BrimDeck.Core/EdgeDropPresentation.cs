// Translated from Edge-Drop 0.4.0 (767b6d63), Apache-2.0.
// Sources: src/lib/previewImageFit.ts, urlPreview.ts and components/StackItems.tsx.
// WPF adaptation and supported URL branches are documented in third_party/EdgeDrop.PORTING.md.
using System.Net;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

public readonly record struct PreviewImageBounds(double MinWidth, double MaxWidth, double MaxHeight, double SidePadding, double Dpi);
public readonly record struct PreviewImageFit(double FlyoutWidth, double Width, double Height, bool Scroll);
public sealed record OfflineLink(string Service, string Subtitle, string Title, string DisplayUrl, string CleanUrl);

public static class EdgeDropPresentation
{
    public static PreviewImageFit FitImage(double imageWidth, double imageHeight, PreviewImageBounds b)
    {
        double minInner = Math.Max(1, b.MinWidth - b.SidePadding), maxInner = Math.Max(minInner, b.MaxWidth - b.SidePadding), maxH = Math.Max(1, b.MaxHeight);
        if (!(imageWidth > 0) || !(imageHeight > 0)) return new(b.MinWidth, minInner, Math.Min(maxH, minInner), false);
        double dpi = double.IsFinite(b.Dpi) ? Math.Max(1, b.Dpi) : 1;
        double upscaleCap = 2 / dpi;
        bool wide = imageWidth / dpi > minInner;
        double boxW = wide ? maxInner : minInner, cap = wide ? Math.Min(upscaleCap, 1 / dpi) : upscaleCap;
        double scale = Math.Min(cap, Math.Min(boxW / imageWidth, maxH / imageHeight));
        bool scroll = false;
        if (scale < .6 && imageHeight / imageWidth >= 1.8 && maxH / imageHeight < boxW / imageWidth)
        { scale = Math.Min(cap, boxW / imageWidth); scroll = imageHeight * scale > maxH; }
        // JavaScript Math.round rounds positive halves upward, unlike Math.Round's default banker's rounding.
        double width = Math.Max(1, Math.Floor(imageWidth * scale + .5)), height = Math.Max(1, Math.Floor(imageHeight * scale + .5));
        return new(Math.Min(b.MaxWidth, Math.Max(b.MinWidth, width + b.SidePadding)), width, height, scroll);
    }

    // CardMenu / colorUtils.colorFormats: convert only the host's inert, single-color payloads, never a gradient.
    public static IReadOnlyList<(string Format, string Text)> ColorFormats(string raw)
    {
        var swatches = ClipboardColors.Swatches(raw);
        if (swatches.Count != 1) return [];
        string value = swatches[0];
        int offset = value.Length == 9 ? 3 : 1;
        int r = Convert.ToInt32(value.Substring(offset, 2), 16), g = Convert.ToInt32(value.Substring(offset + 2, 2), 16), b = Convert.ToInt32(value.Substring(offset + 4, 2), 16);
        double a = value.Length == 9 ? Convert.ToInt32(value.Substring(1, 2), 16) / 255d : 1;
        double rn = r / 255d, gn = g / 255d, bn = b / 255d, max = Math.Max(rn, Math.Max(gn, bn)), min = Math.Min(rn, Math.Min(gn, bn));
        double l = (max + min) / 2, d = max - min, s = d == 0 ? 0 : l > .5 ? d / (2 - max - min) : d / (max + min);
        double h = d == 0 ? 0 : max == rn ? (gn - bn) / d + (gn < bn ? 6 : 0) : max == gn ? (bn - rn) / d + 2 : (rn - gn) / d + 4;
        static int Round(double number) => (int)Math.Floor(number + .5);
        string alpha = (Round(a * 100) / 100d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        bool translucent = a < 1;
        string hex = $"#{r:X2}{g:X2}{b:X2}" + (translucent ? Round(a * 255).ToString("X2") : "");
        string rgb = translucent ? $"rgba({r}, {g}, {b}, {alpha})" : $"rgb({r}, {g}, {b})";
        string hsl = translucent ? $"hsla({Round(h * 60) % 360}, {Round(s * 100)}%, {Round(l * 100)}%, {alpha})" : $"hsl({Round(h * 60) % 360}, {Round(s * 100)}%, {Round(l * 100)}%)";
        string source = raw.TrimStart().StartsWith('#') ? "HEX" : "RGB";
        return new[] { ("HEX", hex), ("RGB", rgb), ("HSL", hsl) }.Where(format => format.Item1 != source).ToArray();
    }

    public static double StackOpenWidth(int count, bool gallery)
    {
        int columns = gallery ? count : (int)Math.Ceiling(count / 2d);
        return Math.Max(240, columns * (gallery ? 128 : 232) + Math.Max(0, columns - 1) * 6 + (gallery ? 8 : 0) + 22);
    }

    private static readonly string[] TrackingPrefixes = ["utm_", "ga_", "mc_", "hs_", "pk_", "mtm_"];
    private static readonly HashSet<string> Tracking = new("fbclid gclid dclid msclkid twclid gbraid wbraid ttclid li_fat_id epik srsltid ref ref_src ref_url source si igshid igsh feature trk trkid pp _hsenc _hsmi mc_cid mc_eid yclid zanpid sc_cid cmpid icid ncid mkt_tok spreportid __twitter_impression vero_id vero_conv oly_anon_id oly_enc_id rb_clickid s_cid share_id".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Shorteners = new("bit.ly bitly.com t.co tinyurl.com ow.ly buff.ly rebrand.ly is.gd v.gd cutt.ly rb.gy shorturl.at s.id t.ly bl.ink dub.sh tiny.cc shorte.st lnkd.in amzn.to amzn.in amzn.eu a.co goo.gl maps.app.goo.gl forms.gle g.co fb.me pin.it spoti.fi spotify.link apple.co aka.ms 1drv.ms redd.it dl.flipkart.com vm.tiktok.com vt.tiktok.com instagr.am trib.al ift.tt lnk.to msft.it surl.li".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GithubReserved = new("settings features marketplace orgs topics explore notifications pulls issues sponsors about pricing login join new codespaces enterprise collections trending events search apps security customer-stories readme site team solutions resources contact organizations dashboard copilot premium-support".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Fillers = new("view edit index default home main p dp gp d s w r u c g e t a b l x product products item items itm article articles post posts page pages search amp m share shared embed watch file files folder folders open details detail show read story stories content contents entry category categories tag tags id preview download status comments thread topic articleshow photostory videoshow liveblog web www public static assets en us html php aspx redirect link out go click".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Acronyms = new("AI API UI UX SDK CLI URL URI CSS HTML JS TS SQL DB AWS GCP OS ID IP VR AR MR ML LLM PR FAQ PDF CSV JSON YAML XML SVG PNG JPG GIF NPM PYPI USA UK EU GB TB MB KB HD UHD TV PC USB SSD GPU CPU RAM IOS NFT CEO GPT".Split(' '));
    private static readonly HashSet<string> SmallWords = new("a an the and or but of in on at to for by with vs via as from into".Split(' '));
    private static readonly Dictionary<string, OfflineLink> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    private static bool OnHost(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    private static string Decode(string text) { try { return Uri.UnescapeDataString(text); } catch (UriFormatException) { return text; } }
    public static string TrimLinkText(string raw)
    {
        string text = raw.Trim().TrimStart('<', '"', '\'');
        for (int i = 0; i < 8 && text.Length > 1; i++)
        {
            char last = text[^1];
            if (".,;:!?'\">".Contains(last)) { text = text[..^1]; continue; }
            char opening = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (opening != '\0' && text.Count(c => c == last) > text.Count(c => c == opening)) { text = text[..^1]; continue; }
            break;
        }
        return text;
    }
    public static string StripTracking(string raw)
    {
        raw = raw.Trim(); int question = raw.IndexOf('?');
        if (question < 0 || !(raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) return raw;
        int hashAt = raw.IndexOf('#', question);
        string query = raw[(question + 1)..(hashAt < 0 ? raw.Length : hashAt)], hash = hashAt < 0 ? "" : raw[hashAt..];
        string[] parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(part => { string key = Decode(part.Split('=', 2)[0].Replace('+', ' ')); return !Tracking.Contains(key) && !TrackingPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); }).ToArray();
        return kept.Length == parts.Length ? raw : raw[..question] + (kept.Length > 0 ? "?" + string.Join('&', kept) : "") + hash;
    }
    private static string Query(Uri uri, string key)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        { var pair = part.Split('=', 2); if (Decode(pair[0].Replace('+', ' ')) == key) return pair.Length > 1 ? Decode(pair[1].Replace('+', ' ')) : ""; }
        return "";
    }
    private static string Search(Uri uri) => new[] { "q", "query", "search_query", "search", "k", "keywords", "keyword", "term", "text", "p", "wd" }.Select(key => Query(uri, key)).FirstOrDefault(value => value.Length > 0 && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? "";
    private static string? Unwrap(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant(); if (host.StartsWith("www.")) host = host[4..];
        string path = uri.AbsolutePath; string target = "";
        if (Regex.IsMatch(host, @"^google\.[a-z.]+$") && path == "/url") target = Query(uri, "q") is { Length: > 0 } q ? q : Query(uri, "url");
        else if ((host is "l.facebook.com" or "lm.facebook.com" or "l.messenger.com") && path.StartsWith("/l.php") || host == "l.instagram.com") target = Query(uri, "u");
        else if (host == "youtube.com" && path == "/redirect") target = Query(uri, "q");
        else if (OnHost(host, "safelinks.protection.outlook.com") || host == "out.reddit.com" || host == "slack-redir.net" && path.StartsWith("/link") || host == "linkedin.com" && path.StartsWith("/redir/redirect")) target = Query(uri, "url");
        else if (host == "duckduckgo.com" && path.StartsWith("/l/")) target = Query(uri, "uddg");
        return Uri.TryCreate(target, UriKind.Absolute, out var inner) && inner.Scheme is "http" or "https" ? target : null;
    }
    public static string CleanLink(string raw)
    {
        string text = TrimLinkText(raw);
        for (int i = 0; i < 3; i++)
        {
            if (!Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var uri) || Unwrap(uri) is not { } target) break;
            text = target;
        }
        return StripTracking(text);
    }
    public static string HumanizeSlug(string slug)
    {
        string decoded = Regex.Replace(Regex.Replace(Decode(slug), @"[-_+]+", " "), @"\.(html?|php|aspx?|jsp|json|md|txt|cms|shtml|cfm)$", "", RegexOptions.IgnoreCase).Trim();
        if (decoded.Length < 2 || Regex.IsMatch(decoded, @"^[0-9a-f]{8,}$|^\d+$", RegexOptions.IgnoreCase)) return "";
        return string.Join(' ', decoded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((word, i) => Acronyms.Contains(word.ToUpperInvariant()) ? word.ToUpperInvariant() : i > 0 && SmallWords.Contains(word.ToLowerInvariant()) ? word.ToLowerInvariant() : word.Any(char.IsUpper) && word.Any(char.IsLower) ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }
    private static string PathTitle(string[] segments)
    {
        foreach (string part in segments.Reverse())
        {
            string value = Decode(part);
            if (Fillers.Contains(value) || Regex.IsMatch(value, @"^[0-9a-f]{8,}$|^\d+$|^v?\d+(\.\d+)+$|^[0-9a-f]{8}-[0-9a-f]{4}-", RegexOptions.IgnoreCase)) continue;
            value = Regex.Replace(value, @"[-_](?:[0-9a-f]{8,}|\d{5,})$", "", RegexOptions.IgnoreCase);
            if (value.Length >= 6 && value.Any(char.IsDigit) && !value.Any(c => "-_.~+ ".Contains(c))) continue;
            if (value.Any(char.IsLetter) && HumanizeSlug(value) is { Length: >= 2 } title) return title;
        }
        return "";
    }
    public static OfflineLink Link(string raw)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(raw, out var cached)) return cached;
            var value = ParseLink(raw);
            if (Cache.Count >= 400) Cache.Remove(CacheOrder.Dequeue());
            Cache.Add(raw, value); CacheOrder.Enqueue(raw); return value;
        }
    }
    private static OfflineLink ParseLink(string raw)
    {
        string clean = CleanLink(raw);
        if (!Uri.TryCreate(clean.Contains("://") ? clean : "https://" + clean, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return new("", "", "", raw, clean);
        // ASCII domains stay visible: the host must never disguise a look-alike internationalized domain.
        string host = uri.IdnHost.ToLowerInvariant(); if (host.StartsWith("www.")) host = host[4..];
        string display = host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + Decode(uri.AbsolutePath == "/" ? "" : uri.AbsolutePath) + uri.Query + Decode(uri.Fragment);
        string[] seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToArray();
        string title = PathTitle(seg), subtitle = "", service = host;
        if (host == "localhost" || host.EndsWith(".localhost") || IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))
        { service = "This computer"; subtitle = uri.IsDefaultPort ? "" : "Port " + uri.Port; }
        else if (host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".internal") || IPAddress.TryParse(host, out var address) && address.GetAddressBytes() is { Length: 4 } b && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254)) service = "Local network";
        else if (host == "github.com")
        {
            service = "GitHub";
            if (seg.Length > 0 && GithubReserved.Contains(seg[0])) title = seg[0] == "search" ? Search(uri) : HumanizeSlug(seg[^1]);
            else if (seg.Length >= 2)
            {
                string repo = seg[0] + "/" + Regex.Replace(seg[1], @"\.git$", "", RegexOptions.IgnoreCase); string kind = seg.Length > 2 ? seg[2] : ""; title = repo; subtitle = "Repository";
                if (seg.Length > 3 && kind is "pull" or "issues" or "discussions") { title = repo + " #" + seg[3]; subtitle = kind == "pull" ? "Pull request" : kind == "issues" ? "Issue" : "Discussion"; }
                else if (seg.Length >= 4 && kind is "blob" or "tree") { title = repo + " · " + (seg.Length > 4 ? string.Join('/', seg.Skip(4)) : seg[3]); subtitle = kind == "blob" ? "File" : "Folder"; }
            }
            else if (seg.Length == 1) { title = "@" + seg[0]; subtitle = "Profile"; }
        }
        else if (OnHost(host, "youtube.com") || host == "youtu.be")
        { service = "YouTube"; subtitle = seg.FirstOrDefault() == "results" ? "Search" : seg.FirstOrDefault() == "shorts" ? "Short" : "Video"; title = subtitle == "Search" ? Search(uri) : ""; }
        else if (OnHost(host, "google.com")) { service = "Google"; if (Search(uri) is { Length: > 0 } search) { title = search; subtitle = "Search"; } }
        else if (host == "duckduckgo.com") { service = "DuckDuckGo"; title = Search(uri); subtitle = title.Length > 0 ? "Search" : ""; }
        if (Shorteners.Contains(host)) { subtitle = "Short link"; title = ""; }
        return new(service, subtitle, title, display, clean);
    }
}
