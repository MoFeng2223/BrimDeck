using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private static async Task PlatformChecks()
    {
        using var host = Host();
        IModulePlatform? platform = null;
        IModuleRegistration? first = null;
        var order = new List<string>();
        IDisposable? removed = null;
        var definition = Def("platform", context =>
        {
            platform = context.Platform();
            first = platform.Registries.Register("page", Page("registered"));
            platform.Events.On<ModulePageOpening>(_ => order.Add("normal-first"));
            platform.Events.On<ModulePageOpening>(e => { order.Add("highest"); removed?.Dispose(); e.Cancel(); }, ModuleEventPriority.Highest);
            platform.Events.On<ModulePageOpening>(_ => throw new InvalidOperationException("hook fault"), ModuleEventPriority.High);
            platform.Events.On<ModulePageOpening>(_ => order.Add("normal-second"));
            removed = platform.Events.On<ModulePageOpening>(_ => order.Add("removed"), ModuleEventPriority.Low);
            return ValueTask.CompletedTask;
        });
        await host.ReconcileAsync([definition]);
        Check("platform registry exposes namespaced registration handle", first!.Id == "platform:page" && host.Registry.List<ModulePage>().Count == 1);
        var opening = new ModulePageOpening("platform:page"); host.Publish(opening);
        Check("event priorities are stable and failures do not stop later hooks", order.SequenceEqual(["highest", "normal-first", "normal-second"]) && host.Faults.Count == 1);
        Check("opening hook supports one-way cancellation", opening.Cancelled);
        Check("unsubscribe during dispatch skips already-snapshotted callback", !order.Contains("removed"));
        Throws("module cannot spoof host opening event", () => platform!.Events.Publish(new ModulePageOpening("platform:page")));
        Throws("module cannot spoof boxed host opening event", () => platform!.Events.Publish<object>(new ModuleActivityOpening("platform:session")));
        Throws("event priority rejects invalid enum", () => platform!.Events.On<int>(_ => { }, (ModuleEventPriority)999));
        first.Dispose();
        var second = platform!.Registries.Register("page", Page("replacement")); first.Dispose();
        Check("retired registration handle cannot remove replacement", host.Registry.List<ModulePage>().Single().Value.Title == "replacement");
        second.Dispose(); Check("registry handle removal is immediate", host.Registry.List<ModulePage>().Count == 0);
        int custom = 0;
        platform.Events.On<string>(_ => custom++); platform.Events.Publish("custom");
        Check("custom events work through platform bus", custom == 1);
        await host.ReconcileAsync([]); host.Publish("custom");
        Check("module stop removes platform event subscriptions", custom == 1);
        Throws("retained platform registry is revoked", () => platform.Registries.Register("late", Page("late")));
        Throws("retained platform event publisher is revoked", () => platform.Events.Publish("late"));
        using var scoped = Host(); IModulePresentation? presentation = null;
        await scoped.ReconcileAsync([Def("scoped", c =>
        {
            var p = c.Platform(); presentation = p.Services.Get<IModulePresentation>();
            Check("platform services share activation-scoped adapter cache", ReferenceEquals(presentation, p.Services.Get<IModulePresentation>()));
            return ValueTask.CompletedTask;
        }) with { Manifest = new() { Id = "scoped", Name = "scoped", Capabilities = [ModuleApi.UiPresentation] } }]);
        presentation!.Invalidate(); await scoped.ReconcileAsync([]);
        Throws("platform service is revoked on stop", presentation.Invalidate);
        await host.ReconcileAsync([Def("owner", c =>
        {
            c.Platform().Registries.Register("foreign", new ModuleWidget("Foreign", "Foreign", "another:settings", () => true, _ => { }));
            return ValueTask.CompletedTask;
        })]);
        Check("widgets cannot route settings into another module", host.Modules.Single().Status == ModuleStatus.Failed && host.Registry.List<ModuleWidget>().Count == 0);
        BundleChecks();
    }
    private static void BundleChecks()
    {
        string source = Path.Combine(_root, "distribution"), target = Path.Combine(_root, "installed");
        string module = Path.Combine(source, "package"); Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "module.json"), JsonSerializer.Serialize(new ModuleManifest
            { Id = "package", Name = "Package", Assembly = "Package.dll", EntryType = "Package.Module" }));
        File.WriteAllText(Path.Combine(module, "Package.dll"), "v1");
        var receipts = new Dictionary<string, string>();
        var initial = ModuleBundleInstaller.Install(source, target, receipts);
        Check("bundled modules install through a generic directory contract", initial.Count == 1 && initial[0].PreviousDigest is null && receipts.Count == 1);
        Check("bundle bootstrap is idempotent", ModuleBundleInstaller.Install(source, target, receipts).Count == 0);
        string previous = initial[0].Digest;
        File.WriteAllText(Path.Combine(module, "Package.dll"), "v2");
        var upgraded = ModuleBundleInstaller.Install(source, target, receipts);
        Check("unchanged bundled installation upgrades with matching receipt", upgraded.Count == 1 && upgraded[0].PreviousDigest == previous && File.ReadAllText(Path.Combine(target, "package", "Package.dll")) == "v2");
        File.WriteAllText(Path.Combine(target, "package", "Package.dll"), "custom");
        File.WriteAllText(Path.Combine(module, "Package.dll"), "v3");
        Check("bundle upgrades preserve user-modified module files", ModuleBundleInstaller.Install(source, target, receipts).Count == 0 && File.ReadAllText(Path.Combine(target, "package", "Package.dll")) == "custom");
        Check("a distribution cannot adopt an unrelated installed module", ModuleBundleInstaller.Install(source, target, new Dictionary<string, string>()).Count == 0);
        Throws("bundle install refuses identical source and destination", () => ModuleBundleInstaller.Install(source, source, receipts));
        Check("bundle transaction leaves no staging directories", !Directory.EnumerateDirectories(target).Any(p => Path.GetFileName(p).StartsWith('.')));
        var settings = new BrimDeck.Core.ModuleSettings { BundledPackages = new(receipts) }; var copy = settings.Copy(); copy.BundledPackages.Clear();
        Check("bundle receipts are deeply copied in application settings", settings.BundledPackages.Count == 1);
    }
}
