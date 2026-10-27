using BrimDeck.Core;

// Whether "Reload" has anything to reload. The interesting case is the one a timestamp misses: a file replaced by a
// different one of the same length, with its write time preserved — what restoring from version control produces.
internal static class ExtensionStampTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "stamp-fixture");
        try
        {
            Directory.CreateDirectory(folder);
            string Write(string name, string text)
            {
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                return path;
            }

            var style = Write("style.css", "body{color:red}");
            Write("index.html", "<div>hello</div>");
            var first = ExtensionStamp.Of(folder);
            check("ExtensionStamp: a readable folder answers with a hash", first is { Length: 64 });

            // Nothing touched: the same answer, which is what keeps a plugin's pages and their state across a reload.
            check("ExtensionStamp: an unchanged folder stamps the same", ExtensionStamp.Of(folder) == first);

            // The case this class exists for: same length, same write time, different bytes. A stamp built from size and
            // timestamps would call this unchanged and the reload would silently do nothing.
            var written = File.GetLastWriteTimeUtc(style);
            File.WriteAllText(style, "body{color:blu}"); // same length as "red"
            File.SetLastWriteTimeUtc(style, written);
            var replaced = ExtensionStamp.Of(folder);
            check("ExtensionStamp: a same-length edit with a preserved timestamp is a change",
                replaced is not null && replaced != first);

            // A file that matters and is not the entry or the manifest: still the folder's business.
            var script = Write("app.js", "console.log(1)");
            var beforeScript = ExtensionStamp.Of(folder);
            File.WriteAllText(script, "console.log(2)");
            check("ExtensionStamp: editing a script is a change", ExtensionStamp.Of(folder) != beforeScript);

            // A file added or removed is a change too, even if the text of everything else is the same.
            var withExtra = Write("extra.txt", "x");
            var beforeRemove = ExtensionStamp.Of(folder);
            File.Delete(withExtra);
            check("ExtensionStamp: removing a file is a change", ExtensionStamp.Of(folder) != beforeRemove);

            // Subfolders are part of the plugin, so they are part of the stamp.
            var nested = Write("assets/deep.css", "a{}");
            var beforeNested = ExtensionStamp.Of(folder);
            File.WriteAllText(nested, "b{}");
            check("ExtensionStamp: a file in a subfolder is part of the stamp", ExtensionStamp.Of(folder) != beforeNested);

            // A folder that cannot be read is not a folder that did not change: no answer at all, so nothing is reused.
            check("ExtensionStamp: a missing folder answers nothing", ExtensionStamp.Of(Path.Combine(root, "no-such-folder")) is null);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception) { }
        }
    }
}
