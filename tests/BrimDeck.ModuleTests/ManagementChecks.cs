using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Modules;

internal static partial class Program
{
    private static async Task ManagementChecks()
    {
        string root = Path.Combine(_root, "management"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        foreach (string name in new[] { "module.json", "BrimDeck.Sample.dll", "BrimDeck.Sample.Helper.dll", "BrimDeck.Sample.deps.json" }) File.Copy(Path.Combine(_artifacts.Notes, name), Path.Combine(source, name));
        string data = Path.Combine(root, "host"); var installer = new ModulePackageInstaller(data);
        string digest = ModuleCatalog.Fingerprint(source);
        var first = installer.Install(source, digest);
        Check("folder install stages a statically checked package without data or approval", !first.Pending && Directory.Exists(first.Directory) && !Directory.Exists(Path.Combine(data, "modules-data")) && !File.Exists(Path.Combine(data, "settings.json")));
        Throws("folder installer refuses implicit replacement", () => installer.Install(source, digest));
        var current = new ModuleCatalog(Path.Combine(data, "mods"), "", []).Discover().Single();
        File.WriteAllText(Path.Combine(source, "asset.txt"), "updated resource");
        Throws("folder installer detects content changed after confirmation", () => installer.Install(source, digest, current));
        var updated = installer.Install(source, ModuleCatalog.Fingerprint(source), current);
        Check("folder update keeps previous package restore point", !updated.Pending && updated.RestorePoint is not null && Directory.Exists(updated.RestorePoint) && ModuleCatalog.Fingerprint(updated.RestorePoint) == digest);
        Throws("folder installer rejects overlapping installation source", () => installer.Install(first.Directory));
        var latest = new ModuleCatalog(Path.Combine(data, "mods"), "", []).Discover().Single();
        var restored = installer.Restore(updated.RestorePoint!, latest);
        Check("explicit package restore returns old code without creating or deleting business data", !restored.Pending && ModuleCatalog.Fingerprint(first.Directory) == digest && !Directory.Exists(Path.Combine(data, "modules-data")));
        foreach (string interruption in new[] { "staged", "previous-saved", "package-swapped" })
        {
            string recoveryRoot = Path.Combine(root, "recover-" + interruption);
            var cleanInstaller = new ModulePackageInstaller(recoveryRoot); cleanInstaller.Install(source);
            var oldPackage = new ModuleCatalog(Path.Combine(recoveryRoot, "mods"), "", []).Discover().Single();
            File.WriteAllText(Path.Combine(source, "asset.txt"), interruption);
            var interrupted = new ModulePackageInstaller(recoveryRoot, phase => { if (phase == interruption) throw new InvalidOperationException("simulated process interruption"); });
            Throws("installation interruption at " + interruption, () => interrupted.Install(source, null, oldPackage));
            var recovered = cleanInstaller.Recover();
            Check("installation resumes after restart at " + interruption, recovered.Count == 1 && !recovered[0].Pending && ModuleCatalog.Fingerprint(oldPackage.Directory) == ModuleCatalog.Fingerprint(source));
            Check("installation recovery is idempotent at " + interruption, cleanInstaller.Recover().Count == 0);
        }
        {
            string recoveryRoot = Path.Combine(root, "replacement-recovery"); var cleanInstaller = new ModulePackageInstaller(recoveryRoot); cleanInstaller.Install(source);
            var oldPackage = new ModuleCatalog(Path.Combine(recoveryRoot, "mods"), "", []).Discover().Single();
            File.WriteAllText(Path.Combine(source, "asset.txt"), "next");
            var interrupted = new ModulePackageInstaller(recoveryRoot, phase => { if (phase == "previous-saved") throw new InvalidOperationException("interrupted"); });
            Throws("update interrupted after preserving previous directory", () => interrupted.Install(source, null, oldPackage));
            Directory.CreateDirectory(oldPackage.Directory); string sentinel = Path.Combine(oldPackage.Directory, "keep.txt"); File.WriteAllText(sentinel, "replacement");
            Check("recovery never overwrites a replacement directory", cleanInstaller.Recover().Single().Pending && File.ReadAllText(sentinel) == "replacement");
        }
        var permission = new ModulePermissionIdentity("sample", digest, "theme.accent", 1);
        var pending = new TaskCompletionSource<ModulePermissionChoice>(); int commits = 0, prompts = 0;
        var gate = new ModulePermissionGate(_ => false, (_, _) => { prompts++; return new(pending.Task); }, _ => { });
        using var lifetime = new CancellationTokenSource();
        var operation = gate.ExecuteAsync(permission, lifetime.Token, () => commits++).AsTask();
        Check("repeated permission requests do not stack prompts", !await gate.ExecuteAsync(permission, lifetime.Token, () => commits++) && prompts == 1);
        gate.Revoke("sample"); pending.SetResult(ModulePermissionChoice.Remember);
        Check("permission revoked while prompt is open cannot commit", !await operation && commits == 0);
        gate = new(_ => true, (_, _) => ValueTask.FromResult(ModulePermissionChoice.Deny), _ => { }); lifetime.Cancel();
        Check("retired service handle cannot use remembered permission", !await gate.ExecuteAsync(permission, lifetime.Token, () => commits++) && commits == 0);
        string? approved = null; gate = new(i => approved == i.Digest, (_, _) => ValueTask.FromResult(ModulePermissionChoice.Remember), i => approved = i.Digest);
        Check("remembered sensitive grant binds the current content identity", await gate.ExecuteAsync(permission, CancellationToken.None, () => commits++) && approved == digest && commits == 1);
        var fields = new ModuleSetting[] { new("number", "Number", "Number", ModuleSettingKind.Number, 0, 10) { DefaultValue = 2d }, new("text", "Text", "Text", ModuleSettingKind.Text) { DefaultValue = "default", MaximumLength = 10 } };
        var storage = new ModuleStorage(Path.Combine(root, "schema")); var settings = new ModuleSettingsStore("Test", "Test", fields, storage);
        var initial = settings.GetSnapshot();
        Check("schema store exposes immutable defaults without writing", (double)initial.Values["number"] == 2 && initial.Revision == 0 && storage.ReadResult<JsonElement>("settings").Status == ModuleReadStatus.Missing);
        var result = settings.Apply(new(initial.Revision, new Dictionary<string, object> { ["number"] = 4d }));
        Check("schema patch persists and increments revision", result.Status == ModuleSettingsWriteStatus.Saved && settings.GetSnapshot().Revision == 1);
        Check("stale form patch cannot overwrite newer settings", settings.Apply(new(0, new Dictionary<string, object> { ["number"] = 8d })).Status == ModuleSettingsWriteStatus.Conflict && (double)settings.Read("number") == 4);
        result = settings.Apply(new(1, new Dictionary<string, object> { ["number"] = 5d, ["text"] = new string('x', 11) }));
        Check("schema validation rejects the complete invalid batch", result.Status == ModuleSettingsWriteStatus.Invalid && (double)settings.Read("number") == 4 && settings.GetSnapshot().Revision == 1);
        settings.Reset(); Check("settings reset removes overrides but preserves independent business data", settings.GetSnapshot().Revision == 2 && (double)settings.Read("number") == 2);
    }
}
