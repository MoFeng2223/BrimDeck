using BrimDeck;

namespace ReadmeMedia;

// The "AI coding tools" settings page with tools found of each kind: those whose own prompt shows beside the island's (no
// switch), those that hold their prompt back while the island waits (a switch for approvals, for questions, or for both), and
// one that only tells. Whether each tool's hooks are installed is read from this PC's settings files, without changing them. For
// checking, not for the README.
internal sealed partial class Renderer
{
    private async Task AgentAsksScene()
    {
        _settings.Animations = false;
        _settings.AgentHarnessesFound = ["claude", "codex", "qoder", "workbuddy", "commandcode", "zcode", "kimi"];
        _settings.AgentApprovalsHeld = ["codex"];
        _settings.AgentQuestionsHeld = ["commandcode"];
        await SettingsPage("agent-asks", 21, height: 1500, tips: true);
        await SettingsPage("agent-asks-light", 21, height: 1500, theme: BrimDeck.Core.SettingsTheme.Light);
        // No tool found: the section holds the waiting time alone.
        _settings.AgentHarnessesFound = [];
        await SettingsPage("agent-asks-none", 21, height: 900);
    }
}
