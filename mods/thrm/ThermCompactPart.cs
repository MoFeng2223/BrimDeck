using BrimDeck.Modules;

namespace BrimDeck.Thrm;

// The cooler's presence in the collapsed row, as a compact part: three readings beside the built-in ones, in the user's
// order and under the user's switch. This is the platform's compact-part channel, not an activity — a reading has no
// Active state and no control bar, so there is nothing to start or stop. Enabling the module is the only switch.
internal sealed class ThermCompactPart(ThermWorkspace workspace) : IModuleCompactPart
{
    public IModuleView CreateView(ModuleViewContext context) => new ThermActivityView(workspace, context, ActivityViewKind.Compact);
}
