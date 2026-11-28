using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using BrimDeck.Core;
using BrimDeck.Core.Agenda;

namespace BrimDeck;

// "日程与待办" (AgendaSettingsPage): the planner page's settings. What the collapsed planner part shows (whether it shows at all is
// each style's switch under 收起外观 › 显示内容), the month's first day, a new event's length and reminders, the snooze, where
// unfinished and ticked to-dos go, events found in notices, and agenda.json: where it is, export and import. Whether the planner
// page shows and its size are under 展开外观.
public sealed partial class SettingsWindow
{
    internal const int AgendaSettingsPage = 23;
    // What the last export or import did, shown under the row's title until another page is opened. Problem: it failed.
    private (string Text, bool Problem)? _agendaResult;

    private void AgendaSettings()
    {
        var intro = TextLine(Loc.T("计划页中的月历、日程、待办和每天的便签，以及收起时显示其中的哪一种。计划页的开关和面板大小在“展开外观”中设置。",
            "The planner page's month, events, to-dos and daily notes, and which of them shows on the collapsed island. Whether the planner page shows and its size are set under Expanded view."), 12.5, TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 24);
        _content.Children.Add(intro);

        AgendaStripSection();

        SectionTitle(Loc.T("月历", "Month"));
        Group(MusicChoice(Loc.T("每周第一天", "Week starts on"), S.AgendaWeekStart,
            [(AgendaWeekStart.Monday, Loc.T("周一", "Monday")), (AgendaWeekStart.Sunday, Loc.T("周日", "Sunday"))],
            value => Change(s => s.AgendaWeekStart = value), width: 216));

        SectionTitle(Loc.T("日程", "Events"));
        var lengths = AgendaCalendar.EventLengths;
        Group(Row(Loc.T("新日程的默认时长", "A new event lasts"), ProfileCombo(Loc.T("新日程的默认时长", "A new event lasts"), [.. lengths.Select(LengthText)],
            Math.Max(0, lengths.ToList().IndexOf(S.AgendaEventMinutes)), index => Change(s => s.AgendaEventMinutes = lengths[index]), 150)));

        SectionTitle(Loc.T("提醒", "Reminders"));
        Group(RemindRow(Loc.T("新日程的默认提醒", "A new event reminds"), false, S.AgendaRemindTimed, value => Change(s => s.AgendaRemindTimed = value)),
            RemindRow(Loc.T("全天日程的默认提醒", "A new all-day event reminds"), true, S.AgendaRemindAllDay, value => Change(s => s.AgendaRemindAllDay = value)),
            Row(Loc.T("稍后提醒", "Remind again after"), ProfileCombo(Loc.T("稍后提醒", "Remind again after"),
                    [.. AgendaReminders.SnoozeChoices.Select(minutes => Loc.T($"{minutes} 分钟后", Loc.Count(minutes, "minute", "minutes")))],
                    Math.Max(0, AgendaReminders.SnoozeChoices.ToList().IndexOf(S.AgendaSnoozeMinutes)), index => Change(s => s.AgendaSnoozeMinutes = AgendaReminders.SnoozeChoices[index]), 150),
                help: () => Loc.T("岛上“稍后提醒”按钮使用的时长。", "How long the island's \"Remind later\" button puts a reminder off.")));

        SectionTitle(Loc.T("待办", "To-dos"));
        Group(MusicChoice(Loc.T("以前未完成的待办", "Unfinished to-dos of earlier days"), S.AgendaLeftTodos,
                [(AgendaLeftTodos.Ask, Loc.T("提示后移动", "Ask, then move")), (AgendaLeftTodos.Move, Loc.T("自动移到今天", "Move to today"))],
                value => Change(s => s.AgendaLeftTodos = value), width: 260,
                help: Loc.T("提示：今天的待办顶部出现“移到今天”。", "Ask: \"Move to today\" shows at the top of today's to-dos.")),
            MusicChoice(Loc.T("已完成的待办", "Ticked to-dos"), S.AgendaDoneTodos,
                [(AgendaDoneTodos.Stay, Loc.T("留在原处", "Stay in place")), (AgendaDoneTodos.Last, Loc.T("移到末尾", "Move to the end")), (AgendaDoneTodos.Hidden, Loc.T("不显示", "Hide"))],
                value => Change(s => s.AgendaDoneTodos = value), width: 324));

        SectionTitle(Loc.T("通知", "Notices"));
        Group(Toggle(Loc.T("从通知中识别日程时间", "Find event times in notices"), S.AgendaFromNotices, value => Change(s => s.AgendaFromNotices = value),
            help: Loc.T("认出时间的通知上会出现“加入日程”按钮，点击后才加入，不会自动加入。关闭后不再识别，仍可以手动填写后加入。",
                "A notice whose time is recognized offers an \"Add to events\" button; nothing is added until it is clicked. Off, no times are looked for, and an event can still be filled in by hand and added.")));

        AgendaDataSection();
    }

