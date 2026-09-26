using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App
{
    private async Task VerifySourceMenuAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        var settings = Settings.Copy(); settings.MusicPage = true; settings.Animations = false;
        settings.OpenDelay = 0; settings.CloseDelay = 0;
        settings.Maximized = settings.Borderless = settings.Exclusive = WindowBehavior.Normal;
        UpdateSettings(settings); Deck.TestContext(ScreenContext.Desktop); Deck.SetSnapshots(DemoData.Create());
        var first = new MediaTrack { Id = "source-one", Source = "网易云音乐", Title = "第一首", State = Core.MediaState.Paused };
        var second = first with { Id = "source-two", Source = "QQ 音乐", Title = "第二首" };
        var watch = (PromptTimer)typeof(MainWindow).GetField("_sourceMenuWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Deck)!;
        var leave = (PromptTimer)typeof(MainWindow).GetField("_leave", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Deck)!;
        ContextMenu Menu() => Deck.MediaSourceButton.ContextMenu!;
        Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        Point Center(Rect rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        Point Outside() { var panel = Bounds(Deck.PanelVisual); var popup = Bounds(Menu()); return new(Math.Max(panel.Right, popup.Right) + 100, Math.Max(panel.Bottom, popup.Bottom) + 100); }
        async Task Open()
        {
            Deck.Preview(true); Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(first, tracks: [first, second]); Deck.UpdateLayout();
            Deck.MediaSourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30); Menu().UpdateLayout();
            Deck.ResumeAfterPreview(); watch.Stop(); leave.Stop();
        }
        await Open();
        var now = DateTimeOffset.UtcNow;
        var anchor = Bounds(Deck.MediaSourceButton); var menu = Bounds(Menu());
        Deck.CheckSourceMenuPointer(Center(anchor), now: now);
        Deck.CheckSourceMenuPointer(Center(anchor), now: now.AddSeconds(1));
        Check("Source menu stays open at its trigger", Menu().IsOpen && Deck.IsExpanded);
        var bridge = new Point(anchor.Left + anchor.Width / 2, (anchor.Bottom + menu.Top) / 2);
        Deck.CheckSourceMenuPointer(bridge, now: now.AddSeconds(2));
        Deck.CheckSourceMenuPointer(bridge, now: now.AddSeconds(3));
        Check("Crossing the trigger-to-menu gap does not dismiss either surface", Menu().IsOpen && Deck.IsExpanded);
        Deck.CheckSourceMenuPointer(Center(menu), now: now.AddSeconds(4));
        Deck.CheckSourceMenuPointer(Center(menu), now: now.AddSeconds(5));
        Check("Hovering the separate popup keeps the panel expanded", Menu().IsOpen && Deck.IsExpanded);
        var outside = Outside();
        Deck.CheckSourceMenuPointer(outside, now: now.AddSeconds(6));
        Deck.CheckSourceMenuPointer(outside, now: now.AddMilliseconds(6100));
        Check("Brief pointer movement outside receives a return grace period", Menu().IsOpen && Deck.IsExpanded);
        Deck.CheckSourceMenuPointer(outside, now: now.AddMilliseconds(6600));
        Check("Leaving both surfaces closes the menu and collapses the panel", !Menu().IsOpen && !Deck.IsExpanded);

        await Open(); now = DateTimeOffset.UtcNow;
        var panel = Bounds(Deck.PanelVisual); var insidePanel = new Point(panel.Left + 12, panel.Bottom - 20);
        Deck.CheckSourceMenuPointer(insidePanel, now: now);
        Deck.CheckSourceMenuPointer(insidePanel, now: now.AddMilliseconds(300));
        Check("Moving to another part of the panel closes only the source menu", !Menu().IsOpen && Deck.IsExpanded);

        await Open(); Deck.CheckSourceMenuPointer(Outside(), pressed: true);
        Check("An outside press closes the popup and panel immediately", !Menu().IsOpen && !Deck.IsExpanded);
        await Open(); panel = Bounds(Deck.PanelVisual); insidePanel = new Point(panel.Left + 12, panel.Bottom - 20);
        Deck.CheckSourceMenuPointer(insidePanel, pressed: true);
        Check("A press elsewhere inside the panel closes only the source menu", !Menu().IsOpen && Deck.IsExpanded);
        await Open(); Deck.CheckSourceMenuPointer(Center(Bounds(Menu())), pressed: true);
        Check("Presses inside the popup remain available to source selection", Menu().IsOpen && Deck.IsExpanded);
        Deck.SetExpanded(false);
        Check("Every panel collapse also closes its source popup", !Menu().IsOpen && !Deck.IsExpanded);
        await Open(); Deck.SelectPage(DeckPage.Usage);
        Check("Changing pages closes the source popup", !Menu().IsOpen);
        await Open(); Deck.MediaSourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("Pressing the source trigger again dismisses its menu", !Menu().IsOpen);
        await Open(); Deck.Preview(true); watch.Stop(); now = DateTimeOffset.UtcNow; outside = Outside();
        Deck.CheckSourceMenuPointer(outside, now: now); Deck.CheckSourceMenuPointer(outside, now: now.AddMilliseconds(300));
        Check("Leaving a menu during settings preview keeps the forced panel preview", !Menu().IsOpen && Deck.IsExpanded);
        await VerifySourceMenuDesignAsync(output, checks);
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);

        if (Environment.GetCommandLineArgs().Contains("--source-menu-interactive"))
        {
            Deck.Title = "BrimDeck source menu check"; Deck.ShowInTaskbar = true;
            Deck.Preview(true); Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(first, tracks: [first, second]);
            Deck.MediaSourceButton.Click += (_, _) => Deck.ResumeAfterPreview();
            var external = new Window { Title = "BrimDeck outside test", Width = 320, Height = 160, Left = 40, Top = 420,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = new TextBlock { Text = "Outside-click verification", Margin = new Thickness(24) } };
            external.Show(); Deck.Activate();
            var trace = new List<string>();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            try
            {
                while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTimeOffset.UtcNow < deadline)
                {
                    var cursor = WindowsHost.Cursor();
                    var state = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, expanded = Deck.IsExpanded,
                        menu = Deck.MediaSourceButton.ContextMenu?.IsOpen == true, watcher = watch.IsEnabled, x = cursor.X, y = cursor.Y });
                    File.WriteAllText(Path.Combine(output, "state.json"), state); trace.Add(state);
                    await Task.Delay(80);
                }
            }
            finally { external.Close(); File.WriteAllLines(Path.Combine(output, "interaction-trace.jsonl"), trace); }
        }
        Deck.Preview(false);
    }

    private async Task VerifySourceMenuDesignAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        void CaptureOpenMenu(ContextMenu menu, string file)
        {
            var panel = Deck.PanelVisual; var dpi = VisualTreeHelper.GetDpi(panel);
            var origin = panel.PointFromScreen(menu.PointToScreen(new Point()));
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                dc.DrawRectangle(new VisualBrush(menu), null, new Rect(origin, new Size(menu.ActualWidth, menu.ActualHeight)));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(panel.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file); png.Save(stream);
        }
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var watch = (PromptTimer)typeof(MainWindow).GetField("_sourceMenuWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Deck)!;
        var pressField = typeof(MainWindow).GetField("_sourceMenuPress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void IsolateVisualCapture(ContextMenu menu)
        {
            void StopInput() { watch.Stop(); (pressField.GetValue(Deck) as IDisposable)?.Dispose(); pressField.SetValue(Deck, null); }
            menu.StaysOpen = true; menu.Opened += (_, _) => StopInput(); StopInput();
        }
        var first = new MediaTrack { Id = "design-qq", Source = "QQ 音乐", Title = "热爱105℃的你", State = Core.MediaState.Playing };
        var second = first with { Id = "design-netease", Source = "网易云音乐", Title = "晴天", State = Core.MediaState.Paused };
        var third = second with { Id = "design-spotify", Source = "Spotify", Title = "Blinding Lights — a long title that should be trimmed" };
        var button = Deck.MediaSourceButton;
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var settings = Settings.Copy(); (settings.MusicWidth, settings.MusicHeight) = MusicSizes.For(size); UpdateSettings(settings);
            Deck.Preview(true); Deck.SelectPage(DeckPage.Music); Deck.SetMediaForTest(first, tracks: [first, second, third]); Deck.UpdateLayout();
            button.SetValue(hoverKey, false); Deck.UpdateLayout();
            Capture(Deck.PanelVisual, Path.Combine(output, $"source-{size}-default.png"));
            button.SetValue(hoverKey, true); Deck.UpdateLayout();
            Capture(Deck.PanelVisual, Path.Combine(output, $"source-{size}-hover.png"));
            button.SetValue(hoverKey, false);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            IsolateVisualCapture(button.ContextMenu!);
            await Task.Delay(40); watch.Stop(); Deck.UpdateLayout(); var menu = button.ContextMenu!; menu.UpdateLayout();
            var items = menu.Items.Cast<MenuItem>().ToArray();
            CaptureOpenMenu(menu, Path.Combine(output, $"source-{size}-open.png"));
            Capture(menu, Path.Combine(output, $"source-{size}-menu.png"), Brushes.Black);
            if (size == PanelSize.Standard)
            {
                Check("Menu lists every source and checks only the current one", items.Select(item => item.IsChecked).SequenceEqual([true, false, false]));
                var highlightKey = (DependencyPropertyKey)typeof(MenuItem).GetField("IsHighlightedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Button Pin(MenuItem item) => (Button)item.Template.FindName("PinButton", item);
                var currentPin = Pin(items[0]); var otherPin = Pin(items[1]);
                items[1].SetValue(highlightKey, true); menu.UpdateLayout();
                CaptureOpenMenu(menu, Path.Combine(output, "source-pin-hover-row.png"));
                items[1].SetValue(highlightKey, false);
                currentPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); menu.UpdateLayout(); Deck.UpdateLayout();
                Check("Pinning keeps the menu open and shows a pin in the toolbar", menu.IsOpen && Deck.MediaSourcePin.IsVisible);
                CaptureOpenMenu(menu, Path.Combine(output, "source-pin-pinned.png"));
                otherPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); menu.UpdateLayout();
                Check("Pinning another source selects it", items[1].IsChecked && !items[0].IsChecked);
                otherPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); menu.UpdateLayout(); Deck.UpdateLayout();
                Check("Clicking the pin again unpins and leaves the source selected", items[1].IsChecked && !Deck.MediaSourcePin.IsVisible);
                items[0].IsChecked = true; items[1].IsChecked = false;
                var resized = Settings.Copy(); resized.MusicWidth += 10; resized.MusicHeight += 10; UpdateSettings(resized);
                Deck.UpdateLayout(); await Task.Delay(40); watch.Stop(); menu.UpdateLayout();
                Check("An open menu follows free resizing without being recreated", menu.IsOpen && ReferenceEquals(button.ContextMenu, menu));
            }
            menu.IsOpen = false; Deck.UpdateLayout();
        }
        var small = Settings.Copy(); (small.MusicWidth, small.MusicHeight) = MusicSizes.For(PanelSize.Compact); UpdateSettings(small);
        Deck.SetMediaForTest(first, tracks: Enumerable.Range(0, 12).Select(i => first with { Id = $"many-{i}", Source = $"应用 {i + 1}", Title = third.Title }).ToArray());
        Deck.UpdateLayout(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); IsolateVisualCapture(button.ContextMenu!); await Task.Delay(40); watch.Stop();
        var longMenu = button.ContextMenu!; longMenu.UpdateLayout();
        var scroll = SettingsElements(longMenu).OfType<ScrollViewer>().Single();
        Check("Many applications scroll within the small panel height", scroll.ScrollableHeight > 0 &&
            longMenu.PointToScreen(new Point(0, longMenu.ActualHeight)).Y <= Deck.PanelVisual.PointToScreen(new Point(0, Deck.PanelVisual.ActualHeight)).Y);
        Capture(longMenu, Path.Combine(output, "source-Compact-many-apps.png"), Brushes.Black); longMenu.IsOpen = false;
        await VerifyFullControlEntryAsync(output, checks, CaptureOpenMenu, IsolateVisualCapture);
        Deck.SelectPage(DeckPage.Usage); Deck.UpdateLayout();
    }
}
