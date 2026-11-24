using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The two "灵动提示" pages. How notices show (NoticesSettingsPage): the default level, how each of the three levels looks, the
// collapsed count of unread notices, the quiet times and the history; whether notices show at all is each profile's switch under
// 收起外观 (NoticeIslandSection). Apps (NoticeAppsSettingsPage): the Windows applications whose notices show and
// each one's level and banner, the command-line and WSL sources, and the WSL distributions. An application may have its own looks
// for the levels, under its row, closed until asked for. Source states change while a page is open (access granted, an agent
// started); their lines follow NoticeCenter.Changed without rebuilding the page.
public sealed partial class SettingsWindow
{
    internal const int NoticesSettingsPage = 19, NoticeAppsSettingsPage = 20;
    private readonly List<Action> _noticeStatusLines = [];
    // The applications whose own looks are open under their row.
    private readonly HashSet<string> _noticeAppsOpen = new(StringComparer.Ordinal);

    private static string LevelName(NoticeLevel level) => level switch
    {
        NoticeLevel.Plain => Loc.T("文字", "Text"), NoticeLevel.Persistent => Loc.T("常驻", "Stay"), NoticeLevel.Silent => Loc.T("只进通知页", "List only"),
        _ => Loc.T("闪动", "Flash")
    };

    private static string LevelTitle(NoticeLevel level) => level switch
    {
        NoticeLevel.Plain => Loc.T("等级 1 · 文字", "Level 1 · Text"), NoticeLevel.Persistent => Loc.T("等级 3 · 常驻", "Level 3 · Stay"), _ => Loc.T("等级 2 · 闪动", "Level 2 · Flash")
    };

    private static readonly NoticeLevel[] Levels = [NoticeLevel.Plain, NoticeLevel.Glow, NoticeLevel.Persistent];

    // ----- How notices show -----

    private void NoticesPage()
    {
        var center = _app.Notices;
        _noticeStatusLines.Clear();
        NoticeIslandOffRow();
        SectionTitle(Loc.T("通用", "General"));
        Group(MusicChoice(Loc.T("默认等级", "Default level"), NoticeLevels.Default(S.NoticeDefaultLevel), [.. Levels.Select(level => (level, LevelName(level)))],
                value => Change(s => s.NoticeDefaultLevel = value),
                help: Loc.T("应用的提示等级选“默认”时使用。", "Used for an app whose level is set to Default.")),
            Toggle(Loc.T("遮住验证码", "Hide verification codes"), S.NoticeCodes, value => Change(s => s.NoticeCodes = value),
                help: Loc.T("识别通知里的验证码，用圆点遮住，并在通知页提供“复制验证码”按钮。复制 60 秒后自动清除剪贴板。",
                    "Recognizes a verification code in a notice, hides it with dots and offers a \"Copy code\" button on the notices page. The clipboard is cleared 60 seconds after copying.")));
        foreach (var level in Levels)
        {
            var test = ActionButton(Loc.T("试一下", "Try it"), () => center.SendTest(level));
            AutomationProperties.SetName(test, Loc.T("试一下", "Try it") + " " + LevelTitle(level));
            SectionTitle(LevelTitle(level), test, () => LevelHelp(level));
            var style = S.NoticeStyles.For(level);
            Group([LevelPreview(level, style), .. LevelRows(level, style, change => EditNoticeStyles(s => change(s.NoticeStyles.For(level))), level.ToString(), own: false)]);
        }
        NoticeStripSection();
        NoticeQuietSection(center);
        NoticeHistorySection(center);
        WatchNotices(center);
    }

    private static string LevelHelp(NoticeLevel level) => level switch
    {
        NoticeLevel.Plain => Loc.T("岛的形状不变，只把里面的内容换成提示文字，停留时间到后恢复原样。", "The island keeps its shape; only its content turns into the notice's words, until the time is up."),
        NoticeLevel.Persistent => Loc.T("提示一直留在岛上并定时闪动，直到展开面板或处理后才消失。来电、闹钟和需要审批的提示总是使用这一等级，除非该应用设为“只进通知页”或“不提示”。",
            "The notice stays on the island and flashes now and then until the panel is opened or it is handled. Calls, alarms and approvals always use this level, unless their app is set to List only or Don't show."),
        _ => Loc.T("在等级 1 的基础上，岛的边缘闪一下。辉光：边缘亮一下；灵动：辉光加液体波纹。", "As level 1, and the island's edge flashes. Glow: the edge lights up once. Lively: the glow and a liquid ripple.")
    };

    // Whether arriving notices show on the collapsed island: each profile's switch, under 收起外观 (Compact). Independent of the
    // notices page under 展开外观; either one receives notices.
    private void NoticeIslandSection()
    {
        SectionTitle(Loc.T("灵动提示", "Live notices"), ActionButton(Loc.T("提示方式", "How notices show"), () => ShowPage(NoticesSettingsPage)));
        Group(Toggle(Loc.T("提示新通知", "Show new notices"), S.NoticeIsland, value => Change(s => s.NoticeIsland = value),
            help: Loc.T("收到通知时，在刘海、胶囊或指示条上按“提示方式”中的等级显示。每块屏幕按自己使用的配置决定是否显示。与“展开外观”中的通知页互不影响。",
                "Shows an arriving notice on the notch, capsule or indicator, by the levels under How notices show. Each screen follows the profile it uses. Independent of the notices page under Expanded view.")));
    }

    // The profile in use shows no notices on its island: said at the top of the page whose levels decide how they look, with the
    // page where the switch is.
    private void NoticeIslandOffRow()
    {
        if (S.NoticeIsland) return;
        var note = TextLine(Loc.T("使用这份配置的屏幕不显示提示，可在“收起外观”中开启", "Screens using this profile show no notices; turn it on under Collapsed view"), 11.5, TextSecondary);
        Group(Row(Loc.T("当前配置未开启“提示新通知”", "\"Show new notices\" is off in this profile"), ActionButton(Loc.T("收起外观", "Collapsed view"), () => ShowPage(1)), note));
    }

    // No switch is on, so no notices are received: said at the top of the apps page, with the pages where the switches are.
    private void NoticesOffRow()
    {
        if (S.NoticesEnabled) return;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(ActionButton(Loc.T("收起外观", "Collapsed view"), () => ShowPage(1)));
        var expanded = ActionButton(Loc.T("展开外观", "Expanded view"), () => ShowPage(2)); expanded.Margin = new Thickness(6, 0, 0, 0);
        controls.Children.Add(expanded);
        var note = TextLine(Loc.T("可在“收起外观”或“展开外观”中开启", "Turn it on under Collapsed view or Expanded view"), 11.5, TextSecondary);
        Group(Row(Loc.T("灵动通知未开启", "Live notices are off"), controls, note));
    }

    // The collapsed count of unread notices: switched on for the notch and the capsule under 收起外观 › 显示内容, shown here.
    private void NoticeStripSection()
    {
        SectionTitle(Loc.T("通知条", "Notice count"), ActionButton(Loc.T("收起外观", "Collapsed view"), () => ShowPage(1)),
            () => Loc.T("显示未读通知的条数，在“收起外观”的显示内容中打开。打开通知页后清零。", "Shows how many notices are unread on the island; turn it on in Collapsed view's content. Opening the notices page clears it."));
        Group(Toggle(Loc.T("显示应用图标", "Show app icons"), S.NoticeStripIcons, value => Change(s => s.NoticeStripIcons = value),
            help: Loc.T("在条数前显示最近几条未读通知的应用图标，最多 3 个。关闭后显示铃铛。", "Shows the icons of up to 3 apps the latest unread notices came from before the count. Off, a bell shows.")));
    }

    // The settings' looks change and the page shows them (its previews follow).
    private void EditNoticeStyles(Action<DeckSettings> edit) { Change(edit); ShowPage(_page); }

