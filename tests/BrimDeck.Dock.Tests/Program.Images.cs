using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class Program
{
    private static void CheckChatImageHost(MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string scratch, string directory)
    {
        var snapshot = history.Snapshot(); var query = view.QueryState; var presentation = view.CapturePresentation();
        bool mode = view.ApplicationsMode, grid = settings.Clipboard.GridView; var headerMode = settings.Clipboard.HeaderMode;
        double height = window.Height, width = window.Width;
        try
        {
            settings.Clipboard.GridView = false; settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge;
            var images = new List<ClipboardEntry>();
            foreach (var (name, w, h) in new[] { ("portrait", 300, 800), ("landscape", 900, 240), ("square", 300, 300) })
            {
                var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(new LinearGradientBrush(Colors.Teal, Colors.CornflowerBlue, 40), null, new Rect(0, 0, w, h));
                    context.DrawEllipse(Brushes.Gold, null, new Point(w / 2d, h / 2d), w / 4d, Math.Min(w, h) / 4d);
                }
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.Combine(scratch, "synthetic-qq-" + name + ".png");
                using (var stream = File.Create(path)) encoder.Save(stream);
                var data = new DataObject(); var paths = new StringCollection { path }; data.SetFileDropList(paths);
                var captured = ClipboardListener.ReadContent(data, settings.Clipboard, "QQ");
                Check("real QQ image capture " + name + ": file offer becomes bounded image pixels with original path", captured is { Kind: ClipboardContentKind.Image } && captured.PixelWidth == w && captured.PixelHeight == h && captured.Files.Single() == path);
                images.Add(new ClipboardEntry { Id = "qq-" + name, SourceName = "QQ", Content = captured!, CopiedAt = DateTimeOffset.UtcNow });
            }
            history.Restore(new ClipboardSnapshot { Items = images }, settings.Clipboard, DateTimeOffset.UtcNow);
            view.ApplicationsMode = false; view.QueryState = new(); view.ApplyLayout();
            window.Width = 1920; window.Height = 700;
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 1920, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 1920, 1032));
            foreach (var palette in new[] { palettes[0], palettes[1], palettes[^1] })
            foreach (int percent in new[] { 100, 150 })
            {
                settings.PanelScale = percent; Call(window, "ApplyScale"); Palette(palette); Call(window, "ApplyTheme");
                Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
                var list = All<ListBox>(view).Single(); list.ScrollIntoView(list.Items[0]); Pump();
                var cards = All<ClipboardCard>(view).Where(card => card.Entry is not null).ToArray();
                Check("real QQ image cards " + palette.Id + "-" + percent + ": image bodies replace filename tiles in the reference card size", cards.Length == 3 && cards.All(card => Near(card.ActualWidth, 210) && Near(card.ActualHeight, 146)
                    && All<System.Windows.Controls.Image>(card).Any(image => image.Source is BitmapSource && image.Stretch == Stretch.UniformToFill)
                    && All<TextBlock>(card).Any(text => text.Text == ClipboardContentTypes.Label(ClipboardContentKind.Image))));
                Render((IslandBorder)window.FindName("Island"), directory, "clipboard-qq-images-" + palette.Id + "-" + percent + ".png");
                foreach (var entry in history.Items)
                {
                    OpenAutomaticPreview(view,entry); Pump();
                    var body = (FrameworkElement)((Popup)Get(view, "_preview")!).Child;
                    Check("real QQ image preview " + palette.Id + "-" + percent + "-" + entry.Id + ": local image fills the preview instead of a file row", view.PreviewOpen && All<System.Windows.Controls.Image>(body).Any(image => image.Source is BitmapSource && image.Stretch == Stretch.Uniform));
                    if (entry.Id == "qq-portrait" && percent == 100) RenderPreview((IslandBorder)window.FindName("Island"), body, directory, "clipboard-qq-portrait-preview-" + palette.Id + ".png");
                    view.ClosePopups(); Pump();
                }
            }
            Check("real QQ image capture: rendering keeps originals and recording opt-out intact", images.All(entry => File.Exists(entry.Content.Files.Single())) && !settings.Clipboard.Enabled);
        }
        finally
        {
            view.ClosePopups(); settings.Clipboard.GridView = grid; settings.Clipboard.HeaderMode = headerMode; settings.PanelScale = 100;
            history.Restore(snapshot, settings.Clipboard, DateTimeOffset.UtcNow); view.ApplicationsMode = mode; view.QueryState = query; view.ApplyLayout();
            window.Width = width; window.Height = height; Palette(palettes[0]); Call(window, "ApplyTheme"); Call(window, "ApplyScale");
            Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump(); view.RestorePresentation(presentation);
        }
    }
}
