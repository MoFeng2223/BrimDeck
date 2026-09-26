using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    // Checks the rendered pixels, not only positions: the edge fades must stay on the viewport's own
    // edges while the rings or the text move underneath, and nothing at rest may lie inside them.
    private void VerifyEdgeFadePixels(string output, List<string> checks, CompactMusicLayout layout)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        double scale = VisualTreeHelper.GetDpi(Deck).DpiScaleX;
        (byte[] Pixels, int Width, int Height) Render(FrameworkElement element, string name)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize)); element.UpdateLayout();
            int width = (int)Math.Ceiling(element.ActualWidth * scale), height = (int)Math.Ceiling(element.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(element);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, name))) png.Save(stream);
            return (pixels, width, height);
        }
        // Total premultiplied brightness between two horizontal DIP positions.
        static double Ink((byte[] Pixels, int Width, int Height) image, double left, double right, double scale)
        {
            double sum = 0;
            for (int x = Math.Max(0, (int)Math.Floor(left * scale)); x < Math.Min(image.Width, (int)Math.Ceiling(right * scale)); x++)
                for (int y = 0; y < image.Height; y++) { int i = (y * image.Width + x) * 4; sum += image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]; }
            return sum;
        }

        // Identical full rings: every visible slot must carry the same ink at rest and at the end of travel.
        var items = Enumerable.Range(0, 5).Select(i => (100.0, "#FFFFFF", "")).ToArray();
        var carousel = new QuotaCarousel(items, layout, animate: false) { Margin = new Thickness(0) };
        var strip = (Canvas)typeof(QuotaCarousel).GetField("_strip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(carousel)!;
        var shift = (TranslateTransform)strip.RenderTransform;
        var rings = strip.Children.Cast<FrameworkElement>().ToArray();
        double Slot(int ring) => Canvas.GetLeft(rings[ring]);
        // Rings sit on whole device pixels, so identical rings must produce identical column profiles,
        // including two empty columns on each side: no fade, and no stretched edge column from the mask layer.
        bool Even((byte[] Pixels, int Width, int Height) image, IEnumerable<int> visible, double offset)
        {
            double[] Profile(int ring)
            {
                int left = (int)Math.Round((Slot(ring) + offset) * scale) - 2, width = (int)Math.Round(layout.RingSize * scale) + 4;
                return Enumerable.Range(left, width).Select(x => x < 0 || x >= image.Width ? -1.0 : Enumerable.Range(0, image.Height)
                    .Sum(y => { int i = (y * image.Width + x) * 4; return (double)image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]; })).ToArray();
            }
            var profiles = visible.Select(Profile).ToArray(); var reference = profiles[profiles.Length / 2];
            double limit = reference.Max() * .02;
            return reference.Max() > 0 && profiles.All(p => p.Zip(reference).All(c => c.First >= 0 && Math.Abs(c.First - c.Second) <= limit));
        }
        int count = items.Length, shown = layout.VisibleRings;
        shift.X = 0; var rest = Render(carousel, "fade-rings-rest.png");
        Check("Pixels: resting rings are all drawn at full strength", Even(rest, Enumerable.Range(0, shown), 0));
        shift.X = -carousel.Travel; var end = Render(carousel, "fade-rings-end.png");
        Check("Pixels: rings at the end of travel are all drawn at full strength", Even(end, Enumerable.Range(count - shown, shown), -carousel.Travel));
        // Mid-scroll the first ring straddles the left edge and is faded there; the fade does not follow the strip.
        double straddle = -Slot(0); shift.X = straddle; var moving = Render(carousel, "fade-rings-moving.png");
        double edgeInk = Ink(moving, 0, layout.RingSize, scale), fullInk = Ink(rest, Slot(1) - .5, Slot(1) + layout.RingSize + .5, scale);
        Check("Pixels: a ring crossing the edge is faded by the viewport", edgeInk < fullInk * .95 && Even(moving, Enumerable.Range(1, shown - 1), straddle));

        // Overflowing text: at rest nothing is hidden on the left, so the first characters match an unmasked
        // rendering and only the far edge fades. Once the text has moved, the near edge fades as well.
        var marquee = new MusicMarquee { Text = "为你唱首歌 · 痛仰乐队 · 为你唱首歌 · 痛仰乐队", Width = 100, Height = 22, CenterWhenFits = false };
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var textShift = (TranslateTransform)typeof(MusicMarquee).GetField("_shift", Private)!.GetValue(marquee)!;
        var leftStop = (GradientStop)typeof(MusicMarquee).GetField("_left", Private)!.GetValue(marquee)!;
        var restMasked = Render(marquee, "fade-text-rest.png");
        textShift.X = -20; leftStop.Offset = MusicMarquee.Edge / marquee.Width; var movingMasked = Render(marquee, "fade-text-moving.png");
        var mask = marquee.OpacityMask; marquee.OpacityMask = null;
        var movingPlain = Render(marquee, "fade-text-moving-plain.png");
        textShift.X = 0; var restPlain = Render(marquee, "fade-text-plain.png"); marquee.OpacityMask = mask;
        double Near((byte[], int, int) image) => Ink(image, 0, 14, scale);
        double Far((byte[], int, int) image) => Ink(image, marquee.Width - MusicMarquee.Edge, marquee.Width, scale);
        Check("Pixels: the first characters of resting text are not faded", Near(restPlain) > 0 && Math.Abs(Near(restMasked) - Near(restPlain)) <= Near(restPlain) * .01);
        Check("Pixels: overflowing text is faded at the far edge", Far(restPlain) > 0 && Far(restMasked) < Far(restPlain) * .8);
        Check("Pixels: moving text fades at the near edge inside its own box", Ink(movingPlain, 0, MusicMarquee.Edge, scale) > 0
            && Ink(movingMasked, 0, MusicMarquee.Edge, scale) < Ink(movingPlain, 0, MusicMarquee.Edge, scale) * .8);
    }
}