    // The collapsed planner part: one kind at a time or all, and which kinds, each in its form. A kind's form is turned off while
    // the kind is.
    private void AgendaStripSection()
    {
        SectionTitle(Loc.T("收起时显示", "On the island"), ActionButton(Loc.T("收起外观", "Collapsed view"), () => ShowPage(1)),
            () => Loc.T("在“收起外观”的显示内容中打开“计划”并排序。", "Turn on and place \"Planner\" in Collapsed view's content."));
        _content.Children.Add(AgendaPreview());
        Group(MusicChoice(Loc.T("显示方式", "Shows"), S.AgendaStripMode,
                [(AgendaStripMode.Auto, Loc.T("自动", "Auto")), (AgendaStripMode.All, Loc.T("全部", "All"))],
                value => { Change(s => s.AgendaStripMode = value); FillAgendaPreview(); }, width: 216,
                help: Loc.T("自动：一次只显示一种，依次是进行中或一小时内开始的日程、今日便签、未完成的待办、今天稍后的日程，没有内容的跳过。全部：打开的几种同时显示。",
                    "Auto: one kind at a time, the first that has something of: an event under way or starting within the hour, today's note, unfinished to-dos, a later event today. All: every kind switched on, side by side.")),
            KindRow(Loc.T("日程", "Events"), S.AgendaStripEvents, value => { Change(s => s.AgendaStripEvents = value); FillAgendaPreview(); }, S.AgendaStripEventForm,
                [(AgendaStripEventForm.Title, Loc.T("时间和标题", "Time and title")), (AgendaStripEventForm.Time, Loc.T("只显示时间", "Time only"))],
                value => { Change(s => s.AgendaStripEventForm = value); FillAgendaPreview(); },
                Loc.T("今天的下一个日程，进行中时写还剩多久。今天没有更多日程时不显示。", "Today's next event; while it is under way, how long is left. Not shown when today has no more events.")),
            KindRow(Loc.T("待办", "To-dos"), S.AgendaStripTodos, value => { Change(s => s.AgendaStripTodos = value); FillAgendaPreview(); }, S.AgendaStripTodoForm,
                [(AgendaStripTodoForm.Count, Loc.T("未完成的数量", "Number unfinished")), (AgendaStripTodoForm.Text, Loc.T("第一条的文字", "First one's words"))],
                value => { Change(s => s.AgendaStripTodoForm = value); FillAgendaPreview(); },
                Loc.T("今天未完成的待办。全部完成或没有待办时不显示。", "Today's unfinished to-dos. Not shown when all are done or there are none.")),
            Toggle(Loc.T("便签", "Note"), S.AgendaStripNote, value => { Change(s => s.AgendaStripNote = value); FillAgendaPreview(); },
                help: Loc.T("今日便签开头的文字。今天没写便签时不显示。", "The start of today's note. Not shown when today has no note.")));
    }

