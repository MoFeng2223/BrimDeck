using BrimDeck.Core;

// The file tray's own rules, without a screen: what it takes, what it refuses, and the order a drag inside it leaves behind.
internal static class FileTrayTests
{
    public static void Run(Action<string, bool> check)
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var folder = Path.Combine(Path.GetTempPath(), "brimdeck-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "notes.txt");
            File.WriteAllText(file, "hello");
            string picture = Path.Combine(folder, "picture.png");
            File.WriteAllText(picture, "not really a picture");

            var tray = new FileTray();
            check("Tray: an existing file is taken and a missing one is not",
                tray.Add([file, Path.Combine(folder, "missing.txt")], now) == 1 && tray.Count == 1);
            check("Tray: the same path is never taken twice", tray.Add([file, file], now) == 0 && tray.Count == 1);
            check("Tray: a folder is taken as a folder", tray.Add([folder], now) == 1 && tray.Items[1].Kind == TrayItemKind.Folder);
            check("Tray: a name is the file's own name", tray.Items[0].Name == "notes.txt");

            check("Tray: a drag inside the tray only moves the item",
                tray.Reorder(0, 1) && tray.Items[0].Kind == TrayItemKind.Folder && tray.Items[1].Name == "notes.txt");
            check("Tray: a reorder to the same place changes nothing", !tray.Reorder(1, 1));
            check("Tray: a reorder out of range changes nothing", !tray.Reorder(0, 9) && !tray.Reorder(-1, 0));
            check("Tray: the item keeps its place by id, not by where it sits", tray.IndexOf(tray.Items[1].Id) == 1);

            tray.Items[0].Pinned = true;
            check("Tray: clearing keeps the pinned item and takes the rest",
                tray.Clear(keepPinned: true) == 1 && tray.Count == 1 && tray.Items[0].Pinned);
            check("Tray: clearing without that takes everything", tray.Clear(keepPinned: false) == 1 && tray.Count == 0);

            // Retention: only unpinned items older than the time go.
            var ageing = new FileTray();
            ageing.Add([picture], now);
            ageing.Items[0].Pinned = true;
            check("Tray: retention leaves a pinned item alone", ageing.Expire(TimeSpan.FromMinutes(1), now.AddDays(1)) == 0 && ageing.Count == 1);
            check("Tray: retention with no time set keeps everything", ageing.Expire(TimeSpan.Zero, now.AddDays(1)) == 0);
            ageing.Items[0].Pinned = false;
            check("Tray: retention takes an unpinned item once it is old enough",
                ageing.Expire(TimeSpan.FromMinutes(1), now.AddMinutes(2)) == 1 && ageing.Count == 0);

            // Missing: shown, not silently dropped.
            var gone = new FileTray();
            gone.Add([file], now);
            File.Delete(file);
            check("Tray: existence is swept at most once per interval, so a page of cards costs one sweep, not one per card",
                gone.RefreshExistence(now) && !gone.RefreshExistence(now.AddSeconds(1)) && gone.RefreshExistence(now.AddSeconds(3)));
            check("Tray: a file that has gone is reported missing rather than forgotten", gone.MissingCount == 1 && gone.Count == 1);
            check("Tray: the missing one can be pruned on purpose", gone.PruneMissing() == 1 && gone.Count == 0);

            // Storage: what is written is what is read back.
            string storeFolder = Path.Combine(folder, "store");
            var store = new FileTrayStore(storeFolder);
            var saved = new FileTray();
            saved.Add([picture], now);
            saved.Items[0].Pinned = true;
            store.Save(saved);
            var read = store.Load();
            check("Tray: the file it writes is the tray it reads back",
                read.Count == 1 && read.Items[0].Path == picture && read.Items[0].Pinned);
            store.Save(new FileTray());
            check("Tray: an emptied tray stays empty", store.Load().Count == 0);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