    // The rows of one look (style, shown as a notice of level shows): the effect of levels 2 and 3, the backlight's color, the icon,
    // and how long levels 1 and 2 stay. The "提示方式" page offers the color for level 3 only, as the one of a message that names
    // none; an application's own looks (own) offer it for level 2 as well. edit changes the look. name tells the rows of several
    // groups on a page apart for screen readers and the segment groups.
    private List<FrameworkElement> LevelRows(NoticeLevel level, NoticeStyle style, Action<Action<NoticeStyle>> edit, string name, bool own)
    {
        var rows = new List<FrameworkElement>();
        if (level != NoticeLevel.Plain)
            rows.Add(NoticeChoice(Loc.T("效果", "Effect"), "NoticeEffect" + name, style.Liquid, [(false, Loc.T("辉光", "Glow")), (true, Loc.T("灵动", "Lively"))],
                value => edit(s => s.Liquid = value), 150));
        if (level == NoticeLevel.Persistent || own && level == NoticeLevel.Glow) rows.Add(LightRow(style, edit, name, own));
        rows.Add(Toggle(Loc.T("显示应用图标", "Show app icon"), style.Icon, value => edit(s => s.Icon = value)));
        if (level != NoticeLevel.Persistent)
            rows.Add(SliderRow(Loc.T("停留时间", "Shown for"), style.Seconds, NoticeStyle.MinSeconds, NoticeStyle.MaxSeconds, 1, Loc.T(" 秒", " s"), value => edit(s => s.Seconds = (int)Math.Round(value))));
        return rows;
    }

