namespace BrimDeck.Core;

// One message between BrimDeck and a helper process of its own (the paste helper, the drop helper): a word and its fields on one
// line, separated by tabs. The fields are numbers and paths; Windows allows no control characters in names, so a path holds
// neither a tab nor a line break and needs no escaping. A field that does is refused rather than sent broken.
public static class HelperLine
{
    public static string Make(string word, params string[] fields)
    {
        foreach (var part in fields.Prepend(word))
            if (part.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0) throw new ArgumentException("A helper message field holds a tab or a line break.", nameof(fields));
        return fields.Length == 0 ? word : word + "\t" + string.Join('\t', fields);
    }

    public static string[] Parse(string line) => line.Split('\t');

    // A point on screen in physical pixels as OLE passes it to a drop target (POINTL by value): x in the low half, y in the high.
    public static long Point(int x, int y) => ((long)y << 32) | (uint)x;
    public static (int X, int Y) Split(long point) => ((int)(point & 0xFFFFFFFF), (int)(point >> 32));
}
