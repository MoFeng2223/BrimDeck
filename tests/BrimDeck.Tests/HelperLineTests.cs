using BrimDeck.Core;

// The lines BrimDeck and its helper processes exchange (HelperLine): paths with spaces and other scripts come back as they went,
// a field that would break the line is refused, and a point on screen survives the trip through OLE's POINTL in both halves.
internal static class HelperLineTests
{
    internal static void Run(Action<string, bool> check)
    {
        string[] paths = [@"C:\Users\某某\Desktop\序号", @"D:\a b\c.txt"];
        var line = HelperLine.Make("drop", ["1", "-20", "300", .. paths]);
        check("Helper lines: a drop with its key state, point and paths reads back field by field",
            HelperLine.Parse(line) is ["drop", "1", "-20", "300", var first, var second] && first == paths[0] && second == paths[1]);
        check("Helper lines: a word without fields is the word alone", HelperLine.Make("hide") == "hide" && HelperLine.Parse("hide") is ["hide"]);
        bool refused(string field) { try { HelperLine.Make("drop", field); return false; } catch (ArgumentException) { return true; } }
        check("Helper lines: a field holding a tab or a line break is refused", refused("a\tb") && refused("a\nb") && refused("a\rb"));
        check("Helper lines: a point on screen goes through POINTL and back, negative places included",
            HelperLine.Split(HelperLine.Point(-1920, 1080)) == (-1920, 1080) && HelperLine.Split(HelperLine.Point(2560, -40)) == (2560, -40)
            && HelperLine.Point(10, 20) == (20L << 32 | 10));
    }
}
