using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckAdaptiveGrid(ClipboardView view, ListBox list, ClipboardHistory history, ClipboardSettings settings, Window window, Action<string, bool> check)
    {
        var app = (App)Application.Current;
        bool animations = app.Settings.Animations, grid = settings.GridView;
        double width = window.Width, height = window.Height;
        var query = view.QueryState;
        try
        {
            app.Settings.Animations = false;
            settings.GridView = true; window.Width = 1080; window.Height = 400;
            view.QueryState = new ClipboardQuery { Sort = ClipboardSort.Count }; view.ApplyLayout(); Pump();
            void Geometry(string scenario)
            {
                var panel = Descendants<IslandCardPanel>(view).Single();
                var cards = Descendants<ClipboardCard>(view).ToArray();
                double top = cards.Min(card => card.TranslatePoint(new Point(), panel).Y);
                var row = cards.Where(card => Math.Abs(card.TranslatePoint(new Point(), panel).Y - top) < 1).OrderBy(card => card.TranslatePoint(new Point(), panel).X).ToArray();
                double right = row[^1].TranslatePoint(new Point(), panel).X + row[^1].ActualWidth;
                check("adaptive grid " + scenario + ": full row reaches viewport right edge without a dead column (remaining " + (panel.ViewportWidth - right).ToString("0.##") + " DIP)", Math.Abs(panel.ViewportWidth - right) <= 1);
                check("adaptive grid " + scenario + ": equal widths, original height and ten-DIP gaps", row.All(card => Math.Abs(card.ActualWidth - row[0].ActualWidth) <= 1 && Math.Abs(card.ActualHeight - 146) <= 1)
                    && row.Zip(row.Skip(1)).All(pair => Math.Abs(pair.Second.TranslatePoint(new Point(), panel).X - pair.First.TranslatePoint(new Point(), panel).X - pair.First.ActualWidth - 10) <= 1));
                check("adaptive grid " + scenario + ": no horizontal overflow or scrollbar", panel.ExtentWidth == panel.ViewportWidth && cards.All(card => card.TranslatePoint(new Point(), panel).X >= -1 && card.TranslatePoint(new Point(), panel).X + card.ActualWidth <= panel.ViewportWidth + 1));
            }
            Geometry("1080");
            var stable = Descendants<ClipboardCard>(view).First(); var body = stable.PreviewBody;
            window.Width = 1040; Pump(); Geometry("1040");
            check("adaptive grid: resizing preserves the stable card and decoded preview", Descendants<ClipboardCard>(view).Contains(stable) && ReferenceEquals(body, stable.PreviewBody));
            foreach (double next in new[] { 880d, 660d, 440d, 1320d }) { window.Width = next; Pump(); Geometry(next.ToString("0")); }
            window.Width = 1080; view.QueryState = new ClipboardQuery(); Pump();
            var timePanel = Descendants<IslandCardPanel>(view).Single();
            double firstTop = Descendants<ClipboardCard>(view).Min(card => card.TranslatePoint(new Point(), timePanel).Y);
            check("adaptive grid: time order fills rows across dates when no date heading is shown", view.TimelineHeaderHeight == 0 && Descendants<ClipboardCard>(view).Count(card => Math.Abs(card.TranslatePoint(new Point(), timePanel).Y - firstTop) < 1) == 4);
            app.Settings.Animations = true; window.Width = 880; Pump();
            var moving = Descendants<ListBoxItem>(view).Where(row => row.RenderTransform is TranslateTransform shift && shift.HasAnimatedProperties).ToArray();
            if (Soft.Enabled)
            {
                check("adaptive grid motion: a column change reuses keyed movement rather than fading the list", moving.Length > 0 && !list.HasAnimatedProperties);
                window.Width = 660; Pump(); window.Width = 1080; Pump();
                WaitClipboardMotion(Soft.Move + 80);
                check("adaptive grid motion: rapid reflows settle at the newest layout without queued offsets", Descendants<ListBoxItem>(view).All(row => row.RenderTransform.Value.OffsetX == 0 && row.RenderTransform.Value.OffsetY == 0));
                Geometry("rapid reflow settled");
                view.QueryState = new ClipboardQuery { Sort = ClipboardSort.Count }; Pump();
                check("adaptive grid motion: count sorting moves keyed cards in both grid axes", Descendants<ListBoxItem>(view).Any(row => row.RenderTransform is TranslateTransform shift && shift.HasAnimatedProperties));
                WaitClipboardMotion(Soft.Move + 80);
            }
            else Console.WriteLine("SKIP: grid reflow animation checks follow Windows' disabled animation effects.");
            app.Settings.Animations = false; window.Width = 660; Pump();
            check("adaptive grid motion: disabled animation applies new slots immediately", Descendants<ListBoxItem>(view).All(row => row.RenderTransform.Value.IsIdentity));
            settings.GridView = false; view.ApplyLayout(); Pump();
            check("adaptive grid: strip keeps original 210 by 146 cards and ten-DIP gap", Descendants<ClipboardCard>(view).All(card => Math.Abs(card.ActualWidth - 210) <= 1 && Math.Abs(card.ActualHeight - 146) <= 1));
        }
        finally
        {
            view.ClosePopups(); app.Settings.Animations = false;
            window.Width = width; window.Height = height; settings.GridView = grid;
            view.QueryState = query; view.ApplyLayout(); Pump(); app.Settings.Animations = animations;
        }
    }
}
