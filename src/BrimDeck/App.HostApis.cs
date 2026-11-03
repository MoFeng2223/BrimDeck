using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public partial class App
{
    private long _usageRevision, _mediaRevision, _systemRevision;
    internal ModuleSnapshotSource<ModuleUsageSnapshot> ModuleUsageSource { get; } = new(new(0, DateTimeOffset.UtcNow, Array.AsReadOnly(Array.Empty<ModuleUsageEntry>())));
    internal ModuleSnapshotSource<ModuleMediaSnapshot> ModuleMediaSource { get; } = new(new(0, DateTimeOffset.UtcNow, ModuleDataStatus.Unavailable, "unknown", "", "", "", "", null, null));
    internal ModuleSnapshotSource<ModuleSystemSnapshot> ModuleSystemSource { get; } = new(new(0, DateTimeOffset.UtcNow, ModuleDataStatus.Unavailable, null, null, null, null, null, null, null));
    private IEnumerable<ModuleService> HostApiBindings()
    {
        yield return ModuleService.Of<IModulePresentation>(ModuleApi.UiPresentation, scope => new PresentationWriter(this, scope));
        yield return ModuleService.Of<IModuleUsage>(ModuleApi.UsageRead, scope => new UsageReader(scope, ModuleUsageSource));
        yield return ModuleService.Of<IModuleMedia>(ModuleApi.MediaRead, scope => new MediaReader(scope, ModuleMediaSource));
        yield return ModuleService.Of<IModuleSystem>(ModuleApi.SystemRead, scope => new SystemReader(scope, ModuleSystemSource));
        yield return ModuleService.Of<IModuleNotifications>(ModuleApi.NotificationsShow, scope =>
        { scope.OnDispose(() => DropModuleNotices(scope.ModuleId, scope.Lifetime)); return new NoticeWriter(this, scope); });
    }
    private void InitializeHostApiDemand()
    {
        void OnUi(Action action)
        {
            if (Exiting || Dispatcher.HasShutdownStarted) return;
            void Apply() { if (Deck is not null && !Exiting) action(); }
            if (Dispatcher.CheckAccess()) Apply(); else Dispatcher.BeginInvoke(Apply);
        }
        ModuleMediaSource.DemandChanged += _ => OnUi(StartMedia);
        ModuleSystemSource.DemandChanged += _ => OnUi(UpdateSystemDemand);
    }
    internal void PublishModuleUsage(IReadOnlyList<ProviderSnapshot> snapshots)
    { if (!Exiting) ModuleUsageSource.Publish(ModuleHostProjection.Usage(++_usageRevision, DateTimeOffset.UtcNow, snapshots)); }
    private void PublishModuleMedia() => ModuleMediaSource.Publish(ModuleHostProjection.Media(++_mediaRevision, DateTimeOffset.UtcNow, Media?.Current));
    private void PublishModuleSystem(MonitorSnapshot snapshot) => ModuleSystemSource.Publish(ModuleHostProjection.System(++_systemRevision, snapshot));

    private abstract class SnapshotReader<T>(ModuleServiceScope scope, ModuleSnapshotSource<T> source) : IModuleSnapshotReader<T> where T : class
    {
        public virtual T GetSnapshot() => scope.Read(source);
        public IDisposable Subscribe(Action<T> listener) => scope.Subscribe(source, listener);
    }
    private sealed class UsageReader(ModuleServiceScope scope, ModuleSnapshotSource<ModuleUsageSnapshot> source) : SnapshotReader<ModuleUsageSnapshot>(scope, source), IModuleUsage;
    private sealed class SystemReader(ModuleServiceScope scope, ModuleSnapshotSource<ModuleSystemSnapshot> source) : SnapshotReader<ModuleSystemSnapshot>(scope, source), IModuleSystem
    {
        public override ModuleSystemSnapshot GetSnapshot()
        { var value = base.GetSnapshot(); return value.Status == ModuleDataStatus.Available && DateTimeOffset.UtcNow - value.ObservedAt > TimeSpan.FromSeconds(6) ? value with { Status = ModuleDataStatus.Stale } : value; }
    }
    private sealed class MediaReader(ModuleServiceScope scope, ModuleSnapshotSource<ModuleMediaSnapshot> source) : SnapshotReader<ModuleMediaSnapshot>(scope, source), IModuleMedia
    {
        public override ModuleMediaSnapshot GetSnapshot()
        {
            var value = base.GetSnapshot();
            return value.PlaybackState == "playing" && value.PositionSeconds is { } position && value.DurationSeconds is { } duration
                ? value with { PositionSeconds = Math.Clamp(position + Math.Max(0, (DateTimeOffset.UtcNow - value.ObservedAt).TotalSeconds) * value.PlaybackRate, 0, duration) } : value;
        }
    }
    private sealed class NoticeWriter(App app, ModuleServiceScope scope) : IModuleNotifications
    {
        public ModuleNoticeResult Show(ModuleNotice notice)
        { scope.RequireLive(); return scope.Ready ? app.PostModuleNotice(scope.ModuleId, scope.Lifetime, notice) : ModuleNoticeResult.NotReady; }
    }
    private sealed class PresentationWriter(App app, ModuleServiceScope scope) : IModulePresentation
    {
        public void Invalidate()
        { scope.RequireLive(); if (scope.Ready) app.QueueModuleViewsRefresh(); }
    }
}
