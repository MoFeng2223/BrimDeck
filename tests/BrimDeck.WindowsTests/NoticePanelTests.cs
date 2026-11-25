using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;

// The notices page's own panels, laid out by WPF as the page lays them out (src/BrimDeck/NoticePanels.cs): the list's header keeps
// its count (and the pause time in it) at any width, a row keeps its title readable and places its time the same way in every
// row, and the fades at the foot of a column are masks rather than colors laid over it.
internal static class NoticePanelTests
{
    private static TextBlock Text(string text, double size, bool wrap = false)
        => new() { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

    private static Rect Lay(Panel panel, double width, out Size desired)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        desired = panel.DesiredSize;
        panel.Arrange(new Rect(0, 0, width, desired.Height));
        return new Rect(0, 0, width, desired.Height);
    }

    private static Rect Box(UIElement element) => new(element.TranslatePoint(new Point(), (UIElement)System.Windows.Media.VisualTreeHelper.GetParent(element)), element.RenderSize);

    public static void Run(Action<string, bool> check)
    {
        // ----- The header: title, count, tools -----
        NoticeHead Head(string count)
        {
            var head = new NoticeHead();
            var title = Text("通知", 15); title.Height = 20;
            var tools = new Border { Width = 112, Height = 20 };
            var counter = Text(count, 11.5, wrap: true); counter.LineHeight = NoticeLines.CountLine; counter.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            head.Children.Add(title); head.Children.Add(counter); head.Children.Add(tools);
            return head;
        }
        const string paused = "今天 8 条 · 暂停至 17:46";
        var wide = Head(paused); Lay(wide, 400, out var wideSize);
        check("A header that fits is one line, the count beside the title and the tools at the right",
            wideSize.Height == NoticeLines.HeadLine && wide.Children[1].RenderSize.Width > 0 && Box(wide.Children[1]).Left >= Box(wide.Children[0]).Right
            && Math.Abs(Box(wide.Children[2]).Right - 400) < .5 && Box(wide.Children[1]).Right <= Box(wide.Children[2]).Left);
        var narrow = Head(paused); Lay(narrow, 170, out var narrowSize);
        var (nTitle, nCount, nTools) = (narrow.Children[0], narrow.Children[1], narrow.Children[2]);
        check("A header that does not fit puts the count, with the pause time whole, under the title and the tools",
            narrowSize.Height > NoticeLines.HeadLine && Box(nCount).Top >= NoticeLines.HeadLine && Box(nCount).Left == 0
            && ((TextBlock)nCount).Text.Contains("17:46") && Box(nCount).Width <= 170.5 && Box(nTitle).Right <= Box(nTools).Left
            && Math.Abs(Box(nTools).Right - 170) < .5 && Box(nTools).Top == 0 && nCount.RenderSize.Height <= NoticeLines.CountLine * NoticeLines.CountLines + .5);
        var english = Head("8 today · paused until 17:46"); Lay(english, 150, out var englishSize);
        check("An English count that is too long for one line wraps under the tools, at most two lines",
            englishSize.Height > NoticeLines.HeadLine && englishSize.Height <= NoticeLines.HeadLine + NoticeLines.HeadGap + NoticeLines.CountLine * NoticeLines.CountLines + .5);

        // ----- A row: title, time, preview -----
        NoticeRowTexts Row(string title, string time)
        {
            var row = new NoticeRowTexts();
            var t = Text(title, 12.5); t.Height = NoticeLines.RowTitleLine; t.FontWeight = FontWeights.SemiBold;
            var when = Text(time, 11); when.Height = NoticeLines.RowTitleLine;
            var preview = Text("E1-S23 两条训练队列运行中，预计 11-01 早上完成", 12); preview.LineHeight = NoticeLines.RowPreviewLine;
            row.Children.Add(t); row.Children.Add(when); row.Children.Add(preview);
            return row;
        }
        var roomy = Row("Claude Code · ocbench-gen", "15 分钟前"); Lay(roomy, 170, out var roomySize);
        var (rTitle, rTime, rPreview) = (roomy.Children[0], roomy.Children[1], roomy.Children[2]);
        check("A row wide enough has its time at the right of the title and the preview on its own line",
            Math.Abs(Box(rTime).Right - 170) < .5 && Box(rTime).Top == 0 && Box(rTitle).Right <= Box(rTime).Left && Box(rTitle).Width >= NoticeLines.RowTitleMinimum - .5
            && Box(rPreview).Top >= NoticeLines.RowTitleLine && Box(rPreview).Left == 0 && Math.Abs(Box(rPreview).Width - 170) < .5 && roomySize.Height == 36);
        var tight = Row("Claude Code · ocbench-gen", "15 分钟前"); Lay(tight, 120, out var tightSize);
        var (tTitle, tTime, tPreview) = (tight.Children[0], tight.Children[1], tight.Children[2]);
        check("A row too narrow for the title and the time moves the time to the front of the preview, and the title keeps the whole line",
            Math.Abs(Box(tTitle).Width - 120) < .5 && Box(tTime).Top >= NoticeLines.RowTitleLine && Box(tTime).Left == 0 && Box(tPreview).Left >= Box(tTime).Right
            && Box(tPreview).Top >= NoticeLines.RowTitleLine && Box(tPreview).Right <= 120.5 && tightSize.Height == 36);
        // The choice depends on the width alone: a short time and a long one, a short title and a long one, are laid out alike.
        bool consistent = true;
        foreach (double width in new[] { 110, 130, 146, 160, 200 })
        {
            var a = Row("短", "1 分钟前"); var b = Row("A very long title that has to be trimmed at the end of the line", "45 minutes ago");
            Lay(a, width, out _); Lay(b, width, out _);
            consistent &= (Box(a.Children[1]).Top == 0) == (Box(b.Children[1]).Top == 0);
        }
        check("Every row of a list puts its time in the same place, whatever its words", consistent);
        check("The list's own minimum width leaves a row room for its time beside a title of at least 88 DIP",
            NoticeLines.TimeBesideTitle(146) && !NoticeLines.TimeBesideTitle(145) && NoticeLines.RowTitleMinimum == 88);

        // ----- The focus's buttons: delete, two actions, the main one -----
        NoticeButtons Buttons(bool main = true)
        {
            var row = new NoticeButtons { MainLast = main };
            row.Children.Add(new Border { Width = 32, Height = 32 });
            row.Children.Add(new Border { Width = 60, Height = 32 });
            row.Children.Add(new Border { Width = 60, Height = 32 });
            row.Children.Add(new Border { Width = 100, Height = 32 });
            return row;
        }
        var oneLine = Buttons(); Lay(oneLine, 400, out var oneSize);
        check("Buttons that fit are one line, the main one at the right end",
            oneSize.Height == 32 && Math.Abs(Box(oneLine.Children[3]).Right - 400) < .5 && Box(oneLine.Children[0]).Left == 0 && Box(oneLine.Children[2]).Right <= Box(oneLine.Children[3]).Left);
        var twoLines = Buttons(); Lay(twoLines, 178, out var twoSize);
        check("Buttons that do not fit wrap to a second line instead of being cut off, the main one at the right end of its line",
            twoSize.Height == 32 + NoticeButtons.LineGap + 32 && Box(twoLines.Children[3]).Top == 32 + NoticeButtons.LineGap && Math.Abs(Box(twoLines.Children[3]).Right - 178) < .5
            && Enumerable.Range(0, 4).All(i => Box(twoLines.Children[i]).Right <= 178.5 && Box(twoLines.Children[i]).Left >= 0));
        var plain = Buttons(main: false); Lay(plain, 400, out _);
        check("Without a main button the last one stays with the others", Math.Abs(Box(plain.Children[3]).Right - 400) > 1 && Box(plain.Children[3]).Left == Box(plain.Children[2]).Right + 8);

        // ----- The focus's round buttons: delete, copy (optional), event, to-do, and "open app", 80 wide with its words, 32 as an icon -----
        NoticeButtons Round()
        {
            var row = new NoticeButtons { MainLast = true };
            for (int i = 0; i < 4; i++) row.Children.Add(new Border { Width = 32, Height = 32 });
            var main = new Border { Width = 80, Height = 32 };
            row.Children.Add(main);
            row.Optional = row.Children[1];
            row.CompactMain = compact => main.Width = compact ? 32 : 80;
            return row;
        }
        bool OneLine(NoticeButtons row, double width) => row.DesiredSize.Height == 32 && Math.Abs(Box(row.Children[4]).Right - width) < .5 && Box(row.Children[0]).Left == 0;
        double Space(NoticeButtons row, int a, int b) => Box(row.Children[b]).Left - Box(row.Children[a]).Right;
        var spacious = Round(); Lay(spacious, 400, out _);
        check("Round buttons with room: all on one line, the main one with its words, 8 apart",
            OneLine(spacious, 400) && spacious.Children[1].Visibility == Visibility.Visible && Box(spacious.Children[4]).Width == 80 && Space(spacious, 0, 1) == 8);
        var iconMain = Round(); Lay(iconMain, 200, out _);
        check("Narrower, the main button shows its icon and copy stays",
            OneLine(iconMain, 200) && iconMain.Children[1].Visibility == Visibility.Visible && Box(iconMain.Children[4]).Width == 32 && Space(iconMain, 1, 2) == 8);
        var noCopy = Round(); Lay(noCopy, 180, out _);
        check("Narrower still, copy is left out and the rest stay on one line 8 apart",
            OneLine(noCopy, 180) && noCopy.Children[1].Visibility == Visibility.Collapsed && Space(noCopy, 0, 2) == 8 && Space(noCopy, 2, 3) == 8);
        var close = Round(); Lay(close, 150, out _);
        check("At the narrowest column (150) the buttons move closer, not under 6, and stay on one line",
            OneLine(close, 150) && close.Children[1].Visibility == Visibility.Collapsed && Space(close, 0, 2) >= NoticeButtons.LeastGap && Space(close, 0, 2) < 8);
        Lay(close, 400, out _);
        check("Wide again, copy comes back and the main button shows its words",
            OneLine(close, 400) && close.Children[1].Visibility == Visibility.Visible && Box(close.Children[4]).Width == 80 && Space(close, 0, 1) == 8);
        var wrapped = Round(); Lay(wrapped, 140, out var wrappedSize);
        check("Where even that does not fit, the buttons wrap with copy shown",
            wrappedSize.Height > 32 && wrapped.Children[1].Visibility == Visibility.Visible && Enumerable.Range(0, 5).All(i => Box(wrapped.Children[i]).Right <= 140.5));

        // ----- The fade at a column's foot: a mask on the viewer -----
        var viewer = new ScrollViewer { Content = new Border { Height = 600, Width = 100 }, Height = 200, Width = 100 };
        NoticeFade.Foot(viewer, 36);
        var window = new Window { Width = 300, Height = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, Content = viewer };
        try
        {
            window.Show();
            Pump();
            var mask = viewer.OpacityMask as LinearGradientBrush;
            bool shape = mask is { GradientStops.Count: 3 } && mask.GradientStops[0].Color.A == 255 && mask.GradientStops[1].Color.A == 255 && mask.GradientStops[2].Color.A == 0
                && mask.StartPoint == new Point(0, 0) && mask.EndPoint == new Point(0, 1);
            check("A column's foot fades by an opacity mask on the viewer, opaque down to the last 36 DIP and clear at the foot", shape
                && Math.Abs(mask!.GradientStops[1].Offset - (1 - 36 / viewer.ActualHeight)) < .001 && mask.GradientStops[2].Offset == 1);
            viewer.Height = 100; Pump();
            check("The fade follows the viewer's height", Math.Abs(((LinearGradientBrush)viewer.OpacityMask).GradientStops[1].Offset - (1 - 36 / viewer.ActualHeight)) < .001);
        }
        finally { window.Close(); }

        // ----- A line that keeps its measurement while it fits -----
        var shortLine = new FitLineBox { Child = Text("短标题", 12.5) };
        var longLine = new FitLineBox { Child = Text("A very long title that has to be trimmed at the end of the line", 12.5) };
        var lines = new StackPanel { Children = { shortLine, longLine } };
        Lay(lines, 300, out _);
        var fitted = shortLine.Child.DesiredSize;
        Lay(lines, 200, out _);
        check("A line that fits keeps the measurement of its whole width at another width, and is not cut",
            shortLine.Child.DesiredSize == fitted && !shortLine.Cut && Math.Abs(shortLine.Child.RenderSize.Width - shortLine.Natural) < .5);
        check("A line that does not fit is laid out at the width it has, with its ellipsis", longLine.Cut && longLine.Child.RenderSize.Width <= 200.5);
        ((TextBlock)shortLine.Child).Text = "一个长得放不下的标题，一个长得放不下的标题，一个长得放不下的标题";
        Lay(lines, 200, out _);
        check("A line whose words changed is measured again", shortLine.Cut && shortLine.Child.RenderSize.Width <= 200.5);
        var rtl = new FitLineBox { Child = new TextBlock { Text = "שלום", FontSize = 12, FlowDirection = FlowDirection.RightToLeft } };
        var rtlRow = new StackPanel { Children = { rtl } };
        Lay(rtlRow, 200, out _);
        check("A right-to-left line that fits sits at the right end of its room, where it would be laid out at the whole width",
            Math.Abs(Box(rtl.Child).Right - 200) < .5 || Math.Abs(rtl.Child.TranslatePoint(new Point(), rtl).X - 200) < .5);

        // ----- A stack that lays out again only what is in view -----
        var stack = new ViewportStack();
        var rows = Enumerable.Range(0, 30).Select(i => new Border { Height = 40, Child = Text("行 " + i, 12) }).ToList();
        foreach (var row in rows) stack.Children.Add(row);
        var list = new ScrollViewer { Content = stack, Width = 300, Height = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var listWindow = new Window { Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, Content = list };
        try
        {
            listWindow.Show(); Pump();
            list.Width = 220; Pump();
            check("A stack lays out the children in view at its new width and leaves the others as they were",
                rows[0].RenderSize.Width == 220 && rows[4].RenderSize.Width == 220 && rows[20].RenderSize.Width == 300 && rows[29].RenderSize.Width == 300
                && stack.DesiredSize.Height == 30 * 40 && Box(rows[29]).Top == 29 * 40);
            list.ScrollToEnd(); Pump();
            check("A child scrolled into view is laid out at the stack's width", rows[29].RenderSize.Width == 220 && rows[25].RenderSize.Width == 220);
        }
        finally { listWindow.Close(); }

        // ----- Children whose height follows the width (the focus's wrapped messages) -----
        var words = new ViewportStack();
        var messages = Enumerable.Range(0, 20).Select(i => new TextBlock { Text = string.Concat(Enumerable.Repeat("一段会换行的消息 ", 6)) + i, FontSize = 13, TextWrapping = TextWrapping.Wrap }).ToList();
        foreach (var message in messages) words.Children.Add(message);
        var wordsViewer = new ScrollViewer { Content = words, Width = 300, Height = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var wordsWindow = new Window { Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, Content = wordsViewer };
        try
        {
            wordsWindow.Show(); Pump();
            wordsViewer.ScrollToVerticalOffset(wordsViewer.ScrollableHeight * 2 / 3); Pump();
            var marker = messages.First(m => m.TranslatePoint(new Point(), wordsViewer).Y >= 0);
            double markerAt = marker.TranslatePoint(new Point(), wordsViewer).Y, extent = wordsViewer.ExtentHeight;
            for (double width = 296; width >= 220; width -= 4) { wordsViewer.Width = width; Pump(); }
            check("While the width keeps changing, the messages out of view keep the width and height they had",
                messages[0].RenderSize.Width == 300 && messages[^1].RenderSize.Width == 300 && Math.Abs(marker.TranslatePoint(new Point(), wordsViewer).Y - markerAt) < 1);
            Wait(400);
            check("Once the width has settled, every message is laid out at it and the stack is as tall as they are",
                messages.All(m => m.RenderSize.Width == 220) && Math.Abs(words.DesiredSize.Height - messages.Sum(m => m.DesiredSize.Height)) < .5 && wordsViewer.ExtentHeight > extent);
            check("The messages above the view growing taller move the view with them, so what was in view stays where it was",
                Math.Abs(marker.TranslatePoint(new Point(), wordsViewer).Y - markerAt) < 1);
        }
        finally { wordsWindow.Close(); }
    }

    // Runs the dispatcher for the given time, then until it is idle.
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
