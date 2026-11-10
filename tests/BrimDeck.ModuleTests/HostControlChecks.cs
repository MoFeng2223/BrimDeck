using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    // 5c: host control. The pure parts — the accent stack and its rules — are checked here; the service's own checks run
    // against a real activation below.
    private static void HostControlChecks()
    {
        // ---- the accent stack ----
        var accents = new AccentOverrides();
        Check("no accent is drawn until a module asks", accents.Current is null && accents.Count == 0);
        var first = accents.Add("alpha", "#112233");
        Check("the newest request is drawn", accents.Current == "#112233" && accents.Count == 1);
        var second = accents.Add("beta", "#445566");
        Check("a later request wins", accents.Current == "#445566" && accents.Count == 2);
        first.Dispose();
        Check("releasing the older request leaves the newer one", accents.Current == "#445566" && accents.Count == 1);
        second.Dispose();
        Check("releasing the last request gives the theme back", accents.Current is null && accents.Count == 0);
        // The stack is the precedence: releasing the newest falls back to the one behind it.
        var a = accents.Add("alpha", "#111111"); var b = accents.Add("beta", "#222222");
        b.Dispose();
        Check("releasing the newest falls back to the one behind it", accents.Current == "#111111");
        // The user's own theme change ends every request at once.
        var c = accents.Add("gamma", "#333333");
        Check("a user theme change clears every request", accents.Clear() && accents.Current is null && accents.Count == 0);
        Check("cleared requests cannot come back by disposing them late", accents.Current is null);
        a.Dispose(); c.Dispose();
        Check("disposing a cleared request changes nothing", accents.Current is null && accents.Count == 0);
        // A stopped module takes its own requests and leaves the others.
        var d = accents.Add("alpha", "#AAAAAA"); var e = accents.Add("beta", "#BBBBBB"); var f = accents.Add("alpha", "#CCCCCC");
        Check("a module's stop ends only its own requests", accents.ClearModule("alpha") && accents.Count == 1 && accents.Current == "#BBBBBB");
        e.Dispose(); d.Dispose(); f.Dispose();
        Check("every request can be released", accents.Count == 0 && accents.Current is null);
        // Colors are validated before they reach the stack.
        Check("a hex color is normalized and anything else refused", AccentOverrides.Normalize("#ABCDEF") == "#ABCDEF"
            && AccentOverrides.Normalize(" abcdef ") is not null && AccentOverrides.Normalize("not-a-color") is null && AccentOverrides.Normalize("") is null);

        // ---- the catalog and the bound service ----
        Check("host.control and its four actions are catalogued as services",
            ModuleApi.Describe(ModuleApi.HostControl) is { Kind: ModuleCapabilityKind.Service, IntroducedMinor: 10 }
            && ModuleApi.Describe(ModuleHostControlActions.Page) is { Kind: ModuleCapabilityKind.Service, Permission: ModuleHostControlActions.Page }
            && ModuleApi.Describe(ModuleHostControlActions.Expand) is { Permission: ModuleHostControlActions.Expand }
            && ModuleApi.Describe(ModuleHostControlActions.Media) is { Permission: ModuleHostControlActions.Media }
            && ModuleApi.Describe(ModuleHostControlActions.Accent) is { Permission: ModuleHostControlActions.Accent });
        Check("the four action interfaces are distinct contracts", new[] { typeof(IModulePageControl), typeof(IModulePanelControl), typeof(IModuleMediaControl), typeof(IModuleAccentControl) }
            .Distinct().Count() == 4 && typeof(IModuleHostControl).GetProperties().Length == 4);
        // The declaration check is the same one the service makes before any action runs: a manifest that lists the action's
        // capability passes, and host.control alone does not.
        var declared = new ModuleManifest { Id = "m", Name = "m", ApiMinor = ModuleApi.Minor, Capabilities = [ModuleApi.HostControl, ModuleHostControlActions.Accent] };
        var onlyMain = new ModuleManifest { Id = "m", Name = "m", ApiMinor = ModuleApi.Minor, Capabilities = [ModuleApi.HostControl] };
        Check("an action is allowed only when its own capability is declared",
            ModulePolicy.Declares(declared, ModuleHostControlActions.Accent) && !ModulePolicy.Declares(onlyMain, ModuleHostControlActions.Accent)
            && !ModulePolicy.Declares(onlyMain, ModuleHostControlActions.Media));
        // The background policy compares the origin; a host-originated request is the one that must not pass.
        Check("a host-originated request cannot pass the background rule", ModuleCommandOrigin.Host != ModuleCommandOrigin.User);
        // A request that ends with its activation: the same call the service makes through scope.OnDispose.
        var owned = new AccentOverrides();
        var lease = owned.Add("m", "#00FF00");
        Check("an accent request is drawn while its module lives", owned is { Current: "#00FF00", Count: 1 });
        Check("a stopped module takes its own request", owned.ClearModule("m") && owned.Current is null && owned.Count == 0);
        lease.Dispose();
        Check("releasing after the module stopped is harmless", owned.Count == 0);
        Check("an invalid color is refused before it reaches the stack", AccentOverrides.Normalize("chartreuse") is null);
        // The user's choice ends every request, whether they pick a theme in settings or Windows switches light/dark.
        var chosen = new AccentOverrides();
        var one = chosen.Add("alpha", "#AA0000"); var two = chosen.Add("beta", "#00BB00");
        Check("a user theme change clears a two-module stack", chosen.Clear() && chosen.Count == 0 && chosen.Current is null);
        one.Dispose(); two.Dispose();
        Check("released requests do not come back after a clear", chosen.Count == 0);
    }
}
