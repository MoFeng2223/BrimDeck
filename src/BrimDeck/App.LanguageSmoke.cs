using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;
using BrimDeck.Native;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class App
{
    // Switches the interface to English through the settings page, checks that every surface follows, and captures the
    // English layout of each page for review; then returns to Chinese and restores defaults.
    private async Task VerifyLanguageAsync(string output, List<string> checks)
    {
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        var saved = Settings.Copy(); var fixedLanguage = FixedLanguage; var snapshots = Deck.Snapshots;
        async Task Snap(FrameworkElement element, string name, Brush? background = null)
        { await Task.Delay(80); element.UpdateLayout(); Capture(element, Path.Combine(output, name + ".png"), background); }
        try
        {
            FixedLanguage = null;
            var start = Settings.Copy(); start.Language = Loc.Chinese; start.Animations = false; UpdateSettings(start);
            OpenSettings(); await Task.Delay(80);
            var window = _settingsWindow!; window.ShowPage(0); window.UpdateLayout();
            ComboBox LanguageList() => SettingsElements(_settingsWindow!.RootVisual).OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "language");
            Check("the language list offers Simplified Chinese and English", LanguageList().Items.Cast<string>().SequenceEqual(["简体中文", "English"]) && LanguageList().SelectedIndex == 0);
            LanguageList().SelectedIndex = 1; await Task.Delay(80); window = _settingsWindow!; window.UpdateLayout();
            Check("choosing English saves it and rebuilds the settings window in English",
                Settings.Language == Loc.English && Loc.IsEnglish && window.Title == "BrimDeck Settings" && LanguageList().SelectedIndex == 1);
            Check("the panel and tray menu follow the language", AutomationProperties.GetName(Deck.SettingsButton) == "Settings"
                && TrayIcon.CreateMenu(() => { }, () => { }).Items.OfType<MenuItem>().First().Header as string == "Settings");

            // Every settings page in full, including an expanded custom quota source.
            var withCustom = Settings.Copy(); withCustom.Apps.Add(new AppEntry { QuotaSource = ProviderId.Custom, UsageSource = ProviderId.Claude, Script = ProviderScripts.Example });
            UpdateSettings(withCustom);
            foreach (var id in new[] { "anthropic/claude-sonnet-example", "openai/gpt-example-long-model-name" })
                Prices.TrySetManual(id, new(.000003m, .000015m, .0000003m, .00000375m, null), false, out _);
            var surface = UI.Brush(window.IsDarkTheme ? "#202023" : "#F7F7F8");
            for (int page = 0; page < 8; page++)
            {
                window.ShowPage(page);
                if (page == 3) window.ToggleSourceRow(Settings.Apps[^1].InstanceId);
                await Snap(window.RootVisual, $"en-settings-{page}");
                await Snap(window.PageVisual, $"en-settings-{page}-full", surface);
            }
            window.ShowPage(4); var editor = window.CreatePriceEditor(null); editor.Show(); await Snap((FrameworkElement)editor.Content, "en-price-editor"); editor.Close();
            window.MinWidth = 0; window.Width = 840; window.Height = 640; window.ShowPage(3); await Snap(window.RootVisual, "en-settings-3-narrow");
            // The same width with the four built-in applications only, which leaves no scroll bar on a tall window.
            var fourApps = Settings.Copy(); fourApps.Apps.RemoveAll(app => app.QuotaSource == ProviderId.Custom); UpdateSettings(fourApps);
            window.Height = 960; window.ShowPage(3); await Snap(window.RootVisual, "en-settings-3-narrow-four");
            window.Height = 640;
            window.ShowPage(4); await Snap(window.RootVisual, "en-settings-4-narrow");
            var plain = saved.Copy(); plain.Language = Loc.English; UpdateSettings(plain);

            // The panel at the sizes the typography tiers change at, with demo data worded in English.
            Deck.SetSnapshots(DemoData.Create()); Deck.ShowModelDetails(false); Deck.Preview(true);
            foreach (var (width, height, apps) in new[] { (440, 140, 2), (640, 140, 4), (760, 240, 4), (1100, 310, 4), (520, 200, 2) })
            {
                var size = new DeckSettings { Language = Loc.English, Width = width, Height = height, Animations = false };
                foreach (var app in size.Apps) app.Enabled = apps == 4 || app.QuotaSource is ProviderId.Claude or ProviderId.Codex;
                UpdateSettings(size); Deck.SelectPage(DeckPage.Usage); await Snap(Deck.PanelVisual, $"en-panel-{width}x{height}-{apps}");
            }
            foreach (var width in new[] { 640, 1100 })
            {
                var size = new DeckSettings { Language = Loc.English, Width = width, Height = 240, Animations = false };
                foreach (var app in size.Apps) app.Enabled = true;
                UpdateSettings(size); Deck.OpenModelDetails(ProviderId.Claude, 1); await Snap(Deck.PanelVisual, $"en-models-{width}");
                Deck.ShowModelDetails(false);
            }
            var dates = new ModelDateWindow(new UsageDateRange(DateTime.Today.AddDays(-6), DateTime.Today)); dates.Show();
            await Snap((FrameworkElement)dates.Content, "en-date-window"); dates.Close();

            // The collapsed notch and its threshold notice.
            var notch = new DeckSettings { Language = Loc.English, Animations = false, MusicPage = false };
            UpdateSettings(notch); Deck.Preview(false); Deck.TestContext(ScreenContext.Desktop); Deck.SetExpanded(false, true);
            Deck.ShowAlert(Settings.Apps[0], UI.QuotaLabel(new Quota("每周额度", 88, null)), 88, DateTimeOffset.Now.AddDays(3)); await Task.Delay(420);
            Capture(Deck.SurfaceVisual, Path.Combine(output, "en-compact-alert.png"), UI.Brush("#DDE7EF"),
                new Rect(Deck.PanelVisual.TransformToAncestor(Deck.SurfaceVisual).Transform(new Point()).X, 0, Deck.PanelVisual.ActualWidth, Deck.PanelVisual.ActualHeight));
            Deck.ClearAlert();

            // The music page: a normal track, NetEase without SMTC, and the full-control entry alone.
            var music = new DeckSettings { Language = Loc.English, Animations = false, MusicPage = true, LyricsEnabled = true, NeteaseFullControlEntry = true };
            UpdateSettings(music); Deck.Preview(true); Deck.SelectPage(DeckPage.Music);
            var art = new DrawingVisual(); using (var dc = art.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(25, 56, 70), Color.FromRgb(111, 193, 174), 45), null, new Rect(0, 0, 256, 256));
            var cover = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); cover.Render(art); cover.Freeze();
            var song = new MediaTrack { Id = "english-a", Source = "网易云音乐", Title = "Blue in Green", Artist = "Miles Davis", Album = "Kind of Blue", State = MediaState.Playing,
                End = TimeSpan.FromSeconds(337), ReportedPosition = TimeSpan.FromSeconds(102), PositionAt = DateTimeOffset.UtcNow, CanToggle = true, CanNext = true, CanPrevious = true,
                CanSeek = true, CanRepeat = true, CanShuffle = true, Shuffle = true };
            var lyric = Lyrics.Parse("[01:38]First line of the lyric\n[01:42]The line playing now\n[01:46]The line that follows", null, 337, "test");
            Deck.NeteaseInstalledForTest = true;
            foreach (var size in Enum.GetValues<PanelSize>())
            {
                var next = Settings.Copy(); (next.MusicWidth, next.MusicHeight) = MusicSizes.For(size); UpdateSettings(next); Deck.SelectPage(DeckPage.Music);
                Deck.SetMediaForTest(song with { PositionAt = DateTimeOffset.UtcNow }, cover, lyric); await Snap(Deck.PanelVisual, $"en-music-{size}");
                Deck.SetMediaForTest(song with { IsNeteaseLog = true, CanToggle = false, CanNext = false, CanPrevious = false, CanSeek = false, CanRepeat = false, CanShuffle = false, PositionAt = DateTimeOffset.UtcNow }, cover);
                await Snap(Deck.PanelVisual, $"en-music-{size}-smtc");
            }
            Deck.SetMediaForTest(null); await Snap(Deck.PanelVisual, "en-music-empty");
            Check("the lone full-control entry is worded in English", Deck.MediaSourceLabel.Text == "NetEase full control");
            Deck.SetMediaForTest(song, cover, lyric);
            Check("player names are shown in English", Deck.MediaSourceLabel.Text == "NetEase Cloud Music");
            Deck.NeteaseInstalledForTest = null;
            foreach (var action in Enum.GetValues<NeteaseControlAction>())
            {
                var dialog = new NeteaseControlWindow(SettingsTheme.Dark, action); dialog.Show();
                await Snap((FrameworkElement)dialog.Content, $"en-netease-{action.ToString().ToLowerInvariant()}"); dialog.Close();
            }
            var tray = TrayIcon.CreateMenu(() => { }, () => { });
            tray.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint; tray.HorizontalOffset = 200; tray.VerticalOffset = 200;
            tray.IsOpen = true; await Task.Delay(150); tray.UpdateLayout(); Capture(tray, Path.Combine(output, "en-tray-menu.png")); tray.IsOpen = false;

            // Back to Chinese through the list, then "恢复默认" on the first page takes the Windows display language.
            var back = Settings.Copy(); back.Language = Loc.English; UpdateSettings(back);
            OpenSettings(); window = _settingsWindow!; window.ShowPage(0); window.UpdateLayout();
            LanguageList().SelectedIndex = 0; await Task.Delay(80); window = _settingsWindow!;
            Check("choosing Simplified Chinese switches back", Settings.Language == Loc.Chinese && !Loc.IsEnglish && window.Title == "BrimDeck 设置"
                && AutomationProperties.GetName(Deck.SettingsButton) == "设置");
            var english = Settings.Copy(); english.Language = Loc.English; UpdateSettings(english); window.RebuildTheme(); window.ShowPage(0); window.UpdateLayout();
            SettingsElements(window.RootVisual).OfType<Button>().Single(b => b.Content as string == "Restore defaults").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            Check("restoring the defaults of the first page takes the Windows display language", Settings.Language == Loc.SystemLanguage() && Loc.Language == Loc.SystemLanguage());
        }
        finally
        {
            Deck.NeteaseInstalledForTest = null; FixedLanguage = fixedLanguage;
            UpdateSettings(saved); Deck.SetSnapshots(snapshots); Deck.Preview(false); _settingsWindow?.RebuildTheme();
        }
    }
}
