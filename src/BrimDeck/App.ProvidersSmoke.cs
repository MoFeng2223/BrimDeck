using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyProvidersAsync(string output, List<string> checks)
    {
        void Check(string name, bool ok) => checks.Add((ok ? "PASS " : "FAIL ") + name);
        var saved = Settings.Copy(); var snapshots = Deck.Snapshots;
        OpenSettings(); var window = _settingsWindow!;
        T ById<T>(string prefix, Guid id) where T : DependencyObject => SettingsElements(window.RootVisual).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == prefix + "-" + id.ToString("N"));
        void Click(Button button) { button.Focus(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        IEnumerable<string> Texts(DependencyObject element) => SettingsElements(element).OfType<TextBlock>().Select(t => t.Text);
        try
        {
            var setup = new DeckSettings { Width = 940, Height = 250, Animations = false, Theme = SettingsTheme.Light, Apps = [
                new() { QuotaSource = ProviderId.Claude },
                new() { QuotaSource = ProviderId.NewApi, UsageSource = ProviderId.Codex, DisplayName = "我的中转", Site = "https://example.test", ThemeColor = "#98DBB0" },
                new() { QuotaSource = ProviderId.GlmChina, UsageSource = ProviderId.Claude, ThemeColor = "#9CB9FF" },
                new() { QuotaSource = ProviderId.Sub2Api, UsageSource = ProviderId.Cursor, DisplayName = "中转站 B", Site = "https://sub.example.test", ThemeColor = "#B9A8FF" }
            ] };
            UpdateSettings(setup); window.RebuildTheme(); window.ShowPage(3); window.UpdateLayout(); await Task.Delay(40);
            var remote = Settings.Apps[1]; var glm = Settings.Apps[2]; var sub = Settings.Apps[3];
            window.ToggleSourceRow(remote.InstanceId); window.UpdateLayout();
            var site = ById<TextBox>("site", remote.InstanceId);
            Check("expanding a provider reveals its site, key and test controls", site.IsVisible && ById<PasswordBox>("secret", remote.InstanceId).IsVisible && ById<Button>("test-source", remote.InstanceId).IsVisible);
            var password = ById<PasswordBox>("secret", remote.InstanceId); password.Focus(); password.Password = "fixture-secret-not-for-a-provider"; site.Focus();
            await Task.Delay(30);
            FlushSettings();
            Check("provider credentials round trip under DPAPI and never enter settings.json", Secrets.Read(remote.InstanceId) == "fixture-secret-not-for-a-provider" && !File.ReadAllText(Store.FilePath).Contains("fixture-secret") && !File.ReadAllText(Path.Combine(Store.DirectoryPath, "secrets.dat")).Contains("fixture-secret"));
            Check("saved credentials are cleared from editor controls", password.Password.Length == 0);
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-newapi-light.png"));
            var reveal = SettingsElements(window.RootVisual).OfType<Button>().First(b => AutomationProperties.GetName(b) == "显示密钥");
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            var plain = ById<TextBox>("secret-text", remote.InstanceId);
            Check("revealing a saved key shows it and swaps the struck-through eye for the open eye", AutomationProperties.GetName(reveal) == "隐藏密钥" && plain.IsVisible && plain.Text == "fixture-secret-not-for-a-provider");
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-newapi-revealed.png"));
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Check("hiding an unedited key puts the saved hint back", password.IsVisible && password.Password.Length == 0);
            window.Activate(); password.Focus(); password.Password = "typed-but-not-saved";
            bool focused = password.IsKeyboardFocused;
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            bool shownWhileTyping = plain.Text == "typed-but-not-saved" && plain.IsKeyboardFocused == focused;
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Check("toggling the eye while typing keeps the typed key and saves nothing", focused && shownWhileTyping && password.Password == "typed-but-not-saved" &&
                Secrets.Read(remote.InstanceId) == "fixture-secret-not-for-a-provider");
            Check("the password box still offers the caret placement used when hiding a key",
                typeof(PasswordBox).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(int), typeof(int)]) is not null);
            password.Password = "fixture-secret-not-for-a-provider";
            window.Activate(); password.Focus();
            typeof(PasswordBox).GetMethod("Select", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [typeof(int), typeof(int)])!.Invoke(password, [3, 0]);
            foreach (var typed in new[] { "x", "y" })
                password.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(System.Windows.Input.InputManager.Current, password, typed)) { RoutedEvent = TextCompositionManager.TextInputEvent });
            Check("typing into the hidden key keeps the caret after each typed character", password.Password == "fixxyture-secret-not-for-a-provider" && ById<TextBox>("secret-text", remote.InstanceId).Text == password.Password);
            password.Password = "fixture-secret-not-for-a-provider";
            window.ToggleSourceRow(glm.InstanceId); window.UpdateLayout();
            Check("only one provider row expands and GLM has no site field", !site.IsVisible && ById<PasswordBox>("secret", glm.InstanceId).IsVisible && !SettingsElements(window.RootVisual).OfType<TextBox>().Any(e => AutomationProperties.GetAutomationId(e) == "site-" + glm.InstanceId.ToString("N")));
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-glm-light.png"));
            window.MoveApp(2, 0); window.UpdateLayout();
            Check("reordering preserves provider instance identity", Settings.Apps[0].InstanceId == glm.InstanceId);
            var custom = Settings.Copy(); var customEntry = custom.Entry(sub.InstanceId)!; customEntry.QuotaSource = ProviderId.Custom; customEntry.Script = ProviderScripts.Example;
            UpdateSettings(custom); window.ShowPage(3); window.ToggleSourceRow(sub.InstanceId); window.UpdateLayout();
            var editor = ById<ICSharpCode.AvalonEdit.TextEditor>("script", sub.InstanceId);
            Check("custom provider exposes an editable async JavaScript script", editor.IsVisible && editor.Text.Contains("async function"));
            Check("custom script is colored as JavaScript", editor.SyntaxHighlighting?.Name == "BrimDeckJavaScript" && editor.SyntaxHighlighting.GetNamedColor("Keyword") is not null);
            editor.BringIntoView(); window.UpdateLayout();
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-custom-light.png"));
            var dark = Settings.Copy(); dark.Theme = SettingsTheme.Dark; UpdateSettings(dark); window.RebuildTheme(); window.ShowPage(3); window.ToggleSourceRow(remote.InstanceId); window.UpdateLayout();
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-newapi-dark.png"));
            window.ToggleSourceRow(sub.InstanceId); window.UpdateLayout(); ById<ICSharpCode.AvalonEdit.TextEditor>("script", sub.InstanceId).BringIntoView(); window.UpdateLayout();
            Capture(window.RootVisual, Path.Combine(output, "settings-provider-custom-dark.png"));

            var panelSettings = setup.Copy(); panelSettings.Apps[1] = Settings.Entry(remote.InstanceId)!.Copy(); panelSettings.Height = 250;
            UpdateSettings(panelSettings);
            List<UsageMetric> mixed = [new(MetricKind.Percent, "5 小时") { Id = "five", Percent = 12, Window = 300, ResetAt = DateTimeOffset.Now.AddHours(3) },
                new(MetricKind.Percent, "每周") { Id = "week", Percent = 40, Window = 10080, ResetAt = DateTimeOffset.Now.AddDays(3) },
                new(MetricKind.Balance, "密钥余额") { Id = "balance", Amount = 12.4m, Total = 18, Currency = "USD", ExpiresAt = DateTimeOffset.Now.AddDays(30) }];
            var examples = DemoData.Create().ToList();
            examples.Single(s => s.Id == ProviderId.Claude).Quotas.Add(new("Fable 每周", 18, DateTimeOffset.Now.AddDays(3), 10080));
            examples.Add(new(ProviderId.NewApi) { Configurations = [Settings.Entry(remote.InstanceId)!.ConfigurationKey], Metrics = [mixed[2]], LiveQuota = true, Scope = "key" });
            examples.Add(new(ProviderId.GlmChina) { Configurations = [Settings.Entry(glm.InstanceId)!.ConfigurationKey], Metrics = [mixed[0], new(MetricKind.Count, "GLM-5.3-Flash") { Id = "start-plan", Used = 22426, Total = 300_000_000, Unit = "令牌", ExpiresAt = DateTimeOffset.Now.AddDays(3).AddHours(9) }, new(MetricKind.Count, "工具调用") { Id = "mcp", Used = 1860, Total = 2000, Unit = "次" }], LiveQuota = true, Scope = "plan" });
            examples.Add(new(ProviderId.Sub2Api) { Configurations = [Settings.Entry(sub.InstanceId)!.ConfigurationKey], Metrics = mixed, LiveQuota = true, Scope = "key" });
            foreach (var example in examples) example.QuotaTime ??= DateTimeOffset.Now;
            Deck.ShowModelDetails(false); Deck.SetSnapshots(examples); Deck.Preview(true); await Task.Delay(80);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-short.png"));
            Check("count quotas show exact amounts on hover", SettingsElements(Deck.PanelVisual).OfType<FrameworkElement>()
                .Count(e => e.ToolTip is string tip && tip == "已用 22,426 令牌 · 剩余 299,977,574 令牌\n总额 300,000,000 令牌") == 2);
            var subColumn = SettingsElements(Deck.PanelVisual).OfType<Border>().Single(b => b.Tag is Guid id && id == sub.InstanceId);
            void Tap(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
            Tap(subColumn); await Task.Delay(20);
            Check("paging exposes a balance after short and weekly windows", Texts(Deck.PanelVisual).Count(t => t == "密钥余额") == 2);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-next-page.png"));
            Border CurrentSubColumn() => SettingsElements(Deck.PanelVisual).OfType<Border>().Single(b => b.Tag is Guid id && id == sub.InstanceId);
            Tap(CurrentSubColumn());
            Check("clicking a quota column cycles its page", Texts(CurrentSubColumn()).Contains("5 小时") && !Texts(CurrentSubColumn()).Contains("密钥余额"));
            Tap(SettingsElements(CurrentSubColumn()).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == $"quota-title-{sub.InstanceId:N}"));
            Check("clicking a quota title cycles quotas without opening token details", Texts(CurrentSubColumn()).Contains("密钥余额") && !Deck.ShowingModelDetails);
            Tap(CurrentSubColumn());
            var tall = Settings.Copy(); tall.Height = 370; UpdateSettings(tall); await Task.Delay(40);
            Check("remote providers retain the selected statistics application's footer", Texts(Deck.PanelVisual).Count(t => t == "今日") == 4 && Texts(Deck.PanelVisual).Count(t => t == "7 天") == 4);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-tall.png"));
            Border CurrentClaudeColumn() => SettingsElements(Deck.PanelVisual).OfType<Border>().Single(b => b.Tag is Guid id && id == Settings.Apps[0].InstanceId);
            Tap(CurrentClaudeColumn());
            Check("clicking a tall panel reveals the third quota on the second page", Texts(CurrentClaudeColumn()).Contains("Fable 每周") && !Texts(CurrentClaudeColumn()).Contains("5 小时"));
            var resized = Settings.Copy(); resized.Height = 400; resized.Width = 1100; UpdateSettings(resized);
            Check("resizing preserves the selected quota page", Texts(CurrentClaudeColumn()).Contains("Fable 每周") && !Texts(CurrentClaudeColumn()).Contains("5 小时"));
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-tall-second-page.png"));
            Tap(CurrentClaudeColumn()); UpdateSettings(tall);
            examples.Last().IsStale = true; examples.Last().LiveQuota = false; examples.Last().StatusLabel = "读取超时"; examples.Last().Status = "读取超时，显示上次数据。"; Deck.SetSnapshots(examples);
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-stale.png"));
            var small = Settings.Copy(); small.Height = 170; UpdateSettings(small); await Task.Delay(80); Deck.UpdateLayout();
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-small.png"));
            UpdateSettings(tall); Deck.UpdateLayout();
            foreach (var days in new[] { 1, 7 })
            {
                Click(SettingsElements(Deck.PanelVisual).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == $"usage-{days}-{remote.InstanceId:N}"));
                Check($"statistics footer opens the selected application's {days}-day records", Deck.ShowingModelDetails && Deck.ModelApps.SetEquals([ProviderId.Codex]) && Deck.ModelRange == UsageDateRange.Recent(days, DateTime.Today));
                Deck.ShowModelDetails(false);
            }
            examples.Last().IsStale = false; examples.Last().LiveQuota = true;
            examples.Single(s => s.Id == ProviderId.NewApi).Metrics = [new(MetricKind.Text, "5 小时") { Id = "0", Percent = 93, Value = "$12.50", Suffix = "已用", Note = "无上限", ResetText = "2 时 13 分后" },
                new(MetricKind.Text, "余额") { Id = "1", Suffix = "可用", Note = "35% 已用" }];
            Deck.SetSnapshots(examples); await Task.Delay(40); Deck.UpdateLayout();
            var customColumn = SettingsElements(Deck.PanelVisual).OfType<Border>().Single(b => b.Tag is Guid id && id == remote.InstanceId);
            // The value and its suffix are separate runs of one text block.
            List<string> CustomTexts() => [.. Texts(customColumn), .. SettingsElements(customColumn).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()).Select(r => r.Text)];
            Check("custom items show title, value with suffix, note and reset text", new[] { "5 小时", "$12.50", " 已用", "无上限", "2 时 13 分后", "余额", "35% 已用" }.All(CustomTexts().Contains));
            Check("a custom item without a value shows neither number nor suffix", !CustomTexts().Any(t => t.Contains("可用")));
            Capture(Deck.PanelVisual, Path.Combine(output, "panel-providers-custom.png"));
            var removed = Settings.Copy(); removed.Apps.RemoveAll(e => e.InstanceId == remote.InstanceId); UpdateSettings(removed);
            Check("removing a provider row deletes its saved credential", Secrets.Read(remote.InstanceId).Length == 0);
            var ringGallery = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Black };
            foreach (var value in new[] { 0, 25, 50, 75, 100 })
            {
                var example = new StackPanel { Width = 72, Margin = new Thickness(8, 12, 8, 12) };
                example.Children.Add(UI.Ring(value, "#98DBB0", 48, 5));
                var caption = UI.Centered(value + "%", 12, UI.Primary); caption.Margin = new Thickness(0, 8, 0, 0); example.Children.Add(caption);
                ringGallery.Children.Add(example);
            }
            ringGallery.Measure(new Size(440, 100)); ringGallery.Arrange(new Rect(0, 0, 440, 100)); ringGallery.UpdateLayout();
            Capture(ringGallery, Path.Combine(output, "rings-counterclockwise.png"));
        }
        finally { UpdateSettings(saved); Deck.SetSnapshots(snapshots); Deck.Preview(false); window.RebuildTheme(); }
    }
}
