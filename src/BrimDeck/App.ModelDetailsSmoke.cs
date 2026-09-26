using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task CheckModelDetailsAsync(string output, List<string> checks)
    {
        var savedSettings = Settings.Copy(); var savedSnapshots = Deck.Snapshots;
        void Check(string name, bool okay) => checks.Add((okay ? "PASS " : "FAIL ") + name);
        IEnumerable<FrameworkElement> Elements(DependencyObject root)
        {
            if (root is FrameworkElement element) yield return element;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Elements(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        Button Button(string name) => Elements(Deck.PanelVisual).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        async Task Layout() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded); Deck.UpdateLayout(); await Task.Delay(25); }
        var fixtures = DemoData.Create();
        foreach (var snapshot in fixtures)
        {
            snapshot.UsageAvailable = true; snapshot.Entries.Clear();
            for (int day = 0; day < 30; day++)
                for (int model = 0; model < 4; model++)
                    snapshot.Entries.Add(new($"{snapshot.Id}:{day}:{model}", new DateTimeOffset(DateTime.Today.AddDays(-day).AddHours(1)),
                        model == 0 ? "same-model-in-two-apps" : $"example-model-{model}", 4500 + model, 120_000, 50, 80, 1000, (int)snapshot.Id + 1.25m));
        }
        Deck.SetSnapshots(fixtures);
        try
        {
            var summaries = new[] { Button("查看 Claude 今日明细"), Button("查看 Claude 7 天明细") };
            foreach (var summary in summaries) summary.Background = UI.Brush(UI.Fill);
            await Layout(); Capture(Deck.PanelVisual, Path.Combine(output, "summary-highlight.png"));
            foreach (var summary in summaries) summary.ClearValue(Control.BackgroundProperty);
            Click("查看 Claude 7 天明细"); await Layout();
            Check("statistics row preselects its application and period", Deck.ShowingModelDetails && Deck.ModelApps.SetEquals(new[] { ProviderId.Claude }) && Deck.ModelRange.Start == DateTime.Today.AddDays(-6));
            Click("筛选 Codex"); Click("筛选 Claude"); Click("筛选 Codex");
            Check("multiple selection never deselects its last application", Deck.ModelApps.SetEquals(new[] { ProviderId.Codex }));
            Click("筛选 Claude"); Click("30 天"); await Layout();
            Deck.Preview(false); Deck.Preview(true); await Layout();
            Check("collapsing preserves page and filters", Deck.ShowingModelDetails && Deck.ModelApps.Count == 2 && Deck.ModelRange.Start == DateTime.Today.AddDays(-29));
            Deck.Preview(false); Deck.SetExpanded(true, true);
            Check("expanded overlay can receive Escape without keyboard focus", Deck.EscapeRegistered);
            Deck.SetExpanded(false, true);
            Check("collapsed overlay releases Escape", !Deck.EscapeRegistered);
            Deck.Preview(true); await Layout();
            foreach (var (width, height, count) in new[] { (440, 200, 2), (599, 200, 3), (600, 200, 3), (1100, 310, 4) })
            {
                var settings = savedSettings.Copy(); settings.Width = width; settings.Height = height;
                var ids = new[] { ProviderId.Claude, ProviderId.Codex, ProviderId.Cursor, ProviderId.Antigravity }.Take(count).ToArray();
                settings.Apps = ids.Select(id => new AppEntry { Id = id }).ToList(); UpdateSettings(settings);
                Deck.OpenModelDetails(ProviderId.Claude, 30); foreach (var id in ids.Skip(1)) Deck.ToggleModelApp(id); await Layout();
                Capture(Deck.PanelVisual, Path.Combine(output, $"model-details-{width}x{height}.png"));
            }
            Click("按时间"); await Layout();
            var dateWindow = Windows.OfType<ModelDateWindow>().Single();
            Deck.PointerChanged(false); await Task.Delay(Settings.CloseDelay + 80);
            Check("date popup keeps the island open outside its bounds", Deck.IsExpanded && dateWindow.IsVisible);
            Capture((FrameworkElement)dateWindow.Content, Path.Combine(output, "model-date-picker.png"));
            dateWindow.ChooseDate(DateTime.Today.AddDays(-6)); dateWindow.ChooseDate(DateTime.Today.AddDays(-2)); dateWindow.ApplySelection(); await Layout();
            Check("calendar applies an inclusive date range", Deck.ModelRange == new UsageDateRange(DateTime.Today.AddDays(-6), DateTime.Today.AddDays(-2)));
            Capture(Deck.PanelVisual, Path.Combine(output, "model-details-custom-range.png"));
            Click("7 天"); Check("a quick period clears the custom range", Deck.ModelRange.End == DateTime.Today && Deck.ModelRange.Start == DateTime.Today.AddDays(-6));
            Click("按时间"); await Layout(); Windows.OfType<ModelDateWindow>().Single().CancelSelection();
            Check("cancelling the date popup preserves filters", Deck.ModelRange == UsageDateRange.Recent(7, DateTime.Today));
            Click("按时间"); await Layout();
            Deck.Activate(); await Layout();
            Check("deactivating the date popup closes it without reentering Close", !Windows.OfType<ModelDateWindow>().Any());
            // Exercise the real hover policy, not settings preview (which never auto-collapses).
            var hoverSettings = Settings.Copy(); hoverSettings.Width = 640; hoverSettings.Height = 140;
            hoverSettings.Animations = false; hoverSettings.CloseDelay = 2000;
            UpdateSettings(hoverSettings); Deck.Preview(false);
            foreach (bool apply in new[] { true, false })
            {
                foreach (var dismissal in new[] { "return", "outside", "escape" })
                {
                    Deck.SetExpanded(true, true); Click("按时间"); await Layout();
                    var calendar = Windows.OfType<ModelDateWindow>().Single();
                    var original = Deck.ModelRange;
                    calendar.ChooseDate(DateTime.Today.AddDays(apply ? -5 : -9)); calendar.ChooseDate(DateTime.Today.AddDays(apply ? -1 : -3));
                    calendar.UpdateLayout();
                    var action = Elements(calendar).OfType<Button>().Single(button => AutomationProperties.GetName(button) == (apply ? "应用日期筛选" : "取消日期筛选"));
                    var cursor = action.PointToScreen(new Point(action.ActualWidth / 2, action.ActualHeight / 2));
                    var outside = calendar.PointToScreen(new Point(calendar.ActualWidth + 20, calendar.ActualHeight + 20));
                    Deck.PointerChanged(false);
                    action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    // Supply screen coordinates to the same check used by the leave timer without moving the user's mouse.
                    Deck.CheckPointerExit(cursor); Deck.RenderUsage(); Deck.CheckPointerExit(cursor);
                    Check($"{(apply ? "apply" : "cancel")} keeps results expanded at the former calendar button ({dismissal})", Deck.IsExpanded && !calendar.IsVisible);
                    Check($"{(apply ? "apply" : "cancel")} preserves the correct date range ({dismissal})", Deck.ModelRange == (apply ? new UsageDateRange(DateTime.Today.AddDays(-5), DateTime.Today.AddDays(-1)) : original));
                    if (dismissal == "return")
                    {
                        Deck.PointerChanged(true); Deck.PointerChanged(false); Deck.CheckPointerExit(cursor);
                    }
                    else if (dismissal == "outside") Deck.CheckPointerExit(outside);
                    else Deck.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(Deck), 0, System.Windows.Input.Key.Escape)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    Check($"{dismissal} dismisses the island after {(apply ? "apply" : "cancel")}", !Deck.IsExpanded && !Deck.EscapeRegistered);
                }
            }
            Deck.Preview(true);
            Click("返回用量");
            var normal = Settings.Copy(); normal.Width = 760; normal.Height = 240; UpdateSettings(normal); await Layout();
            Click("查看 Codex 今日明细");
            Check("today's statistics row opens today's model details", Deck.ModelApps.SetEquals(new[] { ProviderId.Codex }) && Deck.ModelRange == UsageDateRange.Recent(1, DateTime.Today));
        }
        finally { Deck.ShowModelDetails(false); UpdateSettings(savedSettings); Deck.SetSnapshots(savedSnapshots); Deck.Preview(true); }
    }
}
