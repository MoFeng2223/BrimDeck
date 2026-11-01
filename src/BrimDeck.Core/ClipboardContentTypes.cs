using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BrimDeck.Core;

// Classification is local and deterministic. A URL, command or SVG is data, never something to fetch or execute.
public static partial class ClipboardContentTypes
{
    public static ClipboardContentKind Detect(string text)
    {
        string value = text.Trim();
        if (ClipboardColors.Swatches(value).Count > 0) return ClipboardContentKind.Color;
        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") return ClipboardContentKind.Link;
        if (IsSvg(value)) return ClipboardContentKind.Svg;
        return Language(value).Length > 0 ? ClipboardContentKind.Code : ClipboardContentKind.Text;
    }

    public static string Language(string text)
    {
        string value = text.Trim();
        if (value.Length == 0) return "";
        if (value[0] is '{' or '[')
        {
            try { using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 }); return "JSON"; }
            catch (JsonException) { }
        }
        // Only inspect a bounded prefix for heuristic hints; full contents remain searchable and reusable.
        value = value[..Math.Min(value.Length, 12000)];
        if (Shell().IsMatch(value)) return "Shell";
        if (Log().IsMatch(value)) return "Log";
        if (Python().IsMatch(value)) return "Python";
        if (Sql().IsMatch(value)) return "SQL";
        if (Web().IsMatch(value)) return "JavaScript";
        if (CSharp().IsMatch(value)) return "C#";
        if (value.StartsWith("```", StringComparison.Ordinal)) return value.Split('\n', 2)[0].Trim('`', '\r', ' ') is { Length: > 0 } language ? language : "Code";
        return "";
    }

    public static bool IsSvg(string text)
    {
        if (text.Length > 1_000_000 || !text.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            var root = XDocument.Load(reader).Root;
            return root?.Name.LocalName == "svg" && root.Descendants().Take(2049).Count() <= 2048;
        }
        catch (XmlException) { return false; }
    }

    public static string PlainHtml(string html)
    {
        if (html.Length > 1_000_000) return "";
        string fragment = html;
        int start = fragment.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase), end = fragment.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) fragment = fragment[(start + 20)..end];
        return WebUtility.HtmlDecode(Markup().Replace(fragment, " ")).Trim();
    }

    public static string Label(ClipboardContentKind kind) => kind switch
    {
        ClipboardContentKind.Link => Loc.T("链接", "Links"), ClipboardContentKind.Color => Loc.T("颜色", "Colors"),
        ClipboardContentKind.Image => Loc.T("图片", "Images"), ClipboardContentKind.Screenshot => Loc.T("截图", "Screenshots"),
        ClipboardContentKind.Files => Loc.T("文件", "Files"), ClipboardContentKind.Code => Loc.T("代码 / 数据", "Code / data"),
        ClipboardContentKind.RichText => Loc.T("富文本", "Rich text"), ClipboardContentKind.Svg => "SVG", _ => Loc.T("文本", "Text")
    };
    public static string Detail(ClipboardContent content) => content.Kind switch
    {
        ClipboardContentKind.Code => content.Language.Length > 0 ? content.Language : Language(content.Text),
        ClipboardContentKind.Link when Uri.TryCreate(content.Text.Trim(), UriKind.Absolute, out var uri) => uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "GitHub" : uri.Host,
        _ => Label(content.Kind)
    };

    [GeneratedRegex(@"^(?:\$ |PS [^>\r\n]*> |(?:sudo |npm |npx |pnpm |yarn |git |dotnet |curl |docker |kubectl |pip |python |node |ssh |cd |ls |Get-|Set-|Invoke-))", RegexOptions.IgnoreCase, 100)] private static partial Regex Shell();
    [GeneratedRegex(@"(?:^|\n)(?:Traceback \(|\s*at [\w.]+\(|(?:ERROR|FATAL|Exception|TypeError|SyntaxError|ReferenceError)\b)", RegexOptions.None, 100)] private static partial Regex Log();
    [GeneratedRegex(@"(?:^|\n)(?:def \w+\(|from \w[\w.]* import |import \w[\w.]*\s*(?:\n|$)|class \w+(?:\([^)]*\))?:)", RegexOptions.None, 100)] private static partial Regex Python();
    [GeneratedRegex(@"\b(?:SELECT\s+.+\s+FROM|INSERT\s+INTO|CREATE\s+TABLE|UPDATE\s+\w+\s+SET)\b", RegexOptions.IgnoreCase, 100)] private static partial Regex Sql();
    [GeneratedRegex(@"(?:^|\n)\s*(?:(?:export |async )?(?:const|let|var|function) \w+\s*[=(]|import .+ from ['""].+['""])", RegexOptions.None, 100)] private static partial Regex Web();
    [GeneratedRegex(@"(?:^|\n)\s*(?:(?:public|private|internal) (?:static |sealed )?(?:class|void|Task|record|string|int)\b|using [\w.]+;|namespace [\w.]+[;{])", RegexOptions.None, 100)] private static partial Regex CSharp();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.NonBacktracking, 100)] private static partial Regex Markup();
}

public static partial class ClipboardColors
{
    // Returns WPF/CSS-independent ARGB hex values. Gradient stops are sampled, not interpreted as executable CSS.
    public static IReadOnlyList<string> Swatches(string text)
    {
        string value = text.Trim();
        if (value.Length > 2048) return [];
        if (ClipboardContent.ColorHex(value) is { } hex) return [hex.Length == 9 ? "#" + hex[7..9] + hex[1..7] : hex];
        if (TryRgb(value) is { } rgb) return [rgb];
        if (!(value.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase) || value.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase)) || !value.EndsWith(')')) return [];
        var colors = new List<string>();
        foreach (Match match in Stops().Matches(value))
        {
            string stop = match.Value;
            if (stop.StartsWith('#') && ClipboardContent.ColorHex(stop) is { } h) colors.Add(h.Length == 9 ? "#" + h[7..9] + h[1..7] : h);
            else if (TryRgb(stop) is { } r) colors.Add(r);
            if (colors.Count >= 16) break;
        }
        return colors.Count >= 2 ? colors : [];
    }
    private static string? TryRgb(string value)
    {
        var match = Rgb().Match(value);
        if (!match.Success) return null;
        var parts = match.Groups[1].Value.Split([',', '/', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return null;
        var channels = new int[4]; channels[3] = 255;
        for (int i = 0; i < parts.Length; i++)
        {
            bool percent = parts[i].EndsWith('%');
            if (!double.TryParse(parts[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return null;
            double maximum = percent ? 100 : i == 3 ? 1 : 255;
            if (number < 0 || number > maximum) return null;
            channels[i] = (int)Math.Round(number / maximum * 255);
        }
        return channels[3] == 255 ? $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}" : $"#{channels[3]:X2}{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
    }
    [GeneratedRegex(@"^rgba?\(([^()]*)\)$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 100)] private static partial Regex Rgb();
    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|rgba?\([^()]*\)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, 100)] private static partial Regex Stops();
}
