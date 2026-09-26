using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifySourcesAsync(string output, List<string> checks)
    {
        void Check(string name, bool result) => checks.Add((result ? "PASS " : "FAIL ") + name);
        var saved = Settings.Copy(); var snapshots = Deck.Snapshots;
        OpenSettings(); var window = _settingsWindow!;
        var size = new Size(window.Width, window.Height);
        T ById<T>(string id) where T : DependencyObject => SettingsElements(window.RootVisual).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
        Button Named(string name) => SettingsElements(window.RootVisual).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        void Click(Button button) { button.Focus(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        void Pick(Button selector, ProviderId provider)
        {
            Click(selector);
            var popup = (Popup)selector.Tag; popup.Child.UpdateLayout();
            var option = SettingsElements(popup.Child).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "选择 " + AppPresets.Name(provider));
            Click(option);
        }
        void Name(Guid id, string value)
        {
            var input = ById<TextBox>("display-name-" + id.ToString("N")); input.Focus(); input.Text = value;
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        void Source(Guid id, bool quota, ProviderId provider) => Pick(ById<Button>((quota ? "quota-" : "usage-") + id.ToString("N")), provider);
        Border Column(Guid id) => SettingsElements(Deck.PanelVisual).OfType<Border>().Single(b => b.Tag is Guid value && value == id);
        try
        {
            var defaults = new DeckSettings { Theme = SettingsTheme.Dark, Width = 950, Height = 240, Animations = false };
            UpdateSettings(defaults); window.RebuildTheme(); window.ShowPage(3); Deck.SetSnapshots(DemoData.Create()); Deck.Preview(true);
            await Task.Delay(80);
            var first = Settings.Apps[0].InstanceId;
            Click(Named("添加应用"));
            var repeat = Settings.Apps[^1].InstanceId;
            Check("adding an application creates a separate enabled Claude row", Settings.Apps.Count == 5 && repeat != first
                && Settings.Entry(repeat) is { QuotaSource: ProviderId.Claude, UsageSource: ProviderId.Claude, Enabled: true });
            FlushSettings(); window.ShowPage(0); window.ShowPage(3); window.UpdateLayout(); await Task.Delay(60);
            Check("new applications persist when leaving the settings page", Store.Load().Entry(repeat) is { QuotaSource: ProviderId.Claude, UsageSource: ProviderId.Claude });
            Capture(window.RootVisual, Path.Combine(output, "settings-add-default-claude.png"));
            Source(first, false, ProviderId.Codex);
            Source(repeat, false, ProviderId.Cursor);
            Check("statistics selection does not change quotas or the default name", Settings.Entry(first) is { QuotaSource: ProviderId.Claude, UsageSource: ProviderId.Codex, Name: "Claude" });
            Source(first, true, ProviderId.Antigravity);
            Check("automatic names follow the quota selection while statistics remain independent", Settings.Entry(first)!.Name == "Antigravity" && Settings.Entry(first)!.UsageSource == ProviderId.Codex);
            Name(first, "Claude 配额 · Codex 统计"); Source(first, true, ProviderId.Claude);
            Check("editing a name survives later quota changes", Settings.Entry(first)!.Name == "Claude 配额 · Codex 统计");
            Name(repeat, "Claude 配额 · Cursor 统计");
            window.UpdateLayout(); Deck.RenderUsage(); Deck.UpdateLayout();
            await Task.Delay(30);
            var firstColumn = Column(first);
            var footer = SettingsElements(firstColumn).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == $"usage-1-{first:N}");
            long codexTokens = Deck.Snapshots.Single(s => s.Id == ProviderId.Codex).Entries.Where(e => e.Time.LocalDateTime.Date == DateTime.Today).Sum(e => e.Total);
            var renderedQuota = SettingsElements(firstColumn).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Select(run => run.Text).ToArray();
            var renderedUsage = SettingsElements(footer).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check("mixed columns show the selected source's quota", renderedQuota.Contains("47%"));
            Check("mixed columns show the statistics application's token count", renderedUsage.Contains(UI.Number(codexTokens)));
            Click(footer);
            Check("statistics links open the selected statistics application's details", Deck.ShowingModelDetails && Deck.ModelApps.SetEquals([ProviderId.Codex]));
            Deck.ShowModelDetails(false);
            Click(SettingsElements(Column(repeat)).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == $"usage-1-{repeat:N}"));
            Check("custom dashboard footer links navigate to the correct statistics application", Deck.ModelApps.SetEquals([ProviderId.Cursor]));
            Deck.ShowModelDetails(false);
            window.MoveApp(4, 0);
            Check("drag reordering keeps repeated source rows distinct", Settings.Apps[0].InstanceId == repeat && Settings.Apps[1].InstanceId == first
                && Settings.Apps.Select(a => a.InstanceId).Distinct().Count() == 5 && Settings.Entry(first)!.UsageSource == ProviderId.Codex);
            var toggle = ById<CheckBox>("enabled-" + repeat.ToString("N")); toggle.IsChecked = false;
            Check("a repeated row can be hidden without hiding the other", !Settings.Entry(repeat)!.Enabled && Settings.Entry(first)!.Enabled
                && !SettingsElements(Deck.PanelVisual).OfType<Border>().Any(b => b.Tag is Guid id && id == repeat));
            toggle.IsChecked = true;
            FlushSettings(); var loaded = Store.Load();
            Check("source choices, custom names and row identities survive saving", loaded.Apps[0].InstanceId == repeat
                && loaded.Entry(first) is { QuotaSource: ProviderId.Claude, UsageSource: ProviderId.Codex, Name: "Claude 配额 · Codex 统计" });

            foreach (var theme in new[] { SettingsTheme.Dark, SettingsTheme.Light })
            {
                var next = Settings.Copy(); next.Theme = theme; UpdateSettings(next); window.RebuildTheme(); window.ShowPage(3);
                foreach (var width in new[] { size.Width, window.MinWidth })
                {
                    window.Width = width; await Task.Delay(60); window.UpdateLayout();
                    Capture(window.RootVisual, Path.Combine(output, $"settings-sources-{theme.ToString().ToLowerInvariant()}-{width:0}.png"));
                }
            }
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-mixed-sources.png"));
            Click(ById<Button>("remove-" + repeat.ToString("N")));
            Check("removing one repeated source row preserves its neighbour", Settings.Apps.Count == 4 && Settings.Entry(repeat) is null
                && Settings.Entry(first) is { UsageSource: ProviderId.Codex, Name: "Claude 配额 · Codex 统计" });
            Name(first, "");
            Named("添加应用").Focus();
            Check("clearing a custom name restores the current quota-source name", Settings.Entry(first)!.Name == "Claude" && ById<TextBox>("display-name-" + first.ToString("N")).Text == "Claude");

            for (int i = 0; i < 2; i++) Click(Named("添加应用"));
            Click(Named("添加应用"));
            int RowCount() => SettingsElements(window.RootVisual).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("quota-"));
            Check("the seventh application disables Add Application", Settings.Apps.Count == 7 && RowCount() == 7 && !Named("添加应用").IsEnabled);
            Click(Named("添加应用"));
            Check("repeated add events cannot create an eighth row", Settings.Apps.Count == 7 && RowCount() == 7);
            Click(ById<Button>("remove-" + Settings.Apps[^1].InstanceId.ToString("N")));
            Check("removing an application frees a slot", RowCount() == 6 && Named("添加应用").IsEnabled);
            Click(Named("添加应用"));
            var seventh = Settings.Apps[^1].InstanceId;
            ById<CheckBox>("enabled-" + seventh.ToString("N")).IsChecked = false;
            Check("hiding a configured application does not free a slot", !Named("添加应用").IsEnabled && Settings.Apps.Count == 7);
            window.UpdateLayout(); Named("添加应用").BringIntoView(); await Task.Delay(80);
            Capture(window.RootVisual, Path.Combine(output, "settings-seven-application-limit.png"));

            var groups = Settings.Copy(); groups.Apps = [new() { QuotaSource = ProviderId.Antigravity, UsageSource = ProviderId.Codex }, new() { QuotaSource = ProviderId.Antigravity, UsageSource = ProviderId.Cursor }];
            UpdateSettings(groups); Deck.SetSnapshots(DemoData.Create());
            Deck.SelectQuotaGroup(groups.Apps[0].InstanceId, "Claude");
            bool Selected(Guid id, string group) => SettingsElements(Column(id)).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Antigravity " + group).Tag is "Selected";
            Check("repeated quota sources keep independent quota-group selection", Selected(groups.Apps[0].InstanceId, "Claude") && Selected(groups.Apps[1].InstanceId, "Gemini"));

            var many = Settings.Copy(); many.Width = 1200;
            many.Apps = Enumerable.Range(1, 9).Select(i => new AppEntry { QuotaSource = ProviderId.Claude, UsageSource = ProviderId.Codex, DisplayName = "用量 " + i }).ToList();
            UpdateSettings(many); Deck.ShowModelDetails(false);
            await Task.Delay(80); Deck.UpdateLayout();
            int Columns() => SettingsElements(Deck.PanelVisual).OfType<Border>().Count(b => b.Tag is Guid);
            Check("oversized settings retain only seven applications on the dashboard", Settings.Apps.Count == 7 && Columns() == 7 && Settings.Apps[^1].Name == "用量 7");
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-seven-applications.png"));
            Deck.Preview(false); Deck.SetExpanded(false, true); Deck.UpdateLayout();
            Check("seven applications remain visible in the compact notch summary", Deck.CurrentCompactLayout?.VisibleRings == 7);
        }
        finally
        {
            UpdateSettings(saved); Deck.SetSnapshots(snapshots); Deck.ShowModelDetails(false); Deck.Preview(false);
            window.Width = size.Width; window.Height = size.Height; window.RebuildTheme(); window.ShowPage(3);
        }
    }
}
