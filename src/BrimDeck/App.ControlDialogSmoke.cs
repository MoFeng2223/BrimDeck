using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyControlDialogAsync(string output, List<string> checks)
    {
        void Check(string name, bool pass) => checks.Add((pass ? "PASS " : "FAIL ") + name);
        foreach (var theme in new[] { SettingsTheme.Light, SettingsTheme.Dark })
        {
            var dialog = new NeteaseControlWindow(theme) { Owner = Deck };
            Task? inspection = null;
            dialog.Loaded += (_, _) => inspection = Inspect();
            async Task Inspect()
            {
                try
                {
                    await Task.Delay(100); dialog.UpdateLayout();
                    var controls = SettingsElements(dialog).OfType<Button>().ToArray();
                    var cancel = controls.Single(button => AutomationProperties.GetName(button) == "取消");
                    var enable = controls.Single(button => AutomationProperties.GetName(button) == "重启并启用");
                    Check($"{theme}: Enter cannot automatically enable control", !enable.IsDefault && cancel.IsCancel);
                    Check($"{theme}: security notice remains visible", SettingsElements(dialog).OfType<TextBlock>()
                        .Any(text => text.IsVisible && text.Text == NeteaseControlWindow.SecurityNotice));
                    Capture((FrameworkElement)dialog.Content, Path.Combine(output, $"control-dialog-{theme.ToString().ToLowerInvariant()}.png"));
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }
            Check($"{theme}: modal cancellation returns false", dialog.ShowDialog() == false);
            if (inspection is not null) await inspection;
        }
    }
}
