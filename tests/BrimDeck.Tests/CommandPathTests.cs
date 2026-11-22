using BrimDeck.Core;

// PATH as the registry keeps it (CommandPath): finding BrimDeck's folder in it however it is written, adding it as the last
// entry and taking it out, with every other entry left as it was written.
internal static class CommandPathTests
{
    public static void Run(Action<string, bool> check)
    {
        const string app = @"C:\Users\Li\AppData\Local\Programs\BrimDeck";
        // The variables of the test, read out the same on every machine.
        static string Expand(string text) => text.Replace("%LOCALAPPDATA%", @"C:\Users\Li\AppData\Local", StringComparison.OrdinalIgnoreCase).Replace("%TOOLS%", @"D:\tools", StringComparison.OrdinalIgnoreCase);
        string path = @"C:\Windows\system32;C:\Windows;%TOOLS%\bin;""C:\Program Files\Some;Tool\bin""";

        check("PATH: a folder that is not among the entries is not found", !CommandPath.Contains(path, app, Expand) && !CommandPath.Contains("", app, Expand) && !CommandPath.Contains(null, app, Expand));
        check("PATH: the folder is found whatever its letter case, trailing separator, slashes or quotes", CommandPath.Contains(path + ";" + app, app, Expand)
            && CommandPath.Contains(path + ";" + app.ToUpperInvariant() + "\\", app, Expand) && CommandPath.Contains("\"" + app + "\";" + path, app, Expand)
            && CommandPath.Contains(app.Replace('\\', '/') + ";" + path, app, Expand) && CommandPath.Contains(" " + app + " ", app + "\\", Expand));
        check("PATH: and when it is written with a variable", CommandPath.Contains(path + @";%LOCALAPPDATA%\Programs\BrimDeck", app, Expand) && CommandPath.Contains(path, @"D:\tools\bin", Expand));
        check("PATH: a longer or a shorter folder is another folder", !CommandPath.Contains(app + "2;" + app + @"\bin;C:\Users\Li\AppData\Local\Programs", app, Expand));
        check("PATH: a ; inside quotes belongs to its entry", CommandPath.Entries(path).SequenceEqual([@"C:\Windows\system32", @"C:\Windows", @"%TOOLS%\bin", @"""C:\Program Files\Some;Tool\bin"""])
            && CommandPath.Contains(path, @"C:\Program Files\Some;Tool\bin", Expand) && !CommandPath.Contains(path, @"C:\Program Files\Some", Expand));

        check("PATH: the folder is added as the last entry and the others stay as written", CommandPath.With(path, app, Expand) == path + ";" + app
            && CommandPath.With(path + ";", app, Expand) == path + ";" + app && CommandPath.With("", app, Expand) == app && CommandPath.With(null, app, Expand) == app);
        check("PATH: a folder already there, however written, is not added again", CommandPath.With(path + ";" + app.ToUpperInvariant() + "\\", app, Expand) == path + ";" + app.ToUpperInvariant() + "\\"
            && CommandPath.With(@"%LOCALAPPDATA%\Programs\BrimDeck;" + path, app, Expand) == @"%LOCALAPPDATA%\Programs\BrimDeck;" + path);
        check("PATH: a folder with a ; in its name is added in quotes", CommandPath.With(@"C:\Windows", @"D:\a;b", Expand) == @"C:\Windows;""D:\a;b""" && CommandPath.Contains(@"C:\Windows;""D:\a;b""", @"D:\a;b", Expand));
        check("PATH: taking the folder out leaves every other entry as written", CommandPath.Without(path + ";" + app, app, Expand) == path
            && CommandPath.Without(app + "\\;" + path + ";\"" + app + "\"", app, Expand) == path && CommandPath.Without(path, app, Expand) == path
            && CommandPath.Without(app, app, Expand) == "" && CommandPath.Without(null, app, Expand) == "");
        check("PATH: added and taken out again, PATH is as it was", CommandPath.Without(CommandPath.With(path, app, Expand), app, Expand) == path);
        check("PATH: no folder is no entry", !CommandPath.Contains(path + ";;", "", Expand) && CommandPath.With(path, " ", Expand) == path && CommandPath.Without(path, "", Expand) == path);
        check("PATH: a drive's root keeps its separator", CommandPath.Contains(@"D:\;C:\Windows", @"D:\", Expand) && !CommandPath.Contains(@"D:\tools", @"D:\", Expand));
        check("PATH: without a way to read variables out, the system's own is used", CommandPath.Contains(@"%SystemRoot%\system32", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32")));
    }
}
