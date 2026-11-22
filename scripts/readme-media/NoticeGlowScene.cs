using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The island's light when a notice arrives (IslandNotice: the shade, the backlight and the light along its edge), in every built-in
// theme and both looks of those with a night look, each on a light and on a dark desktop: the capsule, the notch and the indicator
// at the second flash with the liquid's ripples (white), and at the first flash in a chosen color without them. And a long capsule
// and a long notch over the course of their motion. For checking, not for the README. The window is never shown, as in
// NoticeLevelsScene.
internal sealed partial class Renderer
{
    private async Task NoticeGlowScene()
    {
        const double pad = 12, labelHeight = 16;
        Program.SetProperty(app, "Notices", Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!);
        var type = Type.GetType("BrimDeck.IslandNotice, BrimDeck")!;
        var theme = Type.GetType("BrimDeck.PanelTheme, BrimDeck")!;
        var use = theme.GetMethod("Use", [typeof(DeckSettings), typeof(bool), typeof(bool)])!;
        bool wasAnimations = _settings.Animations, wasNotchAuto = _settings.NotchAutoWidth, wasCapsuleAuto = _settings.CapsuleAutoWidth;
        double wasNotch = _settings.NotchWidth, wasCapsule = _settings.CapsuleWidth;
        string wasTheme = _settings.PanelTheme, wasDaylight = _settings.ThemeDaylight;
        Color light = Color.FromRgb(0xEE, 0xF1, 0xF5), dark = Color.FromRgb(0x16, 0x18, 0x1C);
        // The day look: the day began an hour ago and lasts twelve. The night look: it begins in two hours.
        var dawn = TimeSpan.FromHours((DateTime.Now.Hour + 23) % 24);
        string day = PanelThemes.FormatDaylight(dawn, TimeSpan.FromHours((dawn.Hours + 12) % 24));
        var later = TimeSpan.FromHours((DateTime.Now.Hour + 2) % 24);
        string night = PanelThemes.FormatDaylight(later, TimeSpan.FromHours((later.Hours + 12) % 24));
        var looks = new List<(string Id, string Name, string Daylight)>();
        foreach (var info in PanelThemes.BuiltIns)
        {
            looks.Add((info.Id, info.DisplayName, day));
            if (info.Id is PanelThemes.IslandHoliday or PanelThemes.IslandHoliday2) looks.Add((info.Id, info.DisplayName + " (night)", night));
        }
        void Look(string id, string daylight)
        {
            _settings.PanelTheme = id; _settings.ThemeDaylight = daylight;
            use.Invoke(null, [_settings, false, false]);
        }
        BitmapSource Take(Window window, double scale, double time, NoticeLevel level, NoticeStyle look, out Size size)
        {
            var notice = new Notice { AppKey = "windows:Chat", AppName = "QQ", Title = Loc.T("产品群", "Product team"), Body = Loc.T("收到", "Got it"), ReceivedAt = _now, CreatedAt = _now };
            var item = new AttentionItem { Lane = "main", Kind = AttentionKind.Notice, Key = notice.Id, Payload = notice, Level = level, Duration = TimeSpan.FromSeconds(6), Style = look };
            Program.SetField(window, "_item", item);
            Program.Call(window, "Layout", []); Program.Call(window, "Dress", []); Program.Call(window, "BuildContent", []);
            Program.Call(window, "StartMotion", []);
            if (Program.Field(window, "_liquid") is not null) Program.Call(window, "Step", [typeof(double)], (double)Program.Field(window, "_start")! + time);
            var stage = (FrameworkElement)window.Content;
            size = new Size(window.Width, window.Height);
            stage.Measure(size); stage.Arrange(new Rect(size)); stage.UpdateLayout();
            var picture = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            picture.Render(stage); picture.Freeze();
            Program.Call(window, "StopMotion", []);
            return picture;
        }
        void Sheet(string name, double scale, List<(string Label, List<(BitmapSource Picture, Size Size, Color Desk)> Cells)> rows)
        {
            double width = rows.Max(row => row.Cells.Sum(cell => cell.Size.Width + pad)) + pad, height = rows.Sum(row => labelHeight + row.Cells.Max(cell => cell.Size.Height) + pad) + pad;
            var sheet = new DrawingVisual();
            using (var context = sheet.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)), null, new Rect(0, 0, width, height));
                double y = pad;
                foreach (var (label, cells) in rows)
                {
                    context.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1), new Point(pad, y));
                    y += labelHeight;
                    double x = pad;
                    foreach (var (picture, size, desk) in cells)
                    {
                        context.DrawRectangle(new SolidColorBrush(desk), null, new Rect(x, y, size.Width, size.Height));
                        context.DrawImage(picture, new Rect(x, y, size.Width, size.Height));
                        x += size.Width + pad;
                    }
                    y += cells.Max(cell => cell.Size.Height) + pad;
                }
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(sheet); bitmap.Freeze();
            Write(bitmap, Path.Combine(output, name + ".png"));
            Console.WriteLine(name);
        }

        _settings.Animations = true;
        // Every theme: white with the ripples at the second flash, and red without them at the first, each on both desktops.
        foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch, CompactStyle.Line })
        {
            var rows = new List<(string, List<(BitmapSource, Size, Color)>)>();
            foreach (var (id, name, daylight) in looks)
            {
                Look(id, daylight);
                _settings.Style = style; Refresh();
                await Settle();
                var window = (Window)Activator.CreateInstance(type, Program.Any, null, [_deck, app], null)!;
                bool line = style == CompactStyle.Line;
                var white = Take(window, 2, line ? IslandLiquid.Breath / 2 : .42, NoticeLevel.Glow, new NoticeStyle { Liquid = true }, out var size);
                var red = Take(window, 2, line ? IslandLiquid.Breath / 2 : .12, NoticeLevel.Persistent, new NoticeStyle { Liquid = false, Color = "#EF6A6A" }, out _);
                var rest = Take(window, 2, 5, NoticeLevel.Glow, new NoticeStyle { Liquid = false }, out _);
                Program.SetField(window, "_item", null);
                window.Close();
                rows.Add(($"{name}:  white, lively, on a light and a dark desktop;  red, glow only;  at rest", [(white, size, light), (white, size, dark), (red, size, light), (red, size, dark), (rest, size, light), (rest, size, dark)]));
            }
            Sheet($"notice-glow-{style.ToString().ToLowerInvariant()}", 2, rows);
        }

        // A long island over the course of its motion, in the dark and the light theme.
        _settings.NotchAutoWidth = _settings.CapsuleAutoWidth = false;
        _settings.NotchWidth = _settings.CapsuleWidth = 1340;
        foreach (var style in new[] { CompactStyle.Capsule, CompactStyle.Notch })
        {
            var rows = new List<(string, List<(BitmapSource, Size, Color)>)>();
            foreach (string id in new[] { PanelThemes.Dark, PanelThemes.Light })
            {
                Look(id, day);
                _settings.Style = style; Refresh();
                await Settle();
                var window = (Window)Activator.CreateInstance(type, Program.Any, null, [_deck, app], null)!;
                foreach (double time in new[] { .12, .3, .42, .6, .9, 1.3, 1.8, 2.3 })
                {
                    var picture = Take(window, 1.5, time, NoticeLevel.Glow, new NoticeStyle { Liquid = true }, out var size);
                    rows.Add((string.Create(CultureInfo.InvariantCulture, $"{id}, 1340 wide, t = {time:0.##} s"), [(picture, size, id == PanelThemes.Dark && time < 1 ? light : time < 1 ? dark : light)]));
                }
                Program.SetField(window, "_item", null);
                window.Close();
            }
            Sheet($"notice-glow-long-{style.ToString().ToLowerInvariant()}", 1.5, rows);
        }

        _settings.NotchAutoWidth = wasNotchAuto; _settings.CapsuleAutoWidth = wasCapsuleAuto; _settings.NotchWidth = wasNotch; _settings.CapsuleWidth = wasCapsule;
        _settings.Animations = wasAnimations; _settings.Style = CompactStyle.Notch;
        Look(wasTheme, wasDaylight); Refresh();
    }
}
