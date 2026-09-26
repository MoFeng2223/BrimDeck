using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task CheckBackgroundRefreshAsync(List<string> checks)
    {
        var savedSettings = Settings.Copy(); var savedSnapshots = Deck.Snapshots;
        void Check(string name, bool okay) => checks.Add((okay ? "PASS " : "FAIL ") + name);
        async Task Layout() { await Dispatcher.Yield(DispatcherPriority.Loaded); Deck.UpdateLayout(); await Task.Delay(40); }
        async Task Reopen()
        {
            Deck.SetExpanded(false, true); Deck.PointerChanged(false); Deck.PointerChanged(true);
            await Layout();
        }
        IReadOnlyList<ProviderSnapshot> Snapshots(long input) => new[] { ProviderId.Claude, ProviderId.Codex }.Select(id => new ProviderSnapshot(id)
        {
            UsageAvailable = true,
            Entries = Enumerable.Range(0, 12).Select(i => new TokenEntry($"{id}:{i}", new DateTimeOffset(DateTime.Today.AddDays(-2).AddHours(12)),
                $"refresh-model-{i:00}", input, 100, 20, 0, 30, 1m)).ToList()
        }).ToArray();
        var outstanding = new List<TaskCompletionSource<IReadOnlyList<ProviderSnapshot>>>();
        TaskCompletionSource<IReadOnlyList<ProviderSnapshot>> Pending()
        {
            var source = new TaskCompletionSource<IReadOnlyList<ProviderSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            outstanding.Add(source); return source;
        }
        var pending = Pending(); int loads = 0;
        try
        {
            var settings = savedSettings.Copy(); settings.Width = 760; settings.Height = 240;
            settings.Animations = false; settings.OpenDelay = 1; settings.CloseDelay = 2000;
            settings.Maximized = settings.Borderless = settings.Exclusive = WindowBehavior.Normal;
            settings.Apps = new[] { ProviderId.Claude, ProviderId.Codex }.Select(id => new AppEntry { Id = id }).ToList();
            UpdateSettings(settings);
            var before = Snapshots(100); var after = Snapshots(900);
            Deck.SetSnapshots(before); Deck.OpenModelDetails(ProviderId.Claude, 30); Deck.ToggleModelApp(ProviderId.Codex);
            var range = new UsageDateRange(DateTime.Today.AddDays(-7), DateTime.Today.AddDays(-1));
            Deck.SelectModelRange(range); Deck.Preview(false);
            Deck.SnapshotLoader = _ => { loads++; return pending.Task; };
            await Reopen();
            Check("reopening model details does not refresh or replace the prior snapshot", loads == 0 && ReferenceEquals(Deck.Snapshots, before) && Deck.ShowingModelDetails);
            Deck.ShowModelDetails(false); await Reopen();
            Check("reopening the quota page does not refresh", loads == 0 && !Deck.ShowingModelDetails);
            Deck.ShowModelDetails(true); await Layout();
            var background = Deck.RefreshAsync(queueIfBusy: false);
            Check("background refresh retains the prior snapshot while reading", loads == 1 && ReferenceEquals(Deck.Snapshots, before));
            Deck.ApplySettings(); await Layout();
            Check("relayout of an expanded panel does not start another refresh", loads == 1);
            await Reopen();
            Check("reopening during a pending refresh does not request another read", loads == 1 && ReferenceEquals(Deck.Snapshots, before));
            await Deck.RefreshAsync(queueIfBusy: false);
            Check("a timer tick during background refresh does not start a concurrent read", loads == 1 && ReferenceEquals(Deck.Snapshots, before));
            pending.SetResult(after); await background; await Layout();
            Check("a timer tick during background refresh does not queue a duplicate read", loads == 1);
            Check("completed refresh replaces the snapshot without leaving model details", ReferenceEquals(Deck.Snapshots, after) && Deck.ShowingModelDetails);
            Check("reopening and refresh preserve application and date filters", Deck.ModelApps.SetEquals(new[] { ProviderId.Claude, ProviderId.Codex }) && Deck.ModelRange == range);

            pending = Pending(); background = Deck.RefreshAsync(queueIfBusy: false);
            pending.SetException(new IOException("Simulated refresh failure")); await background; await Layout();
            Check("a failed refresh leaves previously displayed data available", ReferenceEquals(Deck.Snapshots, after) && Deck.LastRefreshError is not null);
            await Reopen();
            Check("reopening after a failed refresh does not trigger a retry", loads == 2 && Deck.LastRefreshError is not null);
            pending = Pending(); background = Deck.RefreshAsync(queueIfBusy: false); pending.SetResult(before); await background; await Layout();
            Check("a later background refresh recovers after a failure", loads == 3 && ReferenceEquals(Deck.Snapshots, before) && Deck.LastRefreshError is null);

            var first = Pending(); var next = Pending(); int requested = 0;
            Deck.SnapshotLoader = _ => { loads++; return ++requested == 1 ? first.Task : next.Task; };
            var current = Deck.RefreshAsync(queueIfBusy: false);
            await Deck.RefreshAsync(); await Deck.RefreshAsync(); await Deck.RefreshAsync(queueIfBusy: false);
            Check("changed filters queue a follow-up without concurrent reads", loads == 4);
            first.SetResult(before); await current; await Layout();
            Check("automatic refresh preserves the single required follow-up", loads == 5 && ReferenceEquals(Deck.Snapshots, before));
            next.SetResult(after); await Layout();
            Check("the required follow-up publishes its completed result", loads == 5 && ReferenceEquals(Deck.Snapshots, after));
            Deck.Preview(true, false); Deck.Preview(true); await Layout();
            Check("settings preview does not trigger account refreshes", loads == 5);
        }
        finally
        {
            foreach (var source in outstanding) source.TrySetResult(savedSnapshots);
            await Layout();
            Deck.SnapshotLoader = null; Deck.Preview(true); Deck.ShowModelDetails(false);
            UpdateSettings(savedSettings); Deck.SetSnapshots(savedSnapshots);
        }
    }
}
