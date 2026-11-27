using BrimDeck.Protocol;

namespace BrimDeck.Core.Agents;

// An approval or a question of a tool that shows no prompt of its own while its hook waits (NoticeMessage.Holds). Waiting on
// the island would take the prompt out of the tool for as long, so BrimDeck waits only for the tools the user chose
// (DeckSettings.AgentApprovalsHeld, AgentQuestionsHeld). For the others the island only tells: the notice loses its buttons
// and options and stays until the session moves on, the hook hears that nobody waits and returns at once, and the tool asks
// in its own window.
public static class AgentAsks
{
    // Whether the island only tells of this ask. A tool BrimDeck does not know has no switch, and is waited for as it asked.
    public static bool OnlyTold(DeckSettings settings, NoticeMessage ask)
        => ask is { Wait: true, Holds: true } && AgentKeys.Ask(ask.ReplaceId) is var (harness, _) && AgentHarnesses.Find(harness) is not null
            && !(ask.Questions is { Count: > 0 } ? settings.AgentQuestionsHeld : settings.AgentApprovalsHeld).Contains(harness);

    // Makes of the ask the notice that only tells.
    public static void Tell(NoticeMessage ask)
    {
        ask.Wait = null; ask.Holds = null; ask.TimeoutMs = null; ask.Actions = null; ask.Questions = null;
        ask.Level = "persistent";
        if (string.IsNullOrWhiteSpace(ask.Body)) ask.Body = Loc.T($"请在 {ask.AppName} 中处理", $"Answer it in {ask.AppName}");
    }
}
