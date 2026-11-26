using System.Globalization;
using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Mail;
using BrimDeck.Core.Notices;

namespace BrimDeck;

// "邮箱" (MailSettingsPage): the mailboxes whose new mail comes as notices. Each row is one mailbox: the service's icon, the address,
// a line on its connection, and the island and receive switches under the same column headings as the "应用" page; opened, it
// shows where it connects and who signs in, and connects it again. "添加邮箱" opens a window that recognizes the service from the
// address or offers the services to choose from, then says what to do on the service's website and takes the authorization code
// (or signs in to the Microsoft account). A mailbox is saved only once it has connected.
public sealed partial class SettingsWindow
{
    internal const int MailSettingsPage = 22;
    // Each shown mailbox row's reading of its connection; filled as the page is built, emptied with it.
    private readonly List<Action> _mailRows = [];
    // The mailboxes whose rows are open, by id: they stay open while the page is built again (a switch, the connection).
    private readonly HashSet<Guid> _mailOpen = [];

    // The window that adds a mailbox or connects one again: its title row, which each step fills (back, icon, title), and its body.
    private sealed record MailDialogShell(Window Window, DockPanel Heading, StackPanel Body);

    private enum MailDot { Good, Waiting, Caution, Problem }

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

        string title = Loc.T("已连接的邮箱", "Connected mailboxes");
        string Help() => Loc.T("只读取收件箱里新到的邮件，不改变已读状态，不发送也不删除邮件。授权码或登录凭据加密保存在本机，只发往邮箱服务商的服务器。",
            "Only mail new to the inbox is read; nothing is marked read, sent or deleted. The authorization code or sign-in credential is stored encrypted on this PC and sent only to the mail provider's servers.");
        if (S.MailAccounts.Count == 0)
        {
            SectionTitle(title, help: Help);
            Group(MailEmpty());
        }
        else
        {
            SectionTitle(title, MailAddButton(), Help);
            var rows = new List<FrameworkElement> { MailColumns() };
            foreach (var account in S.MailAccounts) rows.Add(MailRow(account));
            Group([.. rows]);
        }

