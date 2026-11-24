using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Mail;

namespace ReadmeMedia;

// The "邮箱" settings page without a mailbox and with mailboxes in each state (connected, connecting, connection lost, refused,
// switched off, a very long address), and the window that adds a mailbox: the services to choose from, each service's steps and
// fields, an error under them, and the Outlook page of a version that cannot sign in. No mailbox is connected: the watches are
// made and never started, and their states are set by hand. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task MailScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var centerType = Type.GetType("BrimDeck.MailCenter, BrimDeck")!;
        var center = Activator.CreateInstance(centerType, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Mail", center);
        _settings.MailAccounts = [];
        await SettingsPage("mail-settings-empty", 22, height: 420, tips: true);

        var entries = (IDictionary)Program.Field(center, "_entries")!;
        var entryType = centerType.GetNestedType("Entry", Program.Any)!;
        MailAccount Add(string provider, string address, MailStatus? status, bool enabled = true, string host = "")
        {
            var account = new MailAccount { Provider = provider, Address = address, Enabled = enabled, Host = host };
            _settings.MailAccounts.Add(account);
            if (status is null || !enabled) return account;
            var watch = new MailWatch(new MailEndpoint("127.0.0.1", 9, address, Tls: false), _ => throw new MailSecretException(""), _ => { }, _ => { });
            var entry = Activator.CreateInstance(entryType, Program.Any, null, [account, watch], null)!;
            entryType.GetProperty("Status")!.SetValue(entry, status);
            entries[account.Id] = entry;
            return account;
        }
        Add(MailProviders.Qq, "moyu2048@qq.com", new(MailState.Watching));
        Add(MailProviders.NetEase, "bluewhale@163.com", new(MailState.Connecting));
        Add(MailProviders.Gmail, "north.lantern@gmail.com", new(MailState.Retrying, "The operation has timed out."));
        var refused = Add(MailProviders.ICloud, "paperboat@icloud.com", new(MailState.Refused, "LOGIN failed."));
        Add(MailProviders.Exmail, "zhang.wei@studio-with-a-very-long-company-domain-name.example.com", new(MailState.Refused, "Login fail."));
        Add(MailProviders.Sina, "oldfriend@sina.com", null, enabled: false);
        Add(MailProviders.Custom, "me@example.org", new(MailState.Watching), host: "imap.example.org");
        _settings.NoticeApps.Add(new() { Key = refused.AppKey, Name = "iCloud", Level = BrimDeck.Core.Notices.NoticeLevel.Silent });
        await SettingsPage("mail-settings", 22, height: 900);
        _settings.NoticeIsland = false; _settings.NoticesPage = false;
        await SettingsPage("mail-settings-notices-off", 22, height: 900);
        _settings.NoticeIsland = true; _settings.NoticesPage = true;

        _settings.Theme = SettingsTheme.Dark;
        var window = new SettingsWindow(app);
        var shellType = typeof(SettingsWindow).GetNestedType("SyncDialogShell", Program.Any)!;
        async Task Dialog(string name, string title, Action<object, StackPanel> fill)
        {
            const double scale = 1.5, width = 460;
            var body = new StackPanel();
            var frame = (FrameworkElement)Program.Call(window, "SyncDialogFrame", [typeof(string), typeof(StackPanel), typeof(Action)], title, body, (Action)(() => { }))!;
            var shell = Activator.CreateInstance(shellType, Program.Any, null, [new Window(), body], null)!;
            fill(shell, body);
            var host = new Border { Child = frame, Resources = window.Resources, Width = width };
            System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
            host.Measure(new Size(width, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize));
            await Settle();
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host); bitmap.Freeze();
            Write(bitmap, Path.Combine(output, name + ".png"));
            Console.WriteLine(name + " " + host.ActualHeight.ToString("0"));
            host.Child = null;
        }
        string add = _english ? "Add mailbox" : "添加邮箱";
        void Providers(object shell) => Program.Call(window, "ShowMailProviders", [shellType, typeof(CancellationToken)], shell, CancellationToken.None);
        void Form(object shell, string provider, MailAccount? existing = null)
            => Program.Call(window, "ShowMailForm", [shellType, typeof(MailProvider), typeof(MailAccount), typeof(CancellationToken)], shell, MailProviders.Find(provider), existing, CancellationToken.None);
        await Dialog("mail-add-providers", add, (shell, _) => Providers(shell));
        foreach (var provider in MailProviders.All)
            await Dialog("mail-add-" + provider.Id, add, (shell, _) => Form(shell, provider.Id));
        await Dialog("mail-add-refused", add, (shell, body) =>
        {
            Form(shell, MailProviders.Qq);
            body.Children.OfType<FrameworkElement>().SelectMany(Boxes).OfType<TextBox>().First().Text = "moyu2048@qq.com";
            var status = body.Children.OfType<TextBlock>().First(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
            typeof(SettingsWindow).GetMethod("SetSyncStatus", Program.Any)!.Invoke(null, [status, _english
                ? "The mail server refused the sign-in. Check the authorization code and that IMAP is turned on." : "登录被拒绝。请确认授权码正确，并且已开启 IMAP 服务。", true]);
        });
        await Dialog("mail-again", _english ? "Connect the mailbox again" : "重新连接邮箱", (shell, _) => Form(shell, MailProviders.ICloud, refused));

        static IEnumerable<DependencyObject> Boxes(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var inner in Boxes(child)) yield return inner;
            }
        }
    }
}
