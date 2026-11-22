using BrimDeck.Core;

internal static class SettingsMigrationTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var v010 = new Version(0, 1, 0);
        check("application versions compare by major, minor and patch", ApplicationVersion.Parse("0.2.0+abc") == new Version(0, 2, 0)
            && ApplicationVersion.Parse("0.2.0-beta") == new Version(0, 2, 0) && ApplicationVersion.Parse("") == new Version(0, 0, 0)
            && ApplicationVersion.Parse("0.10.0") > ApplicationVersion.Parse("0.9.0"));

        var legacyStore = new SettingsStore(Path.Combine(root, "version-legacy"), "0.1.0");
        Directory.CreateDirectory(legacyStore.DirectoryPath);
        File.WriteAllText(legacyStore.FilePath, """{"Width":900,"CompactSummary":false,"Cursor":false,"Apps":null}""");
        var legacy = legacyStore.Load();
        check("a file without a version runs the steps up to the running version", !legacy.NotchSummary && !legacy.CapsuleSummary && legacy.Width == 900
            && legacy.Apps.Count == 4 && !legacy.Entry(ProviderId.Cursor)!.Enabled && legacy.Entry(ProviderId.Claude)!.Enabled);
        legacyStore.Save(legacy);
        var saved = File.ReadAllText(legacyStore.FilePath);
        check("saving records the running application version and no old members",
            saved.Contains("\"AppVersion\": \"0.1.0\"") && !saved.Contains("CompactSummary") && !saved.Contains("\"Cursor\""));

        var current = SettingsMigrations.Read("""{"AppVersion":"0.1.0","CompactSummary":false}""", v010, out var savedBy);
        check("a file from the same version skips its own and earlier steps", savedBy == v010 && current.NotchSummary && current.CapsuleSummary);
        var early = SettingsMigrations.Read("""{"CompactSummary":false}""", new Version(0, 0, 9), out _);
        check("a step does not run before the application reaches its version", early.NotchSummary && early.CapsuleSummary);

        var tolerant = SettingsMigrations.Read("""{"AppVersion":"0.1.0","Height":300,"NotYetKnown":{"a":1}}""", v010, out _);
        check("reading is tolerant of missing and unknown members", tolerant.Height == 300 && tolerant.Width == new DeckSettings().Width && tolerant.MusicPage);
        var clock = SettingsMigrations.Read("""{"AppVersion":"0.1.0","NotchClock":true,"ClockStyle":42,"ClockColor":-1,"Clock24Hour":false}""", v010, out _);
        bool capsuleOff = !clock.ShowsClock(CompactStyle.Capsule);
        clock.CapsuleClock = true;
        check("the clock is switched per style and never shown on the indicator", clock.ShowsClock(CompactStyle.Notch) && capsuleOff
            && clock.ShowsClock(CompactStyle.Capsule) && !clock.ShowsClock(CompactStyle.Line) && !clock.Clock24Hour);
        check("unknown clock styles and colors fall back to the defaults", clock.ClockStyle == ClockStyle.Minimal && clock.ClockColor == ClockColor.White
            && clock.ClockCustomColor == ClockColors.DefaultCustom && clock.ClockGradient.SequenceEqual(ClockColors.Sunset));
        var colors = SettingsMigrations.Read("""{"AppVersion":"0.1.0","ClockColor":7,"ClockCustomColor":"12ab3c","ClockGradient":["#000000","#ffffff"]}""", v010, out _);
        check("a custom clock color is normalized and a gradient needs three valid colors", colors.ClockColor == ClockColor.Custom
            && colors.ClockCustomColor == "#12AB3C" && colors.ClockGradient.SequenceEqual(ClockColors.Sunset));
        var edited = colors.Copy(); edited.ClockGradient[0] = "#000000";
        check("a settings copy has its own gradient", colors.ClockGradient[0] == ClockColors.Sunset[0]);

        // The two-line clock style (1) became the minimal style with the date below, in the file, its profiles and its theme choices.
        var twoLine = SettingsMigrations.Read("""
            {"AppVersion":"1.3.3","ClockStyle":1,"ThemeChoices":{"dark":{"ClockStyle":1},"light":{"ClockStyle":5}},
             "Profiles":[{"Name":"work","Values":{"ClockStyle":1}},{"Name":"home","Values":{"ClockStyle":3}}]}
            """, new Version(1, 3, 3), out _);
        var workValues = twoLine.Profiles.Single(profile => profile.Name == "work").Values!;
        var homeValues = twoLine.Profiles.Single(profile => profile.Name == "home").Values!;
        check("the two-line clock becomes the minimal clock with the date below it", twoLine.ClockStyle == ClockStyle.Minimal && twoLine.ClockDate
            && twoLine.ClockDatePosition == ClockDatePosition.Below && twoLine.ClockDateFormat == ClockDateFormat.MonthDaySlash && twoLine.ClockWeekday == ClockWeekday.After
            && twoLine.ThemeChoices["dark"].ClockStyle == ClockStyle.Minimal && twoLine.ThemeChoices["light"].ClockStyle == ClockStyle.Neon
            && (int)workValues["ClockStyle"]! == 0 && (bool)workValues["ClockDate"]! && (int)homeValues["ClockStyle"]! == 3 && homeValues["ClockDate"] is null);
        var noDate = SettingsMigrations.Read("""{"AppVersion":"1.3.3","ClockStyle":2}""", new Version(1, 3, 3), out _);
        check("the other clock styles keep their numbers and the date stays off", noDate.ClockStyle == ClockStyle.Digits && !noDate.ClockDate
            && (int)ClockStyle.Digits == 2 && (int)ClockStyle.Rounded == 10);
        var badDate = SettingsMigrations.Read("""{"AppVersion":"1.3.3","ClockDatePosition":9,"ClockDateFormat":-1,"ClockWeekday":7}""", new Version(1, 3, 3), out _);
        check("unknown date places and formats fall back to the defaults", badDate.ClockDatePosition == ClockDatePosition.Right
            && badDate.ClockDateFormat == ClockDateFormat.MonthDaySlash && badDate.ClockWeekday == ClockWeekday.After);

        var sunday = new DateTime(2026, 3, 1);
        check("the date is written in each format, the weekday before, after or not at all",
            ClockDates.Text(sunday, ClockDateFormat.MonthDaySlash, ClockWeekday.After, false) == "3/1 周日"
            && ClockDates.Text(sunday, ClockDateFormat.MonthDayDash, ClockWeekday.Before, false) == "周日 03-01"
            && ClockDates.Text(sunday, ClockDateFormat.MonthDayWords, ClockWeekday.Hidden, false) == "3月1日"
            && ClockDates.Text(sunday, ClockDateFormat.YearSlash, ClockWeekday.Hidden, false) == "2026/3/1"
            && ClockDates.Text(sunday, ClockDateFormat.YearDash, ClockWeekday.Hidden, false) == "2026-03-01"
            && ClockDates.Text(sunday, ClockDateFormat.YearWords, ClockWeekday.After, false) == "2026年3月1日 周日"
            && ClockDates.Text(sunday, ClockDateFormat.MonthDayWords, ClockWeekday.Before, true) == "Sun Mar 1"
            && ClockDates.Text(sunday, ClockDateFormat.YearSlash, ClockWeekday.Hidden, true) == "3/1/2026"
            && ClockDates.Text(sunday, ClockDateFormat.YearWords, ClockWeekday.After, true) == "Mar 1, 2026 Sun");
        check("a theme file's two-line clock reads as the minimal clock, and numbers are not style names",
            ClockStyles.TryParse("stacked", out var stacked) && stacked == ClockStyle.Minimal && ClockStyles.TryParse("Pixel", out var pixel) && pixel == ClockStyle.Pixel
            && !ClockStyles.TryParse("1", out _) && !ClockStyles.TryParse("11", out _) && !ClockStyles.TryParse("", out _) && !ClockStyles.TryParse("sparkle", out _));

        // Upgrade 0.1.0 -> 0.2.0, change a setting, return to 0.1.0, then go back to 0.2.0.
        var path = Path.Combine(root, "version-switch");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "settings.json"), """{"AppVersion":"0.1.0","Width":900}""");
        var upgraded = new SettingsStore(path, "0.2.0"); var upgradedSettings = upgraded.Load();
        upgradedSettings.Width = 1000; upgraded.Save(upgradedSettings);
        var file = SettingsMigrations.Parse(File.ReadAllText(Path.Combine(path, "settings.json")));
        check("upgrading keeps the older version's settings as its snapshot", (string?)file["AppVersion"] == "0.2.0" && (double?)file["Width"] == 1000
            && (double?)file["Snapshots"]?["0.1.0"]?["Width"] == 900 && upgraded.LoadWarning is null);
        var downgraded = new SettingsStore(path, "0.1.0"); var downgradedSettings = downgraded.Load();
        check("a downgraded version reads its own snapshot", downgradedSettings.Width == 900 && downgraded.LoadWarning!.Contains("0.2.0"));
        downgradedSettings.Width = 700; downgraded.Save(downgradedSettings);
        file = SettingsMigrations.Parse(File.ReadAllText(Path.Combine(path, "settings.json")));
        check("a downgraded version saves only its own snapshot", (string?)file["AppVersion"] == "0.2.0" && (double?)file["Width"] == 1000
            && (double?)file["Snapshots"]?["0.1.0"]?["Width"] == 700);
        check("the newer version finds its own settings again", new SettingsStore(path, "0.2.0").Load().Width == 1000
            && new SettingsStore(path, "0.1.0").Load().Width == 700);

        // A file created by a newer version has no snapshot for this one: read what is understood, keep the rest.
        var newerStore = new SettingsStore(Path.Combine(root, "version-newer"), "0.1.0");
        Directory.CreateDirectory(newerStore.DirectoryPath);
        File.WriteAllText(newerStore.FilePath, """{"AppVersion":"0.3.0","Width":880,"FutureOption":"kept"}""");
        var newer = newerStore.Load();
        check("without its snapshot a downgraded version reads the members it understands", newer.Width == 880 && newerStore.LoadWarning!.Contains("0.3.0"));
        newer.Width = 1000; newerStore.Save(newer);
        file = SettingsMigrations.Parse(File.ReadAllText(newerStore.FilePath));
        check("the newer version's settings stay untouched", (double?)file["Width"] == 880 && (string?)file["FutureOption"] == "kept"
            && (double?)file["Snapshots"]?["0.1.0"]?["Width"] == 1000 && new SettingsStore(newerStore.DirectoryPath, "0.1.0").Load().Width == 1000);

        var many = Path.Combine(root, "version-many");
        foreach (var minor in Enumerable.Range(1, 8))
        {
            var store = new SettingsStore(many, $"0.{minor}.0"); store.Save(store.Load());
        }
        file = SettingsMigrations.Parse(File.ReadAllText(Path.Combine(many, "settings.json")));
        check("at most five earlier versions are kept", file["Snapshots"] is System.Text.Json.Nodes.JsonObject kept && kept.Count == 5
            && kept.ContainsKey("0.7.0") && kept.ContainsKey("0.3.0") && !kept.ContainsKey("0.2.0"));
    }
}
