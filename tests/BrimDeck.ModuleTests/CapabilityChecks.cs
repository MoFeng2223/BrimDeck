using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private static async Task CapabilityChecks()
    {
        Check("capability ids unique and descriptions complete", ModuleApi.Catalog.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == ModuleApi.Catalog.Count
            && ModuleApi.Catalog.All(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.NameEn) && c.IntroducedMinor is >= 0 && c.IntroducedMinor <= ModuleApi.Minor
                && c.ResourceLifetime == "activation" && c.Environment is "any" or "windows-desktop"));
        Check("capability and permission lists derive from descriptors", ModuleApi.Capabilities.SequenceEqual(ModuleApi.Catalog.Select(c => c.Id))
            && ModuleApi.Permissions.SequenceEqual(ModuleApi.Catalog.Select(c => c.Permission).OfType<string>().Distinct(StringComparer.Ordinal)));
        Throws("catalog list is immutable", () => ((IList<ModuleCapability>)ModuleApi.Catalog).Clear());
        var usage = ModuleApi.Describe(ModuleApi.UsageRead)!;
        _ = usage with { Permission = "changed" };
        Check("descriptor copy cannot alter catalog", ModuleApi.Describe(ModuleApi.UsageRead)!.Permission == ModuleApi.UsageRead);
        Check("unknown optional capability remains queryable as unavailable", ModuleApi.Describe("future.service") is null);
        Check("UI adapters exactly cover catalog contribution contracts", App.ModuleKinds().Select(k => k.Id).Order().SequenceEqual(ModuleApi.Catalog.Where(c => c.Kind == ModuleCapabilityKind.Contribution).Select(c => c.Id).Order()));
        int factories = 0;
        ModuleService Binding(string? capability, string? permission, Type? contract = null) => new(contract ?? typeof(IModuleUsage), capability, permission, _ => { factories++; throw new Exception("Factory must not run during binding validation."); });
        ModuleHost Build(IEnumerable<ModuleKind>? kinds = null, params ModuleService[] services) => new(Path.Combine(_root, Guid.NewGuid().ToString("N")), new Scheduler(), [], kinds ?? [], services);
        void Reject(string name, Func<ModuleHost> create)
        {
            try { using var host = create(); Check(name, false); }
            catch (ModuleApiException error) { Check(name, error.Code == "invalid-host-binding"); }
        }
        Reject("binding rejects unknown capability", () => Build(null, Binding("future.service", null)));
        Reject("binding rejects missing capability", () => Build(null, Binding(null, null)));
        Reject("binding rejects missing permission", () => Build(null, Binding(ModuleApi.UsageRead, null)));
        Reject("binding rejects permission for another service", () => Build(null, Binding(ModuleApi.UsageRead, ModuleApi.MediaRead)));
        Reject("binding rejects mismatched CLR contract", () => Build(null, Binding(ModuleApi.UsageRead, ModuleApi.UsageRead, typeof(IModuleMedia))));
        Reject("binding rejects service bound as contribution", () => Build([ModuleKind.Of<IModuleUsage>(ModuleApi.UsageRead)]));
        Reject("binding rejects contribution bound as service", () => Build(null, Binding(ModuleApi.UiPages, null, typeof(ModulePage))));
        Reject("binding rejects duplicate capability adapters", () => Build(null, Binding(ModuleApi.UsageRead, ModuleApi.UsageRead), Binding(ModuleApi.UsageRead, ModuleApi.UsageRead)));
        Reject("binding rejects wrong contribution contract", () => Build([ModuleKind.Of<string>(ModuleApi.UiPages)]));
        Check("invalid bindings do not execute factories", factories == 0);
        var source = new ModuleSnapshotSource<ModuleUsageSnapshot>(new(0, DateTimeOffset.UtcNow, []));
        var valid = ModuleService.Of<IModuleUsage>(ModuleApi.UsageRead, scope => new UsageClient(scope, source));
        Check("service builder derives permission from catalog", valid.Permission == usage.Permission);
        using var headless = Build(null, valid);
        Check("headless host advertises actual adapters only", headless.AvailableCapabilities.Order().SequenceEqual(new[] { ModuleApi.HostInfo, ModuleApi.StorageData, ModuleApi.UsageRead }.Order()));
        Throws("desktop completeness detects missing adapter", () => headless.RequireCapabilities(ModuleApi.Capabilities));
        IModuleHostInfo? reader = null;
        await headless.ReconcileAsync([Def("metadata", c => { reader = c.GetService<IModuleHostInfo>(); return ValueTask.CompletedTask; })]);
        var info = reader!.GetInfo();
        Check("host details match actual advertised capability ids", info.Capabilities.SequenceEqual(info.CapabilityDetails.Select(c => c.Id)) && info.ContractMinor == ModuleApi.Minor);
        Check("metadata excludes absent UI and media services", !info.CapabilityDetails.Any(c => c.Id == ModuleApi.UiPages || c.Id == ModuleApi.MediaRead));
        Throws("host capability details immutable", () => ((IList<ModuleCapability>)info.CapabilityDetails).Clear());
        Check("host metadata serializes without desktop objects", JsonSerializer.Serialize(info).Contains("ContractType") && !typeof(ModuleCapability).Assembly.GetReferencedAssemblies().Any(a => a.Name is "PresentationCore" or "PresentationFramework" or "BrimDeck.Core"));
        var legacy = new ModuleHostInfo("test", 1, 1, [ModuleApi.HostInfo]);
        Check("legacy host info constructor preserved", legacy.CapabilityDetails.Count == 0 && legacy.ContractMinor == 1);
        await headless.ReconcileAsync([]); Throws("retained metadata reader revoked with activation", () => reader.GetInfo());
    }
}
