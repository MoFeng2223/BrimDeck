using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The "应用" page (NoticeAppsSettingsPage). The common apps are always listed, received or not, so that what BrimDeck supports
// shows at a glance; each row says how it goes (receiving, nothing yet, off in Windows) and opens to the steps that turn its
// notifications on, in the app's own words, or for WeChat to its two ways of being noticed. Any other application's notices are
// received without a row; one is listed to be set apart, from the applications that have sent notices (the sender itself), or
// from the running programs or a chosen file (the sender most likely, NoticeSenderIds). The command-line and WSL sources follow.
public sealed partial class SettingsWindow
{
    private void NoticeAppsPage()
    {
        var center = _app.Notices;
        _noticeStatusLines.Clear();
        _bannerSwitches.Clear();
        NoticesOffRow();
        WindowsAccessSection(center);
        CommonAppsSection(center);
        OtherAppsSection(center);
        ProgramSourcesSection(center);
        WslSection(center);
        NoticeCompatibilitySection();
        WatchNotices(center);
    }

    // While Windows does not let BrimDeck read notifications: first on the page, as it holds back every Windows application below.
    private void WindowsAccessSection(NoticeCenter center)
    {
        var access = AccessRow(center);
        var block = new Border { Child = access, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(0, 0, 0, 28) };
        void Follow() => block.Visibility = access.Visibility;
        Follow(); _noticeStatusLines.Add(Follow);
        Target.Children.Add(block);
    }

    // A common app. AppId is the id its notifications carry and its Start menu entry is registered under, by which it is known to be
    // installed; WeChat sends no notifications and has none.
    private sealed record CommonApp(string Key, string Name, string? AppId);

    private static CommonApp[] CommonApps() =>
    [
        new(WeChatRows.AppKey, Loc.T("微信", "WeChat"), null),
        new(NoticeKeys.Windows("QQ"), "QQ", "QQ"),
        new(DingTalkPopups.AppKey, Loc.T("钉钉", "DingTalk"), null),
        new(FeishuBanners.AppKey, Loc.T("飞书", "Feishu"), null)
    ];

    private static bool IsCommonApp(string key) => CommonApps().Any(app => app.Key == key);

    // Whether an application is installed, by its Start menu entry. The rendering of this page for checking puts its own in its place.
    internal Func<string, bool> AppInstalled = appId => AppIdentity.RegisteredName(appId) is not null;

    private void CommonAppsSection(NoticeCenter center)
    {
        SectionTitle(Loc.T("常用应用", "Common apps"), help: () => Loc.T("这些应用的消息可以在岛上提示。QQ 需要在它的设置中打开消息通知；微信、钉钉、飞书可以选择提示方式。展开一行可以看到方法。",
            "These apps' messages can show on the island. QQ needs message notifications turned on in its settings; for WeChat, DingTalk and Feishu, choose how their messages are noticed. Open a row to see how."));
        var rows = new List<FrameworkElement> { AppColumns(windows: true) };
        foreach (var app in CommonApps()) rows.Add(NoticeAppRow(center, app.Key, app.Name, windows: true, app));
        GroupShown([.. rows]);
    }

