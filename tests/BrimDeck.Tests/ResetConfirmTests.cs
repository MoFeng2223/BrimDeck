using System.Text.RegularExpressions;

// The "restore defaults" confirmation of the quota page says what only that page loses: the application list is put back, so the
// applications the user added and their quota sources go. The page that does so (the case of ResetPage that sets Apps) and the page
// the confirmation warns about (ResetConfirmText) must be the same, so the warning cannot drift from what the reset does.
internal static class ResetConfirmTests
{
    public static void Run(Action<string, bool> check)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "BrimDeck.sln"))) source = source.Parent;
        check("Reset confirm: the source tree is found", source is not null);
        if (source is null) return;
        string window = File.ReadAllText(Path.Combine(source.FullName, "src", "BrimDeck", "SettingsWindow.cs"));
        var resets = Regex.Match(window, @"case (\d+):\{var previous=s\.Apps;[^\r\n]*s\.Apps=defaults\.Apps;");
        var warns = Regex.Match(window, @"\(page == (\d+) \? Loc\.T\(""[^""]*应用列表也会换回默认");
        check("Reset confirm: the page whose reset replaces the application list is found", resets.Success);
        check("Reset confirm: the confirmation warns on a page", warns.Success);
        check("Reset confirm: it is the same page, so the warning matches what the reset does", resets.Success && warns.Success && resets.Groups[1].Value == warns.Groups[1].Value);
        check("Reset confirm: the warning says the added applications and their sources are removed in both languages",
            window.Contains("你添加的应用、各应用的额度来源与自定义设置都会消失") && window.Contains("the applications you added, and each one's quota source and custom settings, are removed"));
        // The confirmation without the warning (the page's text before it) is rejected.
        check("Reset confirm: the finder rejects a confirmation without the warning", !Regex.IsMatch(window.Replace("应用列表也会换回默认", "应用列表"), @"\(page == (\d+) \? Loc\.T\(""[^""]*应用列表也会换回默认"));
    }
}
