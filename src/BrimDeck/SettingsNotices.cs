using System.IO;
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

// The two "灵动提示" pages. How notices show (NoticesSettingsPage): how levels 2 and 3 look (level 1 is BrimDeck's own and
// fixed; the levels themselves are fixed, NoticeLevels.Resolve), the collapsed count of unread notices, the quiet times and the
// history; whether notices show at all is each profile's switch under 收起外观 (NoticeIslandSection). Apps
// (NoticeAppsSettingsPage, SettingsNotices.Apps.cs): the common apps, always listed, and the other Windows applications set
// apart, each one's island, receive and banner switches, the command-line and WSL sources, and the WSL distributions. An application may have its own looks for the levels, under its row, closed until asked for. Source states
// change while a page is open (access granted, an agent started); their lines follow NoticeCenter.Changed without rebuilding
// the page.
public sealed partial class SettingsWindow
{
    internal const int NoticesSettingsPage = 19, NoticeAppsSettingsPage = 20;
    private readonly List<Action> _noticeStatusLines = [];
    // The applications whose own looks are open under their row.
    private readonly HashSet<string> _noticeAppsOpen = new(StringComparer.Ordinal);

    private static string LevelTitle(NoticeLevel level) => level == NoticeLevel.Persistent ? Loc.T("等级 3 · 常驻", "Level 3 · Stay") : Loc.T("等级 2 · 闪动", "Level 2 · Flash");

    // The levels whose looks the settings offer; level 1 is BrimDeck's own and looks the same always.
    private static readonly NoticeLevel[] Levels = [NoticeLevel.Glow, NoticeLevel.Persistent];

    // ----- How notices show -----

    private void NoticesPage()
    {
        var center = _app.Notices;
        _noticeStatusLines.Clear();
        NoticeIslandOffRow();
        SectionTitle(Loc.T("通用", "General"));
        Group(Toggle(Loc.T("遮住验证码", "Hide verification codes"), S.NoticeCodes, value => Change(s => s.NoticeCodes = value),
                help: Loc.T("识别通知里的验证码，用圆点遮住，并在通知页提供“复制验证码”按钮。复制 60 秒后自动清除剪贴板。",
                    "Recognizes a verification code in a notice, hides it with dots and offers a \"Copy code\" button on the notices page. The clipboard is cleared 60 seconds after copying.")));
        foreach (var level in Levels)
        {
            var test = ActionButton(Loc.T("试一下", "Try it"), () => center.SendTest(level));
            AutomationProperties.SetName(test, Loc.T("试一下", "Try it") + " " + LevelTitle(level));
            SectionTitle(LevelTitle(level), test, () => LevelHelp(level));
            var style = S.NoticeStyles.For(level);
            var preview = LevelPreview(level, style);
            Group([preview, .. LevelRows(level, style, change =>
            {
                Change(s => change(s.NoticeStyles.For(level)));
                Redraw(preview, LevelPreview(level, S.NoticeStyles.For(level)));
            }, level.ToString(), own: false)]);
        }
        NoticeSoundSection(center);
        NoticeStripSection();
        NoticeQuietSection(center);
        NoticeHistorySection(center);
        WatchNotices(center);
    }