    // The applications set apart from the rest: added from the ones that have sent notices, each with its own island, receive and
    // banner switches; removing one gives it back the defaults.
    private void OtherAppsSection(NoticeCenter center)
    {
        var add = AddAppButton(Loc.T("添加应用", "Add app"), AddNoticeAppMenu, Loc.T("添加应用", "Add app"));
        SectionTitle(Loc.T("其他应用", "Other apps"), add, () => Loc.T("灵动通知开启时，BrimDeck 接收所有应用发送的 Windows 通知，并按“提示方式”中的设置在岛上提示。不发送 Windows 通知的应用不会在岛上提示。\n添加应用后，可以单独关闭它的岛上提示、接收或系统横幅；移除后恢复默认设置。",
            "While live notices are on, BrimDeck receives the Windows notifications every app sends and shows them on the island as How notices show sets. An app that sends no Windows notifications does not show on the island.\nOnce added, an app can be kept off the island, stop being received or have its system banner switched; removing it goes back to the default settings."));
        var listed = S.NoticeApps.Where(rule => NoticeKeys.OnAppList(rule.Key) && !IsCommonApp(rule.Key)).ToList();
        var rows = new List<FrameworkElement>();
        if (listed.Count == 0)
        {
            var none = TextLine(Loc.T("其他应用发送的 Windows 通知都会接收，并按“提示方式”中的设置显示。要单独关闭或调整某个应用，请点击“添加应用”。",
                "The Windows notifications other apps send are all received and show as How notices show sets. To turn one off or set it apart, click Add app."), 12, TextSecondary);
            none.TextWrapping = TextWrapping.Wrap;
            rows.Add(new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
        }
        else
        {
            rows.Add(AppColumns(windows: true));
            foreach (var rule in listed) rows.Add(NoticeAppRow(center, rule.Key, AppName(center, rule), windows: true));
        }
        if (_noticeAddNote is { } note)
        {
            _noticeAddNote = null;
            var said = StatusLine(() => (note, true));
            rows.Add(new Border { Child = said, Padding = new Thickness(0, 10, 0, 10) });
        }
        GroupShown([.. rows]);
    }

    // The column headings over the rows: the island and receive switches, and for Windows applications the banner switch.
    private FrameworkElement AppColumns(bool windows)
    {
        var row = AppGrid();
        row.MinHeight = 28;
        row.Children.Add(Heading(2, Loc.T("岛上提示", "Island"), () => Loc.T("关闭后，通知只进入通知页，不在岛上提示。", "When off, notices only go to the notices page and do not show on the island.")));
        row.Children.Add(Heading(3, Loc.T("接收", "Receive"), () => windows
            ? Loc.T("关闭后，BrimDeck 不再显示这个应用的通知。Windows 自己的通知不受影响。",
                "When off, BrimDeck no longer shows this app's notices. Windows' own notifications are not affected.")
            : Loc.T("关闭后，BrimDeck 不再显示这个来源的提示。", "When off, BrimDeck no longer shows this source's notices.")));
        if (windows)
        {
            row.Children.Add(Heading(4, Loc.T("系统横幅", "System banner"), () => Loc.T("与 Windows 设置中这个应用的“显示通知横幅”是同一个开关，显示的是它现在的状态，在这里切换会直接修改 Windows 设置，退出 BrimDeck 后仍然保持。关闭后，通知不再从屏幕右下角弹出，仍会进入 Windows 通知中心，Windows 的通知提示音照常播放。",
                "The same switch as this app's \"Show notification banners\" in Windows Settings, shown as it is now; switching it here changes Windows Settings and stays after BrimDeck exits. When off, notices no longer pop up in the corner of the screen; they still go to Windows' notification center, and Windows still plays their sound.")));
        }
        return row;

        FrameworkElement Heading(int column, string title, Func<string> help)
        {
            // The mark's own box leaves room around it; no more is added between it and the heading.
            var info = HelpIcon(title, help, 12);
            info.Margin = new Thickness(0);
            var heading = new HelpHeading(TextLine(title, 11, TextTertiary), info) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(heading, column);
            return heading;
        }
    }

    // icon · name · island · receive · banner · remove
    private static Grid AppGrid()
    {
        var row = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        return row;
    }

    // An application's name as the settings show it: the one it was listed under, else the one its notices carry, else its key.
    private static string AppName(NoticeCenter center, NoticeAppRule rule)
        => rule.Name.Length > 0 ? rule.Name : center.Hub.KnownApps.FirstOrDefault(app => app.Key == rule.Key)?.Name ?? rule.Key;

    // One application or source: its icon, its name with the arrow that opens it, a line on how it goes, its island and receive
    // switches, for a Windows application Windows' own banner switch, and for one set apart the cross that takes it off the list
    // (back to the defaults; a common app stays listed, and a source of the command line shows again with its next message).
    // Opened, a common app shows what to do in it (or WeChat's two ways) above its own looks; any other its own looks.
    private FrameworkElement NoticeAppRow(NoticeCenter center, string key, string name, bool windows, CommonApp? common = null)
    {
        var rule = S.NoticeRule(key);
        var known = center.Hub.KnownApps.FirstOrDefault(app => app.Key == key);
        bool receives = S.NoticeReceives(key);
        var row = AppGrid();
        var icon = NoticeAppIcon(name, known?.IconRef ?? AppBrandIcons.Fallback(key), 20, center); icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        bool open = _noticeAppsOpen.Contains(key);
        void Flip() { if (!_noticeAppsOpen.Remove(key)) _noticeAppsOpen.Add(key); ShowPage(_page); }
        var title = TextLine(name, 13, receives ? TextPrimary : TextSecondary); title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center; title.ToolTip = name;
        var heading = new DockPanel { LastChildFill = false };
        var arrow = ChevronButton((common is null ? Loc.T("单独设置样子：", "Own looks: ") : Loc.T("展开：", "Open: ")) + name, Flip);
        PointArrow(arrow, open); arrow.Margin = new Thickness(2, 0, 0, 0);
        title.MaxWidth = 200;
        heading.Children.Add(title); heading.Children.Add(arrow);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 12, 7) };
        labels.Children.Add(heading);
        labels.Children.Add(common is null ? StatusLine(() => AppNote(center, key, rule)) : CommonStatus(center, common, open ? null : Flip));
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        var island = IslandSwitch(key, name, rule);
        island.IsEnabled = receives;
        Grid.SetColumn(island, 2); row.Children.Add(island);
        var receive = NoticeToggle(name + " · " + Loc.T("接收", "Receive"), receives, value =>
        {
            EditRule(key, name, r => r.Enabled = value); island.IsEnabled = value;
            title.SetResourceReference(TextBlock.ForegroundProperty, value ? "SettingsTextPrimary" : "SettingsTextSecondary");
            foreach (var line in _noticeStatusLines.ToList()) line();
            if (_noticeAppsOpen.Contains(key)) ShowPage(_page);
        });
        receive.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(receive, 3); row.Children.Add(receive);
        if (windows)
        {
            if (key.StartsWith("windows:", StringComparison.Ordinal) && BannerSwitch(center, key["windows:".Length..], name) is { } banner)
            {
                Grid.SetColumn(banner, 4); row.Children.Add(banner);
            }
            if (common is null)
            {
                var cross = RemoveButton(name, () => { Change(s => s.NoticeApps.RemoveAll(r => r.Key == key)); _noticeAppsOpen.Remove(key); ShowPage(_page); });
                Grid.SetColumn(cross, 5); row.Children.Add(cross);
            }
        }
        if (!open) return row;
        var stack = new StackPanel(); stack.Children.Add(row);
        if (common is not null && CommonDetails(center, common, rule) is { } details) stack.Children.Add(details);
        stack.Children.Add(OwnLooks(key, name, rule));
        return stack;
    }

    // What to know about a row set apart: what went wrong with its banner, that it is not received, what the Clock's notices are,
    // that it has its own looks, and when its last notice came.
    private (string, bool) AppNote(NoticeCenter center, string key, NoticeAppRule? rule)
    {
        string appId = key.StartsWith("windows:", StringComparison.Ordinal) ? key["windows:".Length..] : "";
        if (appId.Length > 0 && _bannerNotes.TryGetValue(appId, out var problem)) return (problem, true);
        if (key.StartsWith("flash:", StringComparison.Ordinal) && center.ChatFlashError is { } error) return (Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, true);
        var known = center.Hub.KnownApps.FirstOrDefault(app => app.Key == key);
        string last = known is null ? "" : Loc.T("最近收到：", "Last notice: ") + NoticeText.Relative(known.LastSeen, DateTimeOffset.UtcNow);
        string Joined(string first) => last.Length > 0 ? first + " · " + last : first;
        if (!S.NoticeReceives(key)) return (Joined(Loc.T("已关闭接收", "Receiving is off")), false);
        if (appId.Length > 0 && NoticeTimeSources.ForApp(appId) is { Role: NoticeTimeRole.Clock }) return (Loc.T("闹钟、计时器结束", "Alarms and finished timers"), false);
        if (key.StartsWith("flash:", StringComparison.Ordinal)) return (Loc.T("任务栏图标闪烁时提示“有新消息”", "\"New message\" when its taskbar button flashes"), false);
        if (rule?.Styles is not null) return (Joined(Loc.T("已单独设置样子", "Has its own looks")), false);
        return (last, false);
    }

    private enum NoteDot { None, Good, Waiting, Caution, Problem }

    // A status line led by a colored dot (working, nothing yet, something to set right) and followed by a link that does what the
    // line asks; it follows the sources while the page shows (WatchNotices).
    private FrameworkElement DotLine(Func<(NoteDot Dot, string Text, string? Link, Action? Act)> read, double top = 2)
    {
        var dot = new Ellipse { Width = 6, Height = 6 };
        var text = TextLine("", 11.5, TextSecondary); text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        Action? act = null;
        var link = ActionButton("", () => act?.Invoke()); link.Style = (Style)FindResource("SettingsLink"); link.FontSize = 11.5;
        link.Foreground = UI.Brush(_palette.Link); link.VerticalAlignment = VerticalAlignment.Top;
        var line = new WrapPanel { Margin = new Thickness(0, top, 0, 0) };
        // The dot on the middle of the text's first line (StepLine).
        line.Children.Add(new StepLine(dot, text, 6)); line.Children.Add(link);
        void Update()
        {
            var state = read();
            text.Text = state.Text;
            text.SetResourceReference(TextBlock.ForegroundProperty, state.Dot switch
            {
                NoteDot.Problem => "SettingsError", NoteDot.Caution => "SettingsCaution", _ => "SettingsTextSecondary"
            });
            dot.Visibility = state.Dot == NoteDot.None ? Visibility.Collapsed : Visibility.Visible;
            dot.Fill = state.Dot switch { NoteDot.Good => UI.Brush(_palette.Positive), NoteDot.Caution => UI.Brush(_palette.Caution), NoteDot.Problem => UI.Brush(_palette.Negative), _ => Brushes.Transparent };
            dot.Stroke = state.Dot == NoteDot.Waiting ? UI.Brush(_palette.InputBorder) : null;
            dot.StrokeThickness = state.Dot == NoteDot.Waiting ? 1.2 : 0;
            act = state.Act;
            link.Content = state.Link ?? "";
            link.Visibility = state.Link is null ? Visibility.Collapsed : Visibility.Visible;
            line.Visibility = state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Update();
        _noticeStatusLines.Add(Update);
        return line;
    }

    // How a common app goes, under its name. show opens the row (null while it is open).
    private FrameworkElement CommonStatus(NoticeCenter center, CommonApp app, Action? show) => DotLine(() =>
    {
        if (!S.NoticeReceives(app.Key)) return (NoteDot.None, Loc.T("已关闭接收，不在岛上提示", "Receiving is off; nothing shows on the island"), null, null);
        if (app.Key == WeChatRows.AppKey)
        {
            if (center.ChatFlashError is { } error) return (NoteDot.Problem, Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, null, null);
            return S.NoticeRule(app.Key)?.ReadContent == true ? WeChatShort(center) : (NoteDot.Good, Loc.T("按任务栏闪烁提示", "Noticed by its taskbar flash"), null, null);
        }
        if (app.Key == DingTalkPopups.AppKey) return DingTalkShort(center, show);
        if (app.Key == FeishuBanners.AppKey) return FeishuShort(center, show);
        string appId = app.Key["windows:".Length..];
        if (_bannerNotes.TryGetValue(appId, out var problem))
            // Opened, the row shows the way to Windows Settings under it (WindowsOffBlock).
            return problem == NotificationsOffNote && show is not null ? (NoteDot.Problem, problem, Loc.T("打开 Windows 设置", "Open Windows Settings"), OpenWindowsNotifications) : (NoteDot.Problem, problem, null, null);
        if (center.Hub.KnownApps.FirstOrDefault(known => known.Key == app.Key) is { } seen)
            return (NoteDot.Good, Loc.T("正在接收 · ", "Receiving · ") + NoticeText.Relative(seen.LastSeen, DateTimeOffset.UtcNow), null, null);
        string how = Loc.T("查看开启方法", "How to turn it on");
        if (app.AppId is { } id && !AppInstalled(id)) return (NoteDot.Waiting, Loc.T("未安装", "Not installed"), show is null ? null : how, show);
        return (NoteDot.Waiting, Loc.T("还没有收到通知", "No notice yet"), show is null ? null : how, show);
    });

    private static void OpenWindowsNotifications() => UI.OpenUrl("ms-settings:notifications");

    // A common app opened: what keeps its notifications from BrimDeck (off in Windows) and, until its first notice arrives, the
    // steps that turn them on in the app; for WeChat, its two ways. Null when there is nothing to say.
    private FrameworkElement? CommonDetails(NoticeCenter center, CommonApp app, NoticeAppRule? rule)
    {
        var panel = new StackPanel { Margin = new Thickness(32, 0, 0, 0) };
        if (app.Key == WeChatRows.AppKey) panel.Children.Add(WeChatModes(center, rule, app.Name));
        else if (app.Key == DingTalkPopups.AppKey) panel.Children.Add(DingTalkModes(center, rule, app.Name));
        else if (app.Key == FeishuBanners.AppKey) panel.Children.Add(FeishuModes(center, rule, app.Name));
        else
        {
            string appId = app.Key["windows:".Length..];
            panel.Children.Add(WindowsOffBlock(appId, app.Name));
            if (center.Hub.KnownApps.All(known => known.Key != app.Key) && CommonSteps(app.Key) is { } steps) panel.Children.Add(Steps(steps.Heading, steps.Steps));
        }
        return panel.Children.OfType<FrameworkElement>().Any(child => child.Visibility == Visibility.Visible) ? panel : null;
    }

    // While the application's notifications are off in Windows: that, and the way to Windows Settings to turn them on.
    private FrameworkElement WindowsOffBlock(string appId, string name)
    {
        var title = TextLine(NotificationsOffNote, 13, _palette.Negative, FontWeights.SemiBold); title.TextWrapping = TextWrapping.Wrap;
        var detail = TextLine(Loc.T($"在 Windows 设置的“系统 › 通知”中打开{name}，BrimDeck 才能收到它的消息。", $"Turn on {name} under System › Notifications in Windows Settings for BrimDeck to receive its messages."), 11.5, TextSecondary);
        detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 2, 0, 0);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) }; labels.Children.Add(title); labels.Children.Add(detail);
        var open = ActionButton(Loc.T("打开 Windows 设置", "Open Windows Settings"), OpenWindowsNotifications);
        open.Style = (Style)FindResource("SettingsOutlineButton"); open.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels); Grid.SetColumn(open, 1); row.Children.Add(open);
        var block = new Border { Child = row, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 12) };
        void Follow() => block.Visibility = _bannerNotes.TryGetValue(appId, out var note) && note == NotificationsOffNote ? Visibility.Visible : Visibility.Collapsed;
        Follow(); _noticeStatusLines.Add(Follow);
        return block;
    }

    // What to turn on in a common app for its messages to reach Windows' notifications. QQ's setting is not named: its interface
    // text is not readable from its files. DingTalk's and Feishu's are with their two ways (DingTalkModes, FeishuModes).
    private static (string Heading, string[] Steps)? CommonSteps(string key) => key switch
    {
        "windows:QQ" => (Loc.T("在 QQ 中打开消息通知", "Turn on message notifications in QQ"),
            [Loc.T("确认 QQ 正在运行并已登录。", "Make sure QQ is running and signed in."),
             Loc.T("在 QQ 的设置中检查是否关闭了消息通知，关闭了就重新打开。", "In QQ's settings, check whether message notifications are off, and turn them back on if so.")]),
        _ => null
    };

    // Numbered steps under a heading. The row's status line says on its own whether messages arrive, and the steps of DingTalk
    // and Feishu stay shown while they do, so no step says what shows once they worked.
    private FrameworkElement Steps(string heading, string[] steps)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        var title = TextLine(heading, 12, TextSecondary); title.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(title);
        string[] all = steps;
        for (int i = 0; i < all.Length; i++)
        {
            var number = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, FontFamily = SettingsFont(UiFontRole.Panel) };
            number.SetResourceReference(TextBlock.ForegroundProperty, "SettingsTextSecondary");
            var circle = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9) };
            circle.SetResourceReference(Border.BackgroundProperty, "SettingsInset");
            var text = TextLine(all[i], 12.5, TextPrimary); text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None;
            text.LineHeight = 20; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var step = new StepLine(circle, text, 10, number) { Margin = new Thickness(0, i == 0 ? 0 : 7, 0, 0) };
            panel.Children.Add(step);
        }
        return new Border { Child = panel, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // "添加应用": the applications that have sent notices, latest first, each with when and the title of its last notice, so
    // that the sender itself is listed and can be told apart (a program whose notices Windows names after a helper, a browser's
    // sites); then the running programs, and a program chosen as a file, by the id their notices are most likely sent under
    // (NoticeSenderIds). Those already listed show as added. Typing filters by name; the list scrolls past what fits.
    private void AddNoticeAppMenu(Button anchor)
    {
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var (content, search) = NoticeAppPicker(() => popup.IsOpen = false);
        popup.Child = content;
        popup.Opened += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { search.Focus(); Keyboard.Focus(search); });
        popup.IsOpen = true;
    }

    // The running programs the menu lists. The rendering of this page for checking puts its own in its place.
    internal Func<IReadOnlyList<(string AppId, string Name, string Path)>> RunningSenders =
        () => [.. NoticeSenderIds.Running().Select(sender => (sender.AppId, sender.Name, sender.Path))];
    // Said under the other apps once, after a chosen program's id could not be found.
    private string? _noticeAddNote;

    // The menu's content, apart from its popup so that the rendering of this page for checking can draw it. close runs before a
    // chosen application is added.
    private (Border Content, TextBox Search) NoticeAppPicker(Action close)
    {
        var center = _app.Notices;
        var search = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 28, FontSize = 12.5, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(2, 2, 2, 6) };
        AutomationProperties.SetName(search, Loc.T("搜索应用", "Search apps"));
        var items = new StackPanel { Width = 340 };
        var list = new ScrollViewer { Style = (Style)FindResource("SettingsScrollViewer"), Content = items, Focusable = false, MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var browse = ActionButton(Loc.T("浏览可执行文件…", "Browse for an executable…"), Browse);
        browse.HorizontalAlignment = HorizontalAlignment.Left; browse.Margin = new Thickness(4, 4, 4, 0);
        var footer = TextLine(Loc.T("“最近发送过通知”列出灵动通知开启期间发送过通知的应用。\n从正在运行的应用或可执行文件添加时，实际发送通知的可能是另一个程序，此时设置不会生效。",
            "\"Sent notices lately\" lists the apps that sent notices while live notices were on.\nAn app added from the running apps or as an executable may have its notices sent by another program, and its settings then do not apply."), 11, TextTertiary);
        footer.TextWrapping = TextWrapping.Wrap; footer.Width = 324;
        var foot = new Border { Child = footer, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(4, 5, 4, 0), Padding = new Thickness(6, 7, 6, 3) };
        var body = new StackPanel(); body.Children.Add(search); body.Children.Add(list); body.Children.Add(browse); body.Children.Add(foot);
        var menu = new Border { Child = body, Background = UI.Brush(_palette.Popup), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5) };
        var now = DateTimeOffset.UtcNow;
        var sent = center.Hub.KnownApps.Where(app => NoticeKeys.OnAppList(app.Key) && !IsCommonApp(app.Key)).ToList();
        var sentKeys = sent.Select(app => app.Key).ToHashSet(StringComparer.Ordinal);
        // A running program that has sent notices is listed once, among those; under its name, its file.
        var running = new List<(NoticeKnownApp App, string File)>();
        foreach (var sender in RunningSenders())
        {
            string key = NoticeKeys.Windows(sender.AppId);
            if (sentKeys.Contains(key)) continue;
            string? icon = sender.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "exe:" + sender.Path : null;
            running.Add((new NoticeKnownApp { Key = key, Name = sender.Name, Origin = NoticeOrigin.Windows, IconRef = icon }, System.IO.Path.GetFileName(sender.Path)));
        }
        // The title of each application's latest notice still in the history (codes hidden), by key.
        var lastTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var notice in center.Hub.Notices.OrderByDescending(notice => notice.CreatedAt))
            if (!lastTitles.ContainsKey(notice.AppKey) && LastWords(notice) is { Length: > 0 } words) lastTitles[notice.AppKey] = words;
        void Add(string key, string name)
        {
            close();
            Change(s => { if (s.NoticeRule(key) is null) s.NoticeApps.Add(new NoticeAppRule { Key = key, Name = name }); });
            ShowPage(_page);
        }
        async void Browse()
        {
            close();
            if (BrowseExecutable(Loc.T("选择要添加的应用", "Choose an app to add")) is not { } path) return;
            if (await NoticeSenderIds.ForExecutableAsync(path) is { } chosen) { Add(NoticeKeys.Windows(chosen.AppId), chosen.Name); return; }
            _noticeAddNote = Loc.T("无法确定这个程序发送通知时使用的应用标识。请在它发送过通知后，从“最近发送过通知”中添加。",
                "The app id this program sends notices under could not be found. Add it from \"Sent notices lately\" once it has sent a notice.");
            ShowPage(_page);
        }
        bool Added(string key) => S.NoticeRule(key) is not null || IsCommonApp(key);
        void Header(string text)
        {
            var header = TextLine(text, 10.5, TextTertiary); header.Margin = new Thickness(10, items.Children.Count == 0 ? 4 : 10, 10, 3);
            items.Children.Add(header);
        }
        void Note(string text)
        {
            var none = TextLine(text, 12, TextTertiary);
            none.TextWrapping = TextWrapping.Wrap; none.Margin = new Thickness(10, 4, 10, 6);
            items.Children.Add(none);
        }
        void Fill()
        {
            items.Children.Clear();
            string filter = search.Text.Trim();
            bool Matches(NoticeKnownApp app) => filter.Length == 0 || app.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
            var shownSent = sent.Where(Matches).ToList();
            var shownRunning = running.Where(entry => Matches(entry.App)).ToList();
            if (filter.Length > 0 && shownSent.Count == 0 && shownRunning.Count == 0)
            {
                Note(Loc.T($"没有找到“{filter}”。", $"Nothing called \"{filter}\"."));
                return;
            }
            if (filter.Length == 0 || shownSent.Count > 0)
            {
                Header(Loc.T("最近发送过通知", "Sent notices lately"));
                foreach (var app in shownSent)
                    items.Children.Add(SentEntry(app, lastTitles.GetValueOrDefault(app.Key, ""), NoticeText.Relative(app.LastSeen, now), Added(app.Key), () => Add(app.Key, app.Name)));
                if (shownSent.Count == 0) Note(Loc.T("灵动通知开启以来，还没有其他应用发送过通知。", "No other app has sent a notice since live notices were turned on."));
            }
            if (filter.Length == 0 || shownRunning.Count > 0)
            {
                Header(Loc.T("正在运行的应用", "Running apps"));
                foreach (var (app, file) in shownRunning)
                    items.Children.Add(SentEntry(app, file, "", Added(app.Key), () => Add(app.Key, app.Name)));
                if (shownRunning.Count == 0) Note(Loc.T("没有可添加的应用", "No apps to add"));
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        return (menu, search);

        // The words that tell a notice apart in the menu: its title, else the first line of its body.
        static string LastWords(Notice notice)
        {
            var (title, summary) = NoticeText.Presented(notice, CodeMask.Dots);
            string words = notice.Title.Length > 0 ? title : summary;
            return words.Split('\n')[0].Trim();
        }
    }

    // One application in "添加应用": icon, name and when, and under them its last notice's words (a running program's file); an
    // added one is shown, not chosen.
    private Button SentEntry(NoticeKnownApp app, string words, string when, bool added, Action add)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        var icon = NoticeAppIcon(app.Name, app.IconRef ?? AppBrandIcons.Fallback(app.Key), 16, _app.Notices); icon.Margin = new Thickness(0, 1, 9, 0); icon.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRowSpan(icon, 2); grid.Children.Add(icon);
        var name = TextLine(app.Name, 12.5, added ? TextSecondary : TextPrimary); name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var time = TextLine(when, 11, TextTertiary); time.Margin = new Thickness(12, 0, 0, 0); time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 2); grid.Children.Add(time);
        if (words.Length > 0)
        {
            var sample = TextLine(words, 11, TextTertiary); sample.TextTrimming = TextTrimming.CharacterEllipsis; sample.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetColumn(sample, 1); Grid.SetRow(sample, 1); grid.Children.Add(sample);
        }
        if (added)
        {
            var tag = new Border { BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(12, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Child = TextLine(Loc.T("已添加", "Added"), 10.5, TextTertiary) };
            Grid.SetColumn(tag, 2); Grid.SetRow(tag, 1); grid.Children.Add(tag);
        }
        var item = ActionButton("", add); item.Content = grid;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch; item.Padding = new Thickness(10, 6, 10, 6); item.Margin = new Thickness(0, 1, 0, 1);
        item.ToolTip = words.Length > 0 ? app.Name + "\n" + words : app.Name;
        AutomationProperties.SetName(item, (added ? Loc.T("已添加 ", "Added ") : Loc.T("添加 ", "Add ")) + app.Name);
        // An added one stays readable (IsEnabled would dim it to the point of not being read) but does nothing.
        if (added) { item.IsHitTestVisible = false; item.Focusable = false; }
        return item;
    }
}