        // The rows follow their connections, and when mail last came, while the page shows.
        var watcher = new Border();
        Action refresh = () => { foreach (var row in _mailRows.ToList()) row(); };
        watcher.Loaded += (_, _) => { _app.Mail.Changed += refresh; if (_app.Notices is { } center) center.Changed += refresh; };
        watcher.Unloaded += (_, _) => { _app.Mail.Changed -= refresh; if (_app.Notices is { } center) center.Changed -= refresh; };
        Target.Children.Add(watcher);
    }

    // Without a mailbox: some services' icons, what can be connected, and the button that adds one.
    private FrameworkElement MailEmpty()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 34, 16, 38) };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var id in new[] { MailProviders.Qq, MailProviders.NetEase, MailProviders.Gmail, MailProviders.Outlook, MailProviders.ICloud })
        {
            var icon = MailIcon(MailProviders.Find(id), 28); icon.Margin = new Thickness(4, 0, 4, 0);
            icons.Children.Add(icon);
        }
        panel.Children.Add(icons);
        var heading = TextLine(Loc.T("还没有连接邮箱", "No mailbox connected yet"), 13, TextPrimary, FontWeights.SemiBold); heading.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(heading);
        var note = TextLine(Loc.T("支持 QQ、网易、Gmail、Outlook、iCloud 等邮箱，以及任何提供 IMAP 的公司或学校邮箱。",
            "QQ, NetEase, Gmail, Outlook, iCloud and other mail services, and any work or school mailbox that offers IMAP."), 12, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap; note.TextAlignment = TextAlignment.Center; note.MaxWidth = 340; note.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(note);
        var add = ActionButton(Loc.T("添加邮箱", "Add mailbox"), () => ShowMailDialog(null), true);
        add.HorizontalAlignment = HorizontalAlignment.Center; add.MinWidth = 96; add.Padding = new Thickness(14, 6, 14, 6); add.Margin = new Thickness(0, 18, 0, 0);
        panel.Children.Add(add);
        return panel;
    }

    private Button MailAddButton()
    {
        var add = new Button { Style = (Style)FindResource("SettingsOutlineButton"), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(MailGlyph("M 6,2.5 V 9.5 M 2.5,6 H 9.5", 12, 12));
        content.Children.Add(new TextBlock { Text = Loc.T("添加邮箱", "Add mailbox"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        add.Content = content;
        AutomationProperties.SetName(add, Loc.T("添加邮箱", "Add mailbox"));
        add.Click += (_, _) => ShowMailDialog(null);
        return add;
    }

    // A stroked icon that takes its button's color, so that it follows the button's hover.
    private Path MailGlyph(string data, double width, double height, double thickness = 1.4)
    {
        var path = Stroke(data, width, height, TextSecondary); path.StrokeThickness = thickness;
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return path;
    }

    // icon · address and state · island · receive · remove: the "应用" page's columns without the banner's.
    private static Grid MailGrid()
    {
        var row = new Grid { MinHeight = 58, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        return row;
    }

    private FrameworkElement MailColumns()
    {
        var row = MailGrid();
        row.MinHeight = 28;
        row.Children.Add(Heading(2, Loc.T("岛上提示", "Island"), () => Loc.T("关闭后，新邮件只进入通知页，不在岛上提示。", "When off, new mail only goes to the notices page and does not show on the island.")));
        row.Children.Add(Heading(3, Loc.T("接收", "Receive"), () => Loc.T("关闭后断开这个邮箱，不再收取新邮件。凭据仍保存在本机，重新打开即可恢复。",
            "When off, the mailbox is disconnected and no new mail comes in. Its credential stays on this PC; turning it on again connects it.")));
        return row;

        FrameworkElement Heading(int column, string title, Func<string> help)
        {
            // As the apps' columns: the mark centered on the heading's letters, with no room added beside its own box.
            var info = HelpIcon(title, help, 12);
            info.Margin = new Thickness(0);
            var heading = new HelpHeading(TextLine(title, 11, TextTertiary), info) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(heading, column);
            return heading;
        }
    }

    // A service's icon as the service gives it, in a square of that size; a mailbox with its own server has BrimDeck's envelope on a
    // plate of the inset's color.
    private FrameworkElement MailIcon(MailProvider provider, double size)
    {
        if (MailIconImage(provider) is { } image)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        var glyph = NoticeBadges.Glyph(NoticeGlyph.Mail, Math.Round(size * .62), (Color)ColorConverter.ConvertFromString(TextSecondary));
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        var plate = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(Math.Round(size * .28)), Child = glyph };
        plate.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        return plate;
    }

    private static readonly Dictionary<string, ImageSource?> s_mailIcons = new(StringComparer.Ordinal);

    // The service's built-in icon (AppBrandIcons), read once; null for the mailbox with its own server.
    private static ImageSource? MailIconImage(MailProvider provider)
    {
        if (s_mailIcons.TryGetValue(provider.Id, out var known)) return known;
        ImageSource? image = null;
        if (MailProviders.IconRef(provider) is var reference && reference.StartsWith("brand:", StringComparison.Ordinal) && AppBrandIcons.Read(reference[6..]) is { } bytes)
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes); bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException) { }
        return s_mailIcons[provider.Id] = image;
    }

    // Which service a mailbox is with, as its row says it: the service's name, or a mailbox's own server by its host name.
    private static string MailService(MailProvider provider, MailAccount account)
        => provider.Id == MailProviders.Custom ? account.Host : provider.Name;

    // A mailbox named as the windows about it show it: icon, address, and a line under it.
    private FrameworkElement MailIdentity(MailProvider provider, string address, string line)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = MailIcon(provider, 32); icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = TextLine(address, 13, TextPrimary, FontWeights.SemiBold); title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = address;
        var under = TextLine(line, 11.5, TextSecondary); under.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(title); names.Children.Add(under);
        Grid.SetColumn(names, 1); grid.Children.Add(names);
        return grid;
    }

    // What a mailbox's row says of its connection, and the dot before it. Refused: the sign-in was refused (or there is no server),
    // so the row offers to enter the code again.
    private (MailDot Dot, string Text, bool Refused) MailStateLine(MailAccount account)
    {
        if (!account.Enabled) return (MailDot.Waiting, Loc.T("已关闭接收", "Receiving is off"), false);
        if (!S.NoticesEnabled) return (MailDot.Waiting, Loc.T("未连接", "Not connected"), false);
        return _app.Mail.Status(account.Id) switch
        {
            { State: MailState.Watching } => (MailDot.Good, Loc.T("已连接", "Connected"), false),
            { State: MailState.Retrying } => (MailDot.Caution, Loc.T("连接中断，稍后自动重连", "Connection lost; trying again shortly"), false),
            { State: MailState.Refused } => (MailDot.Problem, Loc.T("登录被拒绝", "Sign-in refused"), true),
            null => (MailDot.Problem, Loc.T("没有填写服务器地址", "No server given"), true),
            _ => (MailDot.Waiting, Loc.T("正在连接…", "Connecting…"), false)
        };
    }

    // When the mailbox's last mail came, from the notices' record of their sources; null before any.
    private DateTimeOffset? MailLastSeen(MailAccount account)
        => _app.Notices?.Hub.KnownApps.FirstOrDefault(app => app.Key == account.AppKey)?.LastSeen;

    // A time as the opened row says it: today's and yesterday's by name, older ones by date.
    private static string MailWhen(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return Loc.T("今天 ", "Today ") + clock;
        if (local.Date == today.AddDays(-1)) return Loc.T("昨天 ", "Yesterday ") + clock;
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // One mailbox: its service's icon; its address with the arrow that opens it, and under them a dot and its state, then the
    // service (or its own server) and when mail last came, or what to do when the sign-in was refused; its island and receive
    // switches; the cross that removes it. Opened: the service, the server, the sign-in name, the last mail and "重新连接".
    private FrameworkElement MailRow(MailAccount account)
    {
        var provider = MailProviders.Find(account.Provider);
        string address = account.Address, source = MailProviders.SourceName(provider, address);
        var row = MailGrid();
        var icon = MailIcon(provider, 32); icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        var title = account.Enabled ? TextLine(address, 13, TextPrimary, FontWeights.SemiBold) : TextLine(address, 13, TextSecondary);
        title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center; title.ToolTip = address;
        var detail = MailDetails(account, provider, out var readDetail);
        Button? arrowButton = null;
        var arrow = ChevronButton(Loc.T("展开 ", "Open ") + address, () =>
        {
            bool open = _mailOpen.Add(account.Id) || !_mailOpen.Remove(account.Id);
            detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            PointArrow(arrowButton!, open);
        });
        arrowButton = arrow;
        arrow.Height = 22; arrow.Margin = new Thickness(2, 0, 0, 0);
        bool opened = _mailOpen.Contains(account.Id);
        PointArrow(arrow, opened); detail.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
        var heading = new DockPanel { LastChildFill = false };
        heading.Children.Add(title); heading.Children.Add(arrow);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 12, 9) };
        labels.Children.Add(heading);
        labels.Children.Add(MailStatus(account, provider, out var readStatus));
        // The address takes what the arrow leaves of the column, and is cut short beyond it.
        labels.SizeChanged += (_, e) => title.MaxWidth = Math.Max(0, e.NewSize.Width - 24);
        Grid.SetColumn(labels, 1); row.Children.Add(labels);

        var island = IslandSwitch(account.AppKey, source, S.NoticeRule(account.AppKey));
        island.IsEnabled = account.Enabled;
        AutomationProperties.SetName(island, address + " · " + Loc.T("岛上提示", "On the island"));
        Grid.SetColumn(island, 2); row.Children.Add(island);
        var receive = NoticeToggle(address + " · " + Loc.T("接收", "Receive"), account.Enabled, value =>
        {
            Change(s => { if (s.MailAccounts.FirstOrDefault(a => a.Id == account.Id) is { } saved) saved.Enabled = value; });
            ShowPage(MailSettingsPage);
        });
        receive.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(receive, 3); row.Children.Add(receive);
        var cross = RemoveButton(address, () =>
        {
            string secret = MailProviders.SecretName(provider);
            if (!Confirm(Loc.T("移除这个邮箱？", "Remove this mailbox?"),
                Loc.Joined(Loc.T($"同时删除保存在本机的 {secret}。邮箱里的邮件不受影响。", $"This also deletes its {secret.ToLowerInvariant()} saved on this PC. The mail in the mailbox is not touched.")),
                Loc.T("移除", "Remove"), subject: MailIdentity(provider, address, MailService(provider, account)))) return;
            Change(s => { s.MailAccounts.RemoveAll(a => a.Id == account.Id); s.NoticeApps.RemoveAll(rule => rule.Key == account.AppKey); });
            _mailOpen.Remove(account.Id);
            ShowPage(MailSettingsPage);
        });
        Grid.SetColumn(cross, 4); row.Children.Add(cross);

        void Read() { readStatus(); readDetail(); }
        _mailRows.Add(Read);
        var stack = new StackPanel();
        stack.Children.Add(row); stack.Children.Add(detail);
        return stack;
    }

    // The line under a mailbox's address: a dot and the state, then the service and when mail last came; when the sign-in was
    // refused, the links that enter the code again and try once more. read reads it again.
    private FrameworkElement MailStatus(MailAccount account, MailProvider provider, out Action read)
    {
        var dot = new Ellipse { Width = 6, Height = 6 };
        var state = TextLine("", 11.5, TextSecondary);
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        TextBlock Separator() { var dotText = TextLine("·", 11.5, TextTertiary); dotText.Margin = new Thickness(6, 0, 6, 0); return dotText; }
        var tailSeparator = Separator();
        var tail = TextLine("", 11.5, TextSecondary);
        var lastSeparator = Separator();
        var last = TextLine("", 11.5, TextSecondary);
        Button Link(string text, string name, Action act)
        {
            var link = ActionButton(text, act); link.Style = (Style)FindResource("SettingsLink"); link.FontSize = 11.5;
            link.Foreground = UI.Brush(_palette.Link); link.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(link, text + " " + name);
            return link;
        }
        var againSeparator = Separator();
        var again = Link(provider.SignIn == MailSignIn.Microsoft ? Loc.T("重新登录", "Sign in again") : Loc.T("重新填写", "Enter again"), account.Address, () => ShowMailDialog(account));
        var retrySeparator = Separator();
        var retry = Link(Loc.T("重试", "Retry"), account.Address, () => _app.Mail.Retry(account.Id));
        var line = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        // The dot on the middle of the state's letters (StepLine).
        foreach (var part in new FrameworkElement[] { new StepLine(dot, state, 6), tailSeparator, tail, lastSeparator, last, againSeparator, again, retrySeparator, retry }) line.Children.Add(part);
        read = () =>
        {
            var (kind, text, refused) = MailStateLine(account);
            state.Text = text;
            state.SetResourceReference(TextBlock.ForegroundProperty, kind == MailDot.Problem ? "SettingsError" : "SettingsTextSecondary");
            // The server's own words, where it gave any, for whoever wants them.
            state.ToolTip = _app.Mail.Status(account.Id) is { Detail.Length: > 0 } status && account.Enabled ? status.Detail : null;
            dot.Fill = kind switch { MailDot.Good => UI.Brush(_palette.Positive), MailDot.Problem => UI.Brush(_palette.Negative), _ => Brushes.Transparent };
            dot.Stroke = kind switch { MailDot.Caution => UI.Brush(CautionColor), MailDot.Waiting => UI.Brush(_palette.InputBorder), _ => null };
            dot.StrokeThickness = kind is MailDot.Caution or MailDot.Waiting ? 1.2 : 0;
            tail.Text = MailService(provider, account);
            last.Text = MailLastSeen(account) is { } seen ? Loc.T("最近收到：", "Last mail: ") + NoticeText.Relative(seen, DateTimeOffset.UtcNow) : "";
            tail.Visibility = tailSeparator.Visibility = !refused && tail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            last.Visibility = lastSeparator.Visibility = !refused && last.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            again.Visibility = againSeparator.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = retrySeparator.Visibility = refused && _app.Mail.Status(account.Id) is not null ? Visibility.Visible : Visibility.Collapsed;
        };
        read();
        return line;
    }

    // An opened mailbox: its service, the server it connects to, the name it signs in with and when mail last came, with the
    // button that connects it again (whatever its state). read follows the last mail.
    private Border MailDetails(MailAccount account, MailProvider provider, out Action read)
    {
        var list = new Grid();
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); list.ColumnDefinitions.Add(new ColumnDefinition());
        var endpoint = account.Endpoint();
        (TextBlock Label, TextBlock Value) Item(string label, string value)
        {
            int at = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = TextLine(label, 12, TextSecondary); name.Margin = new Thickness(0, at == 0 ? 0 : 4, 14, 0);
            var text = TextLine(value, 12, TextPrimary); text.Margin = new Thickness(0, at == 0 ? 0 : 4, 0, 0); text.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(name, at); Grid.SetRow(text, at); Grid.SetColumn(text, 1);
            list.Children.Add(name); list.Children.Add(text);
            return (name, text);
        }
        Item(Loc.T("邮箱服务", "Mail service"), provider.Id == MailProviders.Custom ? Loc.T("其他邮箱（IMAP）", "Other mailbox (IMAP)") : provider.Name);
        string host = endpoint?.Host ?? account.Host;
        Item(Loc.T("IMAP 服务器", "IMAP server"), host.Length > 0 ? Loc.T($"{host}，{MailProviders.Port} 端口", $"{host}, port {MailProviders.Port}") : Loc.T("未填写", "Not given"));
        Item(Loc.T("登录名", "Sign-in name"), endpoint?.UserName ?? account.Address);
        var (lastLabel, last) = Item(Loc.T("最近收到", "Last mail"), "");
        read = () =>
        {
            var seen = MailLastSeen(account);
            last.Text = seen is { } time ? MailWhen(time, DateTimeOffset.UtcNow) : "";
            last.Visibility = lastLabel.Visibility = seen is null ? Visibility.Collapsed : Visibility.Visible;
        };
        read();
        var again = ActionButton(Loc.T("重新连接", "Connect again"), () => ShowMailDialog(account));
        again.Style = (Style)FindResource("SettingsOutlineButton"); again.VerticalAlignment = VerticalAlignment.Bottom; again.Margin = new Thickness(28, 0, 0, 0);
        AutomationProperties.SetName(again, Loc.T("重新连接 ", "Connect again: ") + account.Address);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(list); Grid.SetColumn(again, 1); content.Children.Add(again);
        var block = new Border { Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(44, 0, 0, 12) };
        block.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        return block;
    }

    // A text input of the window that adds a mailbox: its font and colors, drawn on the frame around it (MailInputFrame).
    private void MailInput(Control input)
    {
        input.FontSize = 12.5; input.VerticalAlignment = VerticalAlignment.Center; input.VerticalContentAlignment = VerticalAlignment.Center;
        input.Padding = new Thickness(0); input.BorderThickness = new Thickness(0); input.Background = Brushes.Transparent;
        input.Foreground = UI.Brush(TextPrimary); input.FontFamily = SettingsFont(UiFontRole.Panel);
        if (input is TextBox box) { box.CaretBrush = UI.Brush(_palette.Accent); box.Style = (Style)FindResource("SettingsNumber"); box.TextAlignment = TextAlignment.Left; }
        if (input is PasswordBox password) password.CaretBrush = UI.Brush(_palette.Accent);
    }

    // The frame around a text input, holding the input with anything laid over it.
    private Border MailInputFrame(UIElement content)
    {
        var frame = new Border { Child = content, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0),
            Background = UI.Brush(_palette.Input), BorderBrush = UI.Brush(_palette.InputBorder), BorderThickness = new Thickness(1) };
        FieldFocus(frame, content, () => UI.Brush(_palette.InputBorder));
        return frame;
    }

    // Gray words in an empty input, saying what to put there; shown is asked again whenever the input changes.
    private TextBlock MailPlaceholder(string text)
    {
        var words = TextLine(text, 12.5, TextTertiary);
        words.IsHitTestVisible = false; words.VerticalAlignment = VerticalAlignment.Center; words.TextTrimming = TextTrimming.CharacterEllipsis;
        return words;
    }

    // A field of the window that adds a mailbox: its caption above a framed input, with a note at the caption's right ("选填") and
    // words in the input while it is empty, where given.
    private FrameworkElement MailField(string caption, TextBox input, string? note = null, string? placeholder = null)
    {
        var label = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        if (note is not null)
        {
            var aside = TextLine(note, 12, TextTertiary); DockPanel.SetDock(aside, Dock.Right); aside.Margin = new Thickness(12, 0, 0, 0);
            label.Children.Add(aside);
        }
        label.Children.Add(TextLine(caption, 12, TextSecondary));
        AutomationProperties.SetName(input, caption);
        if (placeholder is not null) AutomationProperties.SetHelpText(input, placeholder);
        var content = new Grid();
        content.Children.Add(input);
        if (placeholder is not null)
        {
            var words = MailPlaceholder(placeholder);
            void Follow() => words.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (_, _) => Follow(); Follow();
            content.Children.Insert(0, words);
        }
        MailInput(input);
        var field = new StackPanel(); field.Children.Add(label); field.Children.Add(MailInputFrame(content));
        return field;
    }

    // The code or password: hidden as typed, with a button that shows it. Read gives what was typed, either way.
    private sealed record MailSecretInput(FrameworkElement Field, Func<string> Read, Action Focus, Action<Action> Changed);

    private MailSecretInput MailSecretField(string caption, string? placeholder)
    {
        var hidden = new PasswordBox { MaxLength = 1024 };
        var plain = new TextBox { MaxLength = 1024, Visibility = Visibility.Collapsed };
        bool shown = false;
        string Read() => shown ? plain.Text : hidden.Password;
        var words = placeholder is null ? null : MailPlaceholder(placeholder);
        void Follow() { if (words is not null) words.Visibility = Read().Length == 0 ? Visibility.Visible : Visibility.Collapsed; }
        const string Eye = "M 1.8,8 Q 4.5,3.8 8,3.8 Q 11.5,3.8 14.2,8 Q 11.5,12.2 8,12.2 Q 4.5,12.2 1.8,8 Z M 9.9,8 A 1.9,1.9 0 1 1 9.89,8";
        const string Shut = Eye + " M 2.5,2.5 L 13.5,13.5";
        string Show() => Loc.Joined(Loc.T($"显示 {caption}", $"Show {caption.ToLowerInvariant()}"));
        string Hide() => Loc.Joined(Loc.T($"隐藏 {caption}", $"Hide {caption.ToLowerInvariant()}"));
        var eye = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Width = 28, Height = 28, Margin = new Thickness(4, 0, -6, 0), Content = MailGlyph(Eye, 16, 16, 1.3), ToolTip = Show() };
        AutomationProperties.SetName(eye, Show());
        eye.Click += (_, _) =>
        {
            shown = !shown;
            if (shown) { plain.Text = hidden.Password; plain.Visibility = Visibility.Visible; hidden.Visibility = Visibility.Collapsed; }
            else { hidden.Password = plain.Text; hidden.Visibility = Visibility.Visible; plain.Visibility = Visibility.Collapsed; }
            ((Path)eye.Content).Data = Geometry.Parse(shown ? Shut : Eye);
            eye.ToolTip = shown ? Hide() : Show(); AutomationProperties.SetName(eye, (string)eye.ToolTip);
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (words is not null) content.Children.Add(words);
        content.Children.Add(hidden); content.Children.Add(plain);
        Grid.SetColumn(eye, 1); content.Children.Add(eye);
        var frame = MailInputFrame(content);
        foreach (var input in new Control[] { hidden, plain })
        {
            MailInput(input);
            AutomationProperties.SetName(input, caption);
            if (placeholder is not null) AutomationProperties.SetHelpText(input, placeholder);
        }
        hidden.PasswordChanged += (_, _) => Follow(); plain.TextChanged += (_, _) => Follow();
        Follow();
        var label = TextLine(caption, 12, TextSecondary); label.Margin = new Thickness(0, 14, 0, 6);
        var field = new StackPanel(); field.Children.Add(label); field.Children.Add(frame);
        return new(field, Read, () => { if (shown) plain.Focus(); else hidden.Focus(); },
            changed => { hidden.PasswordChanged += (_, _) => changed(); plain.TextChanged += (_, _) => changed(); });
    }

    // The window that adds a mailbox, or connects a mailbox again (existing): first the address or the service, then the chosen
    // service's steps and fields. The mailbox is saved once the server has let it in.
    private void ShowMailDialog(MailAccount? existing)
    {
        var heading = new DockPanel();
        var sync = SyncDialog(existing is null ? Loc.T("添加邮箱", "Add mailbox") : Loc.T("重新连接邮箱", "Connect the mailbox again"), 460, heading);
        var shell = new MailDialogShell(sync.Window, heading, sync.Body);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        shell.Window.Closed += (_, _) => stop.Cancel();
        if (existing is null) ShowMailProviders(shell, stop.Token); else ShowMailForm(shell, MailProviders.Find(existing.Provider), existing, stop.Token);
        shell.Window.ShowDialog();
        ShowPage(MailSettingsPage);
    }

    // The title row of a step: the back button (with back), the service's icon (with provider) and the title, which is also the window's.
    private void MailHeading(MailDialogShell shell, string title, MailProvider? provider = null, Action? back = null)
    {
        shell.Heading.Children.Clear();
        shell.Window.Title = title;
        if (back is not null)
        {
            string label = Loc.T("返回选择服务", "Back to the services");
            var button = new Button { Style = (Style)FindResource("SettingsGlyphButton"), Width = 30, Height = 30, Margin = new Thickness(-8, 0, 4, 0),
                Content = MailGlyph("M 9.5,3.5 L 5,8 L 9.5,12.5 M 5,8 H 12", 16, 16, 1.5), ToolTip = label, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => back();
            shell.Heading.Children.Add(button);
        }
        if (provider is not null)
        {
            var icon = MailIcon(provider, 28); icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            shell.Heading.Children.Add(icon);
        }
        var text = TextLine(title, 16, TextPrimary, FontWeights.SemiBold); text.VerticalAlignment = VerticalAlignment.Center; text.TextTrimming = TextTrimming.CharacterEllipsis;
        shell.Heading.Children.Add(text);
    }

    // The first step: one box for the address, which recognizes the service from it, or for words that find a service in the list
    // under it; the mailbox with its own server stays below the list. typed: what the box held when the user came back to it.
    private void ShowMailProviders(MailDialogShell shell, CancellationToken stop, string typed = "")
    {
        MailHeading(shell, Loc.T("添加邮箱", "Add mailbox"));
        shell.Body.Children.Clear();
        var custom = MailProviders.Find(MailProviders.Custom);
        var pick = MailProviders.Choose(typed);
        var box = new TextBox { MaxLength = 254, Text = typed };
        var field = (StackPanel)MailField(Loc.T("邮箱地址", "Email address"), box, placeholder: Loc.T("输入邮箱地址，或搜索邮箱服务", "Enter your email address, or search for a service"));
        ((FrameworkElement)field.Children[0]).Margin = new Thickness(0, 0, 0, 6);
        shell.Body.Children.Add(field);
        var hint = TextLine("", 11.5, TextSecondary); hint.TextWrapping = TextWrapping.Wrap; hint.MinHeight = 17; hint.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
        shell.Body.Children.Add(hint);
        var listCaption = TextLine("", 11, TextSecondary); listCaption.Margin = new Thickness(0, 12, 0, 8);
        shell.Body.Children.Add(listCaption);
        var tiles = new Grid();
        tiles.ColumnDefinitions.Add(new ColumnDefinition()); tiles.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new ScrollViewer { Style = (Style)FindResource("SettingsScrollViewer"), Content = tiles, Focusable = false, MaxHeight = 4 * 52 + 3 * 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        shell.Body.Children.Add(list);
        var other = MailTile(custom, () => Open(custom), wide: true); other.Margin = new Thickness(0, 10, 0, 0);
        void Follow() => other.Margin = new Thickness(0, list.Visibility == Visibility.Visible ? 10 : 12, 0, 0);
        shell.Body.Children.Add(other);

        string Address() => pick.Address ? box.Text.Trim() : "";
        void Open(MailProvider provider) => ShowMailForm(shell, provider, null, stop, Address(), box.Text);
        void Fill()
        {
            pick = MailProviders.Choose(box.Text);
            string filter = box.Text.Trim();
            tiles.Children.Clear(); tiles.RowDefinitions.Clear();
            for (int i = 0; i < pick.Shown.Count; i++)
            {
                var provider = pick.Shown[i];
                int column = i % 2, line = i / 2;
                if (column == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tile = MailTile(provider, () => Open(provider));
                tile.Margin = new Thickness(column == 0 ? 0 : 4, line == 0 ? 0 : 8, column == 0 ? 4 : 0, 0);
                if (pick.Match is { } match) MarkTile(tile, provider == match);
                Grid.SetRow(tile, line); Grid.SetColumn(tile, column); tiles.Children.Add(tile);
            }
            list.Visibility = listCaption.Visibility = pick.Shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pick.Address && pick.Match is null) MarkTile(other, true); else ClearTile(other);
            Follow();
            hint.Inlines.Clear();
            if (pick.Address && pick.Match is { } found)
            {
                hint.Inlines.Add(new Run(Loc.T("识别为 ", "Recognized as ")));
                var name = new Run(found.Name) { FontWeight = FontWeights.SemiBold }; name.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextPrimary");
                hint.Inlines.Add(name);
                hint.Inlines.Add(new Run(Loc.T("，按 Enter 继续。", ". Press Enter to continue.")));
            }
            else if (pick.Address)
                hint.Inlines.Add(new Run(Loc.T($"没有认出 {pick.Domain} 属于哪个服务。公司或学校邮箱请选“其他邮箱”，或在列表中选择它使用的服务。",
                    $"{pick.Domain} is not a listed service. For a work or school mailbox choose Other mailbox, or choose the service it uses from the list.")));
            else if (pick.Shown.Count == 0)
                hint.Inlines.Add(new Run(Loc.T("没有找到这个服务。提供 IMAP 的邮箱可以选“其他邮箱”连接。", "No service by that name. A mailbox that offers IMAP can be connected under Other mailbox.")));
            listCaption.Text = pick.Match is not null || filter.Length > 0 && !filter.Contains('@') ? Loc.T("邮箱服务", "Mail services") : Loc.T("或选择邮箱服务", "Or choose your mail service");
        }
        box.TextChanged += (_, _) => Fill();
        // Enter goes on with the service recognized from the address: the listed one, or the mailbox with its own server.
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (pick.Match is { } match) Open(match);
            else if (pick.Address) Open(custom);
            else return;
            e.Handled = true;
        };
        Fill();
        shell.Window.Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // A service in the first step's list: its icon, its name and the endings of its addresses; wide, it takes the whole row and has an
    // arrow at its right (the mailbox with its own server).
    private Button MailTile(MailProvider provider, Action open, bool wide = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = MailIcon(provider, 28); icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = TextLine(provider.Name, 13, TextPrimary); name.TextTrimming = TextTrimming.CharacterEllipsis;
        string domains = MailProviders.DomainNote(provider);
        var note = TextLine(domains, 11, TextTertiary); note.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(name); names.Children.Add(note);
        Grid.SetColumn(names, 1); grid.Children.Add(names);
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var arrow = Stroke("M 4.5,2.5 L 8,6 L 4.5,9.5", 12, 12, TextTertiary); arrow.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(arrow, 2); grid.Children.Add(arrow);
        }
        var tile = new Button { Style = (Style)FindResource("SettingsOutlineButton"), Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 8, 10, 8), MinHeight = 52, ToolTip = provider.Name + "\n" + domains };
        AutomationProperties.SetName(tile, provider.Name); AutomationProperties.SetHelpText(tile, domains);
        tile.Click += (_, _) => open();
        return tile;
    }

    // A tile the address was recognized as (hit), or one of the others beside it.
    private static void MarkTile(Button tile, bool hit)
    {
        tile.Opacity = hit ? 1 : .45;
        if (!hit) { tile.ClearValue(Control.BackgroundProperty); tile.ClearValue(Control.BorderBrushProperty); return; }
        tile.SetResourceReference(Control.BackgroundProperty, "SettingsAccentTint");
        tile.SetResourceReference(Control.BorderBrushProperty, "SettingsAccent");
    }

    private static void ClearTile(Button tile)
    {
        tile.Opacity = 1;
        tile.ClearValue(Control.BackgroundProperty); tile.ClearValue(Control.BorderBrushProperty);
    }

    // The steps on the service's website, in an inset block: what they lead to, the numbered steps and the button that opens the site.
    private FrameworkElement MailSteps(MailProvider provider)
    {
        var panel = new StackPanel();
        var heading = TextLine(MailProviders.StepsHeading(provider), 12, TextPrimary, FontWeights.SemiBold); heading.TextWrapping = TextWrapping.Wrap; heading.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(heading);
        panel.Children.Add(SyncSteps(MailProviders.Steps(provider)));
        if (provider.CodePage.Length > 0)
        {
            string host = new Uri(provider.CodePage).Host;
            var open = ActionButton(Loc.T($"打开 {host} ↗", $"Open {host} ↗"), () => UI.OpenUrl(provider.CodePage));
            open.Style = (Style)FindResource("SettingsOutlineButton"); open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 10, 0, 0);
            open.ToolTip = provider.CodePage; AutomationProperties.SetName(open, Loc.T($"打开 {host}", $"Open {host}"));
            panel.Children.Add(open);
        }
        var block = new Border { Child = panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12) };
        block.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
        return block;
    }

    // The line beside the window's buttons: nothing, progress (with a turning ring) or an error (with its mark). The rendering of
    // this window for checking finds it by its automation id and sets it through its Tag (text, error, busy).
    private FrameworkElement MailFormStatus(out Action<string, bool, bool> say)
    {
        var ring = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var track = new Ellipse { StrokeThickness = 1.5 }; track.SetResourceReference(Shape.StrokeProperty, "SettingsBorder");
        var arc = new Path { Data = Geometry.Parse("M 6,0.75 A 5.25,5.25 0 0 1 11.25,6"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        arc.SetResourceReference(Shape.StrokeProperty, "SettingsAccent");
        ring.Children.Add(track); ring.Children.Add(arc);
        var turn = new RotateTransform(0, 6, 6); ring.RenderTransform = turn;
        var mark = Stroke("M 7,1.4 A 5.6,5.6 0 1 1 6.99,1.4 M 7,4 V 7.6 M 7,9.8 V 9.9", 14, 14, TextSecondary);
        mark.StrokeThickness = 1.3; mark.SetResourceReference(Shape.StrokeProperty, "SettingsError");
        mark.Margin = new Thickness(0, 1, 8, 0); mark.VerticalAlignment = VerticalAlignment.Top; mark.Visibility = Visibility.Collapsed;
        var text = TextLine("", 12, TextSecondary); text.TextWrapping = TextWrapping.Wrap; text.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        var line = new DockPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(ring, Dock.Left); DockPanel.SetDock(mark, Dock.Left);
        line.Children.Add(ring); line.Children.Add(mark); line.Children.Add(text);
        say = (words, error, busy) =>
        {
            text.Text = words; text.ToolTip = null;
            text.SetResourceReference(TextBlock.ForegroundProperty, error ? "SettingsError" : "SettingsTextSecondary");
            text.VerticalAlignment = error ? VerticalAlignment.Top : VerticalAlignment.Center;
            mark.Visibility = error && words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy && Soft.Enabled) turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.8)) { RepeatBehavior = RepeatBehavior.Forever });
            else turn.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        AutomationProperties.SetAutomationId(line, "mail-status");
        return line;
    }

    // The second step, and the window that connects a mailbox again (existing): the title with the service's icon (and the back
    // button when adding), the mailbox when connecting again, the steps on the service's website, the fields, the status beside the
    // button, and a line on where the credential is kept. address: an address typed in the first step; typed: that step's text.
    private void ShowMailForm(MailDialogShell shell, MailProvider provider, MailAccount? existing, CancellationToken stop, string address = "", string typed = "")
    {
        shell.Body.Children.Clear();
        bool microsoft = provider.SignIn == MailSignIn.Microsoft, custom = provider.Id == MailProviders.Custom;
        var addressBox = new TextBox { MaxLength = 254, Text = existing?.Address ?? address };
        // The connection being tried, if any: Cancel ends it, and so does going back, so that it cannot save the mailbox and close
        // the window once the user has left this step.
        CancellationTokenSource? attempt = null;
        if (existing is null)
            MailHeading(shell, Loc.Joined(Loc.T($"连接 {provider.Name}", $"Connect {provider.Name}")), provider, () => { attempt?.Cancel(); ShowMailProviders(shell, stop, addressBox.Text.Length > 0 ? addressBox.Text : typed); });
        else
        {
            MailHeading(shell, Loc.T("重新连接邮箱", "Connect the mailbox again"));
            var mailbox = MailIdentity(provider, existing.Address, MailService(provider, existing) + " · " + MailStateLine(existing).Text);
            mailbox.Margin = new Thickness(0, 0, 0, 14);
            shell.Body.Children.Add(mailbox);
        }
        shell.Body.Children.Add(MailSteps(provider));
        // A Microsoft mailbox is the account signed in to on Microsoft's page, so there is no address to type; connected again, the
        // address is the mailbox's and is not changed.
        if (!microsoft && existing is null) shell.Body.Children.Add(MailField(Loc.T("邮箱地址", "Email address"), addressBox));
        else if (microsoft && existing is not null)
        {
            var mailbox = TextLine(Loc.T($"请在浏览器中登录 {existing.Address}。", $"Sign in to {existing.Address} in the browser."), 12.5, TextSecondary);
            mailbox.TextWrapping = TextWrapping.Wrap; mailbox.Margin = new Thickness(0, 14, 0, 0);
            shell.Body.Children.Add(mailbox);
        }
        var host = new TextBox { MaxLength = 253, Text = existing?.Host ?? "" };
        if (custom) shell.Body.Children.Add(MailField(Loc.T("IMAP 服务器", "IMAP server"), host, Loc.T("只填主机名", "Host name only")));
        // A mailbox with its own server may sign in with a name that is not the address (a company's employee ID); empty uses the address.
        var user = new TextBox { MaxLength = 254, Text = existing?.UserName ?? "" };
        if (custom) shell.Body.Children.Add(MailField(Loc.T("用户名", "User name"), user, Loc.T("选填", "Optional"), Loc.T("不填时使用邮箱地址", "The email address when empty")));
        string secretName = MailProviders.SecretName(provider);
        var code = MailSecretField(secretName, existing is null ? null : Loc.Joined(Loc.T($"填写新的 {secretName}", $"Enter the new {secretName.ToLowerInvariant()}")));
        if (!microsoft) shell.Body.Children.Add(code.Field);

        var status = MailFormStatus(out var showStatus);
        var cancel = ActionButton(Loc.T("取消", "Cancel"), () => { });
        cancel.Style = (Style)FindResource("SettingsOutlineButton"); cancel.MinWidth = 72; cancel.Padding = new Thickness(14, 6, 14, 6); cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Visibility = Visibility.Collapsed;
        var connect = ActionButton(microsoft ? Loc.T("在浏览器中登录", "Sign in in the browser") : Loc.T("连接", "Connect"), () => { }, true);
        connect.MinWidth = 84; connect.Padding = new Thickness(14, 6, 14, 6); connect.IsDefault = true;
        var foot = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition()); foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(status);
        foreach (var (button, column) in new[] { (cancel, 1), (connect, 2) }) { button.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(button, column); foot.Children.Add(button); }
        shell.Body.Children.Add(foot);

        var privacy = TextLine("", 11.5, TextTertiary); privacy.TextWrapping = TextWrapping.Wrap;
        var lockMark = Stroke("M 3.6,5.4 H 8.4 A 1.2,1.2 0 0 1 9.6,6.6 V 9.2 A 1.2,1.2 0 0 1 8.4,10.4 H 3.6 A 1.2,1.2 0 0 1 2.4,9.2 V 6.6 A 1.2,1.2 0 0 1 3.6,5.4 Z M 4,5.4 V 4 A 2,2 0 0 1 8,4 V 5.4", 12, 12, TextTertiary);
        lockMark.StrokeThickness = 1.2; lockMark.VerticalAlignment = VerticalAlignment.Top; lockMark.Margin = new Thickness(0, 2, 6, 0);
        var privacyLine = new DockPanel(); DockPanel.SetDock(lockMark, Dock.Left); privacyLine.Children.Add(lockMark); privacyLine.Children.Add(privacy);
        shell.Body.Children.Add(new Border { Child = privacyLine, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 16, 0, 0) });
        // Where the code goes: the server of the address as typed (NetEase and Sina have one per domain), or the one the user gives.
        void Privacy()
        {
            const string zhRead = "只读取新到的邮件，不改变已读状态。", enRead = " Only new mail is read, and nothing is marked as read.";
            if (microsoft) { privacy.Text = Loc.T("只保存 Microsoft 给出的登录凭据，不保存账户密码。" + zhRead, "Only the sign-in credential Microsoft gives is saved, never the account password." + enRead); return; }
            string server = MailProviders.HostFor(provider, addressBox.Text);
            privacy.Text = custom
                ? Loc.Joined(Loc.T($"{secretName}加密保存在本机，只发往你填写的服务器。" + zhRead, $"The {secretName.ToLowerInvariant()} is stored encrypted on this PC and sent only to the server you enter." + enRead))
                : Loc.T($"{secretName}加密保存在本机，只发往 {server}。" + zhRead, $"The {secretName.ToLowerInvariant()} is stored encrypted on this PC and sent only to {server}." + enRead);
        }
        Privacy();

        bool busy = false;
        cancel.Click += (_, _) => attempt?.Cancel();
        void Say(string text, bool error = false) { showStatus(text, error, false); status.ToolTip = null; cancel.Visibility = Visibility.Collapsed; }
        void Wait(string text) { showStatus(text, false, true); status.ToolTip = null; cancel.Visibility = Visibility.Visible; }
        // The rendering for checking shows the waiting with its button too.
        status.Tag = (Action<string, bool, bool>)((text, error, waiting) => { if (waiting) { Wait(text); connect.IsEnabled = false; } else Say(text, error); });
        void Ready() => connect.IsEnabled = !busy && (microsoft || addressBox.Text.Trim().Length > 0 && code.Read().Length > 0 && (!custom || host.Text.Trim().Length > 0));
        addressBox.TextChanged += (_, _) => { Ready(); Privacy(); }; host.TextChanged += (_, _) => Ready(); code.Changed(Ready);
        Ready();
        if (!microsoft) shell.Window.Dispatcher.BeginInvoke(() => { if (existing is null && addressBox.Text.Length == 0) addressBox.Focus(); else code.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);

        connect.Click += async (_, _) =>
        {
            if (busy) return;
            bool Added(string mail) => existing is null && S.MailAccounts.Any(a => string.Equals(a.Address, mail, StringComparison.OrdinalIgnoreCase));
            var draft = new MailAccount { Id = existing?.Id ?? Guid.NewGuid(), Provider = provider.Id, Address = addressBox.Text.Trim(), Enabled = true, Secret = Guid.NewGuid() };
            if (!microsoft)
            {
                if (MailProviders.Domain(draft.Address) is null) { Say(Loc.T("请填写完整的邮箱地址，例如 name@example.com。", "Enter the whole address, such as name@example.com."), true); return; }
                if (MailProviders.ForAddress(draft.Address) is { SignIn: MailSignIn.Microsoft })
                { Say(Loc.T("Outlook、Hotmail 邮箱需要登录 Microsoft 账户，请返回选择 Outlook。", "Outlook and Hotmail mailboxes need the Microsoft sign-in. Go back and choose Outlook."), true); return; }
                if (Added(draft.Address)) { Say(Loc.T("这个邮箱已经添加。", "This mailbox is already added."), true); return; }
            }
            if (custom)
            {
                if (MailProviders.Host(host.Text) is not { } server) { Say(Loc.T("服务器地址只填主机名，例如 imap.example.com。", "Enter the server's host name only, such as imap.example.com."), true); return; }
                draft.Host = server;
                draft.UserName = user.Text.Trim();
            }
            busy = true; connect.IsEnabled = false;
            using var trying = CancellationTokenSource.CreateLinkedTokenSource(stop);
            attempt = trying;
            var token = trying.Token;
            try
            {
                string saved;
                (bool Refused, string Detail)? failed;
                if (microsoft)
                {
                    Wait(Loc.T("等待在浏览器中登录…", "Waiting for the sign-in in the browser…"));
                    var tokens = await _app.Mail.SignInMicrosoftAsync(existing?.Address, url => { try { UI.OpenUrl(url); return true; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; } }, token);
                    if (tokens is null) { Say(Loc.T("登录没有完成，请重试。", "The sign-in was not completed. Try again."), true); return; }
                    if (tokens.Address is not { } signedIn)
                    { Say(Loc.T("没有读到这个账户的邮箱地址。请登录一个有 Outlook 邮箱的账户。", "This account's email address could not be read. Sign in to an account that has an Outlook mailbox."), true); return; }
                    if (existing is not null && !string.Equals(signedIn, existing.Address, StringComparison.OrdinalIgnoreCase))
                    { Say(Loc.T($"登录的是 {signedIn}，请改为登录 {existing.Address}。", $"You signed in to {signedIn}. Sign in to {existing.Address} instead."), true); return; }
                    if (Added(signedIn)) { Say(Loc.T($"{signedIn} 已经添加。", $"{signedIn} is already added."), true); return; }
                    draft.Address = signedIn;
                    Wait(Loc.T($"正在连接 {draft.Endpoint()!.Host}…", $"Connecting to {draft.Endpoint()!.Host}…"));
                    saved = tokens.Refresh;
                    failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(null, tokens.Access), token);
                }
                else
                {
                    Wait(Loc.T($"正在连接 {draft.Endpoint()!.Host}…", $"Connecting to {draft.Endpoint()!.Host}…"));
                    saved = code.Read().Trim();
                    failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(saved), token);
                    // Apple names the part before "@" as the sign-in name and the whole address as the one to try next.
                    if (failed is { Refused: true } && provider.LocalUser)
                    {
                        draft.UserName = draft.Address;
                        failed = await MailWatch.CheckAsync(draft.Endpoint()!, new MailSecret(saved), token);
                    }
                }
                if (token.IsCancellationRequested) { if (!stop.IsCancellationRequested) Say(""); return; }
                if (failed is { } problem)
                {
                    Say(problem.Refused
                        ? microsoft ? Loc.T("登录被拒绝。请确认已按第 1 步打开 IMAP；工作或学校账户请联系单位管理员。", "The mail server refused the sign-in. Make sure IMAP is on (step 1). For a work or school account, ask its administrator.")
                            : Loc.Joined(Loc.T($"登录被拒绝。请确认 {secretName} 正确，并且已开启 IMAP 服务。", $"The mail server refused the sign-in. Check the {secretName.ToLowerInvariant()} and that IMAP is turned on."))
                        : Loc.T($"无法连接 {draft.Endpoint()!.Host}，请检查网络{(custom ? "和服务器地址" : "")}。",
                            $"Cannot reach {draft.Endpoint()!.Host}. Check the network{(custom ? " and the server name" : "")}."), true);
                    status.ToolTip = problem.Detail.Length > 0 ? problem.Detail : null;
                    return;
                }
                try { _app.Secrets.Write(draft.Id, saved); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
                { Say(Loc.T("无法保存到本机，请检查本机设置目录后重试。", "It could not be saved on this PC. Check the local settings folder and try again."), true); return; }
                // A mailbox connected again keeps its place and its switch.
                Change(s =>
                {
                    int at = s.MailAccounts.FindIndex(a => a.Id == draft.Id);
                    if (at < 0) s.MailAccounts.Add(draft); else { draft.Enabled = s.MailAccounts[at].Enabled; s.MailAccounts[at] = draft; }
                });
                shell.Window.Close();
            }
            // Cancelled by the button: the window stays, with nothing to say; closed, there is nothing left to show.
            catch (OperationCanceledException) { if (!stop.IsCancellationRequested) Say(""); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or MailSecretException or System.Net.Sockets.SocketException)
            { Say(Loc.T("登录没有完成，请检查网络后重试。", "The sign-in was not completed. Check the network and try again."), true); }
            finally { attempt = null; busy = false; if (shell.Window.IsLoaded) Ready(); }
        };
    }
}
