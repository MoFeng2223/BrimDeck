using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Mail;

namespace BrimDeck;

// "邮箱" (MailSettingsPage): the mailboxes whose new mail comes as notices. Each row is one mailbox with its state, its level and
// its switch; "添加邮箱" opens a window that offers the mail services, says what to do on the service's website and takes the
// authorization code (or signs in to the Microsoft account). A mailbox is saved only once it has connected.
public sealed partial class SettingsWindow
{
    internal const int MailSettingsPage = 22;
    // Each shown mailbox row's reading of its connection; filled as the page is built, emptied with it.
    private readonly List<Action> _mailRows = [];

    private void MailSettings()
    {
        _mailRows.Clear();
        var intro = TextLine(Loc.T("连接邮箱后，新邮件显示在通知页和岛上，邮件里的验证码可以直接复制。邮件客户端或网页邮箱已经发出 Windows 通知的，不需要再连接。",
            "Once a mailbox is connected, its new mail shows on the notices page and the island, and a verification code in it can be copied at once. Where a mail app or webmail already sends Windows notifications, no connection is needed."), 12.5, TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 24);
        intro.Inlines.Add(new Run(" "));
        intro.Inlines.Add(InlineLink(Loc.T("使用说明 ↗", "How it works ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/mail.md", "/blob/main/docs/en-US/mail.md")));
        _content.Children.Add(intro);
        NoticesOffRow();

        var add = ActionButton(Loc.T("添加邮箱", "Add mailbox"), () => ShowMailDialog(null));
        add.Style = (Style)FindResource("SettingsOutlineButton");
        SectionTitle(Loc.T("邮箱", "Mailboxes"), add, () => Loc.T(
            "只读取收件箱里新到的邮件，不改变已读状态，不发送也不删除邮件。授权码或登录凭据加密保存在本机，只发往邮箱服务商的服务器。",
            "Only mail new to the inbox is read; nothing is marked read, sent or deleted. The authorization code or sign-in credential is stored encrypted on this PC and sent only to the mail provider's servers."));
        var rows = new List<FrameworkElement>();
        if (S.MailAccounts.Count == 0)
        {
            var none = TextLine(Loc.T("还没有邮箱。点“添加邮箱”连接一个。", "No mailbox yet. Choose \"Add mailbox\" to connect one."), 12, TextSecondary);
            rows.Add(new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
        }
        foreach (var account in S.MailAccounts) rows.Add(MailRow(account));
        Group([.. rows]);

        // The rows follow their connections while the page shows.
        var watcher = new Border();
        Action refresh = () => { foreach (var row in _mailRows.ToList()) row(); };
        watcher.Loaded += (_, _) => _app.Mail.Changed += refresh;
        watcher.Unloaded += (_, _) => _app.Mail.Changed -= refresh;
        Target.Children.Add(watcher);
    }

    // What a mailbox's row says under its address, and in which color its dot is.
    private (string Text, string Color, bool Refused) MailStateLine(MailAccount account)
    {
        if (!account.Enabled) return (Loc.T("已关闭", "Off"), TextTertiary, false);
        if (!S.NoticesEnabled) return (Loc.T("未连接", "Not connected"), TextTertiary, false);
        return _app.Mail.Status(account.Id) switch
        {
            { State: MailState.Watching } => (Loc.T("已连接", "Connected"), _palette.Positive, false),
            { State: MailState.Retrying } => (Loc.T("连接中断，稍后自动重连", "Connection lost; trying again shortly"), CautionColor, false),
            { State: MailState.Refused } => (Loc.T("登录被拒绝", "Sign-in refused"), _palette.Negative, true),
            null => (Loc.T("没有填写服务器地址", "No server given"), _palette.Negative, true),
            _ => (Loc.T("正在连接…", "Connecting…"), TextSecondary, false)
        };
    }

    // One mailbox: the service and the address, the connection's state, and at the right what to do when the sign-in was
    // refused, whether its notices show on the island, its switch and the cross that removes it.
    private FrameworkElement MailRow(MailAccount account)
    {
        var provider = MailProviders.Find(account.Provider);
        string name = provider.Name + " · " + account.Address;
        var title = TextLine(provider.Name, 13, TextPrimary, FontWeights.SemiBold);
        var address = TextLine(account.Address, 11.5, TextTertiary); address.TextTrimming = TextTrimming.CharacterEllipsis; address.ToolTip = account.Address;
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        var state = TextLine("", 11.5, TextSecondary); state.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        var stateLine = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(dot, Dock.Left); stateLine.Children.Add(dot); stateLine.Children.Add(state);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        labels.Children.Add(title); labels.Children.Add(address); labels.Children.Add(stateLine);

        var again = ActionButton(provider.SignIn == MailSignIn.Microsoft ? Loc.T("重新登录", "Sign in again") : Loc.T("重新填写", "Enter again"), () => ShowMailDialog(account));
        again.Style = (Style)FindResource("SettingsOutlineButton"); again.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(again, (string)again.Content + " " + name);
        var retry = ActionButton(Loc.T("重试", "Retry"), () => _app.Mail.Retry(account.Id)); retry.Margin = new Thickness(0, 0, 12, 0);
        AutomationProperties.SetName(retry, Loc.T("重试 ", "Retry ") + name);
        var islandSwitch = IslandSwitch(account.AppKey, provider.Name, S.NoticeRule(account.AppKey));
        islandSwitch.IsEnabled = account.Enabled;
        // Two switches side by side: each says what it is.
        var island = Captioned(Loc.T("岛上提示", "Island"), islandSwitch); island.Margin = new Thickness(0, 0, 14, 0);
        var receive = NoticeToggle(name + " · " + Loc.T("接收", "Receive"), account.Enabled, value =>
        {
            Change(s => { if (s.MailAccounts.FirstOrDefault(a => a.Id == account.Id) is { } saved) saved.Enabled = value; });
            ShowPage(MailSettingsPage);
        });
        var cross = RemoveButton(name, () =>
        {
            if (!Confirm(Loc.T("移除邮箱", "Remove mailbox"), Loc.Joined(Loc.T($"将移除 {account.Address}，并删除保存在本机的 {MailProviders.SecretName(provider)}。邮箱里的邮件不受影响。",
                $"This removes {account.Address} and deletes its {MailProviders.SecretName(provider).ToLowerInvariant()} saved on this PC. The mail in the mailbox is not touched.")), Loc.T("移除", "Remove"))) return;
            Change(s => { s.MailAccounts.RemoveAll(a => a.Id == account.Id); s.NoticeApps.RemoveAll(rule => rule.Key == account.AppKey); });
            ShowPage(MailSettingsPage);
        });
        cross.Margin = new Thickness(10, 0, 0, 0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new FrameworkElement[] { again, retry, island, Captioned(Loc.T("接收", "Receive"), receive), cross }) { control.VerticalAlignment = VerticalAlignment.Center; controls.Children.Add(control); }

        void Read()
        {
            var (text, color, refused) = MailStateLine(account);
            state.Text = text; dot.Fill = UI.Brush(color);
            // The server's own words, where it gave any, for whoever wants them.
            state.ToolTip = _app.Mail.Status(account.Id) is { Detail.Length: > 0 } status && account.Enabled ? status.Detail : null;
            again.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = refused && _app.Mail.Status(account.Id) is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        Read(); _mailRows.Add(Read);

        var row = new Grid { MinHeight = 64 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels); Grid.SetColumn(controls, 1); row.Children.Add(controls);
        return row;
    }

    // A switch with a short caption before it.
    private StackPanel Captioned(string caption, FrameworkElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var text = TextLine(caption, 11.5, TextSecondary); text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, 6, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(text); panel.Children.Add(control);
        return panel;
    }

    // A field of the window that adds a mailbox: its caption above a framed input.
    private FrameworkElement MailField(string caption, Control input)
    {
        var label = TextLine(caption, 12, TextSecondary); label.Margin = new Thickness(0, 14, 0, 6);
        input.FontSize = 12.5; input.VerticalAlignment = VerticalAlignment.Center; input.VerticalContentAlignment = VerticalAlignment.Center;
        input.Padding = new Thickness(0); input.BorderThickness = new Thickness(0); input.Background = Brushes.Transparent;
        input.Foreground = UI.Brush(TextPrimary); input.FontFamily = SettingsFont(UiFontRole.Panel);
        if (input is TextBox box) { box.CaretBrush = UI.Brush(_palette.Accent); box.Style = (Style)FindResource("SettingsNumber"); box.TextAlignment = TextAlignment.Left; }
        if (input is PasswordBox password) password.CaretBrush = UI.Brush(_palette.Accent);
        AutomationProperties.SetName(input, caption);
        var frame = new Border { Child = input, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0),
            Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1) };
        FieldFocus(frame, input, () => UI.Brush(_palette.InputBorder));
        var field = new StackPanel(); field.Children.Add(label); field.Children.Add(frame);
        return field;
    }

