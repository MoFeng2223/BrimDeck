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
        // Two calls: the reading in hand once the module is ready, then the one published after it.
        Check("ready scope receives snapshots timer and lifecycle", calls == 2 && ticks == 1 && lifecycle == 1 && source.SubscriberCount == 1);
        // A subscription made while the module starts is handed the reading in hand once it is ready, and only once: the
        // module never has to read the snapshot itself to learn where it begins.
        var staged = new ModuleSnapshotSource<ModuleUsageSnapshot>(empty with { Revision = 7 });
        int delivered = 0; ModuleUsageSnapshot? first = null;
        var reload = new ManualResetEventSlim();
        var stagedHost = new ModuleHost(Path.Combine(_root, "staged"), new Scheduler(), [], App.ModuleKinds(),
            [ModuleService.Of<IModuleUsage>(ModuleApi.UsageRead, ModuleApi.UsageRead, scope => new UsageClient(scope, staged))]) { StartTimeout = TimeSpan.FromSeconds(2) };
        try
        {
            var startingStaged = stagedHost.ReconcileAsync([Client(ApiManifest("staged"), async c =>
            { c.GetService<IModuleUsage>().Subscribe(value => { delivered++; first ??= value; }); await Task.Run(reload.Wait); })]);
            staged.Publish(empty with { Revision = 8 });
            Check("starting module is handed nothing while it starts", delivered == 0);
            reload.Set(); await startingStaged;
            Check("subscription receives the latest reading once it is ready", delivered == 1 && first?.Revision == 8);
            staged.Publish(empty with { Revision = 9 });
            Check("later readings follow the first", delivered == 2);
            await stagedHost.ReconcileAsync([]);
            staged.Publish(empty with { Revision = 10 });
            Check("a stopped module is handed no later readings", delivered == 2 && staged.SubscriberCount == 0);
        }
        finally { stagedHost.Dispose(); }
        Check("host information reports actual capabilities", context!.GetService<IModuleHostInfo>().GetInfo().Capabilities.Contains(ModuleApi.UsageRead));
        Throws("module cannot forge lifecycle with object payload", () => context!.Publish<object>(new ModuleSuspending()));
        context!.Publish("module-message");
        await host.ReconcileAsync([]);
        Check("disable releases host subscriptions and timers", source.SubscriberCount == 0 && clock.Callbacks.Count == 0);
        Throws("retained host service revoked after disable", () => reader!.GetSnapshot());
        Throws("retained service cannot subscribe after disable", () => reader!.Subscribe(_ => { }));
        int before = calls;
        await host.ReconcileAsync([Client(ApiManifest(), c => { c.GetService<IModuleUsage>().Subscribe(_ => throw new Exception("listener")); return ValueTask.CompletedTask; }),
            Client(ApiManifest("healthy"), c => { c.GetService<IModuleUsage>().Subscribe(_ => calls++); return ValueTask.CompletedTask; })]);
        source.Publish(empty);
        // Both modules' first readings are handed over at activation, so the one publish adds one to each; the failing listener
        // is attributed to its own module and the healthy one keeps receiving.
        Check("snapshot callback failure isolated and attributed", calls == before + 2 && host.Faults.Any(f => f.ModuleId == "client" && f.Operation == "host snapshot"));
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
        // A reading of zero is a reading, not a missing one: an idle machine reports no traffic, and the module must see it.
        var idle = new MonitorSnapshot(DateTimeOffset.Now, 4, new CpuSample([new CoreSample(0, 0, CoreTier.Unknown, 1, 0, default, null)], default),
            new MemorySample(16UL << 30, 0, 0, 0), new NetworkSample(0, 0), new GpuSample(0, Reading<ulong>.Of(0)) { DedicatedTotal = 8UL << 30 }, default);
        var quiet = ModuleHostProjection.System(3, idle);
        Check("a legitimate zero stays available", quiet is { DownloadRate.Status: ModuleDataStatus.Available, DownloadRate.Value: 0, GpuDedicatedBytesUsed.Status: ModuleDataStatus.Available }
            && quiet.DownloadBytesPerSecond == 0 && quiet.GpuDedicatedBytesUsed.Value == 0);
        Check("a sensor that was not read is missing, not zero", quiet.CpuTemperature.Status == ModuleDataStatus.Unavailable && quiet.CpuTemperature.Value is null
            && quiet.DiskReadBytesPerSecond.Status == ModuleDataStatus.Unavailable && quiet.DiskReadBytesPerSecond.Value is null
            && quiet.PowerWatts.Status == ModuleDataStatus.Unavailable);
        Check("storage is frozen and carries its own readings", quiet is { StorageRead: false } && ((IList<ModuleStorageDrive>)quiet.Storage).IsReadOnly);
        var heated = new MonitorSnapshot(DateTimeOffset.Now, 4, new CpuSample([new CoreSample(0, 0, CoreTier.Unknown, 1, .5, default, null)], default) { Temperature = 61, TemperatureSource = BrimDeck.Core.CpuTemperatureSource.Processor },
            new MemorySample(16UL << 30, 8UL << 30, 0, 0), new NetworkSample(1200, 800),
            new GpuSample(.3, Reading<ulong>.Of(1UL << 30)) { Temperature = 55, Name = "Card", Key = "gpu:1", DedicatedTotal = 8UL << 30 }, default)
        {
            Disk = new DiskRates(4096, 2048),
            Power = new PowerSample(96, PowerOrigin.Estimated, PowerSource.Components, [new("cpu", "CPU", 96, "RAPL")]),
            Storage = [new StorageDrive(0, "Disk", [new StorageVolume("C:", "System", 100UL << 30, 40UL << 30)]) { Temperature = new DiskTemperature("Disk", 41), SolidState = true }]
        };
        var busy = ModuleHostProjection.System(4, heated);
        Check("temperatures, memory and disk carry values and their sources", busy is
            { CpuTemperature.Value: 61, CpuTemperatureSource: "processor", GpuTemperature.Value: 55, GpuAdapterKey: "gpu:1", GpuAdapterName: "Card" }
            && busy.MemoryUsed.Value == 8UL << 30 && busy.DiskReadBytesPerSecond.Value == 4096 && busy.DiskWriteBytesPerSecond.Value == 2048);
        Check("power keeps its measurement and its source", busy is { PowerWatts.Value: 96, PowerEstimated: true, PowerSource: "components" });
        Check("drives and volumes are projected with their state", busy.StorageRead && busy.Storage.Single() is { SolidState: true, Volumes.Count: 1 } drive
            && drive.Volumes[0].UsedBytes == 60UL << 30 && drive.Temperature.Status == ModuleDataStatus.Available);
        Check("readings serialize without host internals", !JsonSerializer.Serialize(busy).Contains("PRIVATE"));
        // The panel's state is the main panel's, and a page that belongs to one module is named only to that module.
        var state = new ModuleHostStateSnapshot(1, now) { Expanded = true, PageId = "thrm:main", ThemeId = "dark", ContentScale = 1.25, DisplayRevision = 3 };
        Check("a module sees its own page", App.PageIdForModules(state.PageId, "thrm") == "thrm:main");
        Check("another module's page is hidden behind the generic page", App.PageIdForModules(state.PageId, "other") == DeckPageIds.LegacyMiniApps);
        Check("built-in pages stay visible to every module", App.PageIdForModules(DeckPageIds.Monitor, "other") == DeckPageIds.Monitor);
        Check("a state snapshot is immutable data", state is { Expanded: true, ContentScale: 1.25, DisplayRevision: 3 } && state.Revision == 1);
        // One publication per real change: a repaint that changed nothing costs no callback, and the revision never goes back.
        var revisions = new HostStateRevisions();
        var first = revisions.Apply(state, force: false)!;
        Check("the first state is published with a revision", first.Revision == 1);
        Check("an unchanged state is not published again", revisions.Apply(state, force: false) is null);
        Check("a changed state is published with the next revision", revisions.Apply(state with { Expanded = false }, force: false) is { Revision: 2 });
        Check("a forced publication moves the revision on", revisions.Apply(state with { Expanded = false }, force: true) is { Revision: 3 });
        Check("the state in hand is the last published", revisions.Current is { Revision: 3, Expanded: false });
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
