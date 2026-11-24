using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Native;

namespace BrimDeck;

// "AI 编程工具" (AgentsSettingsPage): finds the coding agents installed on this PC and adds BrimDeck's hooks to their settings,
// or takes them out again, one tool at a time or all at once. With the hooks, each session's state shows on the collapsed row,
// a finished turn comes as a notice, and approvals and questions can be answered on the island. A tool BrimDeck does not know
// can be connected by hand; the page shows the two commands and links the guide.
public sealed partial class SettingsWindow
{
    internal const int AgentsSettingsPage = 21;
    // Why the last install or removal of a tool failed, by the tool's id, until the page is built for another reason.
    private readonly Dictionary<string, string> _agentProblems = new(StringComparer.Ordinal);
    // Set once the user looked for the tools while this window is open: the page then says what was found.
    private bool _agentsLooked;

    // brim.exe beside BrimDeck, as the hooks run it; the hooks need it, so without it nothing can be installed.
    private static string BrimFile => BrimCommand.File;

    private void AgentsSettings()
    {
        var intro = TextLine(Loc.T("为本机的 AI 编程工具安装钩子后，会话状态显示在收起的岛上；完成、需要审批和提问时收到通知，并可以直接在岛上允许、拒绝或回答。",
            "With hooks installed in the AI coding tools on this PC, their sessions' state shows on the collapsed island; you get a notice when one finishes, needs approval or asks a question, and can allow, deny or answer on the island."), 12.5, TextSecondary);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 24);
        intro.Inlines.Add(new Run(" "));
        intro.Inlines.Add(InlineLink(Loc.T("使用说明 ↗", "How it works ↗"), Updates.GitHubUpdates.RepositoryUrl + Loc.T("/blob/main/docs/zh-CN/agents.md", "/blob/main/docs/en-US/agents.md")));
        _content.Children.Add(intro);

        var paths = AgentPaths.Current();
        var find = ActionButton(Loc.T("查找已安装的工具", "Find installed tools"), () =>
        {
            var found = AgentHarnesses.All.Where(h => AgentHarnesses.Detected(h, paths, p => File.Exists(p) || Directory.Exists(p))).Select(h => h.Id).ToList();
            _agentsLooked = true; _agentProblems.Clear();
            Change(s => s.AgentHarnessesFound = found);
            ShowPage(AgentsSettingsPage);
        });
        find.Style = (Style)FindResource("SettingsOutlineButton");
        SectionTitle(Loc.T("已安装的工具", "Installed tools"), find, () => Loc.T(
            "按各工具的设置目录和程序位置查找。几个应用共用同一个设置文件时算作一个工具，例如 Claude Code CLI 与 Claude 桌面版。安装钩子会修改该工具的用户设置文件，首次修改前在同一目录保留一份 .brimdeck-backup 副本；卸载只删除 BrimDeck 添加的条目。",
            "Tools are found by their settings folders and program locations. Apps that share one settings file count as one tool, such as the Claude Code CLI and the Claude desktop app. Installing hooks changes the tool's user settings file, and a .brimdeck-backup copy is kept beside it before the first change; removing them deletes only what BrimDeck added."));
        var found = S.AgentHarnessesFound.Select(AgentHarnesses.Find).OfType<AgentHarness>().ToList();
        var rows = new List<FrameworkElement>();
        if (!File.Exists(BrimFile))
            rows.Add(Note(Loc.T("BrimDeck 目录中没有 brim.exe，无法安装钩子。请重新安装 BrimDeck。", "brim.exe is missing from BrimDeck's folder, so hooks cannot be installed. Reinstall BrimDeck."), true));
        if (found.Count == 0)
            rows.Add(Note(_agentsLooked ? Loc.T("没有找到支持的工具。可以按下方“其他工具”的方法自行连接。", "No supported tool was found. You can connect one yourself as \"Other tools\" below describes.")
                : Loc.T("点“查找已安装的工具”，列出本机安装的 AI 编程工具。", "Choose \"Find installed tools\" to list the AI coding tools on this PC."), false));
        else
        {
            var states = found.ToDictionary(h => h.Id, h => AgentHookFiles.State(h, paths, BrimFile));
            var all = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var installAll = ActionButton(Loc.T("全部安装", "Install all"), () => Apply(found.Where(h => states[h.Id] is HookState.Missing or HookState.Outdated), install: true));
            var removeAll = ActionButton(Loc.T("全部卸载", "Remove all"), () => Apply(found.Where(h => states[h.Id] is HookState.Installed or HookState.Outdated), install: false));
            installAll.IsEnabled = File.Exists(BrimFile) && states.Values.Any(s => s is HookState.Missing or HookState.Outdated);
            removeAll.IsEnabled = states.Values.Any(s => s is HookState.Installed or HookState.Outdated);
            all.Children.Add(installAll); all.Children.Add(removeAll);
            var head = Row(_agentsLooked ? Loc.T($"找到 {found.Count} 个工具", Loc.Count(found.Count, "tool found", "tools found")) : Loc.T($"{found.Count} 个工具", Loc.Count(found.Count, "tool", "tools")), all);
            rows.Add(head);
            foreach (var harness in found) rows.Add(HarnessRow(harness, states[harness.Id], paths));
        }
        Group([.. rows]);