    // The window that adds a mailbox, or takes a mailbox's code again (existing): first the mail services to choose from, then
    // the chosen service's steps and fields. The mailbox is saved once the server has let it in.
    private void ShowMailDialog(MailAccount? existing)
    {
        var shell = SyncDialog(existing is null ? Loc.T("添加邮箱", "Add mailbox") : Loc.T("重新连接邮箱", "Connect the mailbox again"), 460);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        shell.Window.Closed += (_, _) => stop.Cancel();
        if (existing is null) ShowMailProviders(shell, stop.Token); else ShowMailForm(shell, MailProviders.Find(existing.Provider), existing, stop.Token);
        shell.Window.ShowDialog();
        ShowPage(MailSettingsPage);
    }

    private void ShowMailProviders(SyncDialogShell shell, CancellationToken stop)
    {
        shell.Body.Children.Clear();
        var prompt = TextLine(Loc.T("选择邮箱服务", "Choose the mail service"), 12.5, TextSecondary); prompt.Margin = new Thickness(0, 0, 0, 12);
        shell.Body.Children.Add(prompt);
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-4) };
        foreach (var provider in MailProviders.All)
        {
            var choice = ActionButton(provider.Name, () => ShowMailForm(shell, provider, null, stop));
            choice.Style = (Style)FindResource("SettingsOutlineButton"); choice.Height = 40; choice.Margin = new Thickness(4);
            grid.Children.Add(choice);
        }
        shell.Body.Children.Add(grid);
    }

    private void ShowMailForm(SyncDialogShell shell, MailProvider provider, MailAccount? existing, CancellationToken stop)
    {
        shell.Body.Children.Clear();
        var heading = TextLine(provider.Name, 13, TextPrimary, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 12);
        shell.Body.Children.Add(heading);
        bool microsoft = provider.SignIn == MailSignIn.Microsoft;
        Button Back()
        {
            var back = ActionButton(Loc.T("上一步", "Back"), () => ShowMailProviders(shell, stop));
            back.Style = (Style)FindResource("SettingsOutlineButton"); back.MinWidth = 84; back.Padding = new Thickness(14, 6, 14, 6);
            return back;
        }
        shell.Body.Children.Add(SyncSteps(MailProviders.Steps(provider)));
        if (provider.CodePage.Length > 0)
        {
            var open = TextLine("", 12.5, TextSecondary); open.Margin = new Thickness(20, 8, 0, 0);
            open.Inlines.Add(InlineLink(Loc.T($"打开 {new Uri(provider.CodePage).Host} ↗", $"Open {new Uri(provider.CodePage).Host} ↗"), provider.CodePage));
            shell.Body.Children.Add(open);
        }
        // A Microsoft mailbox is the account signed in to on Microsoft's page, so there is no address to type.
        var address = new TextBox { MaxLength = 254, Text = existing?.Address ?? "" };
        if (!microsoft) shell.Body.Children.Add(MailField(Loc.T("邮箱地址", "Email address"), address));
        else if (existing is not null)
        {
            var mailbox = TextLine(Loc.T($"请登录 {existing.Address}。", $"Sign in to {existing.Address}."), 12.5, TextPrimary);
            mailbox.TextWrapping = TextWrapping.Wrap; mailbox.Margin = new Thickness(0, 12, 0, 0);
            shell.Body.Children.Add(mailbox);
        }
        var host = new TextBox { MaxLength = 253, Text = existing?.Host ?? "" };
        if (provider.Id == MailProviders.Custom) shell.Body.Children.Add(MailField(Loc.T("IMAP 服务器", "IMAP server"), host));
        var code = new PasswordBox { MaxLength = 1024 };
        if (!microsoft) shell.Body.Children.Add(MailField(MailProviders.SecretName(provider), code));
        var status = SyncStatusLine(); status.TextAlignment = TextAlignment.Left;
        shell.Body.Children.Add(status);
        var connect = ActionButton(microsoft ? Loc.T("在浏览器中登录", "Sign in in the browser") : Loc.T("连接", "Connect"), () => { }, true);
        connect.MinWidth = 84; connect.Padding = new Thickness(14, 6, 14, 6); connect.IsDefault = true; connect.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        if (existing is null) buttons.Children.Add(Back());
        buttons.Children.Add(connect);
        shell.Body.Children.Add(buttons);

        void Ready() => connect.IsEnabled = microsoft || address.Text.Trim().Length > 0 && code.Password.Length > 0 && (provider.Id != MailProviders.Custom || host.Text.Trim().Length > 0);
        address.TextChanged += (_, _) => Ready(); host.TextChanged += (_, _) => Ready(); code.PasswordChanged += (_, _) => Ready();
        Ready();
        if (!microsoft) shell.Window.Dispatcher.BeginInvoke(() => { if (address.Text.Length == 0) address.Focus(); else code.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);

        bool busy = false;
        connect.Click += async (_, _) =>
        {
            if (busy) return;
            bool Added(string mail) => existing is null && S.MailAccounts.Any(a => string.Equals(a.Address, mail, StringComparison.OrdinalIgnoreCase));
            var draft = new MailAccount { Id = existing?.Id ?? Guid.NewGuid(), Provider = provider.Id, Address = address.Text.Trim(), Enabled = true, Secret = Guid.NewGuid() };
            if (!microsoft)
            {
                if (MailProviders.Domain(draft.Address) is null) { SetSyncStatus(status, Loc.T("请填写完整的邮箱地址，例如 name@example.com。", "Enter the whole address, such as name@example.com."), true); return; }
                if (MailProviders.ForAddress(draft.Address) is { SignIn: MailSignIn.Microsoft })
                { SetSyncStatus(status, Loc.T("Outlook、Hotmail 邮箱需要登录 Microsoft 账户，请返回选择 Outlook。", "Outlook and Hotmail mailboxes need the Microsoft sign-in. Go back and choose Outlook."), true); return; }
                if (Added(draft.Address)) { SetSyncStatus(status, Loc.T("这个邮箱已经添加。", "This mailbox is already added."), true); return; }
            }
            if (provider.Id == MailProviders.Custom)
            {
                if (MailProviders.Host(host.Text) is not { } server) { SetSyncStatus(status, Loc.T("服务器地址只填主机名，例如 imap.example.com。", "Enter the server's host name only, such as imap.example.com."), true); return; }
                draft.Host = server;
            }
            busy = true; connect.IsEnabled = false;
            try
            {
                string saved;
                (bool Refused, string Detail)? failed;
                if (microsoft)
                {
                    SetSyncStatus(status, Loc.T("等待在浏览器中登录…", "Waiting for the sign-in in the browser…"));
                    var tokens = await _app.Mail.SignInMicrosoftAsync(existing?.Address, url => { try { UI.OpenUrl(url); return true; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; } }, stop);
                    if (tokens is null) { SetSyncStatus(status, Loc.T("登录没有完成，请重试。", "The sign-in was not completed. Try again."), true); return; }
                    if (tokens.Address is not { } signedIn)
                    { SetSyncStatus(status, Loc.T("没有读到这个账户的邮箱地址。请登录一个有 Outlook 邮箱的账户。", "This account's email address could not be read. Sign in to an account that has an Outlook mailbox."), true); return; }
                    if (existing is not null && !string.Equals(signedIn, existing.Address, StringComparison.OrdinalIgnoreCase))
                    { SetSyncStatus(status, Loc.T($"登录的是 {signedIn}，请改为登录 {existing.Address}。", $"You signed in to {signedIn}. Sign in to {existing.Address} instead."), true); return; }
                    if (Added(signedIn)) { SetSyncStatus(status, Loc.T($"{signedIn} 已经添加。", $"{signedIn} is already added."), true); return; }
                    draft.Address = signedIn;
                    SetSyncStatus(status, Loc.T("正在连接…", "Connecting…"));
                    saved = tokens.Refresh;
                    failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(null, tokens.Access), stop);
                }
                else
                {
                    SetSyncStatus(status, Loc.T("正在连接…", "Connecting…"));
                    saved = code.Password.Trim();
                    failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(saved), stop);
                    // Apple names the part before "@" as the sign-in name and the whole address as the one to try next.
                    if (failed is { Refused: true } && provider.LocalUser)
                    {
                        draft.UserName = draft.Address;
                        failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(saved), stop);
                    }
                }
                if (stop.IsCancellationRequested) return;
                if (failed is { } problem)
                {
                    string secret = MailProviders.SecretName(provider);
                    SetSyncStatus(status, problem.Refused
                        ? microsoft ? Loc.T("登录被拒绝。请确认已按第 1 步打开 IMAP；工作或学校账户请联系单位管理员。", "The mail server refused the sign-in. Make sure IMAP is on (step 1). For a work or school account, ask its administrator.")
                            : Loc.Joined(Loc.T($"登录被拒绝。请确认 {secret} 正确，并且已开启 IMAP 服务。", $"The mail server refused the sign-in. Check the {secret.ToLowerInvariant()} and that IMAP is turned on."))
                        : Loc.T($"无法连接 {draft.Endpoint()!.Host}，请检查网络{(provider.Id == MailProviders.Custom ? "和服务器地址" : "")}。",
                            $"Cannot reach {draft.Endpoint()!.Host}. Check the network{(provider.Id == MailProviders.Custom ? " and the server name" : "")}."), true);
                    status.ToolTip = problem.Detail.Length > 0 ? problem.Detail : null;
                    return;
                }
                try { _app.Secrets.Write(draft.Id, saved); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
                { SetSyncStatus(status, Loc.T("无法保存到本机，请检查本机设置目录后重试。", "It could not be saved on this PC. Check the local settings folder and try again."), true); return; }
                // A mailbox connected again keeps its place and its switch.
                Change(s =>
                {
                    int at = s.MailAccounts.FindIndex(a => a.Id == draft.Id);
                    if (at < 0) s.MailAccounts.Add(draft); else { draft.Enabled = s.MailAccounts[at].Enabled; s.MailAccounts[at] = draft; }
                });
                shell.Window.Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or MailSecretException or System.Net.Sockets.SocketException)
            { SetSyncStatus(status, Loc.T("登录没有完成，请检查网络后重试。", "The sign-in was not completed. Check the network and try again."), true); }
            finally { busy = false; if (shell.Window.IsLoaded) Ready(); }
        };
    }
}
