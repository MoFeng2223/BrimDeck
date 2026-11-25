using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The notices' "Apps" settings page in its states: the common apps receiving, with nothing yet, not installed and off in Windows;
// those rows opened (the steps, WeChat's two ways); the other apps; the page at the settings window's narrowest; and the menu that
// adds an application from the notice history, with and without a match. Windows' notification settings are replaced by a
// dictionary (Feishu's notifications off), so the registry is neither read nor written, and whether an app is installed is
// answered here (DingTalk is not). For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task NoticeAppsScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        foreach (var listed in DeckSettings.DefaultNoticeApps()) if (_settings.NoticeRule(listed.Key) is null) _settings.NoticeApps.Add(listed);
        _settings.NoticeAppsListed = true;
        string edge = "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!App";
        string adobe = "Adobe Creative Cloud Background Update And Sync Service";
        _settings.NoticeApps.Add(new NoticeAppRule { Key = "windows:Claude", Name = "Claude" });
        _settings.NoticeApps.Add(new NoticeAppRule { Key = "windows:" + edge + "|bilibili.com", Name = "bilibili.com · Microsoft Edge", Enabled = false });
        _settings.NoticeApps.Add(new NoticeAppRule { Key = "windows:" + adobe, Name = adobe, Level = NoticeLevel.Silent });
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        Program.SetField(center, "<BannerRegistry>k__BackingField", new FakeBanners(["com.electron.Feishu"]));
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        int serial = 0;
        void Add(string appId, string name, string title, int minutesAgo)
            => hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = "windows:" + appId, AppName = name, AppId = appId, InstanceKey = "windows:" + appId + ":" + ++serial, Title = title,
                Body = Loc.T("内容", "Body"), Category = NoticeCategory.Info, CreatedAt = _now.AddMinutes(-minutesAgo)
            }, _now.AddMinutes(-minutesAgo).ToUniversalTime());
        Add("com.squirrel.Discord.Discord", "Discord", Loc.T("#general 有新消息", "New messages in #general"), 60 * 24 * 6);
        Add("com.nvidia.nvapp", "NVIDIA App", Loc.T("有新的驱动程序可以安装", "A new driver is ready to install"), 60 * 24 * 4);
        Add("Microsoft.Windows.SecHealthUI_cw5n1h2txyewy!SecHealthUI", Loc.T("Windows 安全中心", "Windows Security"), Loc.T("病毒和威胁防护：扫描已完成", "Virus & threat protection: scan finished"), 60 * 22);
        Add("BiliBiliPC", Loc.T("哔哩哔哩", "Bilibili"), Loc.T("你关注的 UP 主发布了新视频", "A creator you follow posted a new video"), 60 * 20);
        Add(adobe, adobe, Loc.T("有可用的更新", "Updates are available"), 60 * 30);
        Add("NotifyIconGeneratedAumid_4417", Loc.T("托盘程序", "Tray app"), Loc.T("已连接到代理服务器", "Connected to the proxy server"), 70);
        Add(edge, "Microsoft Edge", Loc.T("下载完成：季度报告-最终版-修订第三稿-给财务部门确认.pdf", "Download finished: quarterly-report-final-revision-three-for-finance-review.pdf"), 26);
        Add("Claude", "Claude", Loc.T("已完成：重构设置页面", "Done: settings page rework"), 18);
        Add("Microsoft.VisualStudioCode", "Visual Studio Code", Loc.T("任务“build”已完成", "Task \"build\" finished"), 10);
        Add("QQ", "QQ", Loc.T("产品群", "Product team"), 3);
        string wechat = WeChatRows.AppKey, qq = NoticeKeys.Windows("QQ"), dingtalk = NoticeKeys.Windows("ali.dingtalk.release"), feishu = NoticeKeys.Windows("com.electron.Feishu");
        Action<SettingsWindow> Installed(bool dingTalk, params string[] open) => window =>
        {
            Program.SetField(window, "AppInstalled", (Func<string, bool>)(id => dingTalk || id != "ali.dingtalk.release"));
            foreach (var key in open) ((HashSet<string>)Program.Field(window, "_noticeAppsOpen")!).Add(key);
        };
        await SettingsPage("notice-apps", 20, height: 1400, tips: true, theme: SettingsTheme.Light, prepare: Installed(false));
        await SettingsPage("notice-apps-dark", 20, height: 1400, theme: SettingsTheme.Dark, prepare: Installed(false));
        await SettingsPage("notice-apps-narrow", 20, height: 1400, width: 840, theme: SettingsTheme.Light, prepare: Installed(true));
        await SettingsPage("notice-apps-open", 20, height: 2000, theme: SettingsTheme.Light, prepare: Installed(true, wechat, qq, dingtalk, feishu));
        await SettingsPage("notice-apps-open-narrow", 20, height: 2000, width: 840, theme: SettingsTheme.Light, prepare: Installed(true, wechat, dingtalk, feishu));
        _settings.NoticeRule(wechat)!.ReadContent = true;
        _settings.NoticeRule(qq)!.Enabled = false;
        await SettingsPage("notice-apps-wechat-read", 20, height: 1100, theme: SettingsTheme.Light, prepare: Installed(true, wechat));
        _settings.NoticeRule(wechat)!.ReadContent = false;
        _settings.NoticeRule(qq)!.Enabled = true;
        await Picker("notice-apps-picker", "", SettingsTheme.Light);
        await Picker("notice-apps-picker-dark", "", SettingsTheme.Dark);
        await Picker("notice-apps-picker-none", Loc.T("企业微信", "WeCom"), SettingsTheme.Light);
        // No other app listed.
        _settings.NoticeApps.RemoveAll(rule => rule.Key is "windows:Claude" || rule.Key.StartsWith("windows:" + edge, StringComparison.Ordinal) || rule.Key == "windows:" + adobe
            || rule.Key == NoticeKeys.Windows("Microsoft.WindowsAlarms_8wekyb3d8bbwe!App"));
        await SettingsPage("notice-apps-empty", 20, height: 1100, theme: SettingsTheme.Light, prepare: Installed(true));
    }

    // The menu of "从通知记录添加" as it opens, drawn without its popup.
    private async Task Picker(string name, string filter, SettingsTheme theme)
    {
        const double scale = 1.5;
        _settings.Theme = theme;
        var window = new SettingsWindow(app);
        window.ShowPage(20);
        var (menu, search) = ((Border, TextBox))Program.Call(window, "NoticeAppPicker", [typeof(Action)], (Action)(() => { }))!;
        search.Text = filter;
        var resources = window.Resources;
        window.Content = null;
        var root = new Border { Child = menu, Padding = new Thickness(16), Background = (Brush)resources["SettingsSurface"], Resources = resources };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); root.Arrange(new Rect(root.DesiredSize));
        await Settle();
        root.UpdateLayout();
        var size = root.DesiredSize;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); bitmap.Freeze();
        Write(bitmap, Path.Combine(output, name + ".png"));
        Console.WriteLine(name);
        try { window.Close(); } catch (ArgumentOutOfRangeException) { }
    }

    // Windows' per-application notification settings, as a dictionary: the listed applications have their notifications off.
    private sealed class FakeBanners(string[] off) : IBannerRegistry
    {
        public BannerReading Read(string appId) => off.Contains(appId, StringComparer.OrdinalIgnoreCase) ? new(true, null, 0) : new(false, null, null);
        public void Write(string appId, int? showBanner) { }
        public void WriteValue(string appId, BannerValue value) { }
        public void DeleteKeyIfEmpty(string appId) { }
    }
}
