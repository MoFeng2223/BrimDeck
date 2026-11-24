using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// What one frame of dragging the panel's edges costs on the notices page, beside the system monitor: the panel is resized as a
// drag resizes it (BeginResize, then ApplyResize each frame), and the layout, the work the page leaves for after it and the
// picture are timed apart, in ms (the first ten frames left out). The window is never shown and nothing is moved on the screen.
// Pictures of the page at the widest and narrowest sizes of the drag are saved after the timings. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task NoticeResizeScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        int serial = 0;
        void Add(string app, string title, string body, int minutesAgo)
            => hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = "windows:" + app, AppName = app, AppId = app, InstanceKey = "windows:" + app + ":" + ++serial, Title = title, Body = body,
                Category = NoticeCategory.Message, Capabilities = NoticeCapabilities.OpenApp, CreatedAt = _now.AddMinutes(-minutesAgo)
            }, _now.AddMinutes(-minutesAgo).ToUniversalTime());
        // Short and long titles and previews, Chinese and English, and titles in Hebrew (right to left).
        for (int i = 0; i < 30; i++)
            Add(i % 3 == 0 ? "QQ" : i % 3 == 1 ? "Outlook" : "WeChat",
                (i % 4) switch { 0 => "群" + i, 1 => "The weekly meeting moves to Thursday " + i, 2 => "产品群：版本发布与更新说明的讨论 " + i, _ => "שלום עולם " + i },
                i % 2 == 0 ? "第 " + i + " 条：今天下午的版本什么时候发？更新说明我来写，写完发群里大家看一下有没有遗漏的地方。" + new string('长', 400) : "ok " + i, 60 - i);
        foreach (var page in new[] { DeckPage.Notices, DeckPage.Monitor })
        {
            SelectPage(page);
            _settings.NoticesWidth = 760; _settings.NoticesHeight = 330;
            Refresh();
            _deck.SetExpanded(true, true);
            await Settle();
            var (startWidth, startHeight) = DeckPages.Size(_settings, page);
            Program.Call(_deck, "BeginResize", [typeof(bool)], false);
            var apply = typeof(MainWindow).GetMethod("ApplyResize", Program.Any)!;
            var layout = new List<double>(); var queued = new List<double>(); var render = new List<double>();
            var clock = new Stopwatch();
            for (int step = 0; step < 160; step++)
            {
                // Down to 300 DIP narrower and 120 lower, and back.
                double t = step < 80 ? step / 79.0 : (159 - step) / 79.0;
                double width = Math.Round(startWidth - 300 * t), height = Math.Round(startHeight - 120 * t);
                clock.Restart();
                apply.Invoke(_deck, [width, height]);
                _stage.UpdateLayout();
                layout.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                // What the page left for after the layout (DispatcherPriority.Loaded and above), and the layout that brings.
                await Dispatcher.Yield(DispatcherPriority.Background);
                _stage.UpdateLayout();
                queued.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                var picture = new RenderTargetBitmap((int)Math.Ceiling(_stage.ActualWidth), (int)Math.Ceiling(_stage.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                picture.Render(_stage);
                render.Add(clock.Elapsed.TotalMilliseconds);
            }
            static string Stats(List<double> values)
            {
                var sorted = values.Skip(10).OrderBy(v => v).ToList();
                return $"mean {sorted.Average():0.00} p50 {sorted[sorted.Count / 2]:0.00} p95 {sorted[(int)(sorted.Count * .95)]:0.00} max {sorted[^1]:0.00}";
            }
            Console.WriteLine($"{page,-8} layout  {Stats(layout)}");
            Console.WriteLine($"{page,-8} queued  {Stats(queued)}");
            Console.WriteLine($"{page,-8} render  {Stats(render)}");
            if (page == DeckPage.Notices)
                foreach (var (width, height) in new[] { (startWidth, startHeight), (startWidth - 300, startHeight - 120), (startWidth - 150, startHeight) })
                {
                    apply.Invoke(_deck, [width, height]);
                    await Save($"notice-resize-{width}x{height}", 2);
                }
            // The rows that were out of view during the drag, scrolled into view at the last size.
            if (page == DeckPage.Notices && Program.Field(_deck, "_pageScroll") is System.Windows.Controls.ScrollViewer list)
            {
                list.ScrollToVerticalOffset(list.ScrollableHeight / 2);
                await Save("notice-resize-scrolled-middle", 2);
                list.ScrollToEnd();
                await Save("notice-resize-scrolled-end", 2);
            }
            Program.SetField(_deck, "_resizeSettling", false);
            Program.SetField(_deck, "_resizeChanged", false);
            _deck.SetExpanded(false, true);
            await Settle();
        }
        SelectPage(DeckPage.Usage);
    }
}