    // A row of segments, as MusicChoice, whose group is named apart from its title: the same title (Effect) shows in several groups
    // of one page.
    private FrameworkElement NoticeChoice<T>(string title, string group, T selected, (T Value, string Label)[] options, Action<T> change, double width) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = width };
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = group, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        return Row(title, SegmentBorder(strip));
    }

    // The backlight: white (the island's own), five colors, or the application's color. On the "提示方式" page it is level 3's
    // default color; in an application's own looks (own) the color its notices glow in.
    private FrameworkElement LightRow(NoticeStyle style, Action<Action<NoticeStyle>> edit, string name, bool own)
    {
        string title = own ? Loc.T("辉光颜色", "Glow color") : Loc.T("默认颜色", "Default color");
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        var choices = new List<(string Value, string Label)> { ("", Loc.T("白色", "White")) };
        choices.AddRange(NoticeStyle.Swatches.Select(color => (color, ColorWord(color))));
        choices.Add((NoticeStyle.AppColor, Loc.T("跟随应用", "The app's color")));
        foreach (var (value, label) in choices)
        {
            var swatch = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = "NoticeLight" + name, IsChecked = style.Color == value, ToolTip = label,
                Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 6, 0, 0, 0) };
            swatch.Background = value switch
            {
                "" => new SolidColorBrush(IslandNotice.DefaultLight),
                NoticeStyle.AppColor => UI.Brush(Inset),
                _ => UI.Brush(value)
            };
            if (value == NoticeStyle.AppColor) swatch.Content = Stroke("M 1,1 H 5 V 5 H 1 Z M 7,1 H 11 V 5 H 7 Z M 1,7 H 5 V 11 H 1 Z M 7,7 H 11 V 11 H 7 Z", 12, 12, TextPrimary);
            AutomationProperties.SetName(swatch, title + " " + label);
            string chosen = value;
            swatch.Checked += (_, _) => edit(s => s.Color = chosen);
            swatches.Children.Add(swatch);
        }
        return own ? Row(title, swatches)
            : Row(title, swatches, help: () => Loc.T("消息没有指定颜色、也不属于警告或错误时使用。", "Used when a message names no color and is not a warning or an error."));
    }

    // The swatches' names, in NoticeStyle.Swatches' order.
    private static string ColorWord(string color) => Array.IndexOf(NoticeStyle.Swatches, color) switch
    {
        0 => Loc.T("蓝色", "Blue"), 1 => Loc.T("绿色", "Green"), 2 => Loc.T("琥珀色", "Amber"), 3 => Loc.T("红色", "Red"), _ => Loc.T("紫色", "Purple")
    };

    // A still picture of a notice of this level on a capsule in the panel theme's colors: the words, the icon while it shows, and
    // the backlight of levels 2 and 3 in the style's color. sample: the application, title and words shown; by default a chat's
    // message, and an alarm for level 3.
    private FrameworkElement LevelPreview(NoticeLevel level, NoticeStyle style, (string App, string Title, string Body)? sample = null)
    {
        bool alarm = level == NoticeLevel.Persistent;
        var (app, heading, body) = sample ?? (alarm ? (Loc.T("时钟", "Clock"), Loc.T("闹钟", "Alarm"), Loc.T("07:30 起床", "07:30 Wake up"))
            : ("QQ", Loc.T("产品群", "Product team"), Loc.T("小周：今天的版本什么时候发？", "Zhou: when does today's build go out?")));
        var look = NoticeLook.Current();
        var accent = (Color)ColorConverter.ConvertFromString(_palette.Accent);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (style.Icon)
        {
            var plate = NoticeLetter.Plate(app, 16, accent);
            plate.Margin = new Thickness(0, 0, 7, 0); plate.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(plate);
        }
        var words = new TextBlock { FontFamily = UI.PanelFont, FontSize = 11.5, Foreground = UI.Brush(look.Primary), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        words.Inlines.Add(new System.Windows.Documents.Run(heading) { FontWeight = FontWeights.SemiBold });
        words.Inlines.Add(new System.Windows.Documents.Run("  " + body) { Foreground = UI.Brush(look.Secondary) });
        row.Children.Add(words);
        var island = new Border { Height = 28, Width = 236, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(look.Surface), Padding = new Thickness(style.Icon ? 7 : 14, 0, 14, 0),
            BorderBrush = UI.Brush(_palette.Divider), BorderThickness = new Thickness(1), Child = row, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (level != NoticeLevel.Plain)
        {
            // White on a light settings window would not show, so there the island's own light is drawn in the tertiary text.
            var color = style.Color.Length > 0 && style.Color != NoticeStyle.AppColor ? (Color)ColorConverter.ConvertFromString(style.Color)
                : style.Color == NoticeStyle.AppColor ? accent : _palette.Dark ? IslandNotice.DefaultLight : (Color)ColorConverter.ConvertFromString(TextTertiary);
            island.BorderBrush = new SolidColorBrush(color);
            island.Effect = new DropShadowEffect { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = .75 };
        }
        AutomationProperties.SetName(island, Loc.T("示意：", "Sample: ") + LevelTitle(level));
        return new Border { Height = 64, Margin = new Thickness(0, 10, 0, 10), CornerRadius = new CornerRadius(8), Background = UI.Brush(_palette.Sidebar), Child = island };
    }

    // When notices keep quiet: Windows' focus and presentation states, and BrimDeck's own pause.
    private void NoticeQuietSection(NoticeCenter center)
    {
        SectionTitle(Loc.T("免打扰", "Quiet times"));
        static string PauseLabel(NoticeCenter center) => center.Paused ? Loc.T("恢复", "Resume") : Loc.T("暂停 1 小时", "Pause for 1 hour");
        var pause = ActionButton(PauseLabel(center), () => { center.Pause(center.Paused ? null : TimeSpan.FromHours(1)); ShowPage(NoticesSettingsPage); });
        // A pause that runs out while the page is open turns the button back as the status line changes.
        _noticeStatusLines.Add(() => pause.Content = PauseLabel(center));
        var pauseStatus = StatusLine(() => center.Paused && S.NoticesPausedUntil is { } until
            ? (Loc.T($"已暂停至 {until.ToLocalTime():HH:mm}，期间的通知只进入通知页", $"Paused until {until.ToLocalTime():HH:mm}; notices meanwhile only go to the notices page"), false) : ("", false));
        var focusStatus = StatusLine(() => center.Quiet && !center.Paused ? (Loc.T("Windows 正处于专注、演示或锁定状态，提示暂不显示", "Windows is in focus, presentation or locked state; notices are held back"), false) : ("", false));
        Group(Row(Loc.T("专注与演示时不打扰", "Quiet during focus and presentations"), NoticeToggle(Loc.T("专注与演示时不打扰", "Quiet during focus and presentations"), S.NoticeRespectFocus, value => Change(s => s.NoticeRespectFocus = value)),
                focusStatus, () => center.FocusSupported ? Loc.T("Windows 专注会话或演示模式期间不在岛上提示，结束后显示一次数量汇总。", "No notices show during a Windows focus session or presentation mode; afterwards one summary says how many arrived.")
                    : Loc.T("此 Windows 版本不提供专注会话接口，只识别演示模式。", "This version of Windows does not report focus sessions; only presentation mode is recognized.")),
            Row(Loc.T("暂停提醒", "Pause notices"), pause, pauseStatus));
    }

    private CheckBox NoticeToggle(string name, bool value, Action<bool> changed, bool enabled = true)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, name);
        toggle.Checked += (_, _) => changed(true); toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    // The history: how long it is kept ("退出时清空" keeps it in memory only), the clear button beside the title, and what went
    // wrong reading the saved file, with what can be done about it.
    private void NoticeHistorySection(NoticeCenter center)
    {
        var clear = ActionButton(Loc.T($"清空（{center.Hub.Notices.Count} 条）", $"Clear ({center.Hub.Notices.Count})"), () => { center.Clear(); ShowPage(NoticesSettingsPage); });
        AutomationProperties.SetName(clear, Loc.T("清空通知历史", "Clear notice history"));
        SectionTitle(Loc.T("通知历史", "Notice history"), clear, () => Loc.T("通知页里的通知保存在本机，用 Windows 为当前用户提供的加密，最多 500 条，不保存附件和头像。只清除 BrimDeck 中的记录，Windows 通知中心不受影响。",
            "The notices of the notices page are saved on this PC with the encryption Windows provides for the current user, at most 500, without attachments or pictures. Clearing removes BrimDeck's records only; Windows' notification center is not affected."));
        int[] hours = [1, 24, 72, 168];
        var labels = new List<string> { Loc.T("退出时清空", "Until exit") };
        labels.AddRange(hours.Select(h => h < 24 ? Loc.T($"{h} 小时", $"{h} h") : Loc.T($"{h / 24} 天", Loc.Count(h / 24, "day", "days"))));
        int index = !S.NoticeHistory ? 0 : Array.IndexOf(hours, S.NoticeHistoryHours) is var at && at >= 0 ? at + 1 : 2;
        var keep = new ComboBox { Width = 120, Style = (Style)FindResource("SettingsCombo"), ItemsSource = labels, SelectedIndex = index };
        AutomationProperties.SetName(keep, Loc.T("保留时间", "Keep for"));
        keep.SelectionChanged += (_, _) =>
        {
            int chosen = keep.SelectedIndex;
            if (chosen < 0) return;
            Change(s => { s.NoticeHistory = chosen > 0; if (chosen > 0) s.NoticeHistoryHours = hours[chosen - 1]; });
        };
        var rows = new List<FrameworkElement> { Row(Loc.T("保留时间", "Keep for"), keep) };
        Button Rebuild() => ActionButton(Loc.T("清空并重建", "Clear and start again"), () => { center.ResetHistory(); ShowPage(NoticesSettingsPage); });
        string Reason() => center.HistoryState == NoticeHistoryState.Refused && center.HistoryRefusal is { } refusal ? NoticeStorage.Describe(refusal, center.HistoryClosable) : NoticeHistory.Describe(center.HistoryState);
        if (center.HistoryState == NoticeHistoryState.Unreadable || center.HistoryState == NoticeHistoryState.Refused && center.HistoryRefusal is null)
            rows.Add(Row(Loc.T("无法读取已保存的通知", "Saved notices unreadable"), Rebuild(), TextLine(Reason(), 11, _palette.Negative)));
        else if (center.HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Refused or NoticeHistoryState.Denied)
        {
            // Follows a retry while the page shows.
            var reason = StatusLine(() => center.HistoryState is NoticeHistoryState.Unavailable or NoticeHistoryState.Refused or NoticeHistoryState.Denied
                ? (Reason(), true) : (Loc.T("已读取。", "Read."), false));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            void Add(Button button) { if (buttons.Children.Count > 0) button.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(button); }
            string title;
            if (center.HistoryState == NoticeHistoryState.Refused)
            {
                title = Loc.T("无法使用保存通知的文件夹", "The notices folder cannot be used");
                if (center.HistoryRefusal == NoticeStorage.FolderTrust.Shared && center.HistoryClosable)
                    Add(ActionButton(Loc.T("仅限本人访问", "Make it mine only"), async () => { await center.CloseHistoryFolderAsync(); ShowPage(NoticesSettingsPage); }));
            }
            else
            {
                title = center.HistoryState == NoticeHistoryState.Denied ? Loc.T("没有权限读取已保存的通知", "Saved notices not readable") : Loc.T("暂时无法读取已保存的通知", "Saved notices not reachable");
                Add(ActionButton(Loc.T("重试", "Retry"), center.RetryHistory));
                if (center.HistoryState == NoticeHistoryState.Denied) Add(Rebuild());
            }
            rows.Add(Row(title, buttons, reason));
        }
        Group([.. rows]);
    }

    // ----- Apps -----

    private void NoticeAppsPage()
    {
        var center = _app.Notices;
        _noticeStatusLines.Clear();
        NoticesOffRow();
        WindowsAppsSection(center);
        ProgramSourcesSection(center);
        WslSection(center);
        NoticeCompatibilitySection();
        WatchNotices(center);
    }

    // What brim notify list and remove may reach (NoticeEntries): without this, the notices of the command line and programs only.
    private void NoticeCompatibilitySection()
    {
        SectionTitle(Loc.T("兼容性", "Compatibility"));
        Group(Toggle(Loc.T("允许 brim 读取和删除所有通知", "Let brim list and remove all notices"), S.NoticeCommandAccess, value => Change(s => s.NoticeCommandAccess = value),
            help: Loc.T("开启后，brim notify list 和 remove 可以读取、删除所有来源的通知，删除 Windows 应用的通知时同时从 Windows 通知中心删除。本机运行的任何程序都能使用 brim。",
                "When on, brim notify list and remove reach the notices of every source, and removing a Windows app's notice also removes it from Windows' notification center. Any program on this PC can run brim.")));
    }

    // The status lines follow the sources while the page is shown; the banner switches read Windows again whenever the settings
    // window comes forward, so one changed in Windows Settings meanwhile shows as it is.
    private void WatchNotices(NoticeCenter center)
    {
        var watcher = new Border();
        Action refresh = () => { foreach (var line in _noticeStatusLines.ToList()) line(); };
        EventHandler banners = (_, _) => { foreach (var read in _bannerSwitches.ToList()) read(); };
        watcher.Loaded += (_, _) => { center.Changed += refresh; Activated += banners; };
        watcher.Unloaded += (_, _) => { center.Changed -= refresh; Activated -= banners; };
        Target.Children.Add(watcher);
    }

    // Each shown banner switch's reading of Windows (BannerSwitch); filled as the page is built, emptied with it.
    private readonly List<Action> _bannerSwitches = [];

    private TextBlock StatusLine(Func<(string Text, bool Problem)> read)
    {
        var line = TextLine("", 11.5, TextSecondary);
        line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None; line.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        // An empty line takes no room, so a line whose state may come later (the local channel failing) can always be on the page.
        void Update() { var (text, problem) = read(); line.Text = text; line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; line.Foreground = UI.Brush(problem ? _palette.Negative : TextSecondary); }
        Update();
        _noticeStatusLines.Add(Update);
        return line;
    }

    // The Windows applications set apart from the rest, and the chats whose taskbar button flashes (WeChat). Every Windows
    // application's notices are received unless its row's switch is off; a taskbar-flash chat only while listed. While Windows does
    // not let BrimDeck read notifications, a row says so first, with the way to allow it.
    private void WindowsAppsSection(NoticeCenter center)
    {
        var add = AddAppButton(Loc.T("添加应用", "Add app"), AddNoticeAppMenu);
        SectionTitle(Loc.T("Windows 应用", "Windows apps"), add, () => Loc.T("所有 Windows 应用的通知默认都会接收。把应用添加到这里，可以单独关闭接收、调整提示等级，或开关它的系统横幅。移除后恢复默认。\nBrimDeck 读取的是应用发给 Windows 的通知：钉钉、飞书需要在各自的设置中打开桌面通知；微信不发送 Windows 通知，只能在任务栏图标闪烁时提示有新消息。",
            "Every Windows app's notices are received by default. Add an app here to stop receiving it, change its level, or switch its system banner. Removing it goes back to the default.\nBrimDeck reads the notifications apps send to Windows: DingTalk and Feishu need desktop notifications turned on in their own settings; WeChat sends none, so only its flashing taskbar button tells of a new message."));
        _bannerSwitches.Clear();
        var rows = new List<FrameworkElement>();
        var access = AccessRow(center);
        rows.Add(access);
        var listed = S.NoticeApps.Where(rule => NoticeKeys.OnAppList(rule.Key)).ToList();
        if (listed.Count == 0)
        {
            var none = TextLine(Loc.T("没有应用。所有应用的通知都会接收；需要单独设置的应用可以添加到这里。", "No apps. Every app's notices are received; add an app here to set it apart."), 12, TextSecondary);
            none.TextWrapping = TextWrapping.Wrap;
            rows.Add(new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
        }
        else
        {
            rows.Add(AppColumns(windows: true));
            foreach (var rule in listed) rows.Add(NoticeAppRow(center, rule.Key, AppName(center, rule), windows: true));
        }
        GroupShown([.. rows]);
    }

    // A group whose rows may hide (a status row with nothing to say): the dividers go with the rows they sit above.
    private void GroupShown(params FrameworkElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            var divider = new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) };
            if (panel.Children.Count > 0)
            {
                panel.Children.Add(divider);
                void Follow() => divider.Visibility = row.Visibility;
                Follow();
                row.IsVisibleChanged += (_, _) => Follow();
                _noticeStatusLines.Add(Follow);
            }
            panel.Children.Add(row);
        }
        Target.Children.Add(new Border { Child = panel, Background = Brushes.Transparent, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 0, 0, 28) });
    }

    // How the settings learn whether brim is found in a terminal and add BrimDeck's folder to PATH; the rendering of this page
    // for checking puts its own in their place, so that it shows every state without reading or writing the registry.
    internal Func<bool> BrimOnPath = BrimCommand.OnPath;
    internal Func<string?> BrimAddToPath = BrimCommand.AddForUser;

    // Shown while BrimDeck's folder is not in PATH (the installer could not add it, or this is a portable copy), laid out as
    // AccessRow is: what is wrong and what follows from it, the button that sets it right for the current user, and behind the
    // info mark how to do the same by hand, with the folder to enter (which the second button copies: a description cannot be
    // copied from). A change that failed says why and points to doing it by hand. Once the folder is in PATH the row only
    // says what is left to do (open a new terminal), and is gone the next time the page is built. The registry is read when
    // the page is built and after the button, not with every refresh.
    private FrameworkElement PathRow()
    {
        bool onPath = BrimOnPath(), added = false;
        string problem = "", folder = BrimCommand.Folder;
        var title = TextLine("", 13, _palette.Negative, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        var help = HelpIcon(Loc.T("brim 命令未加入 PATH", "brim is not on PATH"), () => Loc.T(
            "手动添加：在开始菜单搜索“环境变量”，打开“编辑账户的环境变量”，在“用户变量”中选中 Path，点“编辑”，再点“新建”，填入下面的目录，确定后重新打开终端。\n",
            "To add it by hand: search the Start menu for \"environment variables\", open \"Edit environment variables for your account\", select Path under \"User variables\", choose Edit, then New, enter the folder below, confirm, and open a new terminal.\n") + folder, 14);
        var heading = new HelpHeading(title, help) { HorizontalAlignment = HorizontalAlignment.Left };
        var detail = StatusLine(() => problem.Length > 0 ? (problem, true)
            : added ? (Loc.T("重新打开终端后生效", "Open a new terminal to use it"), false)
            : onPath ? ("", false)
            : (Loc.T("终端、脚本和 AI 工具的钩子找不到 brim，无法通过它向 BrimDeck 发送提示。", "Terminals, scripts and the hooks of AI tools do not find brim, and cannot send notices to BrimDeck through it."), false));
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        labels.Children.Add(heading); labels.Children.Add(detail);
        var row = new Grid { MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string copyName = Loc.T("复制目录", "Copy folder");
        Button? copy = null;
        copy = ActionButton(copyName, () =>
        {
            bool done = NoticeCenter.Copy(folder);
            copy!.Content = done ? Loc.T("已复制", "Copied") : Loc.T("复制失败", "Copy failed");
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            back.Tick += (_, _) => { back.Stop(); copy.Content = copyName; };
            back.Start();
        });
        copy.ToolTip = folder;
        var add = ActionButton(Loc.T("加入 PATH", "Add to PATH"), () => { });
        // Drawn as a button, with its border and fill ("添加应用" on this page is drawn so): it is what sets the row right, and
        // must not read as one more word beside the title.
        add.Style = (Style)FindResource("SettingsOutlineButton");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(copy); controls.Children.Add(add);
        Grid.SetColumn(controls, 1);
        row.Children.Add(labels); row.Children.Add(controls);
        void Update()
        {
            title.Text = onPath ? added ? Loc.T("brim 命令已加入 PATH", "brim is on PATH now") : "" : Loc.T("brim 命令未加入 PATH", "brim is not on PATH");
            title.Foreground = UI.Brush(onPath ? TextPrimary : _palette.Negative);
            // In PATH there is nothing left to do by hand.
            help.Visibility = controls.Visibility = onPath ? Visibility.Collapsed : Visibility.Visible;
            row.Visibility = title.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        add.Click += (_, _) =>
        {
            string? failed = BrimAddToPath();
            onPath = added = failed is null;
            problem = failed is null ? "" : Loc.T("无法修改 PATH：", "PATH could not be changed: ") + failed.TrimEnd('.', '。', ' ') + Loc.T("。请按标题旁的说明手动添加。", ". Add it by hand as the description beside the title says.");
            foreach (var line in _noticeStatusLines.ToList()) line();
        };
        Update(); _noticeStatusLines.Add(Update);
        return row;
    }

    // Shown while Windows has not allowed BrimDeck to read notifications (or reading failed), with the way to change that.
    private FrameworkElement AccessRow(NoticeCenter center)
    {
        var allow = ActionButton(Loc.T("允许访问", "Allow access"), async () => { await center.RequestWindowsAccessAsync(); });
        var settings = ActionButton(Loc.T("打开系统设置", "Open Windows settings"), () => UI.OpenUrl("ms-settings:privacy-notifications"));
        var controls = new StackPanel { Orientation = Orientation.Horizontal }; controls.Children.Add(allow); controls.Children.Add(settings);
        var title = TextLine("", 13, _palette.Negative, FontWeights.SemiBold);
        var detail = StatusLine(() => center.WindowsState switch
        {
            NoticeSourceState.Denied => (Loc.T("在“隐私和安全性 › 通知”中允许应用访问通知", "Allow apps to access notifications in Privacy & security › Notifications"), false),
            NoticeSourceState.Failed => (Loc.T("稍后重试。", "Trying again shortly.") + (center.WindowsFailure.Length > 0 ? " " + center.WindowsFailure : ""), false),
            _ => ("", false)
        });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        labels.Children.Add(title); labels.Children.Add(detail);
        var row = new Grid { MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels); controls.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(controls, 1); row.Children.Add(controls);
        void Update()
        {
            var state = center.WindowsState;
            title.Text = state switch
            {
                NoticeSourceState.NeedsAccess => Loc.T("未获准读取 Windows 通知", "Not allowed to read Windows notifications"),
                NoticeSourceState.Denied => Loc.T("Windows 拒绝了读取通知", "Windows denies reading notifications"),
                NoticeSourceState.Failed => Loc.T("读取 Windows 通知失败", "Reading Windows notifications failed"),
                _ => ""
            };
            allow.Visibility = state == NoticeSourceState.NeedsAccess ? Visibility.Visible : Visibility.Collapsed;
            settings.Visibility = state == NoticeSourceState.Denied ? Visibility.Visible : Visibility.Collapsed;
            row.Visibility = title.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Update(); _noticeStatusLines.Add(Update);
        return row;
    }

    // The column headings over the rows: the level, and for Windows applications the banner switch.
    private FrameworkElement AppColumns(bool windows)
    {
        var row = AppGrid();
        row.MinHeight = 28;
        var level = TextLine(Loc.T("提示等级", "Level"), 11, TextTertiary); level.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(level, 2); row.Children.Add(level);
        if (windows)
        {
            row.Children.Add(Heading(3, Loc.T("接收", "Receive"), () => Loc.T("关闭后，BrimDeck 不再显示这个应用的通知，也不记入通知页。Windows 自己的通知不受影响。",
                "When off, BrimDeck no longer shows this app's notices nor keeps them on the notices page. Windows' own notifications are not affected.")));
            row.Children.Add(Heading(4, Loc.T("系统横幅", "System banner"), () => Loc.T("与 Windows 设置中这个应用的“显示通知横幅”是同一个开关，显示的是它现在的状态，在这里切换会直接修改 Windows 设置，退出 BrimDeck 后仍然保持。关闭后，通知不再从屏幕右下角弹出，仍会进入 Windows 通知中心，提示音不变。",
                "The same switch as this app's \"Show notification banners\" in Windows Settings, shown as it is now; switching it here changes Windows Settings and stays after BrimDeck exits. When off, notices no longer pop up in the corner of the screen; they still go to Windows' notification center and keep their sound.")));
        }
        return row;

        FrameworkElement Heading(int column, string title, Func<string> help)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(TextLine(title, 11, TextTertiary));
            var info = HelpIcon(title, help, 12);
            info.Margin = new Thickness(4, 0, 0, 0); heading.Children.Add(info);
            Grid.SetColumn(heading, column);
            return heading;
        }
    }

    // icon · name · level · receive · banner · remove
    private static Grid AppGrid()
    {
        var row = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        return row;
    }

    // An application's name as the settings show it: the one it was listed under, else the one its notices carry, else its key.
    private static string AppName(NoticeCenter center, NoticeAppRule rule)
        => rule.Name.Length > 0 ? rule.Name : center.Hub.KnownApps.FirstOrDefault(app => app.Key == rule.Key)?.Name ?? rule.Key;

    // One application or source: its icon, its name (with the arrow that opens its own looks), what to know about it, its level,
    // for a Windows application its receive switch and Windows' own banner switch, and the cross that takes it off the list (back
    // to the default; a source of the command line shows again with its next message, so it has none). Its own looks show under
    // it while open.
    private FrameworkElement NoticeAppRow(NoticeCenter center, string key, string name, bool windows)
    {
        var rule = S.NoticeRule(key);
        var known = center.Hub.KnownApps.FirstOrDefault(app => app.Key == key);
        var row = AppGrid();
        var icon = NoticeAppIcon(name, known?.IconRef, 20, center); icon.Margin = new Thickness(0, 0, 12, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        bool open = _noticeAppsOpen.Contains(key);
        var title = TextLine(name, 13, TextPrimary); title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center; title.ToolTip = name;
        var heading = new DockPanel { LastChildFill = false };
        var arrow = ChevronButton(Loc.T("单独设置样子：", "Own looks: ") + name, () => { if (!_noticeAppsOpen.Remove(key)) _noticeAppsOpen.Add(key); ShowPage(_page); });
        PointArrow(arrow, open); arrow.Margin = new Thickness(2, 0, 0, 0);
        title.MaxWidth = 200;
        heading.Children.Add(title); heading.Children.Add(arrow);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 12, 7) };
        labels.Children.Add(heading);
        var note = StatusLine(() => AppNote(center, key, rule));
        labels.Children.Add(note);
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        bool receives = rule?.Enabled ?? true;
        var level = LevelCombo(key, name, rule, windows);
        level.VerticalAlignment = VerticalAlignment.Center; level.HorizontalAlignment = HorizontalAlignment.Left;
        level.IsEnabled = !windows || receives;
        Grid.SetColumn(level, 2); row.Children.Add(level);
        if (windows)
        {
            var receive = NoticeToggle(name + " · " + Loc.T("接收", "Receive"), receives, value =>
            {
                EditRule(key, name, r => r.Enabled = value); level.IsEnabled = value;
                if (_noticeAppsOpen.Contains(key)) ShowPage(_page);
            });
            receive.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(receive, 3); row.Children.Add(receive);
            if (key.StartsWith("windows:", StringComparison.Ordinal) && BannerSwitch(center, key["windows:".Length..], name) is { } banner)
            {
                Grid.SetColumn(banner, 4); row.Children.Add(banner);
            }
            var cross = RemoveButton(name, () => { Change(s => s.NoticeApps.RemoveAll(r => r.Key == key)); _noticeAppsOpen.Remove(key); ShowPage(_page); });
            Grid.SetColumn(cross, 5); row.Children.Add(cross);
        }
        bool wechat = key == WeChatRows.AppKey && receives;
        if (!open && !wechat) return row;
        var stack = new StackPanel(); stack.Children.Add(row);
        if (wechat) stack.Children.Add(WeChatOptions(rule, name));
        if (open) stack.Children.Add(OwnLooks(key, name, rule));
        return stack;
    }

    // What to know about a row: what WeChat's and the Clock's notices are, and what went wrong (the taskbar watch, the banner).
    private (string, bool) AppNote(NoticeCenter center, string key, NoticeAppRule? rule)
    {
        if (key == WeChatRows.AppKey && rule is { Enabled: true, ReadContent: true } && center.ChatFlashError is null) return WeChatNote(center);
        if (key.StartsWith("flash:", StringComparison.Ordinal))
            return center.ChatFlashError is { } error ? (Loc.T("无法监视任务栏：", "Could not watch the taskbar: ") + error, true) : (Loc.T("任务栏图标闪烁时提示“有新消息”", "\"New message\" when its taskbar button flashes"), false);
        if (key.StartsWith("windows:", StringComparison.Ordinal))
        {
            string appId = key["windows:".Length..];
            if (_bannerNotes.TryGetValue(appId, out var problem)) return (problem, true);
            if (NoticeTimeSources.ForApp(appId) is { Role: NoticeTimeRole.Clock }) return (Loc.T("闹钟、计时器结束", "Alarms and finished timers"), false);
        }
        // A common chat none of whose notices has arrived yet: what to turn on in it. Gone with its first notice.
        if (ChatSetup(key) is { } setup && center.Hub.KnownApps.All(app => app.Key != key)) return (setup, false);
        if (rule?.Styles is not null) return (Loc.T("已单独设置样子", "Has its own looks"), false);
        return ("", false);
    }

    // The common chats offered first under "添加应用", whether or not they have sent a notice yet, by the application ids their
    // notices carry (QQ's, DingTalk's and Feishu's are the ids their Start menu entries register); WeChat sends no Windows
    // notifications and is known by its taskbar flash (ChatFlash).
    private static (string Key, string Name)[] CommonChats() =>
    [
        (NoticeKeys.Windows("QQ"), "QQ"), (NoticeKeys.Windows("ali.dingtalk.release"), Loc.T("钉钉", "DingTalk")),
        (NoticeKeys.Windows("com.electron.Feishu"), Loc.T("飞书", "Feishu")), ("flash:wechat", Loc.T("微信", "WeChat"))
    ];

    // What to turn on in a common chat for its messages to reach Windows' notifications, in the chat's own words (DingTalk's from
    // its installed interface text, Feishu's from its help center).
    private static string? ChatSetup(string key) => key switch
    {
        "windows:QQ" => Loc.T("还没有收到通知：确认 QQ 设置中已开启消息通知", "No notice yet: check that message notifications are on in QQ's settings"),
        "windows:ali.dingtalk.release" => Loc.T("还没有收到通知：在钉钉“设置与隐私 › 通知”中打开“消息桌面通知”",
            "No notice yet: in DingTalk, turn on \"Message Desktop Notifications\" under \"Settings and Privacy › Notifications\""),
        "windows:com.electron.Feishu" => Loc.T("还没有收到通知：在飞书“设置 › 通知”中勾选“横幅消息通知”，并选择显示消息内容",
            "No notice yet: in Feishu, select \"Banner notification\" under \"Settings › Notifications\" and choose to show the message content"),
        _ => null
    };

    // Windows' own banner switch of an application, as Windows has it now (NoticeBanners.Shown), read again when the settings window
    // comes forward. Switching it writes Windows' setting at once; one that does not stay is put back and said under the name. Off
    // and unchangeable while the application's notifications are off in Windows; none for an id Windows keeps no setting under.
    private CheckBox? BannerSwitch(NoticeCenter center, string appId, string name)
    {
        if (!NoticeBanners.Usable(appId) || appId == WindowsNoticeSource.OwnAppId) return null;
        string notificationsOff = Loc.T("Windows 中已关闭这个应用的通知", "This app's notifications are off in Windows");
        bool reading = false;
        CheckBox? toggle = null;
        toggle = NoticeToggle(name + " · " + Loc.T("系统横幅", "System banner"), false, value =>
        {
            if (reading) return;
            if (NoticeBanners.SetShown(center.BannerRegistry, appId, value)) _bannerNotes.Remove(appId);
            else _bannerNotes[appId] = value ? Loc.T("未能打开系统横幅，请在 Windows 设置中打开", "The system banner could not be turned on; turn it on in Windows Settings")
                : Loc.T("未能关闭系统横幅，请在 Windows 设置中关闭", "The system banner could not be turned off; turn it off in Windows Settings");
            Read();
        });
        toggle.HorizontalAlignment = HorizontalAlignment.Center;
        void Read()
        {
            bool? shown;
            try { shown = NoticeBanners.Shown(center.BannerRegistry, appId); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { shown = null; }
            if (shown is null) _bannerNotes[appId] = notificationsOff;
            else if (_bannerNotes.TryGetValue(appId, out var note) && note == notificationsOff) _bannerNotes.Remove(appId);
            reading = true;
            toggle!.IsChecked = shown == true;
            toggle.IsEnabled = shown is not null;
            reading = false;
            foreach (var line in _noticeStatusLines.ToList()) line();
        }
        Read();
        _bannerSwitches.Add(Read);
        return toggle;
    }

    // What went wrong with an application's banner switch, said under its name, by application id.
    private readonly Dictionary<string, string> _bannerNotes = new(StringComparer.OrdinalIgnoreCase);

    // The level: Default (the default level, named), the three levels, "只进通知页"; a command-line source has "按发送方" in place of
    // Default and "不提示" as well.
    private ComboBox LevelCombo(string key, string name, NoticeAppRule? rule, bool windows)
    {
        var options = new List<(NoticeLevel Level, bool Enabled, string Label)>
        {
            (NoticeLevel.Auto, true, windows ? Loc.T("默认（", "Default (") + LevelName(NoticeLevels.Default(S.NoticeDefaultLevel)) + Loc.T("）", ")") : Loc.T("按发送方", "As sent")),
            (NoticeLevel.Plain, true, LevelName(NoticeLevel.Plain)), (NoticeLevel.Glow, true, LevelName(NoticeLevel.Glow)), (NoticeLevel.Persistent, true, LevelName(NoticeLevel.Persistent)),
            (NoticeLevel.Silent, true, LevelName(NoticeLevel.Silent))
        };
        if (!windows) options.Add((NoticeLevel.Auto, false, Loc.T("不提示", "Don't show")));
        int selected = rule is { Enabled: false } ? options.Count - 1 : Math.Max(0, options.FindIndex(option => option.Enabled && option.Level == (rule?.Level ?? NoticeLevel.Auto)));
        var combo = new ComboBox { Width = 132, Style = (Style)FindResource("SettingsCombo"), ItemsSource = options.Select(option => option.Label).ToList(), SelectedIndex = selected };
        AutomationProperties.SetName(combo, name + " · " + Loc.T("提示等级", "Level"));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var (level, enabled, _) = options[combo.SelectedIndex];
            EditRule(key, name, r => { r.Level = level; r.Enabled = enabled; });
            // The own looks open under the row show the chosen level's.
            if (_noticeAppsOpen.Contains(key)) ShowPage(_page);
        };
        return combo;
    }

    // Changes an application's rule, adding it for a source that had none; a source's rule that no longer differs from having none
    // goes, so the list holds only what the user chose.
    private void EditRule(string key, string name, Action<NoticeAppRule> edit) => Change(s =>
    {
        var rule = s.NoticeRule(key);
        if (rule is null) { rule = new NoticeAppRule { Key = key, Name = name }; s.NoticeApps.Add(rule); }
        edit(rule);
        if (!NoticeKeys.OnAppList(key) && rule.Enabled && rule.Level == NoticeLevel.Auto && rule.Styles is null) s.NoticeApps.Remove(rule);
    });

    // An application's own looks: off follows the "提示方式" page; on, a copy of it to change. Shown is the look of the level its
    // notices show at (the three levels for a program whose level its messages decide), and the look of the notices that stay
    // until handled whatever the level: a chat's calls (their own look, NoticeLevelStyles.Call), and a clock's, a calendar's or a
    // program's that ring or wait (level 3's, while the level shown is not 3).
    private FrameworkElement OwnLooks(string key, string name, NoticeAppRule? rule)
    {
        var panel = Collect(() =>
        {
            var toggle = Toggle(Loc.T("单独设置样子", "Own looks"), rule?.Styles is not null, value => { EditRule(key, name, r => r.Styles = value ? S.NoticeStyles.Copy() : null); ShowPage(_page); },
                help: Loc.T("打开后，这个应用的提示按这里设置的样子显示，优先于“提示方式”页和消息自己指定的样子；关闭后恢复跟随“提示方式”页。",
                    "When on, this app's notices look as set here, before the How notices show page and what a message asks for; when off, they follow that page again."));
            if (rule?.Styles is not { } styles) { Group(toggle); return; }
            if (!rule.Enabled || rule.Level == NoticeLevel.Silent)
            {
                var none = TextLine(Loc.T("这个应用的通知不在岛上显示。", "This app's notices do not show on the island."), 12, TextSecondary);
                Group(toggle, new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
                return;
            }
            Group(toggle);
            void Edit(Func<NoticeLevelStyles, NoticeStyle> pick, Action<NoticeStyle> change) { EditRule(key, name, r => { r.Styles ??= S.NoticeStyles.Copy(); change(pick(r.Styles)); }); ShowPage(_page); }
            var sample = (name, name, Loc.T("一条新通知", "A new notice"));
            NoticeLevel[] levels = rule.Level == NoticeLevel.Auto
                ? ProgramSource(key) ? Levels : [NoticeLevels.Default(S.NoticeDefaultLevel)]
                : [NoticeLevels.Default(rule.Level)];
            foreach (var level in levels)
            {
                SectionTitle(levels.Length > 1 ? LevelTitle(level) : Loc.T("通知 · ", "Notices · ") + LevelTitle(level));
                Group([LevelPreview(level, styles.For(level), sample), .. LevelRows(level, styles.For(level), change => Edit(s => s.For(level), change), key + level, own: true)]);
            }
            if (TakesCalls(key, name))
            {
                SectionTitle(Loc.T("来电", "Calls"), help: () => Loc.T("语音和视频来电一直留在岛上并定时闪动，直到接听、挂断或展开面板。", "Voice and video calls stay on the island and flash now and then until answered, declined or the panel is opened."));
                Group([LevelPreview(NoticeLevel.Persistent, styles.ForCall(), (name, Loc.T("小周", "Zhou"), Loc.T("邀请你语音通话", "Incoming voice call"))),
                    .. LevelRows(NoticeLevel.Persistent, styles.ForCall(), change => Edit(s => s.Call ??= s.Stay.Copy(), change), key + "Call", own: true)]);
            }
            if (!levels.Contains(NoticeLevel.Persistent) && StayKind(key) is { } stay)
            {
                SectionTitle(stay.Title, help: () => stay.Help);
                Group([LevelPreview(NoticeLevel.Persistent, styles.Stay, (name, name, stay.Sample)), .. LevelRows(NoticeLevel.Persistent, styles.Stay, change => Edit(s => s.Stay, change), key + "Stay", own: true)]);
            }
        });
        if (panel.Children.Count > 0 && panel.Children[^1] is Border last) last.Margin = new Thickness(0, 0, 0, 8);
        panel.Margin = new Thickness(32, 0, 0, 0);
        return panel;
    }

    // A source of the command line or WSL, whose messages may ask for any level.
    private static bool ProgramSource(string key) => key.StartsWith("cli:", StringComparison.Ordinal) || key.StartsWith("wsl:", StringComparison.Ordinal);

    // A Windows application whose notices BrimDeck may read as calls (WindowsNoticeApps.ReadsAsCall): a chat or a phone. A chat
    // known by its taskbar flash only (WeChat) gives no words to read a call from.
    private static bool TakesCalls(string key, string name)
        => key.StartsWith("windows:", StringComparison.Ordinal) && NoticeGlyphs.App(key, name) is NoticeGlyph.Chat or NoticeGlyph.Phone;

    // The notices of a source that stay until handled whatever its level: a clock's alarms, a calendar's or a to-do list's
    // reminders, and what a program waits on (an approval, a question). Null for any other source.
    private static (string Title, string Help, string Sample)? StayKind(string key)
    {
        if (ProgramSource(key))
            return (Loc.T("等待处理", "Waiting on you"), Loc.T("需要批准或回答的提示一直留在岛上，直到处理。", "A notice that asks for an approval or an answer stays on the island until it is handled."), Loc.T("等待批准", "Waiting for approval"));
        if (!key.StartsWith("windows:", StringComparison.Ordinal) || NoticeTimeSources.ForApp(key["windows:".Length..]) is not { } source) return null;
        return source.Role switch
        {
            NoticeTimeRole.Clock => (Loc.T("闹钟与计时器", "Alarms and timers"), Loc.T("响铃的闹钟和结束的计时器一直留在岛上，直到处理。", "A ringing alarm or a finished timer stays on the island until it is handled."), Loc.T("07:30 起床", "07:30 Wake up")),
            NoticeTimeRole.Reminders => (Loc.T("提醒", "Reminders"), Loc.T("到时的提醒一直留在岛上，直到处理。", "A reminder that is due stays on the island until it is handled."), Loc.T("交周报", "Send the weekly report")),
            _ => (Loc.T("日程提醒", "Event reminders"), Loc.T("日程开始前的提醒一直留在岛上，直到处理。", "A reminder of an event about to start stays on the island until it is handled."), Loc.T("周会 15:00", "Weekly meeting 15:00"))
        };
    }

    // The icon a row shows: the application's own, read from its notices, else its first letter on a plate.
    private FrameworkElement NoticeAppIcon(string name, string? iconRef, double size, NoticeCenter center)
    {
        if (center.Icons.Get(iconRef, out _) is { } image)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        return NoticeLetter.Plate(name, size, (Color)ColorConverter.ConvertFromString(_palette.Accent));
    }

    // "添加应用": a search field, the applications that sent notices lately and are not listed, then the other applications Windows
    // keeps notification settings for (read in the background) and the chats whose taskbar button BrimDeck watches. Typing filters
    // both; the list scrolls past what fits.
    private void AddNoticeAppMenu(Button anchor)
    {
        var center = _app.Notices;
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var search = new TextBox { Style = (Style)FindResource("SettingsField"), Height = 28, FontSize = 12.5, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(2, 2, 2, 6) };
        AutomationProperties.SetName(search, Loc.T("搜索应用", "Search apps"));
        var items = new StackPanel { Width = 268 };
        var list = new ScrollViewer { Style = (Style)FindResource("SettingsScrollViewer"), Content = items, Focusable = false, MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel(); body.Children.Add(search); body.Children.Add(list);
        popup.Child = new Border { Child = body, Background = UI.Brush(_palette.Popup), BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5) };
        var now = DateTimeOffset.UtcNow;
        var chats = CommonChats().Where(chat => S.NoticeRule(chat.Key) is null).ToList();
        bool Chat(string key) => chats.Any(chat => chat.Key == key);
        var recent = center.Hub.KnownApps.Where(app => NoticeKeys.OnAppList(app.Key) && S.NoticeRule(app.Key) is null && !Chat(app.Key))
            .Select(app => (app.Key, app.Name, app.IconRef, When: NoticeText.Relative(app.LastSeen, now))).ToList();
        var others = ChatFlashApps.All.Where(app => app.Enabled && S.NoticeRule("flash:" + app.Id) is null && !Chat("flash:" + app.Id) && recent.All(r => r.Key != "flash:" + app.Id))
            .Select(app => ("flash:" + app.Id, app.Name + Loc.T("（任务栏闪烁）", " (taskbar flash)"))).ToList();
        void Add(string key, string name)
        {
            popup.IsOpen = false;
            Change(s => { if (s.NoticeRule(key) is null) s.NoticeApps.Add(new NoticeAppRule { Key = key, Name = name }); });
            ShowPage(_page);
        }
        void Fill()
        {
            items.Children.Clear();
            string filter = search.Text.Trim();
            bool Match(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
            void Header(string text)
            {
                if (items.Children.Count > 0) items.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider), Margin = new Thickness(4, 5, 4, 3) });
                var header = TextLine(text, 10.5, TextTertiary); header.Margin = new Thickness(10, 4, 10, 3); items.Children.Add(header);
            }
            void Entry(string key, string name, string? iconRef, string note)
            {
                var item = MenuEntry(name, note, true, false);
                if (item.Content is DockPanel content)
                {
                    var plate = NoticeAppIcon(name, iconRef, 16, center); plate.Margin = new Thickness(0, 0, 9, 0); plate.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(plate, Dock.Left); content.Children.Insert(0, plate);
                    foreach (var text in content.Children.OfType<TextBlock>()) text.TextTrimming = TextTrimming.CharacterEllipsis;
                }
                item.ToolTip = name;
                AutomationProperties.SetName(item, Loc.T("添加 ", "Add ") + name);
                item.Click += (_, _) => Add(key, name);
                items.Children.Add(item);
            }
            // The common chats, each with what it is known by: when it last sent a notice, or for WeChat its taskbar flash.
            var shownChats = chats.Where(chat => Match(chat.Name)).ToList();
            if (shownChats.Count > 0)
            {
                Header(Loc.T("常用聊天应用", "Common chats"));
                foreach (var (key, name) in shownChats)
                {
                    var known = center.Hub.KnownApps.FirstOrDefault(app => app.Key == key);
                    string note = key.StartsWith("flash:", StringComparison.Ordinal) ? Loc.T("任务栏闪烁", "Taskbar flash") : known is null ? "" : NoticeText.Relative(known.LastSeen, now);
                    Entry(key, name, known?.IconRef, note);
                }
            }
            var shownRecent = recent.Where(app => Match(app.Name)).ToList();
            if (shownRecent.Count > 0) { Header(Loc.T("最近发过通知", "Sent notices lately")); foreach (var app in shownRecent) Entry(app.Key, app.Name, app.IconRef, app.When); }
            var shownOthers = others.Concat(_registeredApps.Where(app => S.NoticeRule(app.Key) is null && !Chat(app.Key) && recent.All(r => r.Key != app.Key)))
                .Where(app => Match(app.Item2)).ToList();
            if (shownOthers.Count > 0) { Header(Loc.T("其他应用", "Other apps")); foreach (var (key, name) in shownOthers) Entry(key, name, null, ""); }
            if (items.Children.Count == 0)
            {
                var none = TextLine(filter.Length > 0 ? Loc.T($"没有找到“{filter}”。应用发过一次通知后会出现在这里。", $"Nothing called \"{filter}\". An app appears here once it has sent a notice.")
                    : Loc.T("没有可添加的应用。应用发过一次通知后会出现在这里。", "No apps to add. An app appears here once it has sent a notice."), 12, TextTertiary);
                none.TextWrapping = TextWrapping.Wrap; none.Margin = new Thickness(10, 8, 10, 10);
                items.Children.Add(none);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        popup.Opened += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { search.Focus(); Keyboard.Focus(search); });
        popup.IsOpen = true;
        // The applications Windows keeps notification settings for, by name, read once per window in the background.
        if (!_registeredRead)
        {
            _registeredRead = true;
            _ = Task.Run(RegisteredApps).ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                if (task.IsCompletedSuccessfully) _registeredApps = task.Result;
                if (popup.IsOpen) Fill();
            }), TaskScheduler.Default);
        }
    }

    private bool _registeredRead;
    private List<(string Key, string Name)> _registeredApps = [];

    private static List<(string Key, string Name)> RegisteredApps()
    {
        var found = new List<(string Key, string Name)>();
        foreach (var appId in NoticeBannerRegistry.Configured())
        {
            if (!NoticeBanners.Usable(appId) || appId == WindowsNoticeSource.OwnAppId) continue;
            string? name = null;
            try { name = AppIdentity.RegisteredName(appId); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException) { }
            name ??= WindowsNoticeApps.KnownName(appId);
            if (name is { Length: > 0 }) found.Add((NoticeKeys.Windows(appId), name.Trim()));
        }
        return [.. found.DistinctBy(app => app.Key).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // The sources that send through the command line or a program (brim-notify, a hook in an AI tool), and WSL applications'
    // desktop notifications: listed once their first message arrives, under the name the sender gives, each with its level.
    private void ProgramSourcesSection(NoticeCenter center)
    {
        // brim.exe, the command line; a copy without it (an older folder) has brim-notify's program in its place.
        var helper = BrimCommand.Installed ? BrimCommand.File : WslNoticeBridge.HelperPath;
        var copy = ActionButton(Loc.T("复制命令行路径", "Copy command line path"), () => { try { Clipboard.SetText(helper); } catch (System.Runtime.InteropServices.ExternalException) { } });
        copy.ToolTip = helper;
        SectionTitle(Loc.T("命令行与程序", "Command line and programs"), copy, () => Loc.T("通过命令行或程序发来的提示，按发送方给出的名称列在这里，第一次发送后自动出现。“按发送方”使用消息里指定的等级。用法见使用说明。",
            "Notices sent through the command line or by a program are listed here under the name their sender gives, once the first one arrives. As sent uses the level the message asks for. See the documentation for how to send them."));
        var sources = center.Hub.KnownApps.Where(app => ProgramSource(app.Key)).Select(app => (app.Key, app.Name))
            .Concat(S.NoticeApps.Where(rule => ProgramSource(rule.Key) && center.Hub.KnownApps.All(app => app.Key != rule.Key)).Select(rule => (rule.Key, Name: rule.Name.Length > 0 ? rule.Name : rule.Key)))
            .Take(60).ToList();
        var rows = new List<FrameworkElement>();
        var pipe = StatusLine(() => center.PipeError is { } error ? (Loc.T("本地通道不可用：", "The local channel is unavailable: ") + error, true) : ("", false));
        var pipeRow = new Border { Child = pipe, Padding = new Thickness(0, 10, 0, 10) };
        void ShowPipe() => pipeRow.Visibility = pipe.Visibility;
        ShowPipe(); _noticeStatusLines.Add(ShowPipe);
        rows.Add(pipeRow);
        // brim is found in a terminal only while BrimDeck's folder is in PATH, where the installer puts it. Where that did not
        // work, or for a portable copy, this row says so and adds it for the current user. The registry is read when the page
        // is built and after the button, not with every refresh of the status lines.
        if (BrimCommand.Installed) rows.Add(PathRow());
        if (sources.Count == 0)
        {
            var none = TextLine(Loc.T("还没有程序发来提示。发送后，来源会按它给出的名称列在这里。", "No program has sent a notice yet. Once one does, it is listed here under the name it gives."), 12, TextSecondary);
            none.TextWrapping = TextWrapping.Wrap;
            rows.Add(new Border { Child = none, Padding = new Thickness(0, 13, 0, 13) });
        }
        else
        {
            rows.Add(AppColumns(windows: false));
            foreach (var (key, name) in sources) rows.Add(NoticeAppRow(center, key, SourceName(key, name), windows: false));
        }
        GroupShown([.. rows]);
    }

    // A WSL source is named with its distribution, as the same name may send from Windows and from each distribution.
    private static string SourceName(string key, string name)
    {
        string rest = key.StartsWith("cli:", StringComparison.Ordinal) ? key[4..] : key.StartsWith("wsl:", StringComparison.Ordinal) ? key[4..] : "";
        int colon = rest.IndexOf(':');
        return colon > 0 && rest[(colon + 1)..] == name ? name + " · " + rest[..colon] : name;
    }

    // WSL: one switch per distribution; none on by default.
    private void WslSection(NoticeCenter center)
    {
        SectionTitle("WSL", help: () => Loc.T("开启后，该发行版中的程序可以用命令行工具和 notify-send 在岛上提示。BrimDeck 不会为此启动发行版；发行版中你的程序全部结束 5 分钟后，接收程序自行退出，不妨碍 WSL 停止发行版。",
            "Once on, programs in the distribution can show notices with the command line tool and notify-send. BrimDeck never starts a distribution for this; 5 minutes after the last of your programs there ended, the receiving agent ends by itself, so WSL can stop the distribution."));
        var rows = new List<FrameworkElement>();
        foreach (var name in WslLocations.Distributions())
        {
            bool on = S.NoticeWslDistros.Contains(name, StringComparer.OrdinalIgnoreCase);
            var toggle = NoticeToggle(name, on, value => Change(s =>
            {
                if (!value) s.NoticeWslDistros.RemoveAll(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
                else if (!s.NoticeWslDistros.Contains(name, StringComparer.OrdinalIgnoreCase)) s.NoticeWslDistros.Add(name);
            }));
            rows.Add(Row(name, toggle, StatusLine(() => on ? WslStatus(center, name) : ("", false))));
        }
        if (rows.Count == 0)
        {
            var none = TextLine(Loc.T("未检测到 WSL 发行版", "No WSL distribution was found"), 12, TextSecondary);
            rows.Add(new Border { Child = none, MinHeight = 44, Padding = new Thickness(0, 13, 0, 13) });
        }
        Group([.. rows]);
    }

    private static (string, bool) WslStatus(NoticeCenter center, string name)
    {
        if (!center.WslStates.TryGetValue(name, out var state)) return ("", false);
        // The agent ended by itself (W2); said apart from the states below, which are the agent's own.
        if (state.State == WslAgentState.Idle)
            return (Loc.T("发行版中已没有你的程序在运行，接收程序已退出；发行版下次运行时自动接入", "Nothing of yours runs there any more, so the agent ended; it connects again the next time the distribution runs"), false);
        var (text, warning) = state.State switch
        {
            WslAgentState.NotRunning => (Loc.T("发行版未运行，运行后自动接入", "The distribution is not running; it connects once it runs"), false),
            WslAgentState.NotPackaged => (state.Detail.Length > 0
                ? Loc.T($"此版本没有附带适用于该发行版（{state.Detail}）的接收程序", $"This build has no agent for this distribution ({state.Detail})")
                : Loc.T("此版本没有附带 WSL 接收程序", "This build has no WSL agent"), true),
            WslAgentState.Unsupported => (Loc.T($"该发行版的处理器架构或 C 库（{state.Detail}）没有可用的接收程序", $"No agent runs on this distribution's processor or C library ({state.Detail})"), true),
            WslAgentState.Installing => (Loc.T("正在安装接收程序…", "Installing the agent…"), false),
            WslAgentState.Starting => (Loc.T("正在启动…", "Starting…"), false),
            WslAgentState.Server or WslAgentState.Monitor => (Loc.T("正在接收", "Receiving"), false),
            WslAgentState.MonitorDenied => (Loc.T("已有通知服务，且总线不允许监视；命令行工具仍可用", "Another notification service runs and the bus does not allow watching it; the command line tool still works"), true),
            WslAgentState.NoBus => (Loc.T("没有可用的会话总线；命令行工具仍可用", "No session bus is available; the command line tool still works"), true),
            WslAgentState.Failed => (Loc.T("连接失败，稍后重试", "Connection failed; trying again later") + (state.Detail.Length > 0 ? Loc.T("（", " (") + state.Detail + Loc.T("）", ")") : ""), true),
            _ => ("", false)
        };
        if (!state.Conflict) return (text, warning);
        return (text + Loc.T("。~/.local/bin 中已有你自己的同名文件，未改动它", ". A file of your own of the same name in ~/.local/bin was left as it is"), true);
    }

    private void NoticeSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.NoticesWidth, 440, 1200, value => Resize(s => s.NoticesWidth = value, DeckPage.Notices)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.NoticesHeight, 140, 400, value => Resize(s => s.NoticesHeight = value, DeckPage.Notices)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }
}
