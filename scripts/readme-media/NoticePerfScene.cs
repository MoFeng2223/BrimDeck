using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// What opening the notices page and one frame of dragging its edges cost when the focus shows a chat of many messages (a group
// chat of 32 merged messages beside 30 other notices), in ms: the opening from SetExpanded to the first picture, with the page
// built as the panel opens and built while it was closed (PrebuildNotices); the drag as NoticeResizeScene times it. Then pictures
// of the focus's messages scrolled part way, while the width changes and once it settles, which must show the same messages in
// the same place. The window is never shown and nothing is moved on the screen. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task NoticePerfScene()
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
        for (int i = 0; i < 30; i++)
            Add(i % 3 == 0 ? "Outlook" : i % 3 == 1 ? "WeChat" : "钉钉", (i % 2 == 0 ? "产品群：版本发布与更新说明的讨论 " : "The weekly meeting moves to Thursday ") + i,
                i % 2 == 0 ? "第 " + i + " 条：今天下午的版本什么时候发？更新说明我来写，写完发群里大家看一下有没有遗漏的地方。" : "ok " + i, 90 - i);
        // The chat in front: 32 messages of one group, the newest last.
        for (int i = 0; i < 32; i++)
            Add("QQ", "claude交流1群", (i % 3) switch { 0 => "[@全体成员] 你值得拥有：五个免费的team速度太慢了啊 " + i, 1 => "otaku：10块 " + i, _ => "INF稳：今天下午的版本什么时候发？更新说明我来写，写完发群里大家看一下有没有遗漏的地方，顺便把截图也换一下 " + i }, 40 - i);

        SelectPage(DeckPage.Notices);
        _settings.NoticesWidth = 640; _settings.NoticesHeight = 300;
        Refresh();
        var clock = new Stopwatch();
        static string Stats(List<double> values, int skip)
        {
            var sorted = values.Skip(skip).OrderBy(v => v).ToList();
            return $"mean {sorted.Average():0.00} p50 {sorted[sorted.Count / 2]:0.00} p95 {sorted[(int)(sorted.Count * .95)]:0.00} max {sorted[^1]:0.00}";
        }

        // Opening: the work before the first frame (SetExpanded and the layout it leaves), and what the page queues behind it. Built
        // as the panel opens (a notice arrived just before), and built while the panel was closed.
        foreach (var built in new[] { false, true })
        {
            var opening = new List<double>(); var openLayout = new List<double>(); var openQueued = new List<double>();
            for (int run = 0; run < 12; run++)
            {
                _deck.SetExpanded(false, true);
                await Settle();
                if (built) { await Task.Delay(run == 0 ? 2300 : 900); await Settle(); }
                else Add("QQ", "claude交流1群", "新消息 " + run, 0);
                clock.Restart();
                _deck.SetExpanded(true, true);
                opening.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                _stage.UpdateLayout();
                var picture = new RenderTargetBitmap((int)Math.Ceiling(_stage.ActualWidth), (int)Math.Ceiling(_stage.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                picture.Render(_stage);
                openLayout.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                await Dispatcher.Yield(DispatcherPriority.Background);
                _stage.UpdateLayout();
                openQueued.Add(clock.Elapsed.TotalMilliseconds);
                if (run == 0) Console.WriteLine($"open {(built ? "built" : "late ")} first  build {opening[0]:0.00} layout+render {openLayout[0]:0.00} queued {openQueued[0]:0.00}");
            }
            Console.WriteLine($"open {(built ? "built" : "late ")} build   {Stats(opening, 2)}");
            Console.WriteLine($"open {(built ? "built" : "late ")} layout  {Stats(openLayout, 2)}");
            Console.WriteLine($"open {(built ? "built" : "late ")} queued  {Stats(openQueued, 2)}");
        }
        await Settle();
        await Save("notice-perf-open", 2);

        var (startWidth, startHeight) = DeckPages.Size(_settings, DeckPage.Notices);
        Program.Call(_deck, "BeginResize", [typeof(bool)], false);
        var apply = typeof(MainWindow).GetMethod("ApplyResize", Program.Any)!;
        var content = (System.Windows.Controls.Grid)Program.Field(_deck, "NoticesContent")!;
        {
            var layout = new List<double>(); var queued = new List<double>(); var render = new List<double>();
            for (int step = 0; step < 320; step++)
            {
                // Down to 200 DIP narrower and 100 lower, and back, twice.
                double t = step % 160 < 80 ? step % 160 / 79.0 : (159 - step % 160) / 79.0;
                double width = Math.Round(startWidth - 200 * t), height = Math.Round(startHeight - 100 * t);
                clock.Restart();
                apply.Invoke(_deck, [width, height]);
                _stage.UpdateLayout();
                layout.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                await Dispatcher.Yield(DispatcherPriority.Background);
                _stage.UpdateLayout();
                queued.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                var picture = new RenderTargetBitmap((int)Math.Ceiling(_stage.ActualWidth), (int)Math.Ceiling(_stage.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                picture.Render(_stage);
                render.Add(clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"resize layout  {Stats(layout, 10)}");
            Console.WriteLine($"resize queued  {Stats(queued, 10)}");
            Console.WriteLine($"resize render  {Stats(render, 10)}");
        }
        {
            var stack = (System.Windows.Controls.DockPanel)((System.Windows.Controls.Grid)content.Children[0]).Children[1];
            // The words scrolled half way, then the panel narrowed: what is in view stays, and the rest is laid out once it settles.
            var words = (System.Windows.Controls.ScrollViewer)stack.Children[^1];
            apply.Invoke(_deck, [startWidth, startHeight]);
            await Settle(); await Task.Delay(300); await Settle();
            words.ScrollToVerticalOffset(words.ScrollableHeight / 2);
            await Settle();
            Console.WriteLine($"words extent {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-words-middle", 2);
            for (int w = 0; w <= 200; w += 4) { apply.Invoke(_deck, [startWidth - w, startHeight]); _stage.UpdateLayout(); }
            Console.WriteLine($"words extent while dragging {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-words-dragged", 2);
            await Task.Delay(300); await Settle();
            Console.WriteLine($"words extent settled {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-words-settled", 2);
            // At the end the long messages are above the view: as they settle taller, the view moves down with them.
            apply.Invoke(_deck, [startWidth, startHeight]);
            await Settle(); await Task.Delay(300); await Settle();
            words.ScrollToVerticalOffset(words.ScrollableHeight - 60); await Settle();
            Console.WriteLine($"end extent {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-end-before", 2);
            for (int w = 0; w <= 200; w += 4) { apply.Invoke(_deck, [startWidth - w, startHeight]); _stage.UpdateLayout(); }
            Console.WriteLine($"end extent while dragging {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-end-dragged", 2);
            await Task.Delay(300); await Settle();
            Console.WriteLine($"end extent settled {words.ExtentHeight:0} offset {words.VerticalOffset:0}");
            await Save("notice-perf-end-settled", 2);
        }
        Program.SetField(_deck, "_resizeSettling", false);
        Program.SetField(_deck, "_resizeChanged", false);
        _deck.SetExpanded(false, true);
        await Settle();
        SelectPage(DeckPage.Usage);
    }
}
