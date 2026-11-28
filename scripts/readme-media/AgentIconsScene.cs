using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The notices page with a notice from each coding tool, each showing the tool's own icon (AgentIcons, "agent:<tool>"), read from
// this PC's Start menu and programs; a tool not installed here shows its letter. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task AgentIconsScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        int serial = 0;
        foreach (var (tool, name) in new[] { ("qoder", "Qoder"), ("qoder-cn", "Qoder CN"), ("codeagent", "CodeAgent"), ("claude", "Claude Code"), ("antigravity", "Antigravity"), ("zcode", "ZCode"), ("workbuddy", "WorkBuddy"),
            ("opencode", "OpenCode"), ("kimi", "Kimi Code"), ("cursor", "Cursor"), ("commandcode", "Command Code"), ("mimo", "MiMo Code") })
        {
            int minutes = ++serial;
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Cli, AppKey = "cli:" + name, AppName = name, InstanceKey = "cli::agent:" + serial, ReplaceKey = "agent-done:" + tool + ":s" + serial,
                Title = Loc.T("回复完成", "Done"), Body = tool, Category = NoticeCategory.Success, IconRef = "agent:" + tool, CreatedAt = _now.AddMinutes(-minutes)
            }, _now.AddMinutes(-minutes).ToUniversalTime());
        }
        // The Qoder CLIs' own programs, the last of their tools' sources, read directly: the desktop apps come first where installed.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (cli, path) in new[] { ("qodercli", @".qoder\bin\qodercli\qodercli.exe"), ("qoderclicn", @".qoder-cn\bin\qoderclicn\qoderclicn.exe") })
        {
            int minutes = ++serial;
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Cli, AppKey = "cli:" + cli, AppName = cli, InstanceKey = "cli::agent:" + serial, Title = Loc.T("回复完成", "Done"), Body = cli,
                Category = NoticeCategory.Success, IconRef = "exe:" + System.IO.Path.Combine(home, path), CreatedAt = _now.AddMinutes(-minutes)
            }, _now.AddMinutes(-minutes).ToUniversalTime());
        }
        SelectPage(DeckPage.Notices);
        _settings.NoticesWidth = 760; _settings.NoticesHeight = 820; Refresh();
        _deck.SetExpanded(true, true);
        // The icons are read in the background; the page is drawn again as each arrives.
        await Task.Delay(3000);
        await Settle();
        await Save("agent-icons", 2);
        _deck.SetExpanded(false, true);
        // The icons built in for the tools that may be installed as a command line alone, read directly (brand:agent-<id>).
        hub.Clear();
        foreach (var tool in new[] { "claude", "codex", "cursor", "antigravity", "kimi", "opencode", "commandcode" })
        {
            int minutes = ++serial;
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Cli, AppKey = "cli:brand-" + tool, AppName = tool, InstanceKey = "cli::brand:" + serial, Title = Loc.T("内置图标", "Built-in icon"), Body = tool,
                Category = NoticeCategory.Success, IconRef = "brand:agent-" + tool, CreatedAt = _now.AddMinutes(-minutes)
            }, _now.AddMinutes(-minutes).ToUniversalTime());
        }
        _deck.SetExpanded(true, true);
        await Task.Delay(2000);
        await Settle();
        await Save("agent-icons-builtin", 2);
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
    }
}
