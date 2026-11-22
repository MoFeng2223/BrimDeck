using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;

namespace ReadmeMedia;

// The model details' usage trend chart: the toolbar's chart button, the chart for one application and two stacked, by day and by
// hour, in tokens and in cost, over today, a week, a month, a quarter and more than a year, at the smallest, a usual and the
// largest panel; a day under the pointer and its tooltip. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task UsageTrendScene()
    {
        var snapshots = Snapshots();
        var random = new Random(7);
        foreach (var (snapshot, scale) in snapshots.Zip(new[] { 1.0, .45, .2, .3 }))
            snapshot.Entries = LongHistory(random, snapshot.Id.ToString(), snapshot.Entries.Select(t => t.Model).Distinct().ToArray(), 420, scale);
        _deck.SetSnapshots(snapshots);
        _settings.Animations = false;
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(true, true);
        Program.Call(_deck, "OpenModelDetails", [typeof(ProviderId), typeof(int)], ProviderId.Claude, 7);
        await Settle();
        (double Width, double Height)[] sizes = [(760, 240), (440, 140), (1200, 400)];

        foreach (var (width, height) in sizes)
        {
            _settings.Width = width; _settings.Height = height; Refresh();
            Rebuild(); await Save($"trend-table-{width}x{height}", 1.5);
        }
        Program.SetField(_deck, "_modelChart", true);
        foreach (var two in new[] { false, true })
        {
            if (two) Program.Call(_deck, "ToggleModelApp", [typeof(ProviderId)], ProviderId.Codex);
            foreach (var hourly in new[] { false, true })
            foreach (var cost in two ? new[] { false } : [false, true])
            foreach (var days in new[] { 1, 7, 30, 90, 420 })
            {
                // The period sets its own interval (today by hour, more by day); the one under test is set after it.
                SetRange(days);
                Program.SetField(_deck, "_trendHourly", hourly); Program.SetField(_deck, "_trendCost", cost);
                foreach (var (width, height) in sizes)
                {
                    _settings.Width = width; _settings.Height = height; Refresh();
                    Rebuild();
                    await Save($"trend-{(two ? "two" : "one")}-{(hourly ? "hour" : "day")}-{(cost ? "cost" : "tokens")}-{days}d-{width}x{height}", 1.5);
                }
            }
        }
        // A day under the pointer, and its tooltip as drawn by the graph.
        SetRange(30);
        Program.SetField(_deck, "_trendHourly", false); Program.SetField(_deck, "_trendCost", false);
        _settings.Width = 760; _settings.Height = 240; Refresh(); Rebuild();
        await Settle();
        var graph = Find(_stage, element => element.GetType().Name == "UsageTrendGraph")!;
        Program.SetField(graph, "_hot", 27); graph.InvalidateVisual();
        await Save("trend-hover", 1.5);
        foreach (var (cost, name) in new[] { (false, "tokens"), (true, "cost") })
        {
            Program.SetField(graph, "_cost", cost);
            var tip = (UIElement)Program.Call(graph, "TipContent", [typeof(int)], 27)!;
            var frame = new Border { Child = tip, Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), Padding = new Thickness(8, 6, 8, 6) };
            System.Windows.Documents.TextElement.SetForeground(frame, Brushes.White);
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); frame.Arrange(new Rect(frame.DesiredSize));
            var picture = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth * 2), (int)Math.Ceiling(frame.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            picture.Render(frame); picture.Freeze();
            Write(picture, Path.Combine(output, $"trend-tip-{name}.png"));
        }
        _settings.Animations = true;
    }

    private void SetRange(int days)
    {
        if (days is 1 or 7 or 30) { Program.Call(_deck, "SelectModelPeriod", [typeof(int)], days); return; }
        // Set directly: SelectModelRange would read the logs again, which here would replace the invented records.
        Program.SetField(_deck, "_customRange", new UsageDateRange(DateTime.Today.AddDays(1 - days), DateTime.Today));
    }

    private void Rebuild()
    {
        var field = _deck.GetType().GetField("_usageHandover", Program.Any)!;
        field.SetValue(_deck, Enum.Parse(field.FieldType, "Dissolve"));
        Program.Call(_deck, "RenderUsage", []);
    }

    private static FrameworkElement? Find(DependencyObject root, Func<FrameworkElement, bool> match)
    {
        if (root is FrameworkElement element && match(element)) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }

    // Records at working hours of every day back to `days` ago, busier on weekdays, with an amount for each.
    private List<TokenEntry> LongHistory(Random random, string key, string[] models, int days, double scale)
    {
        var result = new List<TokenEntry>();
        var now = DateTime.Now;
        for (int day = 0; day < days; day++)
        {
            var date = DateTime.Today.AddDays(-day);
            double busy = (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? .3 : 1) * (.4 + random.NextDouble()) * scale;
            if (random.NextDouble() < .08) continue;
            for (int hour = 9; hour < 23; hour++)
            {
                var time = date.AddHours(hour).AddMinutes(random.Next(60));
                if (time > now || random.NextDouble() < .35) continue;
                long total = (long)(busy * (300_000 + random.Next(900_000)));
                long output = total / 40, input = total / 12, cacheWrite = total / 20, cacheRead = total - output - input - cacheWrite;
                result.Add(new TokenEntry($"{key}:{day}:{hour}", new DateTimeOffset(time), models[random.Next(models.Length)], input, cacheRead, cacheWrite, 0, output,
                    Math.Round(total / 1_000_000m * 0.9m, 4)));
            }
        }
        return result;
    }
}
