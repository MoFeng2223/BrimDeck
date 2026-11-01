using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void CheckAdaptiveGridHost(MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string directory)
    {
        var snapshot = history.Snapshot(); var query = view.QueryState;
        bool oldGrid = settings.Clipboard.GridView; double oldHeight = settings.ClipboardHeight;
        double oldWidth = window.Width, oldWindowHeight = window.Height;
        double oldPageWidth = (double)Get(window, "_pageWidth")!, oldWidest = (double)Get(window, "_widestPage")!;
        var sortCallback = Get(view, "_setSort")!;
        var list = All<ListBox>(view).Single();
        try
        {
            Set(view, "_setSort", (Action<ClipboardSort>)(order => settings.Clipboard.SortOrder = order));
            var now = DateTimeOffset.UtcNow;
            var fillers = Enumerable.Range(0, 24).Select(i => new ClipboardEntry { Id = "grid-fixture-" + i, Content = ClipboardContent.FromText("Synthetic grid sample " + (i + 1) + "\nResponsive cards keep the same spacing and stable identity."), CopiedAt = now.AddDays(-1 - i / 8).AddMinutes(-i), CopyCount = i % 9 + 1 });
            history.Restore(snapshot with { Items = snapshot.Items.Concat(fillers).ToList() }, settings.Clipboard, now);
            settings.Clipboard.GridView = true; settings.ClipboardHeight = 356; settings.Animations = false;
            view.QueryState = new ClipboardQuery(); view.ApplyLayout();
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 2400, 1400));
            Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 2400, 1352));
            window.Width = 2400; window.Height = 1000;
            void Resize(double width)
            {
                Set(window, "_pageWidth", width); Set(window, "_widestPage", width);
                window.SetExpanded(true, instant: true); Pump();
            }
            foreach (var palette in palettes)
            foreach (int percent in new[] { 100, 150 })
            {
                Palette(palette); Call(window, "ApplyTheme"); settings.PanelScale = percent; Call(window, "ApplyScale");
                foreach (double width in new[] { 1080d, 1120d, 880d, 660d, 440d, 1320d })
                {
                    Resize(width); var panel = All<IslandCardPanel>(view).Single();
                    var scroll = All<ScrollViewer>(list).First(); scroll.ScrollToTop(); Pump();
                    var cards = All<ClipboardCard>(view).ToArray();
                    double firstTop = cards.Min(card => card.TranslatePoint(new Point(), panel).Y);
                    var row = cards.Where(card => Math.Abs(card.TranslatePoint(new Point(), panel).Y - firstTop) < 1).OrderBy(card => card.TranslatePoint(new Point(), panel).X).ToArray();
                    int columns = Math.Max(1, (int)Math.Floor((panel.ViewportWidth + 10) / 220));
                    double right = row[^1].TranslatePoint(new Point(), panel).X + row[^1].ActualWidth;
                    string scenario = palette.Id + "-" + percent + "-" + width.ToString("0");
                    Check("real grid " + scenario + ": full row uses actual viewport without right-side blank", row.Length == columns && Near(right, panel.ViewportWidth));
                    Check("real grid " + scenario + ": equal cards and reference gaps remain inside the viewport", row.All(card => Near(card.ActualWidth, row[0].ActualWidth) && Near(card.ActualHeight, 146))
                        && row.Zip(row.Skip(1)).All(pair => Near(pair.Second.TranslatePoint(new Point(), panel).X - pair.First.TranslatePoint(new Point(), panel).X - pair.First.ActualWidth, 10))
                        && cards.All(card => card.TranslatePoint(new Point(), panel).X >= -1 && card.TranslatePoint(new Point(), panel).X + card.ActualWidth <= panel.ViewportWidth + 1));
                    var island = (IslandBorder)window.FindName("Island");
                    Check("real grid " + scenario + ": host scale is applied once and horizontal scrolling stays disabled", Near(island.ActualWidth, width * percent / 100d) && Near(view.ActualWidth, width) && panel.ExtentWidth == panel.ViewportWidth && ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled);
                    if (width == 1080 || width == 660 && percent == 100) Render(island, directory, "clipboard-grid-" + scenario + ".png");
                }
            }
            Palette(palettes[0]); Call(window, "ApplyTheme"); settings.PanelScale = 100; Call(window, "ApplyScale"); Resize(1080);
            list.SelectedItem = history.Items.First(entry => entry.Id == "file"); OpenAutomaticPreview(view,(ClipboardEntry)list.SelectedItem); Pump();
            settings.Animations = true; list.BeginAnimation(UIElement.OpacityProperty, null); Resize(1120);
            if (Soft.Enabled)
            {
                Check("real grid motion: crossing a column boundary moves existing keyed cards, not list opacity", All<ListBoxItem>(view).Any(row => row.RenderTransform is TranslateTransform shift && shift.HasAnimatedProperties) && !list.HasAnimatedProperties);
                Resize(880); Resize(660); Resize(1080); WaitGridMotion();
                Check("real grid motion: rapid resizes settle at the latest slots without stale offsets", All<ListBoxItem>(view).All(row => row.RenderTransform.Value.IsIdentity));
                Call(view, "SetSort", ClipboardSort.Count); Pump();
                Check("real grid motion: count sort animates two-dimensional reordering", All<ListBoxItem>(view).Any(row => row.RenderTransform is TranslateTransform shift && shift.HasAnimatedProperties && Math.Abs(shift.Y) > 1));
                WaitGridMotion();
            }
            else Console.WriteLine("SKIP: real grid motion follows Windows' disabled animation effects.");
            Check("real grid: resize and sort retain selection and explicit preview", (list.SelectedItem as ClipboardEntry)?.Id == "file" && view.PreviewOpen);
            view.ClosePopups(); settings.Animations = false; view.QueryState = new ClipboardQuery(); Pump();
            var scroller = All<ScrollViewer>(list).First(); scroller.ScrollToVerticalOffset(495); Pump();
            var leading = VisibleEntries(list).First();
            double anchorTop = ((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(leading)).TranslatePoint(new Point(), list).Y;
            Resize(880);
            var anchorRow = list.ItemContainerGenerator.ContainerFromItem(leading) as FrameworkElement;
            Check("real grid: changing columns while scrolled preserves the leading entry and inset", anchorRow is not null && Near(anchorRow.TranslatePoint(new Point(), list).Y, anchorTop));
            Check("real grid: scrolled reflow stays virtualized with bounded row realization", All<ClipboardCard>(view).Count() < history.Items.Count && All<IslandCardPanel>(view).Single().VerticalOffset > 0);
            scroller.ScrollToTop(); Pump(); Resize(1120);
            history.Restore(snapshot with { Items = snapshot.Items.Take(3).ToList() }, settings.Clipboard, now); Pump();
            Check("real grid: disappearing scrollbar redistributes its width without overflow", All<ScrollViewer>(list).First().ComputedVerticalScrollBarVisibility == Visibility.Collapsed
                && All<ClipboardCard>(view).All(card => card.TranslatePoint(new Point(), list).X + card.ActualWidth <= list.ActualWidth + 1));
        }
        finally
        {
            view.ClosePopups(); settings.Animations = false; settings.PanelScale = 100;
            settings.Clipboard.GridView = oldGrid; settings.ClipboardHeight = oldHeight; settings.Clipboard.SortOrder = query.Sort;
            Set(view, "_setSort", sortCallback); history.Restore(snapshot, settings.Clipboard, DateTimeOffset.UtcNow); view.QueryState = query; view.ApplyLayout();
            Palette(palettes[0]); Call(window, "ApplyTheme"); Call(window, "ApplyScale");
            window.Width = oldWidth; window.Height = oldWindowHeight;
            Set(window, "_pageWidth", oldPageWidth); Set(window, "_widestPage", oldWidest); window.SetExpanded(true, instant: true); Pump();
        }
    }
    private static void WaitGridMotion()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Soft.Move + 100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