        SectionTitle(Loc.T("审批与提问", "Approvals and questions"));
        Group(MusicChoice(Loc.T("在岛上等待回答", "Wait on the island"), S.AgentWaitMinutes, [(1, Loc.T("1 分钟", "1 minute")), (3, Loc.T("3 分钟", "3 minutes")), (5, Loc.T("5 分钟", "5 minutes")),
                (10, Loc.T("10 分钟", "10 minutes")), (30, Loc.T("30 分钟", "30 minutes"))], value => Change(s => s.AgentWaitMinutes = value),
            help: Loc.T("工具请求审批或提问时，先在岛上等待你回答，期间终端里不会出现确认框；超过这个时间、关闭这条通知或点“在终端处理”后，交回终端按原来的方式确认。",
                "When a tool asks for approval or asks a question, it first waits for your answer on the island, and the terminal shows no prompt meanwhile. After this time, or when you close the notice or choose \"Use the terminal\", the terminal asks as it always does.")));

        OtherToolsSection();
    }

    // A line of the tools' group: what to do, or what went wrong.
    private FrameworkElement Note(string text, bool problem)
    {
        var line = TextLine(text, 12, problem ? _palette.Negative : TextSecondary);
        line.TextWrapping = TextWrapping.Wrap;
        return new Border { Child = line, Padding = new Thickness(0, 13, 0, 13) };
    }

    // One tool: its name and the apps it covers, whether its hooks are in place and what the island can do for it, and the button
    // that installs, updates or removes them.
    private FrameworkElement HarnessRow(AgentHarness harness, HookState state, AgentPaths paths)
    {
        string file = harness.SettingsFile(paths);
        string can = harness.Approvals && harness.Questions ? Loc.T("可在岛上审批、回答提问", "Approvals and questions on the island")
            : harness.Approvals ? Loc.T("可在岛上审批", "Approvals on the island")
            : harness.Tells ? Loc.T("显示状态；完成、待审批、待回答时提醒", "State, and a notice when it finishes or waits for you")
            : Loc.T("显示状态与完成通知", "State and finished notices");
        string status = state switch
        {
            HookState.Installed => Loc.T("已安装钩子 · ", "Hooks installed · ") + can,
            HookState.Outdated => Loc.T("钩子需要更新：BrimDeck 的位置或钩子内容已变化", "Hooks need an update: BrimDeck moved or the hooks changed"),
            HookState.Unreadable => Loc.T("设置文件无法读取，未作修改", "The settings file cannot be read and was left as it is"),
            HookState.Unsupported => harness.Unsupported?.Invoke() ?? "",
            _ => Loc.T("未安装钩子 · 安装后", "No hooks · Once installed: ") + can
        };
        if (state == HookState.Installed && harness.AfterInstall is { } after) status += Loc.T("。", ". ") + after();
        if (_agentProblems.TryGetValue(harness.Id, out var problem)) status = problem;
        var detail = TextLine(status, 11.5, state is HookState.Unreadable or HookState.Outdated || problem is not null ? _palette.Negative : TextSecondary);
        detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 2, 0, 0);
        var components = TextLine(harness.Components(), 11.5, TextTertiary);
        components.TextWrapping = TextWrapping.Wrap;
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        labels.Children.Add(TextLine(harness.Name, 13, TextPrimary, FontWeights.SemiBold));
        labels.Children.Add(components); labels.Children.Add(detail);
        if (file.Length > 0) labels.ToolTip = file;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (state is HookState.Missing or HookState.Outdated)
        {
            var install = ActionButton(state == HookState.Outdated ? Loc.T("更新", "Update") : Loc.T("安装", "Install"), () => Apply([harness], install: true));
            install.Style = (Style)FindResource("SettingsOutlineButton");
            install.IsEnabled = File.Exists(BrimFile);
            AutomationProperties.SetName(install, (state == HookState.Outdated ? Loc.T("更新 ", "Update ") : Loc.T("安装 ", "Install ")) + harness.Name + Loc.T(" 的钩子", " hooks"));
            controls.Children.Add(install);
        }
        if (state is HookState.Installed or HookState.Outdated)
        {
            var remove = ActionButton(Loc.T("卸载", "Remove"), () => Apply([harness], install: false));
            AutomationProperties.SetName(remove, Loc.T("卸载 ", "Remove ") + harness.Name + Loc.T(" 的钩子", " hooks"));
            controls.Children.Add(remove);
        }
        var row = new Grid { MinHeight = 56 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels); Grid.SetColumn(controls, 1); row.Children.Add(controls);
        return row;
    }

    // Installs or removes the hooks of each tool, then builds the page again with what each now has.
    private void Apply(IEnumerable<AgentHarness> harnesses, bool install)
    {
        var paths = AgentPaths.Current();
        _agentProblems.Clear();
        foreach (var harness in harnesses.ToList())
        {
            string? failed = install ? AgentHookFiles.Install(harness, paths, BrimFile) : AgentHookFiles.Uninstall(harness, paths);
            if (failed is not null) _agentProblems[harness.Id] = (install ? Loc.T("安装失败：", "Install failed: ") : Loc.T("卸载失败：", "Remove failed: ")) + failed;
        }
        ShowPage(AgentsSettingsPage);
    }

    // Tools BrimDeck has no hooks for: one whose hooks read and answer as Claude Code's do takes brim hook claude under its own
    // name; any other reports its state with brim agent report from a script of the user's. Approvals and questions of such a
    // tool go through brim notify send --wait.
    private void OtherToolsSection()
    {
        SectionTitle(Loc.T("其他工具", "Other tools"), help: () => Loc.T(
            "不在列表中的工具也可以接入。钩子格式与 Claude Code 相同的工具（标准输入为 JSON，含 hook_event_name），把第一条命令填为它的钩子命令，并换成它的名称；其他工具在自己的脚本中用第二条命令报告状态（started、working、approval、question、done、idle、ended）。详细步骤见“使用说明”。",
            "Tools that are not listed can be connected too. For a tool whose hooks work like Claude Code's (JSON on standard input with hook_event_name), use the first command as its hook command, with its own name. Any other tool can report its state from its own scripts with the second command (started, working, approval, question, done, idle or ended). See \"How it works\" for the steps."));
        string brim = File.Exists(BrimFile) ? BrimFile : "brim.exe";
        Group(CommandRow(Loc.T("与 Claude Code 格式相同的钩子", "Hooks in Claude Code's format"), $"\"{brim}\" hook claude --name \"{Loc.T("工具名称", "Tool name")}\""),
            CommandRow(Loc.T("在脚本中报告状态", "Report state from a script"), $"\"{brim}\" agent report --tool \"{Loc.T("工具名称", "Tool name")}\" --session ID --state working"));
    }

    // A command to copy: what it is for, the command in full, and the button that copies it.
    private FrameworkElement CommandRow(string title, string command)
    {
        var text = TextLine(command, 11.5, TextSecondary);
        text.TextWrapping = TextWrapping.Wrap; text.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"); text.Margin = new Thickness(0, 3, 0, 0);
        string copyName = Loc.T("复制", "Copy");
        Button? copy = null;
        copy = ActionButton(copyName, () =>
        {
            copy!.Content = NoticeCenter.Copy(command) ? Loc.T("已复制", "Copied") : Loc.T("复制失败", "Copy failed");
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            back.Tick += (_, _) => { back.Stop(); copy.Content = copyName; };
            back.Start();
        });
        AutomationProperties.SetName(copy, copyName + " " + title);
        return Row(title, copy, text);
    }
}
