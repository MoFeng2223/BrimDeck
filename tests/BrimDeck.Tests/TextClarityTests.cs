using System.Text.RegularExpressions;

// The rule of TextClarity (src/BrimDeck/TextClarity.cs): WPF draws the text of a layered window and of a pop-up in grayscale, and
// wherever it lands in a layer of its own (a clip, a scroll view, a text box, a fade), unless the element it lies in carries the
// ClearType hint. Set on one element, the hint does not reach the layers below it, so it is given only through TextClarity, which
// puts it on every element of a tree. Every window sets it on its root; a pop-up takes it from what it opens from.
static class TextClarityTests
{
    // The windows that set no TextClarity, and why their text needs none. A new window goes here only if it shows no text.
    private static readonly Dictionary<string, string> WithoutText = new()
    {
        ["MoveOverlay"] = "a picture of the island while it is moved",
    };

    // What gives a window's root TextClarity: the frame of an ordinary window, or the property set in code or in XAML.
    private const string Marked = @"SettingsChrome\.Apply\(|TextClarity\.SetClearType\(|TextClarity\.ClearType=""True""";

    public static void Run(Action<string, bool> check)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BrimDeck.sln"))) root = root.Parent;
        if (root is null) { check("Text clarity: the source tree is found", false); return; }
        string sep = Path.DirectorySeparatorChar.ToString();
        var files = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "BrimDeck"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs") || file.EndsWith(".xaml"))
            .Where(file => !file.Contains(sep + "obj" + sep) && !file.Contains(sep + "bin" + sep))
            .ToDictionary(file => Path.GetFileName(file), File.ReadAllText);

        var hinted = files.Where(file => file.Key != "TextClarity.cs" && Regex.IsMatch(file.Value, @"ClearTypeHint\s*\(|ClearTypeHint\s*=|\.ClearTypeHint\b"))
            .Select(file => file.Key).ToList();
        check("Text clarity: no element sets the ClearType hint itself; TextClarity gives it to a whole tree" + (hinted.Count > 0
            ? $" ({string.Join(", ", hinted)}: set TextClarity.ClearType on the window's root; a pop-up needs nothing)" : ""), hinted.Count == 0);

        // Every window: a class of a window (its files, and its XAML), and the windows a file makes in code, each of which must be
        // marked there.
        var unset = new List<string>();
        var classes = new HashSet<string>();
        foreach (var (file, text) in files)
        {
            foreach (Match match in Regex.Matches(text, @"\bclass\s+(\w+)\s*:\s*Window\b"))
            {
                string name = match.Groups[1].Value;
                classes.Add(name);
                bool marked = files.Any(other => (other.Key == name + ".xaml" || Regex.IsMatch(other.Value, @"\bclass\s+" + name + @"\b")) && Regex.IsMatch(other.Value, Marked));
                if (!marked && !WithoutText.ContainsKey(name)) unset.Add(name);
            }
            int made = Regex.Matches(text, @"\bnew\s+Window\s*\{").Count;
            if (made > Regex.Matches(text, Marked).Count) unset.Add(file);
        }
        check("Text clarity: the windows are found", classes.Contains("MainWindow") && classes.Contains("SettingsWindow") && classes.Contains("IslandNotice"));
        check("Text clarity: every window gives its text ClearType through TextClarity" + (unset.Count > 0
            ? $" ({string.Join(", ", unset)}: its text turns grayscale and jagged, and so does that of every menu opened from it; call SettingsChrome.Apply for an ordinary window, set TextClarity.ClearType on a layered window's root where its text lies over an opaque surface, or enter it in TextClarityTests.WithoutText)"
            : ""), unset.Count == 0);
        var gone = WithoutText.Keys.Where(name => !classes.Contains(name)).ToList();
        check("Text clarity: every window entered without text still exists" + (gone.Count > 0 ? " (" + string.Join(", ", gone) + ")" : ""), gone.Count == 0);
    }
}
