using System.Windows;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace ReadmeMedia;

// The collapsed row's unread notices and coding agents (MainWindow.CompactAttention.cs): the stacked round icons of the
// applications and the total, a call, the bell; the agents' sessions in each state, a tool with several sessions, several tools,
// many sessions; both merged into the notices' place; and a capsule with a width of its own that is too short for everything,
// where they give up their words and then all icons but one instead of widening it. Real icons are read from this machine's
// Start menu (QQ, 钉钉, 飞书 and the coding tools' desktop applications) where installed. For checking.
internal sealed partial class Renderer
{
    private async Task AttentionStripScene()
    {
        const double scale = 3;
        Program.SetProperty(app, "Exiting", true);
        bool animations = _settings.Animations;
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var occupied = _settings.Occupied; _settings.Occupied = WindowBehavior.Normal;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var agents = Activator.CreateInstance(Type.GetType("BrimDeck.AgentCenter, BrimDeck")!, Program.Any, null, [center], null)!;
        Program.SetProperty(app, "Agents", agents);
        var sessions = (BrimDeck.Core.Agents.AgentSessions)agents.GetType().GetField("_sessions", Program.Any)!.GetValue(agents)!;
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        int serial = 0;
        Notice Add(string appKey, string name, string iconRef, bool call = false)
        {
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = "windows:" + appKey, AppName = name, AppId = appKey, InstanceKey = "windows:" + appKey + ":" + ++serial,
                Title = Loc.T("消息", "Message"), Body = Loc.T("内容", "Body"), IconRef = iconRef, CreatedAt = _now.AddSeconds(serial), Call = call
            }, _now.ToUniversalTime().AddSeconds(serial));
            return hub.Notices.First(notice => notice.InstanceKey == "windows:" + appKey + ":" + serial);
        }
        void Session(string harness, string name, string id, string state, int minutesAgo)
            => sessions.Apply(new NoticeMessage { Type = NoticeTypes.Agent, AppId = harness, AppName = name, Session = id, State = state, Context = "brimdeck", Title = state == "working" ? "fix" : null },
                DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).AddSeconds(-12));
        void Clear() { sessions.Clear(); hub.MarkAllViewed(); }
        _settings.NotchNotices = true; _settings.CapsuleNotices = true; _settings.NoticeStripAgents = true;
        _settings.NotchTransfer = false; _settings.CapsuleTransfer = false; _settings.NotchClock = true; _settings.CapsuleClock = true;
        _settings.Style = CompactStyle.Capsule;
        _deck.SetExpanded(false, true);

        async Task Shot(string name)
        {
            Refresh(); await Settle(); await Task.Delay(1500); Refresh(); await Settle(); await Task.Delay(300); Refresh();
            await Save("attention-" + name, scale);
        }

        // The notices.
        Add("QQ", "QQ", "aumid:QQ");
        await Shot("notices-1");
        Add("ali.dingtalk.release", "钉钉", "aumid:ali.dingtalk.release"); Add("com.electron.Feishu", "飞书", "aumid:com.electron.Feishu"); Add("QQ", "QQ", "aumid:QQ");
        await Shot("notices-3");
        for (int i = 0; i < 120; i++) Add("app" + i % 7, "App " + i % 7, "");
        await Shot("notices-many");
        _settings.NoticeStripIcons = false;
        await Shot("notices-bell");
        _settings.NoticeStripIcons = true;
        Clear();
        Add("QQ", "QQ", "aumid:QQ");
        Add("QQ", "QQ", "", call: true).SourcePresent = false;
        await Shot("notices-call");
        Clear();

        // The agents.
        Session("claude", "Claude Code", "a", "working", 3);
        await Shot("agents-working");
        Session("claude", "Claude Code", "a", "approval", 3);
        await Shot("agents-approval");
        Clear(); Session("codex", "Codex", "b", "question", 1);
        await Shot("agents-question");
        Clear(); Session("codex", "Codex", "b", "done", 1);
        await Shot("agents-done");
        Clear(); Session("claude", "Claude Code", "a", "working", 2); Session("claude", "Claude Code", "c", "working", 5); Session("codex", "Codex", "b", "working", 1);
        await Shot("agents-tools");
        Session("claude", "Claude Code", "c", "approval", 5); Session("kimi", "Kimi Code", "d", "question", 4);
        await Shot("agents-waiting");
        Clear(); foreach (var id in new[] { "1", "2", "3", "4", "5" }) Session("claude", "Claude Code", id, "working", 1);
        await Shot("agents-five");
        Session("qoder", "Qoder", "q", "working", 1);
        Clear(); Session("qoder", "Qoder", "q", "working", 1);
        await Shot("agents-letter");

        // Both, apart and merged.
        Clear(); Session("claude", "Claude Code", "a", "approval", 2); Session("codex", "Codex", "b", "working", 1);
        Add("QQ", "QQ", "aumid:QQ"); Add("ali.dingtalk.release", "钉钉", "aumid:ali.dingtalk.release");
        await Shot("both-apart");
        _settings.NoticeAgentsMerged = true;
        await Shot("both-merged");
        _settings.NoticeAgentsMerged = false;

        // A capsule with a width of its own, narrowing.
        _settings.CapsuleAutoWidth = false;
        foreach (var width in new[] { 420, 300, 240, 190, 150 })
        {
            _settings.CapsuleWidth = width;
            await Shot("fixed-" + width);
        }
        _settings.NoticeAgentsMerged = true;
        foreach (var width in new[] { 300, 190 })
        {
            _settings.CapsuleWidth = width;
            await Shot("fixed-merged-" + width);
        }
        _settings.NoticeAgentsMerged = false; _settings.CapsuleAutoWidth = true; _settings.CapsuleWidth = CollapsedLimits.CapsuleWidthDefault;
        // The notch on a side edge: a column.
        _settings.Style = CompactStyle.Notch; _settings.NotchEdge = ScreenEdge.Left;
        await Shot("column");
        _settings.NotchEdge = ScreenEdge.Top;

        Clear();
        _settings.NotchNotices = false; _settings.CapsuleNotices = false;
        _settings.Occupied = occupied; _settings.Animations = animations; Refresh();
        Program.SetProperty(app, "Exiting", false);
        await Settle();
    }
}
