using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyTypographyAsync(string output, List<string> checks)
    {
        var saved = Settings.Copy(); var snapshots = Deck.Snapshots;
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        try
        {
            var settings = new DeckSettings { Width = 940, Height = 230, Animations = false, MusicPage = false };
            UpdateSettings(settings); Deck.ShowModelDetails(false);
            var examples = DemoData.Create().ToList();
            examples.Single(s => s.Id == ProviderId.Claude).Quotas.Add(new("Fable 每周", 18, DateTimeOffset.Now.AddDays(3), 10080));
            Deck.SetSnapshots(examples); Deck.Preview(true);
            foreach (var (width, height) in new[] { (640, 140), (940, 230), (1200, 400) })
            {
                var size = Settings.Copy(); size.Width = width; size.Height = height; UpdateSettings(size); await Task.Delay(60); Deck.UpdateLayout();
                Capture(Deck.PanelVisual, Path.Combine(output, $"typography-{width}x{height}.png"));
            }
            var animated = Settings.Copy(); animated.Animations = true; animated.Width = 940; animated.Height = 230; UpdateSettings(animated);
            OpenSettings(); var window = _settingsWindow!; window.ShowPage(2); window.UpdateLayout();
            // The AI dimensions appear before the separate music dimensions on this page.
            Slider Dimension(string name) => SettingsElements(window.RootVisual).OfType<Slider>().First(s => AutomationProperties.GetName(s) == name);
            Dimension("宽度").Value = 1100; Dimension("高度").Value = 310;
            Deck.UpdateLayout(); await Task.Delay(40);
            Check("dimension edits resize the panel content and frame together",
                Math.Abs(Deck.Island.Width - Deck.ExpandedContent.Width) < .01 && Math.Abs(Deck.Island.Height - Deck.ExpandedContent.Height) < .01);
            Capture(Deck.PanelVisual, Path.Combine(output, "typography-resize-preview.png"));
            window.ShowPage(0);
        }
        finally { UpdateSettings(saved); Deck.SetSnapshots(snapshots); Deck.Preview(false); }
    }
}
