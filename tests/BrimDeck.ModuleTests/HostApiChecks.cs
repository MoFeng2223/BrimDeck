using System.IO;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Timers;
using BrimDeck.Modules;

internal static partial class Program
{
    private sealed class UsageClient(ModuleServiceScope scope, ModuleSnapshotSource<ModuleUsageSnapshot> source) : IModuleUsage
    {
        public ModuleUsageSnapshot GetSnapshot() => scope.Read(source);
        public IDisposable Subscribe(Action<ModuleUsageSnapshot> listener) => scope.Subscribe(source, listener);
    }
    private static ModuleManifest ApiManifest(string id = "client", string[]? caps = null, string[]? permissions = null) => new()
    { Id = id, Name = id, ApiMinor = 1, Capabilities = caps ?? [ModuleApi.UsageRead], Permissions = permissions ?? [ModuleApi.UsageRead] };
    private static async Task HostApiChecks()
    {
        var clock = new Scheduler(); var empty = new ModuleUsageSnapshot(0, DateTimeOffset.UtcNow, Array.AsReadOnly(Array.Empty<ModuleUsageEntry>()));
        var source = new ModuleSnapshotSource<ModuleUsageSnapshot>(empty);
        Throws("binding refuses concrete service implementation", () => ModuleService.Of<UsageClient>(ModuleApi.UsageRead, ModuleApi.UsageRead, scope => new UsageClient(scope, source)));
        using var host = new ModuleHost(Path.Combine(_root, "api"), clock, [], App.ModuleKinds(),
            [ModuleService.Of<IModuleUsage>(ModuleApi.UsageRead, ModuleApi.UsageRead, scope => new UsageClient(scope, source))]) { StartTimeout = TimeSpan.FromSeconds(2) };
        ModuleDefinition Client(ModuleManifest manifest, Func<IModuleContext, ValueTask> activate) => new(manifest, Guid.NewGuid().ToString(), true, () => new TestModule(activate));
        var unavailable = Client(new() { Id = "future", Name = "future", ApiMinor = ModuleApi.Minor + 1 }, Done);
        await host.ReconcileAsync([unavailable]); Check("future minor rejected before execution", host.Modules.Single().Status == ModuleStatus.Failed);
        int creates = 0;
        var unknown = Client(new() { Id = "unknown", Name = "unknown", Requires = new() { Capabilities = ["unknown.api"] } }, _ => { creates++; return ValueTask.CompletedTask; });
        await host.ReconcileAsync([unknown]); Check("unknown required capability rejected before activation", creates == 0 && host.Modules.Single().Status == ModuleStatus.Failed);
        await host.ReconcileAsync([Client(ApiManifest(caps: [ModuleApi.MediaRead]), Done)]);
        Check("known but unavailable capability rejected", host.Modules.Single().Status == ModuleStatus.Failed);
        string? code = null;
        using (var privateHost = Host(null, new PrivateHostService()))
        {
            await privateHost.ReconcileAsync([Def("external", c => { try { c.GetService<IPrivateHostService>(); } catch (ModuleApiException ex) { code = ex.Code; } return ValueTask.CompletedTask; })]);
            Check("external modules cannot obtain private host services", code == "service-unavailable");
        }
        code = null;
        await host.ReconcileAsync([Client(ApiManifest(permissions: []), c => { try { c.GetService<IModuleUsage>(); } catch (ModuleApiException ex) { code = ex.Code; } return ValueTask.CompletedTask; })]);
        Check("service requires declared permission", code == "permission-denied");
        code = null;
        await host.ReconcileAsync([Client(ApiManifest(caps: []), c => { try { c.GetService<IModuleUsage>(); } catch (ModuleApiException ex) { code = ex.Code; } return ValueTask.CompletedTask; })]);
        Check("service requires declared capability", code == "capability-not-declared");
        code = null;
        await host.ReconcileAsync([Client(ApiManifest(), c => { try { c.Register("unsupported", new object()); } catch (ModuleApiException ex) { code = ex.Code; } return ValueTask.CompletedTask; })]);
        Check("unknown contribution rejected", code == "unknown-contribution");
        await host.ReconcileAsync([Client(ApiManifest(), c => { c.Register("undeclared", new ModulePage("Page", "Page", _ => throw new Exception())); return ValueTask.CompletedTask; })]);
        Check("minor one UI contribution needs capability", host.Modules.Single().Status == ModuleStatus.Failed);
        var release = new TaskCompletionSource(); IModuleContext? context = null; IModuleUsage? reader = null; int calls = 0, ticks = 0, lifecycle = 0;
        var starting = host.ReconcileAsync([Client(ApiManifest(), async c =>
        { context = c; reader = c.GetService<IModuleUsage>(); reader.Subscribe(_ => calls++); c.Subscribe<ModuleSuspending>(_ => lifecycle++); c.Every(TimeSpan.FromSeconds(1), () => ticks++); await release.Task; })]);
        Check("snapshot subscriptions and timers staged together", source.SubscriberCount == 0 && clock.Callbacks.Count == 0);
        host.Publish(new ModuleSuspending()); source.Publish(empty); clock.Tick();
        Check("starting module receives no host callbacks", calls == 0 && ticks == 0 && lifecycle == 0);
        release.SetResult(); await starting; source.Publish(empty with { Revision = 1 }); clock.Tick(); host.Publish(new ModuleSuspending());
        Check("ready scope receives snapshots timer and lifecycle", calls == 1 && ticks == 1 && lifecycle == 1 && source.SubscriberCount == 1);
        Check("host information reports actual capabilities", context!.GetService<IModuleHostInfo>().GetInfo().Capabilities.Contains(ModuleApi.UsageRead));
        Throws("module cannot forge lifecycle with object payload", () => context!.Publish<object>(new ModuleSuspending()));
        context!.Publish("module-message");
        await host.ReconcileAsync([]);
        Check("disable releases host subscriptions and timers", source.SubscriberCount == 0 && clock.Callbacks.Count == 0);
        Throws("retained host service revoked after disable", () => reader!.GetSnapshot());
        Throws("retained service cannot subscribe after disable", () => reader!.Subscribe(_ => { }));
        await host.ReconcileAsync([Client(ApiManifest(), c => { c.GetService<IModuleUsage>().Subscribe(_ => throw new Exception("listener")); return ValueTask.CompletedTask; }),
            Client(ApiManifest("healthy"), c => { c.GetService<IModuleUsage>().Subscribe(_ => calls++); return ValueTask.CompletedTask; })]);
        source.Publish(empty); Check("snapshot callback failure isolated and attributed", calls == 2 && host.Faults.Any(f => f.ModuleId == "client" && f.Operation == "host snapshot"));
        var target = new ModuleDefinition(new() { Id = "dep", Name = "dep", Version = "2.0.0" }, "v1", true, () => new TestModule(Done));
        var child = new ModuleDefinition(new() { Id = "child", Name = "child", Dependencies = [new("dep") { MaximumVersionExclusive = "2.0.0" }] }, "v1", true, () => new TestModule(_ => { creates++; return ValueTask.CompletedTask; }));
        await host.ReconcileAsync([child, target]); Check("dependency exclusive maximum blocks incompatible installed version", creates == 0 && host.Modules.Single(m => m.Id == "child").Status == ModuleStatus.DependencyFailed);
        var demand = new ModuleSnapshotSource<ModuleUsageSnapshot>(empty); demand.DemandChanged += _ => throw new Exception("demand failure");
        Throws("failed demand signal returns failure", () => demand.Subscribe(_ => { })); Check("failed subscribe leaves no listener", demand.SubscriberCount == 0);
        var storageDirectory = Path.Combine(_root, "structured"); var storage = new ModuleStorage(storageDirectory);
        Check("structured storage distinguishes missing", storage.ReadResult<int>("absent").Status == ModuleReadStatus.Missing);
        storage.Write("zero", 0); Check("structured storage preserves valid zero", storage.ReadResult<int>("zero") is { Status: ModuleReadStatus.Value, Value: 0 });
        File.WriteAllText(Path.Combine(storageDirectory, "broken.json"), "{");
        Check("structured storage distinguishes corrupt", storage.ReadResult<int>("broken").Status == ModuleReadStatus.Corrupt);
        Throws("legacy read cannot silently replace corrupt with default", () => storage.Read<int>("broken"));
        NoticeChecks(); ProjectionChecks(); ThresholdChecks();
    }
    private static void NoticeChecks()
    {
        var gate = new ModuleNoticeQueue(); using var life = new CancellationTokenSource(); using var next = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow; var notice = new ModuleNotice("quota", "Codex", "达到阈值") { DurationMilliseconds = 1, ProgressPercent = 120 };
        Check("notice bounds normalized", ModuleNoticeQueue.Normalize(notice) is { DurationMilliseconds: 900, ProgressPercent: 100 });
        Throws("invalid notice rejected", () => ModuleNoticeQueue.Normalize(notice with { ProgressPercent = double.NaN }));
        gate.Enqueue("sample", life.Token, notice, now); gate.Enqueue("sample", life.Token, notice with { Message = "new" }, now);
        Check("pending notice coalesces by activation and id", gate.Count == 1 && gate.Take(now)?.Notice.Message == "new");
        for (int i = 0; i < 3; i++) { gate.Enqueue("sample", life.Token, notice with { Id = "quota" + i }, now); gate.Take(now); }
        Check("four per minute budget survives dequeue", gate.Enqueue("sample", life.Token, notice, now) == ModuleNoticeResult.RateLimited);
        Check("rate budget survives activation replacement", gate.Enqueue("sample", next.Token, notice, now) == ModuleNoticeResult.RateLimited);
        Check("budget recovers after time window", gate.Enqueue("sample", next.Token, notice, now.AddMinutes(1)) == ModuleNoticeResult.Queued);
        gate.Remove("sample", life.Token); Check("old teardown cannot remove next generation notice", gate.Count == 1);
        gate.Prune(now.AddMinutes(2)); Check("unshown notices expire while island hidden", gate.Count == 0);
        gate.Enqueue("other", life.Token, notice, now); life.Cancel(); Check("cancelled activation cannot deliver queued notice", gate.Take(now) is null);
        for (int i = 0; i < 16; i++) gate.Enqueue("owner" + i, next.Token, notice, now);
        Check("global notice queue bounded", gate.Enqueue("overflow", next.Token, notice, now) == ModuleNoticeResult.RateLimited && gate.Count == 16);
    }
    private static void ProjectionChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var raw = new ProviderSnapshot(ProviderId.Codex) { Details = new() { ["token"] = "SECRET" }, Source = "PRIVATE-PATH", Scope = "PRIVATE-ACCOUNT", Quotas = [new("5h", 80, now.AddHours(5), 300)],
            UsageAvailable = true, UsageComplete = false, Entries = [new("x", now, "model", long.MaxValue, 1, 1, 0, 10)] };
        var usage = ModuleHostProjection.Usage(1, now, [raw]); raw.Quotas.Clear();
        Check("usage projection detached from mutable provider stores", usage.Entries.Single().Metrics.Single().UsedPercent == 80);
        Throws("snapshot list rejects mutation", () => ((IList<ModuleUsageEntry>)usage.Entries).Clear());
        Check("token aggregation saturates without overflow and marks partial", usage.Entries[0].TotalTokens == long.MaxValue && !usage.Entries[0].TokenUsageComplete);
        string text = JsonSerializer.Serialize(usage); Check("host projection omits secrets account and source paths", !text.Contains("SECRET") && !text.Contains("PRIVATE"));
        var extreme = new ProviderSnapshot(ProviderId.Codex) { Metrics = [new(MetricKind.Count, "extreme") { Used = decimal.MaxValue, Total = 0.0000000000000000000000000001m }] };
        Check("malformed decimal ratio cannot break host projection", ModuleHostProjection.Usage(2, now, [extreme]).Entries[0].Metrics[0].UsedPercent is null);
        var missing = ModuleHostProjection.System(1, MonitorSnapshot.Empty(1, "unavailable"));
        Check("missing monitor values remain null", missing.Status == ModuleDataStatus.Unavailable && missing.CpuUsedPercent is null && missing.MemoryUsedBytes is null && missing.DownloadBytesPerSecond is null);
        var malformed = MonitorSnapshot.Empty(1, "x") with { Cpu = new CpuSample([], default), Memory = new MemorySample(0, 0, 0, 0), Network = new NetworkSample(double.NaN, -1), Gpu = new GpuSample(double.PositiveInfinity, default) };
        Check("invalid system numbers never become fake zero", ModuleHostProjection.System(2, malformed).Status == ModuleDataStatus.Unavailable);
        Check("missing player is unavailable with no timeline", ModuleHostProjection.Media(1, now, null) is { Status: ModuleDataStatus.Unavailable, PositionSeconds: null, DurationSeconds: null });
        Check("playing metadata omits embedded content", !JsonSerializer.Serialize(ModuleHostProjection.Media(2, now, new() { Title = "Track", EmbeddedLyrics = "PRIVATE-LYRICS", State = MediaState.Playing })).Contains("PRIVATE"));
    }
    private static void ThresholdChecks()
    {
        var now = DateTimeOffset.UtcNow; var state = new BrimDeck.UsageAlerts.AlertState(); var gate = new BrimDeck.UsageAlerts.ThresholdGate(state); int shown = 0;
        ModuleUsageSnapshot Snapshot(double? value, DateTimeOffset? reset, ModuleDataStatus status = ModuleDataStatus.Available, bool unlimited = false) => new(1, now,
            [new("codex", "Codex", status, now, [new("five-hour", "percent", "5h", value, null, null, null, "", "", unlimited, reset, 300)], false, 0, 0, 0)]);
        ModuleNoticeResult Show(ModuleNotice notice) { shown++; return ModuleNoticeResult.Queued; }
        Check("below threshold stays silent", !gate.Evaluate(Snapshot(79, now.AddHours(5)), 80, now, Show) && shown == 0);
        Check("crossing threshold queues animation", gate.Evaluate(Snapshot(80, now.AddHours(5)), 80, now, Show) && shown == 1);
        Check("same period and repeated snapshot do not repeat", !gate.Evaluate(Snapshot(90, now.AddHours(5)), 80, now, Show) && shown == 1);
        gate.Evaluate(Snapshot(10, now.AddHours(5)), 80, now, Show); gate.Evaluate(Snapshot(85, now.AddHours(5)), 80, now, Show);
        Check("known period remains deduplicated after percentage drop", shown == 1);
        Check("new reset period rearms", gate.Evaluate(Snapshot(85, now.AddHours(6)), 80, now, Show) && shown == 2);
        var restored = new BrimDeck.UsageAlerts.ThresholdGate(JsonSerializer.Deserialize<BrimDeck.UsageAlerts.AlertState>(JsonSerializer.Serialize(state))!);
        Check("saved state prevents duplicate after restart", !restored.Evaluate(Snapshot(85, now.AddHours(6)), 80, now, Show) && shown == 2);
        Check("stale data never alerts", !gate.Evaluate(Snapshot(99, now.AddHours(7), ModuleDataStatus.Stale), 80, now, Show));
        Check("expired quota never alerts", !gate.Evaluate(Snapshot(99, now.AddSeconds(-1)), 80, now, Show));
        Check("unknown and unlimited quota never alerts", !gate.Evaluate(Snapshot(null, null), 80, now, Show) && !gate.Evaluate(Snapshot(99, null, unlimited: true), 80, now, Show));
        Check("rate limit does not consume threshold episode", !gate.Evaluate(Snapshot(99, now.AddHours(8)), 80, now, _ => ModuleNoticeResult.RateLimited));
        Check("rejected alert can retry", gate.Evaluate(Snapshot(99, now.AddHours(8)), 80, now, Show));
        gate.Evaluate(Snapshot(99, null), 80, now, Show); int baseline = shown;
        gate.Evaluate(Snapshot(79, null), 80, now, Show); gate.Evaluate(Snapshot(81, null), 80, now, Show);
        Check("unknown period ignores one point boundary jitter", shown == baseline);
        gate.Evaluate(Snapshot(78, null), 80, now, Show); gate.Evaluate(Snapshot(81, null), 80, now, Show);
        Check("unknown period rearms after two point recovery", shown == baseline + 1);
        Throws("nonfinite threshold rejected", () => gate.Evaluate(Snapshot(90, null), double.NaN, now, Show));
    }
}
