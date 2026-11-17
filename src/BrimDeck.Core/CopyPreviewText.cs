using System.Globalization;
using System.Text;

namespace BrimDeck.Core;

public static class CopyPreviewText
{
    public const int MinimumLength = 10, MaximumLength = 30, DefaultLength = 20;

    // Bound work even for a million-character history item; keep emoji/combining sequences intact.
    public static string Summarize(string text, int maximum = DefaultLength)
    {
        maximum = Math.Clamp(maximum, MinimumLength, MaximumLength);
        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        int scanned = 0, shown = 0;
        bool space = false;
        while (elements.MoveNext())
        {
            if (++scanned > 1024) { result.Append('…'); break; }
            string element = elements.GetTextElement();
            if (element.Length > 256) { result.Append('…'); break; }
            if (string.IsNullOrWhiteSpace(element)) { space = result.Length > 0; continue; }
            var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (shown + (space ? 1 : 0) >= maximum) { result.Append('…'); break; }
            if (space) { result.Append(' '); shown++; space = false; }
            result.Append(element); shown++;
        }
        return result.ToString();
    }
}
