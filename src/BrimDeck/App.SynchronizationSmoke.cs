using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task CheckUsageSynchronizationAsync(string output, List<string> checks)
    {
        var savedSettings = Settings.Copy(); var savedSnapshots = Deck.Snapshots;
        var pending = new TaskCompletionSource<IReadOnlyList<ProviderSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(string name, bool okay) => checks.Add((okay ? "PASS " : "FAIL ") + name);
        IEnumerable<FrameworkElement> Elements(DependencyObject root)
        {
            if (root is FrameworkElement element) yield return element;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        T Element<T>(string name) where T : FrameworkElement => Elements(Deck.PanelVisual).OfType<T>().Single(element => element.Name == name);
        Button Button(string name) => Elements(Deck.PanelVisual).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        async Task Layout() { await Dispatcher.Yield(DispatcherPriority.Loaded); Deck.UpdateLayout(); await Task.Delay(45); }
        async Task FinishRead()
        {
            for (int i = 0; i < 100 && Deck.IsSynchronizing; i++) await Task.Delay(10);
            await Layout();
            Check("synchronization completes", !Deck.IsSynchronizing);
        }
        IReadOnlyList<ProviderSnapshot> Fixtures(long input) => new[] { ProviderId.Claude, ProviderId.Codex }.Select(id => new ProviderSnapshot(id)
        {
            UsageAvailable = true,
            Entries = Enumerable.Range(0, 8).Select(i => new TokenEntry($"sync:{id}:{i}", DateTimeOffset.Now,
                $"sync-model-{i}", input, 100, 20, 0, 30, 1m)).ToList()
        }).ToArray();
        try
        {
            var settings = savedSettings.Copy(); settings.UsagePage = true; settings.UsageAutoSync = true;
            settings.Width = 760; settings.Height = 240; settings.Animations = true;
            settings.Apps = new[] { ProviderId.Claude, ProviderId.Codex }.Select(id => new AppEntry { Id = id }).ToList();
            UpdateSettings(settings); Deck.ShowModelDetails(false); Deck.Preview(true);
            var before = Fixtures(100); var after = Fixtures(900);
            Deck.SetSnapshots(before); await Layout();
            var sync = Element<Button>("SyncButton");
            Capture(Deck.PanelVisual, Path.Combine(output, "sync-overview.png"));
            Click(sync); await Layout();
            Check("pausing synchronization disables automatic synchronization", !Settings.UsageAutoSync && !Deck.AutomaticSyncEnabled);
            Capture(Deck.PanelVisual, Path.Combine(output, "sync-paused.png"));
            int reads = 0; Deck.SnapshotLoader = _ => { reads++; return pending.Task; };
            await Deck.RefreshAutomaticallyAsync();
            Check("paused automatic refresh does not read provider data", reads == 0 && ReferenceEquals(Deck.Snapshots, before));

            Deck.OpenModelDetails(ProviderId.Claude, 7); Deck.ToggleModelApp(ProviderId.Codex); await Layout();
            Deck.SelectModelRange(new UsageDateRange(DateTime.Today.AddDays(-6), DateTime.Today));
            var selected = Deck.ModelApps.ToHashSet(); var range = Deck.ModelRange;
            Click(sync); Click(sync); await Layout();
            Check("manual synchronization reads once and retains the displayed data while busy", Deck.IsSynchronizing && reads == 1 && ReferenceEquals(Deck.Snapshots, before));
            Capture(Deck.PanelVisual, Path.Combine(output, "sync-model-busy.png"));
            pending.SetResult(after); await FinishRead();
            Check("manual completion refreshes data without enabling automatic synchronization", ReferenceEquals(Deck.Snapshots, after) && !Settings.UsageAutoSync && reads == 1);
            Check("manual completion preserves the selected applications and dates", Deck.ModelApps.SetEquals(selected) && Deck.ModelRange == range);
            Capture(Deck.PanelVisual, Path.Combine(output, "sync-model-complete.png"));

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); Click(sync);
            pending.SetException(new IOException("Simulated manual synchronization failure")); await FinishRead();
            Check("failed manual synchronization retains data", ReferenceEquals(Deck.Snapshots, after) && Deck.LastRefreshError is not null);
            Click(Button("返回用量")); await Layout();
            Click(sync); await Layout();
            Check("automatic synchronization can be enabled again", Settings.UsageAutoSync && Deck.AutomaticSyncEnabled);

            Deck.OpenModelDetails(ProviderId.Claude, 7);
            var disabled = Settings.Copy(); disabled.UsagePage = false; UpdateSettings(disabled); await Layout();
            Check("disabling the AI page hides synchronization and leaves model details", !sync.IsVisible && !Deck.ShowingModelDetails);
            // The compact rings are shown independently of the page and keep the data current on their own.
            var ringsOnly = Settings.Copy(); ringsOnly.NotchSummary = ringsOnly.CapsuleSummary = true; UpdateSettings(ringsOnly); await Layout();
            Check("compact rings keep automatic reads while the AI page is off", Deck.AutomaticSyncEnabled);
            var neither = Settings.Copy(); neither.NotchSummary = neither.CapsuleSummary = false; UpdateSettings(neither); await Layout();
            await Deck.RefreshAutomaticallyAsync(); Check("with the AI page and the rings off nothing is read automatically", reads == 2 && !Deck.AutomaticSyncEnabled);
            Capture(Deck.PanelVisual, Path.Combine(output, "sync-page-disabled.png"));
        }
        finally
        {
            pending.TrySetResult(savedSnapshots); await FinishRead();
            Deck.SnapshotLoader = _ => Task.FromResult(savedSnapshots); await Deck.RefreshAsync(queueIfBusy: false); Deck.SnapshotLoader = null;
            UpdateSettings(savedSettings); Deck.SetSnapshots(savedSnapshots); Deck.ShowModelDetails(false); Deck.Preview(true);
        }
    }
}