    private static string LevelHelp(NoticeLevel level) => level == NoticeLevel.Persistent
        ? Loc.T("来电、闹钟和需要审批的提示使用这一等级：一直留在岛上并定时闪动，直到处理、展开面板或超过最长停留时间。",
            "Calls, alarms and approvals use this level: the notice stays on the island and flashes now and then until it is handled, the panel is opened or the longest stay is over.")
        : Loc.T("应用的通知使用这一等级：岛上显示提示文字，边缘闪一下，停留时间到后恢复原样。辉光：边缘亮一下；灵动：辉光加液体波纹。",
            "Apps' notices use this level: the island shows the notice's words and its edge flashes, until the time is up. Glow: the edge lights up once. Lively: the glow and a liquid ripple.");

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
        SectionTitle(Loc.T("灵动通知", "Live notices"), ActionButton(Loc.T("收起外观", "Collapsed view"), () => ShowPage(1)),
            () => Loc.T("收起时显示未读通知和 AI 编程工具的会话，在“收起外观”的显示内容中打开和排序。打开通知页后未读清零。",
                "Shows the unread notices and the AI coding tools' sessions on the island; turn it on and place it in Collapsed view's content. Opening the notices page clears the unread ones."));
        Group(Toggle(Loc.T("显示应用图标", "Show app icons"), S.NoticeStripIcons, value => Change(s => s.NoticeStripIcons = value),
                help: Loc.T("在总数前叠放有未读通知的应用图标，最多 3 个。关闭后显示铃铛。", "Stacks the icons of up to 3 apps with unread notices before the total. Off, a bell shows.")),
            Toggle(Loc.T("显示 AI 编程工具", "Show AI coding tools"), S.NoticeStripAgents, value => Change(s => s.NoticeStripAgents = value),
                help: Loc.T("显示正在运行、等待审批或回答、刚刚完成的会话。需要先在“AI 编程工具”设置页中为工具安装钩子。",
                    "Shows the sessions that are working, waiting for approval or an answer, or just finished. Install the tools' hooks on the AI coding tools settings page first.")),
            Toggle(Loc.T("合并为一组", "Stack as one group"), S.NoticeAgentsMerged, value => Change(s => s.NoticeAgentsMerged = value),
                help: Loc.T("会话图标和应用图标叠在同一组里，最需要处理的在最前，只写一处文字。关闭时分为两组，AI 编程工具在前。",
                    "Stacks the sessions' and the apps' icons in one group, the most urgent in front, with one word or number. Off, two groups, the AI coding tools first.")));
    }

    // A look changed: its preview (shown) takes the picture of the new look (fresh) in place. The page is not rebuilt, so the
    // control just used keeps its place and focus.
    private static void Redraw(Border shown, Border fresh)
    {
        var island = fresh.Child;
        fresh.Child = null;
        shown.Child = island;
    }

    // The rows of one look (style, shown as a notice of level shows): the effect, the backlight's color, the icon, how long level 2
    // stays and the sound. The "提示方式" page offers the color for level 3 only, as the one of a message that names none, and level
    // 3's longest stay, which is the same for every application; an application's own looks (own) offer the color for level 2 as
    // well. edit saves a change to the look and redraws its preview;
    // the rows stay as they are. name tells the rows of several groups on a page apart for screen readers and the segment groups.
    private List<FrameworkElement> LevelRows(NoticeLevel level, NoticeStyle style, Action<Action<NoticeStyle>> edit, string name, bool own)
    {
        var rows = new List<FrameworkElement>
        {
            NoticeChoice(Loc.T("效果", "Effect"), "NoticeEffect" + name, style.Liquid, [(false, Loc.T("辉光", "Glow")), (true, Loc.T("灵动", "Lively"))],
                value => edit(s => s.Liquid = value), 150)
        };
        if (level == NoticeLevel.Persistent || own) rows.Add(LightRow(style, edit, name, own));
        rows.Add(Toggle(Loc.T("显示应用图标", "Show app icon"), style.Icon, value => edit(s => s.Icon = value)));
        if (level != NoticeLevel.Persistent)
            rows.Add(StepsRow(Loc.T("停留时间", "Shown for"), style.Seconds, NoticeStyle.SecondsSteps, Loc.T(" 秒", " s"), value => edit(s => s.Seconds = value)));
        else if (!own)
            rows.Add(SliderRow(Loc.T("最长停留", "Stays at most"), S.NoticeStaySeconds, DeckSettings.MinNoticeStay, DeckSettings.MaxNoticeStay, 10, Loc.T(" 秒", " s"),
                value => Change(s => s.NoticeStaySeconds = (int)Math.Round(value)),
                help: Loc.T("没有处理的提示到时后从岛上消失，仍保留在通知页。", "A notice not handled by then leaves the island and stays on the notices page.")));
        rows.Add(SoundRow(level, style, edit, name));
        return rows;
    }

    // The sound of level 2 (played once) or level 3 (a loop): none, a built-in sound of the level's kind, a file in the sounds
    // folder, or a file to import into it. Choosing one plays it once. The list is read again when it opens, so that a file imported
    // in another row or added to the folder meanwhile shows without rebuilding the page.
    private FrameworkElement SoundRow(NoticeLevel level, NoticeStyle style, Action<Action<NoticeStyle>> edit, string name)
    {
        var center = _app.Notices;
        var kind = NoticeSounds.KindOf(level) ?? NoticeSoundKind.Short;
        string chosen = NoticeSounds.Of(style.Sound, level);
        var choices = new List<(string Sound, string Label)>();
        var combo = new ComboBox { Width = 200, Style = (Style)FindResource("SettingsCombo") };
        AutomationProperties.SetName(combo, Loc.T("提示音", "Sound") + " " + name + " " + LevelTitle(level));
        bool filling = false;
        int Shown() => Math.Max(0, choices.FindIndex(choice => string.Equals(choice.Sound, chosen, StringComparison.OrdinalIgnoreCase)));
        void Show() { filling = true; combo.SelectedIndex = Shown(); filling = false; }
        void Fill()
        {
            var next = new List<(string Sound, string Label)> { (NoticeSounds.None, Loc.T("无", "None")) };
            next.AddRange(NoticeSounds.BuiltIn.Where(sound => sound.Kind == kind).Select(sound => (sound.Id, sound.Name)));
            var files = NoticeSounds.Files(center.SoundFolder);
            next.AddRange(files.Select(file => (NoticeSounds.ForFile(file), file)));
            // A chosen file that has left the folder stays listed, so that the row shows what is saved (the default plays meanwhile).
            if (NoticeSounds.FileName(chosen) is { } gone && !files.Contains(gone, StringComparer.OrdinalIgnoreCase)) next.Add((chosen, gone + Loc.T("（文件不存在）", " (missing)")));
            if (next.SequenceEqual(choices)) return;
            choices.Clear(); choices.AddRange(next);
            var labels = choices.Select(choice => choice.Label).ToList();
            labels.Add(Loc.T("导入文件…", "Import a file…"));
            filling = true; combo.ItemsSource = labels; filling = false;
            Show();
        }
        Fill();
        combo.DropDownOpened += (_, _) => Fill();
        combo.SelectionChanged += async (_, _) =>
        {
            int picked = combo.SelectedIndex;
            if (filling || picked < 0) return;
            if (picked < choices.Count)
            {
                string sound = choices[picked].Sound;
                if (string.Equals(sound, chosen, StringComparison.OrdinalIgnoreCase)) return;
                chosen = sound;
                center.Sounds.Once(sound);
                edit(s => s.Sound = sound);
                return;
            }
            // "Import a file…" is not a choice to keep: the row shows the saved sound again while the file is picked.
            Show();
            if (await ImportSoundAsync(center) is not { } imported) return;
            chosen = imported;
            center.Sounds.Once(imported);
            edit(s => s.Sound = imported);
            Fill();
            Show();
        };
        return Row(Loc.T("提示音", "Sound"), combo);
    }

    // Lets the user pick an audio file and copies it into the sounds folder; the sound that names the copy, or null when nothing
    // was imported (the status line says why). A file that is in the folder already, or was imported before, is not copied again.
    private async Task<string?> ImportSoundAsync(NoticeCenter center)
    {
        string kinds = string.Join(';', NoticeSounds.FileExtensions.Select(extension => "*" + extension));
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("导入提示音", "Import a sound"), Filter = Loc.T("音频文件", "Audio files") + $" ({kinds})|{kinds}", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return null;
        string source = dialog.FileName, folder = center.SoundFolder;
        try
        {
            long length = new FileInfo(source).Length;
            if (length > NoticeSounds.MaxFileBytes) { SaveStatus(Loc.T("文件超过 50 MB，无法导入。", "The file is larger than 50 MB and cannot be imported."), true); return null; }
            if (!NoticeSounds.FileExtensions.Contains(Path.GetExtension(source).ToLowerInvariant()) || !await NoticeSoundPlayer.PlayableAsync(source))
            {
                SaveStatus(Loc.T("无法播放这个文件。支持 WAV、MP3、M4A、AAC、FLAC、WMA。", "This file cannot be played. WAV, MP3, M4A, AAC, FLAC and WMA are supported."), true);
                return null;
            }
            SaveStatus("");
            string name = NoticeSounds.ImportName(source, []), target = Path.Combine(folder, name);
            bool inFolder = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            if (inFolder || File.Exists(target) && new FileInfo(target).Length == length) return NoticeSounds.ForFile(name);
            Directory.CreateDirectory(folder);
            name = NoticeSounds.ImportName(source, NoticeSounds.Files(folder));
            target = Path.Combine(folder, name);
            await Task.Run(() => File.Copy(source, target));
            return NoticeSounds.ForFile(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus(Loc.T("无法导入这个文件。", "The file could not be imported."), true);
            return null;
        }
    }

    // The sounds' volume, whether Windows applications' notices play them too, and the folder of imported files. Which sound a
    // level plays is chosen with the level's other looks.
    private System.Windows.Threading.DispatcherTimer? _soundTry;
    private void NoticeSoundSection(NoticeCenter center)
    {
        SectionTitle(Loc.T("提示音", "Sounds"), ActionButton(Loc.T("打开文件夹", "Open folder"), () => OpenFolder(center.SoundFolder)),
            () => Loc.T("等级 2 的提示音响一次，等级 3 的提示音循环播放到提示消失，在上面各等级中选择。导入的文件保存在这个文件夹中，删除文件即可移除。",
                "Level 2's sound plays once; level 3's loops until the notice goes. Choose them under the levels above. Imported files are kept in this folder; delete a file there to remove it."));
        // The volume is heard once the slider rests: the sound of level 2, or the default while that level has none.
        void Try()
        {
            if (_soundTry is null)
            {
                _soundTry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _soundTry.Tick += (_, _) =>
                {
                    _soundTry.Stop();
                    center.Sounds.Once(NoticeSounds.Of(S.NoticeStyles.Flash.Sound, NoticeLevel.Glow) is { Length: > 0 } sound ? sound : NoticeSounds.DefaultShort);
                };
            }
            _soundTry.Stop(); _soundTry.Start();
        }
        Group(SliderRow(Loc.T("音量", "Volume"), S.NoticeSoundVolume, 0, 100, 5, "%", value => { Change(s => s.NoticeSoundVolume = (int)Math.Round(value)); Try(); }));
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
    private Border LevelPreview(NoticeLevel level, NoticeStyle style, (string App, string Title, string Body)? sample = null)
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
        int[] hours = [1, 24, 72, 168, 720];
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

    // ----- Apps (the page itself: SettingsNotices.Apps.cs) -----

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
            NoticeSourceState.NeedsAccess => (Loc.T("允许后，BrimDeck 才能收到各个应用的通知。", "Once allowed, BrimDeck can receive apps' notifications."), false),
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

    // Windows' own banner switch of an application, as Windows has it now (NoticeBanners.Shown), read again when the settings window
    // comes forward. Switching it writes Windows' setting at once; one that does not stay is put back and said under the name. Off
    // and unchangeable while the application's notifications are off in Windows; none for an id Windows keeps no setting under.
    private CheckBox? BannerSwitch(NoticeCenter center, string appId, string name)
    {
        if (!NoticeBanners.Usable(appId) || appId == WindowsNoticeSource.OwnAppId) return null;
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
            if (shown is null) _bannerNotes[appId] = NotificationsOffNote;
            else if (_bannerNotes.TryGetValue(appId, out var note) && note == NotificationsOffNote) _bannerNotes.Remove(appId);
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
    // The note of an application whose notifications are off in Windows altogether (no banner to switch, nothing reaches BrimDeck).
    private static string NotificationsOffNote => Loc.T("Windows 中已关闭这个应用的通知", "This app's notifications are off in Windows");

    // Whether an application's notices show on the island as they arrive ("岛上提示"); off, they only go to the notices page
    // (NoticeAppRule.Level Silent). The level they show at is fixed (NoticeLevels.Resolve).
    private CheckBox IslandSwitch(string key, string name, NoticeAppRule? rule)
    {
        var toggle = NoticeToggle(name + " · " + Loc.T("岛上提示", "On the island"), rule?.Level != NoticeLevel.Silent, value =>
        {
            EditRule(key, name, r => r.Level = value ? NoticeLevel.Auto : NoticeLevel.Silent);
            foreach (var line in _noticeStatusLines.ToList()) line();
            // The own looks open under the row say whether the notices show.
            if (_noticeAppsOpen.Contains(key)) ShowPage(_page);
        });
        toggle.HorizontalAlignment = HorizontalAlignment.Center;
        return toggle;
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

    // An application's own looks: off follows the "提示方式" page; on, a copy of it to change. Shown is the look of level 2, at which
    // its notices show (and level 3 for a program, whose messages may ask for it), and the look of the notices that stay until
    // handled: a chat's calls (their own look, NoticeLevelStyles.Call), and a clock's, a calendar's or a
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
            // One look: its preview and rows. shown reads the look from the application's looks; pick does too, making the calls'
            // own look on its first change. A change redraws the preview only.
            void Look(NoticeLevel level, (string, string, string) sample, Func<NoticeLevelStyles, NoticeStyle> shown, Func<NoticeLevelStyles, NoticeStyle> pick, string group)
            {
                var preview = LevelPreview(level, shown(styles), sample);
                Group([preview, .. LevelRows(level, shown(styles), change =>
                {
                    EditRule(key, name, r => { r.Styles ??= S.NoticeStyles.Copy(); change(pick(r.Styles)); });
                    if (S.NoticeRule(key)?.Styles is { } now) Redraw(preview, LevelPreview(level, shown(now), sample));
                }, group, own: true)]);
            }
            var sample = (name, name, Loc.T("一条新通知", "A new notice"));
            NoticeLevel[] levels = ProgramSource(key) ? Levels : [NoticeLevel.Glow];
            foreach (var level in levels)
            {
                SectionTitle(levels.Length > 1 ? LevelTitle(level) : Loc.T("通知 · ", "Notices · ") + LevelTitle(level));
                Look(level, sample, s => s.For(level), s => s.For(level), key + level);
            }
            if (TakesCalls(key, name))
            {
                SectionTitle(Loc.T("来电", "Calls"), help: () => Loc.T("语音和视频来电一直留在岛上并定时闪动，直到接听、挂断或展开面板。", "Voice and video calls stay on the island and flash now and then until answered, declined or the panel is opened."));
                Look(NoticeLevel.Persistent, (name, Loc.T("小周", "Zhou"), Loc.T("邀请你语音通话", "Incoming voice call")), s => s.ForCall(), s => s.Call ??= s.Stay.Copy(), key + "Call");
            }
            if (!levels.Contains(NoticeLevel.Persistent) && StayKind(key) is { } stay)
            {
                SectionTitle(stay.Title, help: () => stay.Help);
                Look(NoticeLevel.Persistent, (name, name, stay.Sample), s => s.Stay, s => s.Stay, key + "Stay");
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

    // The icon a row shows: the application's own, read from its notices, else the one AppBrandIcons gives (a watched chat's
    // program, a common chat's built-in icon, a Windows application's Start menu entry), else its first letter on a plate. An icon still being read takes the plate's place when it arrives.
    private FrameworkElement NoticeAppIcon(string name, string? iconRef, double size, NoticeCenter center)
    {
        Image Picture(System.Windows.Media.Imaging.BitmapSource image)
        {
            var picture = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        if (center.Icons.Get(iconRef, out _) is { } image) return Picture(image);
        var plate = NoticeLetter.Plate(name, size, (Color)ColorConverter.ConvertFromString(_palette.Accent), iconRef: iconRef);
        if (!center.Icons.IsLoading(iconRef)) return plate;
        var holder = new Border { Child = plate };
        void Arrived(string loaded)
        {
            if (loaded != iconRef) return;
            center.Icons.Loaded -= Arrived;
            if (center.Icons.Get(iconRef, out _) is { } icon) holder.Child = Picture(icon);
        }
        center.Icons.Loaded += Arrived;
        return holder;
    }

    // The sources that send through the command line or a program (brim-notify, a hook in an AI tool), and WSL applications'
    // desktop notifications: listed once their first message arrives, under the name the sender gives, each with its level.
    private void ProgramSourcesSection(NoticeCenter center)
    {
        // brim.exe, the command line; a copy without it (an older folder) has brim-notify's program in its place.
        var helper = BrimCommand.Installed ? BrimCommand.File : WslNoticeBridge.HelperPath;
        var copy = ActionButton(Loc.T("复制命令行路径", "Copy command line path"), () => { try { Clipboard.SetText(helper); } catch (System.Runtime.InteropServices.ExternalException) { } });
        copy.ToolTip = helper;
        SectionTitle(Loc.T("命令行与程序", "Command line and programs"), copy, () => Loc.T("通过命令行或程序发来的提示，按发送方给出的名称列在这里，第一次发送后自动出现，按消息里指定的等级显示。用法见使用说明。",
            "Notices sent through the command line or by a program are listed here under the name their sender gives, once the first one arrives. They show at the level the message asks for. See the documentation for how to send them."));
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
