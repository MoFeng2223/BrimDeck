using System.Text.Json;
using BrimDeck.Core;

// The three memory modes (MemoryPlan) and the switch they replaced (SettingsMigrations.MemoryModeFromSwitch).
internal static class MemoryModeTests
{
    public static void Run(Action<string, bool> check)
    {
        check("Memory modes: a new file takes the balanced mode", new DeckSettings().MemoryMode == MemoryMode.Balanced
            && SettingsMigrations.Read("""{"AppVersion":"1.3.0"}""").MemoryMode == MemoryMode.Balanced);
        check("Memory modes: freeing memory when idle becomes the saver mode",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":true}""").MemoryMode == MemoryMode.Saver
            && SettingsMigrations.Read("""{"AppVersion":"1.2.5","TrimMemoryWhenIdle":true}""").MemoryMode == MemoryMode.Saver);
        check("Memory modes: the switch turned off becomes the balanced mode",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":false}""").MemoryMode == MemoryMode.Balanced);
        check("Memory modes: a saved mode wins over the old switch written beside it",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","TrimMemoryWhenIdle":true,"MemoryMode":2}""").MemoryMode == MemoryMode.Smooth);
        check("Memory modes: an unknown mode falls back to balanced",
            SettingsMigrations.Read("""{"AppVersion":"1.3.0","MemoryMode":9}""").MemoryMode == MemoryMode.Balanced);

        var smooth = new DeckSettings { MemoryMode = MemoryMode.Smooth };
        var written = JsonSerializer.Serialize(smooth);
        var again = SettingsMigrations.Read(written);
        check("Memory modes: the mode is saved, and the old switch written to match it for older versions",
            again.MemoryMode == MemoryMode.Smooth && written.Contains("\"TrimMemoryWhenIdle\":false")
            && JsonSerializer.Serialize(new DeckSettings { MemoryMode = MemoryMode.Saver }).Contains("\"TrimMemoryWhenIdle\":true"));

        var (saver, balanced, fluent) = (MemoryPlan.For(MemoryMode.Saver), MemoryPlan.For(MemoryMode.Balanced), MemoryPlan.For(MemoryMode.Smooth));
        check("Memory modes: only the saver mode trims the working set, and the smooth mode keeps the room the heap grew into",
            saver.TrimWorkingSet && !balanced.TrimWorkingSet && !fluent.TrimWorkingSet && saver.ReturnMemory && balanced.ReturnMemory && !fluent.ReturnMemory);
        check("Memory modes: the more a mode keeps, the later it cleans up and the longer readings linger",
            saver.TrimAfter < balanced.TrimAfter && balanced.TrimAfter < fluent.TrimAfter && saver.TrimEvery < balanced.TrimEvery && balanced.TrimEvery < fluent.TrimEvery
            && saver.ReadingsLinger < balanced.ReadingsLinger && balanced.ReadingsLinger < fluent.ReadingsLinger);
        check("Memory modes: the smooth mode still cleans up and stops the readings after a while",
            fluent.TrimAfter < TimeSpan.FromHours(1) && fluent.ReadingsLinger <= TimeSpan.FromMinutes(1));
        check("Memory modes: the saver mode reads the collapsed row every 2 seconds, an open page at the chosen interval",
            saver.ReadingSeconds(1, pageOpen: false) == 2 && saver.ReadingSeconds(2, pageOpen: false) == 2 && saver.ReadingSeconds(1, pageOpen: true) == 1
            && balanced.ReadingSeconds(1, pageOpen: false) == 1 && fluent.ReadingSeconds(1, pageOpen: false) == 1);
        check("Memory modes: the more a mode keeps, the longer the bars' capture runs on and the more covers it keeps",
            saver.MediaLinger == TimeSpan.Zero && saver.MediaLinger < balanced.MediaLinger && balanced.MediaLinger < fluent.MediaLinger
            && saver.RecentCovers < balanced.RecentCovers && balanced.RecentCovers < fluent.RecentCovers);
        check("Memory modes: the saver mode lets the monitor page's extras go when it closes and checks a far pointer less often",
            !saver.LingerExtras && balanced.LingerExtras && fluent.LingerExtras
            && saver.PointerFarMilliseconds > balanced.PointerFarMilliseconds && balanced.PointerFarMilliseconds == fluent.PointerFarMilliseconds);
        check("Memory modes: only the smooth mode keeps caches, warms up at start and holds off full collections while open",
            fluent.KeepCaches && fluent.WarmAtStart && fluent.QuietWhileOpen && !balanced.KeepCaches && !balanced.WarmAtStart && !balanced.QuietWhileOpen
            && !saver.KeepCaches && !saver.WarmAtStart && !saver.QuietWhileOpen);
    }
}
