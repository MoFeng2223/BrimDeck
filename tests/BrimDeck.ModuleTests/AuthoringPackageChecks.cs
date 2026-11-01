using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;

internal static partial class Program
{
    private sealed class AuthoringNotices(ModuleServiceScope scope, Action<ModuleNotice> receive, Func<ModuleNoticeResult> result) : IModuleNotifications
    { public ModuleNoticeResult Show(ModuleNotice notice) { scope.RequireLive(); receive(notice); return scope.Ready ? result() : ModuleNoticeResult.NotReady; } }

    // Separate entry point consumes binaries built from the exported docs, never their source.
    private static int AuthoringPackageChecks(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use an E: disposable verification directory.");
        _root = Path.Combine(Path.GetDirectoryName(directory)!, "runtime-checks");
        Directory.CreateDirectory(_root);
        try
        {
            _ = new Application(); Soft.AnimationsEnabled = false;
            var catalog = new ModuleCatalog(directory, Path.Combine(_root, "cache"), [typeof(ModuleUi).Assembly, typeof(DeckSettings).Assembly]);
            var packages = catalog.Discover();
            Check("three independent documentation packages discovered", packages.Count == 3 && catalog.Failures.Count == 0);
            var scheduler = new Scheduler(); int noticeCount = 0; var result = ModuleNoticeResult.RateLimited;
            var source = new ModuleSnapshotSource<ModuleUsageSnapshot>(new(0, DateTimeOffset.UtcNow, []));
            ModuleHost CreateHost() => new(Path.Combine(_root, "data"), scheduler, [], App.ModuleKinds(), [
                ModuleService.Of<IModulePresentation>(ModuleApi.UiPresentation, scope => new TestPresentation(scope)),
                ModuleService.Of<IModuleUsage>(ModuleApi.UsageRead, scope => new UsageClient(scope, source)),
                ModuleService.Of<IModuleNotifications>(ModuleApi.NotificationsShow, scope => new AuthoringNotices(scope, _ => noticeCount++, () => result))
            ]);
            var owner = new Grid();
            owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck.UI;component/PanelControls.xaml", UriKind.Relative) });
            using (var host = CreateHost())
            {
                host.ReconcileAsync(packages.Select(p => catalog.Definition(p, true))).GetAwaiter().GetResult();
                Check("documented manifests activate against current host", host.Modules.Count == 3 && host.Modules.All(m => m.Status == ModuleStatus.Ready));
                Check("independent DLLs register their four contribution kinds", host.Registry.List<ModulePage>().Count == 2
                    && host.Registry.List<IModuleActivity>().Count == 1 && host.Registry.List<ModuleWidget>().Count == 1 && host.Registry.List<IModuleSettings>().Count == 1);
                var clock = host.Registry.List<ModulePage>().Single(p => p.ModuleId == "clock-demo");
                using (var view = new ModuleViewHost(host, clock.ModuleId, new(owner, _ => { }, _ => { }, _ => { }), clock.Value.CreateView))
                {
                    view.SetViewport(400, 160); view.Measure(new(400, 160)); view.Arrange(new(0, 0, 400, 160));
                    Check("documented clock mounts without reading host source", Descendants(view).OfType<TextBlock>().Any(t => t.Text.Count(c => c == ':') == 2));
                }
                var settings = host.Registry.List<IModuleSettings>().Single().Value;
                var widget = host.Registry.List<ModuleWidget>().Single().Value;
                var activity = host.Registry.List<IModuleActivity>().Single().Value;
                settings.Write("step", 2d);
                var page = host.Registry.List<ModulePage>().Single(p => p.ModuleId == "counter-demo");
                using (var view = new ModuleViewHost(host, page.ModuleId, new(owner, _ => { }, _ => { }, _ => { }), page.Value.CreateView))
                using (var compact = new ModuleViewHost(host, page.ModuleId, new(owner, _ => { }, _ => { }, _ => { }), c => activity.CreateView(c, ActivityViewKind.Compact)))
                using (var expanded = new ModuleViewHost(host, page.ModuleId, new(owner, _ => { }, _ => { }, _ => { }), c => activity.CreateView(c, ActivityViewKind.Expanded)))
                {
                    Descendants(view).OfType<Button>().Single(b => Equals(b.Content, "+")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("documented counter settings affect shared business state", activity.State.Active && Math.Abs(activity.State.Progress - .02) < .00001);
                    Check("independent activity view receives model changes", Descendants(compact).OfType<RollingDigitsView>().Single().Text == "2");
                    expanded.SetViewport(new ModuleViewport(320, 76) { ContentScale = 1.25, DeviceScale = 1.5 });
                    expanded.Measure(new(320, 76)); expanded.Arrange(new(0, 0, 320, 76));
                    Check("documented expanded activity uses a separate stable view", !ReferenceEquals(Descendants(expanded).OfType<RollingDigitsView>().Single(), Descendants(compact).OfType<RollingDigitsView>().Single())
                        && Descendants(expanded).OfType<Button>().All(b => b.Width == 48));
                    Check("documented persistent state is written outside its package", File.Exists(Path.Combine(_root, "data", "counter-demo", "preferences.json")));
                    Throws("documented settings reject invalid input", () => settings.Write("step", double.NaN));
                    settings.Reset(); Check("documented reset restores only its setting", Equals(settings.Read("step"), 1d) && activity.State.Active);
                }
                widget.SetEnabled(false);
                Check("documented widget removes page and activity", host.Registry.List<ModulePage>().All(p => p.ModuleId != "counter-demo") && !activity.State.Active);
                Check("independent settings intentionally remain accessible", settings.Visible && ((IModuleSettingsNavigation)settings).Placement == ModuleSettingsPlacement.Modules);
                widget.SetEnabled(true);
                Check("documented widget re-registers page preserving count", host.Registry.List<ModulePage>().Any(p => p.ModuleId == "counter-demo") && activity.State.Active);
                var reset = DateTimeOffset.UtcNow.AddHours(1);
                ModuleUsageSnapshot Snapshot(ModuleDataStatus status, DateTimeOffset resetAt, DateTimeOffset? expires = null) => new(1, DateTimeOffset.UtcNow,
                    [new("codex", "Codex", status, DateTimeOffset.UtcNow, [new("five-hour", "percent", "五小时", 90, null, null, null, "%", "", false, resetAt, 300) { ExpiresAt = expires }], false, 0, 0, 0)]);
                source.Publish(Snapshot(ModuleDataStatus.Stale, reset));
                source.Publish(Snapshot(ModuleDataStatus.Available, reset, DateTimeOffset.UtcNow.AddSeconds(-1)));
                Check("documented alerts ignore stale and expired metrics", noticeCount == 0);
                source.Publish(Snapshot(ModuleDataStatus.Available, reset));
                Check("documented alert does not mark rate-limited delivery", noticeCount == 1 && !File.Exists(Path.Combine(_root, "data", "usage-guard", "alert-history.json")));
                result = ModuleNoticeResult.Queued; scheduler.Tick();
                Check("documented cached polling retries rejected notification", noticeCount == 2);
                scheduler.Tick(); source.Publish(Snapshot(ModuleDataStatus.Available, reset));
                Check("documented alert accepts one notice per period", noticeCount == 2);
                Check("documented alert persists accepted period", File.Exists(Path.Combine(_root, "data", "usage-guard", "alert-history.json")));
                Check("documented modules report no host callback faults", host.Faults.Count == 0);
            }
            Check("documented modules release subscriptions and timers", source.SubscriberCount == 0 && scheduler.Callbacks.Count == 0);
            using (var restarted = CreateHost())
            {
                restarted.ReconcileAsync(packages.Select(p => catalog.Definition(p, true))).GetAwaiter().GetResult();
                scheduler.Tick();
                Check("documented alert restores persisted deduplication", noticeCount == 2);
                Check("documented counter restores shared session", restarted.Registry.List<IModuleActivity>().Single().Value.State.Active);
            }
            string preferences = Path.Combine(_root, "data", "counter-demo", "preferences.json");
            File.WriteAllText(preferences, "broken-author-data");
            using (var damaged = CreateHost())
            {
                damaged.ReconcileAsync([catalog.Definition(packages.Single(p => p.Manifest.Id == "counter-demo"), true)]).GetAwaiter().GetResult();
                Check("documented module reports corrupt preferences as failed activation", damaged.Modules.Single().Status == ModuleStatus.Failed);
                Check("documented module never overwrites corrupt user data with defaults", File.ReadAllText(preferences) == "broken-author-data");
            }
        }
        catch (Exception error) { Failures.Add(error.ToString()); }
        File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(new { checks = _checks, failures = Failures }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string failure in Failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"Authoring runtime checks: {_checks}, failures: {Failures.Count}");
        return Failures.Count == 0 ? 0 : 1;
    }
}
