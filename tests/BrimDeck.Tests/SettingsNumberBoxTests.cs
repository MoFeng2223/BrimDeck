// Source guard for the settings window's number box (SettingsControls.NumericControls). The click behavior itself is checked on real
// controls in BrimDeck.WindowsTests (SettingsFocusTests); this makes sure the box the window builds is wired to it.
internal static class SettingsNumberBoxTests
{
    public static void Run(Action<string, bool> check)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        if (source is null) { check("The source tree is found for the settings number box", false); return; }
        var root = source.FullName;
        var controls = File.ReadAllText(Path.Combine(root, "src", "BrimDeck", "SettingsControls.cs"));
        int start = controls.IndexOf("NumericControls(string title", StringComparison.Ordinal);
        int end = start < 0 ? -1 : controls.IndexOf("return(slider,chip);", start, StringComparison.Ordinal);
        check("The settings number box hands its whole frame's click to its field (FieldBox)", start >= 0 && end > start && controls[start..end].Contains("FieldBox.Attach(chip, input)"));
        var release = File.ReadAllText(Path.Combine(root, "src", "BrimDeck", "EmptyClickFocus.cs"));
        check("The empty-click rule leaves a FieldBox frame alone", release.Contains("FieldBox.Owns(node)"));
    }
}
