using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private class LegacyNavigationSettings : IModuleSettings
    {
        public string Name => "Settings";
        public string NameEn => "Settings";
        public bool Visible => true;
        public IReadOnlyList<ModuleSetting> Fields => [];
        public object Read(string key) => 0;
        public void Write(string key, object value) { }
        public void Reset() { }
    }
    private sealed class NavigationSettings(ModuleSettingsPlacement placement, string? icon = null, int order = 500)
        : LegacyNavigationSettings, IModuleSettingsNavigation
    {
        public ModuleSettingsPlacement Placement => placement;
        public string? IconPath => icon;
        public int Order => order;
    }
    private static void SettingsNavigationChecks()
    {
        using var host = Host(); bool firstEnabled = true, secondEnabled = true;
        host.ReconcileAsync([Def("navigation", c =>
        {
            c.Register<IModuleSettings>("shared", new LegacyNavigationSettings());
            c.Register("first", new ModuleWidget("First", "First", "navigation:shared", () => firstEnabled, v => firstEnabled = v) { ReadOrder = () => 2 });
            c.Register("second", new ModuleWidget("Second", "Second", "navigation:shared", () => secondEnabled, v => secondEnabled = v) { ReadOrder = () => 1 });
            c.Register<IModuleSettings>("global", new NavigationSettings(ModuleSettingsPlacement.Modules, "M 1,1 L 15,15", 10));
            c.Register("global", new ModuleWidget("Global", "Global", "navigation:global", () => false, _ => { }));
            c.Register<IModuleSettings>("page", new NavigationSettings(ModuleSettingsPlacement.MiniApps, order: 3));
            c.Register<IModuleSettings>("standalone", new LegacyNavigationSettings());
            c.Register<IModuleSettings>("malformed", new NavigationSettings((ModuleSettingsPlacement)999, "not geometry"));
            return ValueTask.CompletedTask;
        })]).GetAwaiter().GetResult();
        var index = ModuleSettingsIndex.Create(host, true);
        var shared = index.Single(e => e.Contribution.Id == "navigation:shared");
        Check("shared settings linked by two widgets produce one navigation entry", index.Count(e => e.Contribution.Id == shared.Contribution.Id) == 1);
        Check("automatic placement retains legacy widget grouping and dynamic order", shared is { Placement: ModuleSettingsPlacement.MiniApps, Visible: true, Order: 1 });
        firstEnabled = false;
        Check("shared settings remain visible while one linked widget is enabled", ModuleSettingsIndex.Create(host, true).Single(e => e.Contribution.Id == shared.Contribution.Id).Visible);
        secondEnabled = false;
        Check("shared settings hide when all linked widgets are disabled", !ModuleSettingsIndex.Create(host, true).Single(e => e.Contribution.Id == shared.Contribution.Id).Visible);
        Check("explicit module settings are independent of linked widget toggles", index.Single(e => e.Contribution.Id == "navigation:global") is { Placement: ModuleSettingsPlacement.Modules, Visible: true, Order: 10, IconPath: "M 1,1 L 15,15" });
        Check("standalone settings can explicitly join mini-app navigation", index.Single(e => e.Contribution.Id == "navigation:page") is { Placement: ModuleSettingsPlacement.MiniApps, Visible: true });
        var hiddenPage = ModuleSettingsIndex.Create(host, false);
        Check("page visibility hides mini-app settings but retains module settings", hiddenPage.Where(e => e.Placement == ModuleSettingsPlacement.MiniApps).All(e => !e.Visible)
            && hiddenPage.Single(e => e.Contribution.Id == "navigation:global").Visible);
        Check("legacy unlinked settings remain under modules", index.Single(e => e.Contribution.Id == "navigation:standalone").Placement == ModuleSettingsPlacement.Modules);
        Check("invalid optional navigation metadata uses safe fallbacks", index.Single(e => e.Contribution.Id == "navigation:malformed") is { Placement: ModuleSettingsPlacement.Modules, IconPath: null });
        for (int i = 0; i < 10; i++) _ = ModuleSettingsIndex.Create(host, true);
        Check("navigation diagnostics are deduplicated without faulting the module", host.Modules.Single() is { Status: ModuleStatus.Ready, Diagnostics.Count: 2 } && host.Faults.Count == 0);
        Throws("module diagnostics snapshot cannot be mutated", () => ((IList<ModuleDiagnostic>)host.Modules.Single().Diagnostics).Clear());
        host.ReconcileAsync([]).GetAwaiter().GetResult();
        Check("stopping a module removes its projected navigation", ModuleSettingsIndex.Create(host, true).Count == 0);
        host.ReconcileAsync([Def("navigation", Done)]).GetAwaiter().GetResult();
        Check("previous activation diagnostics do not follow a new generation", host.Modules.Single().Diagnostics.Count == 0);
    }
}
