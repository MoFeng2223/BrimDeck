using System.Text.Json;
using BrimDeck.Core;

internal static class DeckPageDirectoryTests
{
    internal static void Run(Action<string, bool> check)
    {
        var settings = new DeckSettings { LastPage = DeckPage.MiniApps, DefaultPage = DefaultDeckPage.MiniApps, PageOrder = [DeckPage.Monitor, DeckPage.MiniApps, DeckPage.Usage, DeckPage.Music] };
        settings.MiniApps.Width = 620; settings.MiniApps.Height = 220; settings.Normalize();
        DeckPageDefinition[] plugins = [new("sample:one", "一", "One") { Width = 620, Height = 220 }, new("sample:two", "二", "Two") { Width = 620, Height = 220 }];
        var directory = new DeckPageDirectory(settings, plugins);
        check("Pages: numeric enum migration keeps an unresolved legacy mini-app target", settings.Pages.Version == 1 && settings.Pages.DefaultId == DeckPageIds.LegacyMiniApps && settings.Pages.LastId == DeckPageIds.LegacyMiniApps);
        check("Pages: unresolved legacy target falls back without inventing a plugin default", directory.Resolve(directory.Opening(null)).Id == DeckPageIds.Monitor && settings.Pages.DefaultId == DeckPageIds.LegacyMiniApps);
        check("Pages: built-ins and plugin contributions share a bounded directory", directory.All.Select(p => p.Id).SequenceEqual([DeckPageIds.Monitor, DeckPageIds.Usage, DeckPageIds.Music, "sample:one", "sample:two"]));
        check("Pages: legacy shared dimensions seed both pages", directory.Size(plugins[0]) == (620, 220) && directory.Size(plugins[1]) == (620, 220));
        directory.SetSize("sample:one", 950, 310);
        check("Pages: resizing one plugin page leaves other pages and legacy dimensions alone", directory.Size(plugins[0]) == (950, 310) && directory.Size(plugins[1]) == (620, 220) && settings.MiniApps.Width == 620);
        settings.Pages.Order = ["sample:two", DeckPageIds.Music, "sample:one", DeckPageIds.Usage, DeckPageIds.Monitor];
        directory = new(settings, plugins);
        check("Pages: custom order is shared by mixed built-in and plugin navigation", directory.Visible.Select(p => p.Id).SequenceEqual(settings.Pages.Order));
        settings.Pages.DefaultId = "sample:two"; settings.Pages.LastId = "sample:two";
        var absent = new DeckPageDirectory(settings, plugins.Take(1));
        check("Pages: missing default falls back only at runtime", absent.Resolve(absent.Opening(null)).Id == DeckPageIds.Music && settings.Pages.DefaultId == "sample:two" && settings.Pages.LastId == "sample:two");
        directory = new(settings, plugins);
        check("Pages: restored package restores its default identity and order", directory.Resolve(directory.Opening(null)).Id == "sample:two" && directory.Visible[0].Id == "sample:two");
        directory.SetVisible("sample:two", false);
        check("Pages: hiding a page keeps its saved default and business activation intent", !directory.Visible.Any(p => p.Id == "sample:two") && settings.Pages.DefaultId == "sample:two" && settings.Modules.Enabled);
        directory.SetVisible("sample:two", true);
        settings.Modules.Enabled = false;
        check("Pages: total plugin switch removes pages without removing preferences", !directory.Visible.Any(p => p.BuiltIn is null) && settings.Pages.Entries.ContainsKey("sample:one"));
        settings.Modules.Enabled = true;
        var hostile = new DeckPageDefinition("huge:page", "Huge", "Huge") { Width = double.PositiveInfinity, Height = double.MaxValue, MinWidth = double.NaN, MaxWidth = double.MaxValue, MinHeight = double.NegativeInfinity, MaxHeight = double.MaxValue };
        var limits = directory.Limits(hostile);
        check("Pages: untrusted page limits cannot exceed the host render budget", limits == (440, 1200, 140, 400));
        var safe = new DeckPageDirectory(settings, [hostile]);
        safe.SetSize(hostile.Id, 99999, 99999);
        check("Pages: desired size stays within bounded page limits", safe.Size(hostile) == (1200, 400));
        var before = safe.Size(hostile); _ = DeckPages.MaxWidthOn(800, 1.5);
        check("Pages: temporary display fitting does not write desired dimensions", safe.Size(hostile) == before);
        var copy = settings.Copy(); copy.Pages.Order.Clear(); copy.Pages.Entries["sample:one"].Width = 500;
        check("Pages: copied preferences do not share mutable nested state", settings.Pages.Order.Count == 5 && settings.Pages.Entries["sample:one"].Width == 950);
        string json = JsonSerializer.Serialize(settings); var restored = JsonSerializer.Deserialize<DeckSettings>(json)!; restored.Normalize();
        check("Pages: identities, sizes and unavailable target round-trip", restored.Pages.DefaultId == "sample:two" && restored.Pages.Entries["sample:one"].Width == 950);
        settings.Pages.Entries["bad whitespace"] = new() { Width = double.NaN }; settings.Pages.Entries["sample:one"].Width = double.PositiveInfinity; settings.Pages.Order.Add("sample:two"); settings.Normalize();
        check("Pages: malformed ids, nonfinite saved sizes and duplicate order normalize", !settings.Pages.Entries.ContainsKey("bad whitespace") && settings.Pages.Entries["sample:one"].Width is null && settings.Pages.Order.Count == 5);
        var reserved = new DeckPageDirectory(settings, [new(DeckPageIds.Usage, "Spoof", "Spoof")]);
        check("Pages: plugins cannot replace the reserved builtin namespace", reserved.All.Single(p => p.Id == DeckPageIds.Usage).TitleEn == "AI usage");
    }
}
