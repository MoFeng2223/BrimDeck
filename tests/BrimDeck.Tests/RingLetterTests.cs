using BrimDeck.Core;

// The letter in the middle of a collapsed quota ring (CompactMusicLayout.RingLetter): the first letter or digit of the application's
// name, upper case, so rings are told apart by more than their color.
internal static class RingLetterTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        // The setting (DeckSettings.QuotaRingLetters): off at first, saved and read back, and off for a file that does not have it.
        var store = new SettingsStore(Path.Combine(root, "ring-letters"), "0.1.0");
        var fresh = store.Load();
        bool offAtFirst = !fresh.QuotaRingLetters;
        fresh.QuotaRingLetters = true; store.Save(fresh);
        bool savedOn = new SettingsStore(store.DirectoryPath, "0.1.0").Load().QuotaRingLetters;
        File.WriteAllText(store.FilePath, """{"AppVersion":"0.1.0","NotchSummary":true}""");
        bool oldFileOff = !new SettingsStore(store.DirectoryPath, "0.1.0").Load().QuotaRingLetters;
        check("Ring letter setting: off by default, saved and read back, and off for a file without it", offAtFirst && savedOn && oldFileOff);

        // Off, no ring gets a letter whatever the name; on, the letter is the application's display name's (AppEntry.Name), so a
        // renamed application's letter follows its new name and an unnamed one keeps its source's.
        var entry = new AppEntry { QuotaSource = ProviderId.Claude };
        string sourceLetter = CompactMusicLayout.RingLetter(entry.Name);
        entry.DisplayName = "Zeta";
        string renamedLetter = CompactMusicLayout.RingLetterFor(true, entry.Name);
        entry.DisplayName = "工作用量";
        check("Ring letter is empty with the setting off, whatever the name",
            CompactMusicLayout.RingLetterFor(false, entry.Name) == "" && CompactMusicLayout.RingLetterFor(false, "Claude") == "" && CompactMusicLayout.RingLetterFor(false, null) == "");
        check("Ring letter follows the display name when the application is renamed",
            sourceLetter.Length > 0 && renamedLetter == "Z" && CompactMusicLayout.RingLetterFor(true, entry.Name) == "工");

        // Placing the letter: its ink box (the glyph's own bounds), not the line box with the font's ascent and descent, is centred
        // in the ring's cell, on whole device pixels. A capital at 7 DIP is about 4.9 high inside a 9.3 high line box (Segoe UI) whose
        // ink starts about 2.65 down: centring the line box leaves the ink 0.45 DIP below the centre (0.9 device pixel at 200 %).
        var scales = new[] { 1.0, 1.25, 1.5, 1.75, 2.0 };
        var inks = new[] { (X: .6, Y: 2.65, W: 4.8, H: 4.9), (X: .5, Y: 2.65, W: 5.6, H: 4.9), (X: .3, Y: 2.7, W: 1.9, H: 4.9), (X: .2, Y: 2.6, W: 6.4, H: 5.1) };
        double worst = 0, worstLineBox = 0;
        bool onPixels = true;
        foreach (double ppd in scales)
            foreach (double cell in new[] { 14d, 16d, 18d, 21d, 24d })
                foreach (var ink in inks)
                {
                    var (x, y) = CompactMusicLayout.RingLetterOffset(ink.X, ink.Y, ink.W, ink.H, cell, ppd);
                    worst = Math.Max(worst, Math.Max(Math.Abs(x + ink.X + ink.W / 2 - cell / 2), Math.Abs(y + ink.Y + ink.H / 2 - cell / 2)) * ppd);
                    onPixels &= Math.Abs((x + ink.X) * ppd - Math.Round((x + ink.X) * ppd)) < 1e-9 && Math.Abs((y + ink.Y) * ppd - Math.Round((y + ink.Y) * ppd)) < 1e-9;
                    // What a line box does: the box (9.3 high) centred, the ink where the font leaves it.
                    worstLineBox = Math.Max(worstLineBox, Math.Abs((cell - 9.3) / 2 + ink.Y + ink.H / 2 - cell / 2) * ppd);
                }
        check("Ring letter ink centre is the ring's centre to half a device pixel at 100 to 200 %, the ink box on whole device pixels", worst <= .5 + 1e-9 && onPixels);
        check("Ring letter: centring the line box instead leaves the ink more than half a device pixel off (the cause of the uncentred letter)", worstLineBox > .5);
        check("Ring letter offset ignores a non-number and a non-positive scale",
            CompactMusicLayout.RingLetterOffset(double.NaN, 0, 5, 5, 16, 1) == (0, 0) && CompactMusicLayout.RingLetterOffset(.5, 2, 5, 5, 16, 0) == CompactMusicLayout.RingLetterOffset(.5, 2, 5, 5, 16, 1));

        check("Ring letter is the upper-case first letter", CompactMusicLayout.RingLetter("claude") == "C" && CompactMusicLayout.RingLetter("Codex") == "C");
        check("Ring letter skips leading punctuation and spaces", CompactMusicLayout.RingLetter("  [dsh]") == "D" && CompactMusicLayout.RingLetter("-- kimi") == "K");
        check("Ring letter takes a leading digit", CompactMusicLayout.RingLetter("1Password") == "1");
        check("Ring letter keeps a CJK character as it is", CompactMusicLayout.RingLetter("\u6587\u5fc3") == "\u6587");
        // U+20BB7 (a CJK ideograph outside the BMP) is two UTF-16 units; the letter must be the whole character, not the
        // second ideograph after the surrogate pair was skipped, and not half of a pair.
        string astralName = "\U00020BB7野", astralLetter = CompactMusicLayout.RingLetter(astralName);
        check("Ring letter keeps a supplementary-plane character whole", astralLetter == "\U00020BB7" && astralLetter.Length == 2 && char.IsSurrogatePair(astralLetter, 0));
        check("Ring letter upper-cases a supplementary-plane letter", CompactMusicLayout.RingLetter("\U00010428x") == "\U00010400");
        check("Ring letter skips punctuation before a supplementary-plane character", CompactMusicLayout.RingLetter("[ \U00020BB7]") == "\U00020BB7");
        check("Ring letter ignores a lone surrogate", CompactMusicLayout.RingLetter("\ud842x") == "X");
        check("Ring letter is empty without a letter or digit", CompactMusicLayout.RingLetter("") == "" && CompactMusicLayout.RingLetter("  -_ ") == "" && CompactMusicLayout.RingLetter(null) == "");
    }
}
