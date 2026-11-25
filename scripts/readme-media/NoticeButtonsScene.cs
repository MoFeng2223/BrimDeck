using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// A notice's buttons on the island (IslandNotice.BuildButtons), in the notch and the capsule: with the panel opening on a click, on
// hover after a long delay and on hover at once (none), with one, two and more buttons, BrimDeck's own (copy code, open), long
// labels, long words beside them and no icon. For checking, not for the README. The window is never shown, as in NoticeLevelsScene.
internal sealed partial class Renderer
{
    private async Task NoticeButtonsScene()
    {
        const double scale = 3, pad = 14, labelHeight = 18;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var type = Type.GetType("BrimDeck.IslandNotice, BrimDeck")!;
        bool wasClick = _settings.ClickToExpand, wasCodes = _settings.NoticeCodes;
        int wasDelay = _settings.OpenDelay;
        NoticeAction allow = new(Loc.T("allow", "allow"), Loc.T("允许", "Allow"), Primary: true), deny = new("deny", Loc.T("拒绝", "Deny")), always = new("always", Loc.T("总是允许", "Always allow"));
        NoticeAction copy = new("copy-1", Loc.T("复制", "Copy"), NoticeActionKind.Copy, "482913"), page = new("open-1", Loc.T("打开网页", "Open page"), NoticeActionKind.Open, "https://example.com/");
        NoticeAction wordy = new("long", Loc.T("重新运行全部失败的测试并生成报告", "Run every failed test again and write the report"), Primary: true);
        string ask = Loc.T("请求运行命令", "Run this command?"), command = "dotnet test";
        string longer = Loc.T("部署到生产环境之前需要你确认：本次包含 14 个提交，涉及数据库迁移与缓存清理", "Your approval is needed before deploying to production: 14 commits, with a database migration and a cache purge");
        bool wasNotchAuto = _settings.NotchAutoWidth, wasCapsuleAuto = _settings.CapsuleAutoWidth;
        double wasNotch = _settings.NotchWidth, wasCapsule = _settings.CapsuleWidth;
        // The island as it is by default, and a wide one, where two buttons have room.
        foreach (var (style, wide) in new[] { (CompactStyle.Notch, false), (CompactStyle.Notch, true), (CompactStyle.Capsule, false), (CompactStyle.Capsule, true) })
        {
            _settings.NotchAutoWidth = _settings.CapsuleAutoWidth = !wide;
            _settings.NotchWidth = wide ? 320 : wasNotch; _settings.CapsuleWidth = wide ? 320 : wasCapsule;
            _settings.Style = style; Refresh();
            await Settle();
            var window = (Window)Activator.CreateInstance(type, Program.Any, null, [_deck, app], null)!;
            var shots = new List<(string Label, BitmapSource Picture, Size Size)>();
            void Shot(string label, Notice notice, bool click = true, int delay = 0, bool codes = false)
            {
                _settings.ClickToExpand = click; _settings.OpenDelay = delay; _settings.NoticeCodes = codes;
                var item = new AttentionItem { Lane = "main", Kind = AttentionKind.Notice, Key = notice.Id, Payload = notice, Level = NoticeLevel.Persistent, Duration = TimeSpan.FromSeconds(6) };
                Program.SetField(window, "_item", item);
                Program.Call(window, "Layout", []); Program.Call(window, "Dress", []); Program.Call(window, "BuildContent", []); Program.Call(window, "KeepAt", [typeof(double)], scale);
                var stage = (FrameworkElement)window.Content;
                var size = new Size(window.Width, window.Height);
                stage.Measure(size); stage.Arrange(new Rect(size)); stage.UpdateLayout();
                var picture = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                picture.Render(stage); picture.Freeze();
                shots.Add((label, picture, size));
            }
            Notice Made(string title, string body, params NoticeAction[] actions) => new()
            {
                AppKey = "cli:Claude Code", AppName = "Claude Code", Origin = NoticeOrigin.Cli, Title = title, Body = body, ReceivedAt = _now, CreatedAt = _now,
                Actions = [.. actions], Capabilities = actions.Any(action => action.Kind == NoticeActionKind.Reply) ? NoticeCapabilities.Actions : NoticeCapabilities.None, Awaited = true
            };
            Shot("Click to expand: two buttons, the main one first", Made(ask, command, deny, allow));
            Shot("Click to expand: three given, the island shows two", Made(ask, command, allow, deny, always));
            Shot("Click to expand: one button", Made(Loc.T("构建完成", "Build finished"), Loc.T("用时 2 分 14 秒", "Took 2 min 14 s"), page));
            Shot("Click to expand: long words beside two buttons", Made(Loc.T("部署", "Deploy"), longer, allow, deny));
            Shot("Click to expand: a long label is cut", Made(ask, command, wordy, deny));
            Shot("Click to expand: copy and open buttons of the sender", Made(Loc.T("GitHub 验证码", "GitHub code"), "482913", copy, page));
            var windows = new Notice
            {
                AppKey = "windows:Mail", AppName = "Outlook", AppId = "Mail", Origin = NoticeOrigin.Windows, Title = Loc.T("GitHub", "GitHub"),
                Body = Loc.T("你的验证码是 482913，10 分钟内有效", "Your verification code is 482913, valid for 10 minutes"), ReceivedAt = _now, CreatedAt = _now, Capabilities = NoticeCapabilities.OpenApp
            };
            Shot("Click to expand: BrimDeck's own, copy code and open (a Windows notification with a code)", windows, codes: true);
            Shot("Click to expand: a Windows notification, open only", windows);
            var bare = Made(ask, command, allow, deny); bare.HideIcon = true;
            Shot("Click to expand: no icon", bare);
            Shot("Hover after 600 ms: buttons show", Made(ask, command, allow, deny), click: false, delay: 600);
            Shot("Hover at once: no buttons, the notices page has them", Made(ask, command, allow, deny), click: false, delay: 0);
            Shot("Click to expand: a notice without buttons is as before", Made(Loc.T("产品群", "Product team"), Loc.T("收到", "Got it")));
            Program.SetField(window, "_item", null);
            window.Close();
            double width = Math.Max(420, shots.Max(shot => shot.Size.Width)) + 2 * pad, height = shots.Sum(shot => shot.Size.Height + labelHeight + pad) + pad;
            var sheet = new DrawingVisual();
            using (var context = sheet.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x66)), null, new Rect(0, 0, width, height));
                double y = pad;
                foreach (var (label, picture, size) in shots)
                {
                    context.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1), new Point(pad, y));
                    y += labelHeight;
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash }, new Rect(pad, y, size.Width, size.Height));
                    context.DrawImage(picture, new Rect(pad, y, size.Width, size.Height));
                    y += size.Height + pad;
                }
            }
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(sheet); bitmap.Freeze();
            string name = $"notice-buttons-{style.ToString().ToLowerInvariant()}{(wide ? "-wide" : "")}";
            Write(bitmap, Path.Combine(output, name + ".png"));
            Console.WriteLine(name);
        }
        _settings.NotchAutoWidth = wasNotchAuto; _settings.CapsuleAutoWidth = wasCapsuleAuto; _settings.NotchWidth = wasNotch; _settings.CapsuleWidth = wasCapsule;
        _settings.ClickToExpand = wasClick; _settings.OpenDelay = wasDelay; _settings.NoticeCodes = wasCodes;
        _settings.Style = CompactStyle.Notch; Refresh();
    }
}
