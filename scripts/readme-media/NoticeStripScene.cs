using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The collapsed count of unread notices (CompactPart.Notices): one application, three of five, more than 99 notices from four, a
// long application name in the tip, the bell in place of the icons, on the notch, the capsule and a notch on the left edge beside
// the other parts; then the notices settings page with its switch and the count's section, and the collapsed view's list of parts.
// No notice source is started: the notice center is only made, and the notices go straight to its list. For checking.
internal sealed partial class Renderer
{
    private async Task NoticeStripScene()
    {
        const double scale = 3;
        Program.SetProperty(app, "Exiting", true);
        bool animations = _settings.Animations;
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var occupied = _settings.Occupied; _settings.Occupied = WindowBehavior.Normal;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        // One application with an icon of its own, a green square drawn here; the others show their letters.
        string icon = Path.Combine(Path.GetTempPath(), "brimdeck-strip-icon.png");
        WriteIcon(icon);
        int serial = 0;
        void Add(string name, string? iconRef = null)
            => hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = "windows:" + name, AppName = name, AppId = name, InstanceKey = "windows:" + name + ":" + ++serial,
                Title = Loc.T("消息", "Message"), Body = Loc.T("内容", "Body"), IconRef = iconRef, CreatedAt = _now
            }, _now.ToUniversalTime());
        foreach (var name in new[] { "QQ", "Outlook", "Claude", "Backup Tool With A Very Long Name" })
            if (_settings.NoticeRule("windows:" + name) is null) _settings.NoticeApps.Add(new NoticeAppRule { Key = "windows:" + name, Name = name });
        _settings.NoticeAppsListed = true;
        _settings.NotchNotices = true; _settings.CapsuleNotices = true; _settings.NotchTransfer = false; _settings.CapsuleTransfer = false;
        _deck.SetExpanded(false, true);

        async Task Shots(string name)
        {
            foreach (var (style, edge) in new[] { (CompactStyle.Notch, ScreenEdge.Top), (CompactStyle.Capsule, ScreenEdge.Top), (CompactStyle.Notch, ScreenEdge.Left) })
            {
                _settings.Style = style; _settings.NotchEdge = edge; Refresh();
                await Settle(); await Task.Delay(300); Refresh(); await Settle();
                await Save($"notice-strip-{name}-{style.ToString().ToLowerInvariant()}-{edge.ToString().ToLowerInvariant()}", scale);
            }
            _settings.Style = CompactStyle.Notch; _settings.NotchEdge = ScreenEdge.Top;
        }

        Add("QQ", "file:" + icon);
        await Shots("1");
        Add("Outlook"); Add("QQ", "file:" + icon); Add("Claude"); Add("Outlook");
        await Shots("5");
        for (int i = 0; i < 120; i++) Add(i % 2 == 0 ? "Backup Tool With A Very Long Name" : "QQ", i % 2 == 0 ? null : "file:" + icon);
        await Shots("125");
        _settings.NoticeStripIcons = false;
        await Shots("125-bell");
        _settings.NoticeStripIcons = true;
        // Beside the other parts of the row: the music, the clock and the file count.
        _settings.NotchClock = true; _settings.CapsuleClock = true;
        await Shots("beside");
        _settings.NotchClock = false; _settings.CapsuleClock = false;
        Console.WriteLine("tip: " + ((FrameworkElement?)Program.Field(_deck, "_compactNotices"))?.ToolTip);
        // Seen on the notices page, the count goes.
        hub.MarkAllViewed();
        await Shots("none");

        await SettingsPage("notice-strip-settings", 19, height: 2200, tips: true);
        _settings.NoticeIsland = false; _settings.NoticesPage = false;
        await SettingsPage("notice-strip-settings-off", 19, height: 400, tips: false);
        _settings.NoticeIsland = true; _settings.NoticesPage = true;
        await SettingsPage("notice-strip-collapsed", 1, height: 1300, tips: true);

        _settings.NotchNotices = false; _settings.CapsuleNotices = false;
        _settings.Occupied = occupied; _settings.Animations = animations; Refresh();
        Program.SetProperty(app, "Exiting", false);
        File.Delete(icon);
        await Settle();
    }

    private static void WriteIcon(string path)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0x6B)), null, new Rect(8, 8, 112, 112), 26, 26);
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
