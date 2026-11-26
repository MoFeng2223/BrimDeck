using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Mail;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The "邮箱" settings page without a mailbox and with mailboxes in each state (connected with mail lately, connecting, connection
// lost, refused, switched off, a very long address, a mailbox with its own server opened), in the light and the dark theme; the
// window that adds a mailbox at its first step (empty, an address recognized, an address not recognized, words that filter the
// list, words that find nothing) and its second (every service; QQ connecting and refused; Outlook waiting for the browser), the
// window that connects a mailbox again, and the question before one is removed. Then mail notices with the services' icons and
// the envelope, on the island and on the notices page. No mailbox is connected: the watches are made and never started, and their
// states are set by hand. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task MailScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var notices = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", notices);
        var hub = (NoticeHub)notices.GetType().GetProperty("Hub", Program.Any)!.GetValue(notices)!;
        var centerType = Type.GetType("BrimDeck.MailCenter, BrimDeck")!;
        var center = Activator.CreateInstance(centerType, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Mail", center);
        _settings.MailAccounts = [];
        var themes = new[] { (SettingsTheme.Light, "light"), (SettingsTheme.Dark, "dark") };
        foreach (var (theme, name) in themes) await SettingsPage("mail-settings-empty-" + name, 22, height: 560, tips: theme == SettingsTheme.Light, theme: theme);

        var entries = (IDictionary)Program.Field(center, "_entries")!;
        var entryType = centerType.GetNestedType("Entry", Program.Any)!;
        int uid = 0;
        MailAccount Add(string provider, string address, MailStatus? status, bool enabled = true, string host = "", string user = "", int? mailMinutesAgo = null)
        {
            var account = new MailAccount { Provider = provider, Address = address, Enabled = enabled, Host = host, UserName = user };
            _settings.MailAccounts.Add(account);
            // Mail that came: the notices record its source, which the row reads as "最近收到".
            if (mailMinutesAgo is { } ago)
            {
                var at = _now.AddMinutes(-ago).ToUniversalTime();
                hub.Receive(MailNotices.Notice(account, new MailMessage(1, (uint)++uid, "GitHub", "noreply@github.com", "[GitHub] Sign-in code", "482913", at), at), at);
            }
            if (status is null || !enabled) return account;
            var watch = new MailWatch(new MailEndpoint("127.0.0.1", 9, address, Tls: false), _ => throw new MailSecretException(""), _ => { }, _ => { });
            var entry = Activator.CreateInstance(entryType, Program.Any, null, [account, watch], null)!;
            entryType.GetProperty("Status")!.SetValue(entry, status);
            entries[account.Id] = entry;
            return account;
        }
        var qq = Add(MailProviders.Qq, "lin.chen@qq.com", new(MailState.Watching), mailMinutesAgo: 5);
        var company = Add(MailProviders.Custom, "lin.chen@company.com", new(MailState.Watching), host: "imap.company.com", user: "c00123456", mailMinutesAgo: 95);
        var gmail = Add(MailProviders.Gmail, "linchen.dev@gmail.com", new(MailState.Refused, "AUTHENTICATIONFAILED Invalid credentials"));
        Add(MailProviders.NetEase, "linchen@163.com", new(MailState.Retrying, "The operation has timed out."), mailMinutesAgo: 60 * 26);
        var outlook = Add(MailProviders.Outlook, "lin.chen@outlook.com", null, enabled: false);
        Add(MailProviders.ICloud, "paperboat@icloud.com", new(MailState.Connecting));
        Add(MailProviders.Exmail, "zhang.wei@studio-with-a-very-long-company-domain-name.example.com", new(MailState.Watching));
        _settings.NoticeApps.Add(new() { Key = gmail.AppKey, Name = "Gmail", Level = NoticeLevel.Silent });
        void Open(SettingsWindow window) => ((HashSet<Guid>)Program.Field(window, "_mailOpen")!).Add(company.Id);
        foreach (var (theme, name) in themes) await SettingsPage("mail-settings-" + name, 22, height: 980, theme: theme, prepare: Open);
        await SettingsPage("mail-settings-narrow-light", 22, height: 980, width: 840, theme: SettingsTheme.Light, prepare: Open);
        _settings.NoticeIsland = false; _settings.NoticesPage = false;
        await SettingsPage("mail-settings-notices-off-light", 22, height: 980, theme: SettingsTheme.Light);
        _settings.NoticeIsland = true; _settings.NoticesPage = true;

        var shellType = typeof(SettingsWindow).GetNestedType("MailDialogShell", Program.Any)!;
        foreach (var (theme, name) in themes)
        {
            _settings.Theme = theme;
            var window = new SettingsWindow(app);
            async Task Render(string file, FrameworkElement frame, double width)
            {
                const double scale = 1.5;
                var host = new Border { Child = frame, Resources = window.Resources, Width = width };
                System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
                TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
                host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
                await Settle();
                host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(width * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(host); bitmap.Freeze();
                Write(bitmap, Path.Combine(output, file + "-" + name + ".png"));
                Console.WriteLine(file + "-" + name + " " + host.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
                host.Child = null;
            }
            async Task Dialog(string file, Action<object, StackPanel> fill)
            {
                var body = new StackPanel();
                var heading = new DockPanel();
                var frame = (FrameworkElement)Program.Call(window, "SyncDialogFrame", [typeof(string), typeof(StackPanel), typeof(Action), typeof(FrameworkElement)], "", body, (Action)(() => { }), heading)!;
                var shell = Activator.CreateInstance(shellType, Program.Any, null, [new Window(), heading, body], null)!;
                fill(shell, body);
                await Render(file, frame, 460);
            }
            void Providers(object shell, string typed) => Program.Call(window, "ShowMailProviders", [shellType, typeof(CancellationToken), typeof(string)], shell, CancellationToken.None, typed);
            void Form(object shell, string provider, MailAccount? existing = null, string address = "")
                => Program.Call(window, "ShowMailForm", [shellType, typeof(MailProvider), typeof(MailAccount), typeof(CancellationToken), typeof(string), typeof(string)],
                    shell, MailProviders.Find(provider), existing, CancellationToken.None, address, address);
            // The line beside the button, as the window sets it while it connects or after it failed.
            void Status(StackPanel body, string text, bool error, bool busy)
                => ((Action<string, bool, bool>)Descendants<FrameworkElement>(body).First(e => AutomationProperties.GetAutomationId(e) == "mail-status").Tag)(text, error, busy);

            await Dialog("mail-pick", (shell, _) => Providers(shell, ""));
            await Dialog("mail-pick-recognized", (shell, _) => Providers(shell, "lin.chen@qq.com"));
            await Dialog("mail-pick-unknown", (shell, _) => Providers(shell, "lin.chen@company.com"));
            await Dialog("mail-pick-filter", (shell, _) => Providers(shell, Loc.T("网易", "netease")));
            await Dialog("mail-pick-none", (shell, _) => Providers(shell, "zzz"));
            foreach (var provider in MailProviders.All)
                if (theme == SettingsTheme.Light || provider.Id is MailProviders.Qq or MailProviders.Outlook or MailProviders.Custom)
                    await Dialog("mail-form-" + provider.Id, (shell, _) => Form(shell, provider.Id, address: provider.Id switch
                    {
                        MailProviders.Qq => "lin.chen@qq.com", MailProviders.Custom => "lin.chen@company.com", _ => ""
                    }));
            await Dialog("mail-form-qq-busy", (shell, body) => { Form(shell, MailProviders.Qq, address: "lin.chen@qq.com"); Status(body, Loc.T("正在连接 imap.qq.com…", "Connecting to imap.qq.com…"), false, true); });
            await Dialog("mail-form-qq-refused", (shell, body) =>
            {
                Form(shell, MailProviders.Qq, address: "lin.chen@qq.com");
                Status(body, Loc.T("登录被拒绝。请确认授权码正确，并且已开启 IMAP 服务。", "The mail server refused the sign-in. Check the authorization code and that IMAP is turned on."), true, false);
            });
            await Dialog("mail-form-outlook-waiting", (shell, body) => { Form(shell, MailProviders.Outlook); Status(body, Loc.T("等待在浏览器中登录…", "Waiting for the sign-in in the browser…"), false, true); });
            await Dialog("mail-again-gmail", (shell, _) => Form(shell, MailProviders.Gmail, gmail));
            await Dialog("mail-again-outlook", (shell, _) => Form(shell, MailProviders.Outlook, outlook));
            await Dialog("mail-again-custom", (shell, _) => Form(shell, MailProviders.Custom, company));

            var identity = (FrameworkElement)Program.Call(window, "MailIdentity", [typeof(MailProvider), typeof(string), typeof(string)], MailProviders.Find(MailProviders.Qq), qq.Address, MailProviders.Find(MailProviders.Qq).Name)!;
            string secret = MailProviders.SecretName(MailProviders.Find(MailProviders.Qq));
            var confirm = (FrameworkElement)typeof(SettingsWindow).GetMethod("ConfirmFrame", Program.Any)!.Invoke(window, [window, Loc.T("移除这个邮箱？", "Remove this mailbox?"),
                Loc.Joined(Loc.T($"同时删除保存在本机的 {secret}。邮箱里的邮件不受影响。", $"This also deletes its {secret.ToLowerInvariant()} saved on this PC. The mail in the mailbox is not touched.")),
                Loc.T("移除", "Remove"), true, identity, (Action<bool>)(_ => { }), null])!;
            await Render("mail-confirm", confirm, 400);
            try { window.Close(); } catch (ArgumentOutOfRangeException) { }
        }

        await MailNoticesScene(notices, hub, qq, company);
    }

    // Mail notices on the island and on the notices page: a listed service's with its icon, a mailbox with its own server's with the
    // envelope (and its domain as the source's name).
    private async Task MailNoticesScene(object notices, NoticeHub hub, MailAccount qq, MailAccount company)
    {
        var icons = notices.GetType().GetProperty("Icons", Program.Any)!.GetValue(notices)!;
        var get = icons.GetType().GetMethod("Get", Program.Any)!;
        bool Loading(string reference) => (bool)Program.Call(icons, "IsLoading", [typeof(string)], reference)!;
        var gmail = new MailAccount { Provider = MailProviders.Gmail, Address = "linchen.dev@gmail.com" };
        // The icons are read in the background; the pictures wait until each has arrived (or is known to be missing).
        foreach (var reference in new[] { MailProviders.IconRef(MailProviders.Find(MailProviders.Qq)), MailProviders.IconRef(MailProviders.Find(MailProviders.Gmail)), MailProviders.EnvelopeIcon })
        {
            get.Invoke(icons, [reference, null]);
            for (int i = 0; i < 200 && Loading(reference); i++) await Task.Delay(15);
        }
        var at = _now.ToUniversalTime();
        hub.Receive(MailNotices.Notice(gmail, new MailMessage(1, 900, "Figma", "no-reply@figma.com", Loc.T("有人在“设置页改版”中提到了你", "You were mentioned in Settings redesign"), "", at.AddMinutes(-40)), at.AddMinutes(-40)), at.AddMinutes(-40));
        hub.Receive(MailNotices.Notice(company, new MailMessage(1, 901, Loc.T("王敏", "Wang Min"), "wang.min@company.com", Loc.T("周会改到周四下午三点", "The weekly meeting moves to Thursday at three"),
            Loc.T("会议室不变", "Same room"), at.AddMinutes(-20)), at.AddMinutes(-20)), at.AddMinutes(-20));
        hub.Receive(MailNotices.Notice(qq, new MailMessage(1, 902, "GitHub", "noreply@github.com", Loc.T("[GitHub] 你的验证码是 482913", "[GitHub] Your code is 482913"), "", at), at), at);

        // The island, as NoticeLevelsScene lays it out: one notice from each kind of mailbox.
        _settings.Style = CompactStyle.Notch; Refresh();
        await Settle();
        const double scale = 3;
        var type = Type.GetType("BrimDeck.IslandNotice, BrimDeck")!;
        var window = (Window)Activator.CreateInstance(type, Program.Any, null, [_deck, app], null)!;
        foreach (var (file, notice) in new[] { ("qq", hub.Notices.First(n => n.AppKey == qq.AppKey)), ("custom", hub.Notices.First(n => n.AppKey == company.AppKey)) })
        {
            var item = new AttentionItem { Lane = "main", Kind = AttentionKind.Notice, Key = notice.Id, Payload = notice, Level = NoticeLevel.Plain, Count = 1, Duration = TimeSpan.FromSeconds(6),
                Style = _settings.NoticeStyles.For(NoticeLevel.Plain) };
            Program.SetField(window, "_item", item);
            Program.Call(window, "Layout", []); Program.Call(window, "Dress", []); Program.Call(window, "BuildContent", []); Program.Call(window, "KeepAt", [typeof(double)], scale);
            var stage = (FrameworkElement)window.Content;
            var size = new Size(window.Width, window.Height);
            stage.Measure(size); stage.Arrange(new Rect(size)); stage.UpdateLayout();
            var sheet = new Border { Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x66)), Padding = new Thickness(14), Child = new Image { Source = Snap(stage, size, scale), Width = size.Width, Height = size.Height } };
            var sheetSize = new Size(size.Width + 28, size.Height + 28);
            sheet.Measure(sheetSize); sheet.Arrange(new Rect(sheetSize)); sheet.UpdateLayout();
            Write(Snap(sheet, sheetSize, scale), Path.Combine(output, "mail-island-" + file + ".png"));
            Console.WriteLine("mail-island-" + file);
        }
        Program.SetField(window, "_item", null);
        window.Close();

        // The notices page: its list (24 DIP icons) and its focus (56 DIP), with the latest notice selected.
        SelectPage(DeckPage.Notices);
        _settings.NoticesWidth = 760; _settings.NoticesHeight = 330; Refresh();
        _deck.SetExpanded(true, true);
        await Settle();
        await Save("mail-notices-page", 2);
        // The mailbox with its own server's notice in front: the envelope at the focus's size, under its domain's name.
        Program.SetField(_deck, "_noticeFocus", hub.Notices.First(n => n.AppKey == company.AppKey).Id);
        Refresh();
        await Settle();
        await Save("mail-notices-page-custom", 2);
        Program.SetField(_deck, "_noticeFocus", null);
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);

        static BitmapSource Snap(Visual visual, Size size, double scale)
        {
            var picture = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            picture.Render(visual); picture.Freeze();
            return picture;
        }
    }
}
