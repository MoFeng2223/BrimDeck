using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int PromptsPage = 13;
    private void PromptSettingsPage()
    {
        SectionTitle(Loc.T("按类型恢复提示", "Restore prompts by type"));
        var explanation = TextLine(Loc.T(
            "确认类的「下次不再提示」表示以后主动执行同类操作时，不再二次确认。故障类只隐藏弹出提示，不会忽略错误或跳过保存。恢复只影响之后的提示，不执行任何业务操作。",
            "Remembered confirmations skip the second confirmation for future explicit actions of that type. Hidden fault alerts never bypass errors or save checks. Restoring a prompt affects future alerts only and performs no operation."), 12, TextSecondary);
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 0, 0, 12); Target.Children.Add(explanation);
        Group(Row(Loc.T("所有提示", "All prompts"), ActionButton(Loc.T("恢复全部提示", "Restore all prompts"), () => { _app.RestorePrompt(); ShowPage(PromptsPage); })));
        foreach (var definition in PromptCatalog.All)
        {
            bool suppressed = S.Prompts.IsSuppressed(definition.Id);
            var restore = ActionButton(suppressed ? Loc.T("恢复提示", "Restore prompt") : Loc.T("已开启", "Enabled"), () => { _app.RestorePrompt(definition.Id); ShowPage(PromptsPage); });
            restore.IsEnabled = suppressed;
            AutomationProperties.SetAutomationId(restore, "prompt-restore-" + definition.Key);
            string meaning = definition.Kind == PromptKind.Confirmation ? Loc.T("显式操作的二次确认", "Confirmation for explicit actions") : Loc.T("仅控制弹出提醒", "Alert presentation only");
            Group(Row(definition.Title, restore, help: () => meaning));
        }
    }
}
