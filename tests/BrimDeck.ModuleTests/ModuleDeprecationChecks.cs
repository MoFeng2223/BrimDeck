using BrimDeck.Modules;

internal static partial class Program
{
    private static void ModuleDeprecationChecks()
    {
        // Renames keep working and explain themselves; anything else stays fatal.
        var table = new[]
        {
            new ModuleDeprecation(ModuleDeprecationKind.Capability, "ui.legacy", "ui.pages", "removed in BrimDeck 2.0"),
            new ModuleDeprecation(ModuleDeprecationKind.Permission, "runtime.playback", "playback.read", "removed in BrimDeck 2.0"),
        };
        var manifest = new ModuleManifest
        {
            Id = "legacy", Name = "Legacy",
            Capabilities = ["ui.legacy", "ui.activities"],
            Permissions = ["runtime.playback"],
            Requires = new ModuleRequirements { ContractMinor = 1, Capabilities = ["ui.legacy"] },
        };
        var (resolved, diagnostics) = ModuleDeprecations.Resolve(manifest, table);
        Check("deprecation: renamed capability keeps working under its new name",
            resolved.Capabilities.SequenceEqual(["ui.pages", "ui.activities"]) && resolved.Requires!.Capabilities.SequenceEqual(["ui.pages"]));
        Check("deprecation: renamed permission keeps working under its new name", resolved.Permissions.SequenceEqual(["playback.read"]));
        Check("deprecation: the source manifest is not modified", manifest.Capabilities.SequenceEqual(["ui.legacy", "ui.activities"]) && manifest.Permissions.SequenceEqual(["runtime.playback"]));
        Check("deprecation: each rename is explained once, in the documented wording",
            diagnostics.Count == 3 && diagnostics.All(d => d.Severity == ModuleDiagnosticSeverity.Information)
            && diagnostics.Any(d => d.Message == "capability \"ui.legacy\": removed in BrimDeck 2.0, use \"ui.pages\".")
            && diagnostics.Any(d => d.Message == "permission \"runtime.playback\": removed in BrimDeck 2.0, use \"playback.read\"."));
        var unknown = ModuleDeprecations.Resolve(new ModuleManifest { Id = "x", Name = "x", Capabilities = ["ui.nothing"] }, table);
        Check("deprecation: an unlisted name is left for the validator to reject", unknown.Manifest.Capabilities.SequenceEqual(["ui.nothing"]) && unknown.Diagnostics.Count == 0);
        Check("deprecation: the shipped table is empty today", ModuleDeprecations.Table.Count == 0);

        // Manifest warnings reach the module list, survive a re-reconcile and leave with the generation.
        var warning = new ModuleDiagnostic("renamed-capability", "manifest:ui.legacy", "capability \"ui.legacy\": removed in BrimDeck 2.0, use \"ui.pages\".", ModuleDiagnosticSeverity.Information);
        var noisy = Def("noisy", _ => ValueTask.CompletedTask) with { Warnings = [warning] };
        using var host = Host();
        host.ReconcileAsync([noisy]).GetAwaiter().GetResult();
        Check("warnings surface as module diagnostics", host.Modules.Single(m => m.Id == "noisy").Diagnostics.Any(d => d.Code == "renamed-capability"));
        host.ReconcileAsync([noisy]).GetAwaiter().GetResult();
        Check("re-reconciling does not duplicate manifest warnings", host.Modules.Single(m => m.Id == "noisy").Diagnostics.Count(d => d.Code == "renamed-capability") == 1);
        host.ReconcileAsync([noisy with { Enabled = false }]).GetAwaiter().GetResult();
        Check("disabling drops the generation's manifest warnings", host.Modules.Single(m => m.Id == "noisy").Diagnostics.All(d => d.Code != "renamed-capability"));

        // One noisy module may not push another module's diagnostics out of the shared list.
        using var crowded = Host();
        crowded.ReconcileAsync([Def("noisy", _ => ValueTask.CompletedTask), Def("quiet", _ => ValueTask.CompletedTask)]).GetAwaiter().GetResult();
        for (int i = 0; i < 200; i++) crowded.Diagnose("noisy", "bulk", "code-" + i, "noise");
        crowded.Diagnose("quiet", "own", "kept", "quiet");
        Check("a flooding module cannot evict another module's diagnostics",
            crowded.Modules.Single(m => m.Id == "quiet").Diagnostics.Any(d => d.Code == "kept")
            && crowded.Modules.Single(m => m.Id == "noisy").Diagnostics.Count == 199);
    }
}