    // The collapsed part as the choices below make it, drawn as the island draws it (AgendaStripView) with invented content: an
    // event twenty minutes away, three to-dos and a note. The choices do not all build the page again, so each fills it anew.
    private Border? _agendaPreview;
    private FrameworkElement AgendaPreview()
    {
        _agendaPreview = new Border { Height = 64, CornerRadius = new CornerRadius(8), Background = UI.Brush(_palette.Inset), Margin = new Thickness(0, 0, 0, 8), IsHitTestVisible = false };
        AutomationProperties.SetName(_agendaPreview, Loc.T("收起时的预览", "Preview of the island"));
        FillAgendaPreview();
        return _agendaPreview;
    }

    private void FillAgendaPreview()
    {
        if (_agendaPreview is not { } preview) return;
        var now = new DateTime(2026, 11, 22, 16, 10, 0);
        var parcel = new AgendaEvent { Id = "preview", Title = Loc.T("取快递", "Pick up the parcel"), Start = now.AddMinutes(20), End = now.AddMinutes(50) };
        var content = new AgendaStripContent(new AgendaStripEvent(parcel, AgendaStripState.Soon, 0), new AgendaStripTodos(3, 0, Loc.T("交房租", "Pay the rent"), 0), Loc.T("周报发给老陈", "Report to Chen"));
        var shown = AgendaStrip.Shown(content, S.AgendaStripMode, S.AgendaStripEvents, S.AgendaStripTodos, S.AgendaStripNote);
        if (shown.Count == 0)
        {
            var none = TextLine(Loc.T("收起时不显示计划的内容", "Nothing of the planner shows on the island"), 12, TextTertiary);
            none.HorizontalAlignment = HorizontalAlignment.Center; none.VerticalAlignment = VerticalAlignment.Center;
            preview.Child = none;
            return;
        }
        var look = PanelTheme.Main;
        var part = AgendaStripView.Row(content, shown, now, S.AgendaStripEventForm, S.AgendaStripTodoForm, 0, 0, 1, VisualTreeHelper.GetDpi(this).DpiScaleX, look).Element;
        part.VerticalAlignment = VerticalAlignment.Center;
        preview.Child = new Border
        {
            Background = UI.Brush(look.Hex("surface")), CornerRadius = new CornerRadius(16), Height = 32, Padding = new Thickness(14, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = part
        };
    }

    // One kind of the collapsed part: its title, the choice of its form, and its switch at the right. The form's segments turn off
    // with the switch, in place, so the switch keeps its animation; the row's info mark still shows its description.
    private FrameworkElement KindRow<T>(string title, bool on, Action<bool> show, T form, (T Value, string Label)[] forms, Action<T> choose, string help) where T : notnull
    {
        var strip = new UniformGrid { Columns = forms.Length, Width = 260 };
        foreach (var option in forms)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = "AgendaStrip" + title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, form) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => choose(option.Value); strip.Children.Add(choice);
        }
        var segments = SegmentBorder(strip); segments.IsEnabled = on; segments.Margin = new Thickness(0, 4, 16, 4); segments.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = on, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => { segments.IsEnabled = true; show(true); };
        toggle.Unchecked += (_, _) => { segments.IsEnabled = false; show(false); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(segments); controls.Children.Add(toggle);
        return Row(title, controls, help: () => help);
    }

    // "15 分钟", "1 小时", "1.5 小时".
    private static string LengthText(int minutes)
    {
        if (minutes < 60) return Loc.T($"{minutes} 分钟", Loc.Count(minutes, "minute", "minutes"));
        string hours = (minutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture);
        return Loc.T($"{hours} 小时", minutes == 60 ? "1 hour" : hours + " hours");
    }

    // A new event's reminder: none, or one of the kind's choices (AgendaReminders), in the words the event's form uses.
    private FrameworkElement RemindRow(string title, bool allDay, int? value, Action<int?> choose)
    {
        List<int?> choices = [null, .. AgendaReminders.Choices(allDay).Select(minutes => (int?)minutes)];
        return Row(title, ProfileCombo(title, [.. choices.Select(choice => AgendaReminders.Text(choice, allDay))], Math.Max(0, choices.IndexOf(value)),
            index => choose(choices[index]), 200));
    }

