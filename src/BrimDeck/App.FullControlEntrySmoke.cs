using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    // The "网易云音乐 · 完整控制" entry: alone in the toolbar without a source, otherwise one more menu row.
    private async Task VerifyFullControlEntryAsync(string output, List<string> checks, Action<ContextMenu, string> captureOpenMenu, Action<ContextMenu> isolate)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        var track = new MediaTrack { Id = "entry-qq", Source = "QQ 音乐", Title = "晴天", State = Core.MediaState.Playing };
        var button = Deck.MediaSourceButton;
        Deck.NeteaseInstalledForTest = true;
        try
        {
            foreach (var size in new[] { PanelSize.Compact, PanelSize.Standard })
            {
                var settings = Settings.Copy(); (settings.MusicWidth, settings.MusicHeight) = MusicSizes.For(size); settings.NeteaseFullControlEntry = true; UpdateSettings(settings);
                Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(null); Deck.UpdateLayout();
                var probe = new TextBlock { Text = BrimDeck.MainWindow.FullControlEntryLabel, FontFamily = Deck.MediaSourceLabel.FontFamily, FontSize = Deck.MediaSourceLabel.FontSize };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Check($"{size}: without a source the entry stands alone in the toolbar", button.IsVisible && button.IsEnabled
                    && Deck.MediaSourceLabel.Text == BrimDeck.MainWindow.FullControlEntryLabel && !Deck.MediaSourceChevron.IsVisible);
                Check($"{size}: the lone entry is not trimmed", Deck.MediaSourceLabel.ActualWidth + .5 >= probe.DesiredSize.Width);
                Capture(Deck.PanelVisual, Path.Combine(output, $"entry-{size}-alone.png"));

                Deck.SetMediaForTest(track); Deck.UpdateLayout();
                Check($"{size}: a single source shows the menu arrow", Deck.MediaSourceChevron.IsVisible && Deck.MediaSourceLabel.Text == track.Source);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); isolate(button.ContextMenu!); await Task.Delay(40);
                var menu = button.ContextMenu!; menu.UpdateLayout();
                var rows = menu.Items.OfType<MenuItem>().ToArray();
                Check($"{size}: the entry follows the sources below a separator", rows.Length == 2 && menu.Items.OfType<Separator>().Count() == 1
                    && menu.Items[^1] is MenuItem { Tag: "action", Header: TextBlock entryText } entry && entryText.Text == BrimDeck.MainWindow.FullControlEntryLabel && entry.IsEnabled
                    && menu.Items[^2] is Separator);
                captureOpenMenu(menu, Path.Combine(output, $"entry-{size}-menu.png"));
                menu.IsOpen = false; Deck.UpdateLayout();
            }
            var off = Settings.Copy(); off.NeteaseFullControlEntry = false; UpdateSettings(off);
            Deck.SetMediaForTest(track); Deck.UpdateLayout();
            Check("Switched off, a single source has no menu arrow", button.IsVisible && !Deck.MediaSourceChevron.IsVisible);
            Deck.SetMediaForTest(null); Deck.UpdateLayout();
            Check("Switched off, nothing shows without a source", !button.IsVisible);
            var missing = Settings.Copy(); missing.NeteaseFullControlEntry = true; UpdateSettings(missing); Deck.NeteaseInstalledForTest = false;
            Deck.SetMediaForTest(null); Deck.UpdateLayout();
            Check("Without Netease installed the entry is not offered", !button.IsVisible);
        }
        finally { Deck.NeteaseInstalledForTest = null; }
    }
}
