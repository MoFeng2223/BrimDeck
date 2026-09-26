using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyPageSwitchFramesAsync(string output, List<string> checks)
    {
        var saved = Settings.Copy();
        var hoverZone = (UIElement)Deck.FindName("HoverZone");
        bool hitTestVisible = hoverZone.IsHitTestVisible;
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        void Click(DeckPage page) => ((Button)Deck.FindName(page == DeckPage.Music ? "MusicPageButton" : "AIUsageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Switch(string name, DeckPage page, bool reverse = false)
        {
            Click(page);
            Capture(Deck.PanelVisual, Path.Combine(output, name + "-start.png"));
            await Task.Delay(Settings.AnimationDuration / 3);
            Capture(Deck.PanelVisual, Path.Combine(output, name + "-middle.png"));
            if (reverse)
            {
                Click(page == DeckPage.Music ? DeckPage.Usage : DeckPage.Music);
                await Task.Delay(Settings.AnimationDuration / 8);
                Click(page);
            }
            await Task.Delay(Settings.AnimationDuration + 80);
            Capture(Deck.PanelVisual, Path.Combine(output, name + "-end.png"));
            Check(name + " settles on the latest page", Deck.IsMusicPage == (page == DeckPage.Music));
        }
        try
        {
            hoverZone.IsHitTestVisible = false;
            foreach (bool equalSize in new[] { true, false })
            {
                UpdateSettings(new DeckSettings
                {
                    Width = 880, Height = 330, MusicWidth = equalSize ? 880 : 440, MusicHeight = equalSize ? 330 : 140,
                    MusicPage = true, DefaultPage = DefaultDeckPage.Last, Animations = true, AnimationDuration = 360
                });
                Deck.SetSnapshots(DemoData.Create()); Deck.Preview(false); Deck.TestContext(ScreenContext.Desktop);
                Deck.SetExpanded(false, true); Deck.SelectPage(DeckPage.Usage, remember: false);
                Deck.PointerChanged(true); Deck.SetExpanded(true);
                await Task.Delay(460);
                string prefix = equalSize ? "same-size" : "different-size";
                await Switch(prefix + "-music", DeckPage.Music);
                await Switch(prefix + "-usage", DeckPage.Usage);
                if (!equalSize)
                {
                    await Switch("rapid-music", DeckPage.Music, reverse: true);
                    await Switch("rapid-usage", DeckPage.Usage, reverse: true);
                    var instant = Settings.Copy(); instant.Animations = false; UpdateSettings(instant);
                    await Switch("instant-music", DeckPage.Music);
                    await Switch("instant-usage", DeckPage.Usage);
                }
            }
        }
        finally
        {
            Deck.Preview(false); UpdateSettings(saved); hoverZone.IsHitTestVisible = hitTestVisible;
        }
    }

    private async Task VerifyPageSwitchHoverAsync(string output, List<string> checks)
    {
        var saved = Settings.Copy();
        var hoverZone = (UIElement)Deck.FindName("HoverZone");
        bool hitTestVisible = hoverZone.IsHitTestVisible;
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        void OpenUsage()
        {
            Deck.SetExpanded(false, true); Deck.SelectPage(DeckPage.Usage, remember: false);
            Deck.PointerChanged(true); Deck.SetExpanded(true, true);
        }
        void ClickPage(string name) => ((Button)Deck.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task WaitForCollapse(int timeout = 4000)
        {
            var wait = Stopwatch.StartNew();
            while (Deck.IsExpanded && wait.ElapsedMilliseconds < timeout) await Task.Delay(20);
        }
        try
        {
            // This suite supplies pointer enter/leave events explicitly. Keep the physical cursor
            // from reentering the animated surface and changing the scenario during its delays.
            hoverZone.IsHitTestVisible = false;
            var settings = new DeckSettings
            {
                Width = 1100, Height = 310, MusicWidth = 520, MusicHeight = 200,
                MusicPage = true, DefaultPage = DefaultDeckPage.Last,
                Animations = true, AnimationDuration = 180, OpenDelay = 50, CloseDelay = 300
            };
            UpdateSettings(settings); Deck.SetSnapshots(DemoData.Create());
            Deck.Preview(false); Deck.TestContext(ScreenContext.Desktop);

            OpenUsage(); Deck.PointerChanged(false); await WaitForCollapse(900);
            Check("ordinary pointer exit still uses the configured close delay", !Deck.IsExpanded);

            OpenUsage();
            Deck.PointerChanged(false); // A pending exit must be cancelled by switching pages.
            var switched = Stopwatch.StartNew(); ClickPage("MusicPageButton"); Deck.PointerChanged(false);
            Deck.CheckPointerExit(new Point(-10000, -10000));
            Check("page switch cancels pending automatic collapse", Deck.IsExpanded);
            await Task.Delay(650); Deck.UpdateLayout();
            Check("smaller music page remains expanded after its resize animation", Deck.IsExpanded && Deck.IsMusicPage);
            Capture(Deck.PanelVisual, Path.Combine(output, "music-during-page-switch-hold.png"));
            await WaitForCollapse();
            Check("outside pointer collapses after one second plus the configured close delay", !Deck.IsExpanded && switched.ElapsedMilliseconds is >= 1300 and < 2000);

            OpenUsage(); ClickPage("MusicPageButton"); Deck.PointerChanged(false); await Task.Delay(650);
            switched.Restart(); ClickPage("AIUsageButton"); Deck.PointerChanged(false); await Task.Delay(750);
            Check("switching back to AI restarts the full one-second hold", Deck.IsExpanded && !Deck.IsMusicPage);
            await WaitForCollapse();
            Check("latest page switch determines when automatic collapse resumes", !Deck.IsExpanded && switched.ElapsedMilliseconds is >= 1300 and < 2000);

            OpenUsage(); ClickPage("MusicPageButton"); Deck.PointerChanged(false); await Task.Delay(100); Deck.PointerChanged(true);
            await Task.Delay(1350);
            Check("pointer reentry during the hold keeps the panel expanded afterwards", Deck.IsExpanded);
            Deck.PointerChanged(false); await WaitForCollapse(900);
            Check("leaving after the hold uses the normal close delay", !Deck.IsExpanded);

            OpenUsage(); ClickPage("AIUsageButton"); Deck.PointerChanged(false); await WaitForCollapse(900);
            Check("clicking the already selected page does not add a hold", !Deck.IsExpanded);

            OpenUsage(); ClickPage("MusicPageButton"); Deck.SetExpanded(false, true);
            Check("explicit dismissal remains immediate during a page switch", !Deck.IsExpanded);
            Deck.PointerChanged(true); Deck.SetExpanded(true, true); Deck.PointerChanged(false); await WaitForCollapse(900);
            Check("dismissal clears the hold before the next expansion", !Deck.IsExpanded);

            Deck.Preview(true); ClickPage("AIUsageButton"); Deck.PointerChanged(false); await Task.Delay(1400);
            Check("settings preview stays expanded across page switches", Deck.IsExpanded);
        }
        finally { Deck.Preview(false); UpdateSettings(saved); hoverZone.IsHitTestVisible = hitTestVisible; }
    }
}