    // agenda.json: where it is, and copies of it out and in. Without the planner's data (the README renderer) the buttons are off.
    private void AgendaDataSection()
    {
        AgendaCenter? agenda = _app.Agenda;
        Button Action(string text, Action action) { var button = ActionButton(text, action); button.Style = (Style)FindResource("SettingsOutlineButton"); button.IsEnabled = agenda is not null; return button; }
        SectionTitle(Loc.T("数据", "Data"));
        var open = Action(Loc.T("打开文件夹", "Open folder"), () =>
        {
            if (agenda is null) return;
            string file = agenda.FilePath;
            OpenFolder(Path.GetDirectoryName(file)!, File.Exists(file) ? file : null);
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Action(Loc.T("导出…", "Export…"), ExportAgenda));
        var import = Action(Loc.T("导入…", "Import…"), ImportAgenda); import.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(import);
        TextBlock? result = null;
        if (_agendaResult is { } done)
        {
            result = TextLine(done.Text, 11.5, done.Problem ? _palette.Negative : TextSecondary);
            result.TextWrapping = TextWrapping.Wrap; result.Margin = new Thickness(0, 2, 0, 0);
        }
        Group(Row(Loc.T("数据文件", "Data file"), open,
                help: () => Loc.T("日程、待办和便签保存在这台电脑的 agenda.json 中，不包含在配置备份里。",
                    "Events, to-dos and notes are kept in agenda.json on this PC, and are not part of the settings backup.")),
            Row(Loc.T("导出与导入", "Export and import"), buttons, result,
                help: () => Loc.T("导入会与现有内容合并，不会删除已有的日程和待办。",
                    "Importing adds to what is here; no event or to-do already here is removed.")));
    }

    // Shows what an export or import did under its row.
    private void AgendaResult(string text, bool problem)
    {
        _agendaResult = (text, problem);
        ShowPage(AgendaSettingsPage);
    }

    private void ExportAgenda()
    {
        if (_app.Agenda is not { } agenda) return;
        var dialog = new SaveFileDialog { Title = Loc.T("导出日程与待办", "Export events and to-dos"), Filter = AgendaFilter, DefaultExt = "json", AddExtension = true,
            OverwritePrompt = true, FileName = "BrimDeck-agenda-" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, AgendaStore.Serialize(agenda.Book.Snapshot()), new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { AgendaResult(Loc.T("无法写入这个文件", "The file could not be written"), true); return; }
        AgendaResult(Loc.T("已导出", "Exported"), false);
    }

    // The file's events and to-dos join the planner's (AgendaBook.Import); AgendaCenter saves the change by itself.
    private void ImportAgenda()
    {
        if (_app.Agenda is not { } agenda) return;
        var dialog = new OpenFileDialog { Title = Loc.T("导入日程与待办", "Import events and to-dos"), Filter = AgendaFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        AgendaData data;
        // The same failures AgendaStore.Read catches: the file cannot be read, or its text is not the planner's layout.
        try { data = AgendaStore.Parse(File.ReadAllText(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or JsonException or InvalidOperationException or FormatException or ArgumentException)
        { AgendaResult(Loc.T("无法读取这个文件", "The file could not be read"), true); return; }
        var taken = agenda.Book.Import(data);
        AgendaResult(taken.Any ? ImportedText(taken) : Loc.T("没有新的内容", "Nothing new"), false);
    }

    private static string AgendaFilter => Loc.T("JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*");

    // "已导入 2 个日程、3 条待办、1 天的便签"; the kinds of which nothing was added are left out.
    private static string ImportedText(AgendaImport taken)
    {
        var parts = new List<string>();
        if (taken.Events > 0) parts.Add(Loc.T($"{taken.Events} 个日程", Loc.Count(taken.Events, "event", "events")));
        if (taken.Todos > 0) parts.Add(Loc.T($"{taken.Todos} 条待办", Loc.Count(taken.Todos, "to-do", "to-dos")));
        if (taken.Notes > 0) parts.Add(Loc.T($"{taken.Notes} 天的便签", "notes of " + Loc.Count(taken.Notes, "day", "days")));
        if (Loc.IsEnglish)
            return "Imported " + (parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]);
        return "已导入 " + string.Join("、", parts);
    }
}
