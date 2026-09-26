using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyAppInteractionsAsync(string output, List<string> checks)
    {
        void Check(string name, bool result) => checks.Add((result ? "PASS " : "FAIL ") + name);
        var saved = Settings.Copy();
        OpenSettings(); var window = _settingsWindow!;
        TextBox Input(Guid id) => SettingsElements(window.RootVisual).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "display-name-" + id.ToString("N"));
        Button Add() => SettingsElements(window.RootVisual).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "添加应用");
        Border Row(Guid id) => SettingsElements(window.RootVisual).OfType<Border>().Single(b => b.Tag is AppEntry app && app.InstanceId == id);
        void Key(FrameworkElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        try
        {
            foreach (var theme in new[] { SettingsTheme.Dark, SettingsTheme.Light })
            {
                UpdateSettings(new DeckSettings { Theme = theme, Animations = false });
                window.RebuildTheme(); window.ShowPage(3); window.Activate();
                await Task.Delay(60); window.UpdateLayout();
                var id = Settings.Apps[0].InstanceId; var input = Input(id);
                Check($"{theme} default name is shown without being saved", input.Text == "Claude" && Settings.Entry(id)!.DisplayName == "");
                Capture(window.RootVisual, Path.Combine(output, $"settings-default-names-{theme.ToString().ToLowerInvariant()}.png"));
                // The field's padding is also a click target; keyboard focus uses the same clearing behavior.
                ((Border)input.Parent).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Check($"{theme} clicking the default name clears it for typing", input.IsKeyboardFocused && input.Text == "");
                Add().Focus();
                Check($"{theme} leaving an empty name restores the default", input.Text == "Claude" && Settings.Entry(id)!.DisplayName == "");
                input.Focus(); Key(input, System.Windows.Input.Key.Enter);
                Check($"{theme} submitting an empty name leaves the focused input empty", input.Text == "" && Settings.Entry(id)!.DisplayName == "");
                Add().Focus();
                Check($"{theme} empty Enter followed by blur does not save the fallback as a custom name", input.Text == "Claude" && Settings.Entry(id)!.DisplayName == "");
                input.Focus(); input.Text = "Claude"; Key(input, System.Windows.Input.Key.Enter); Add().Focus();
                Check($"{theme} an explicitly typed name matching the source remains a custom name", Settings.Entry(id)!.DisplayName == "Claude");
                input.Focus(); input.Text = "未保存名称"; Key(input, System.Windows.Input.Key.Escape); Add().Focus();
                Check($"{theme} Escape restores the saved custom name", input.Text == "Claude" && Settings.Entry(id)!.DisplayName == "Claude");
                input.Focus(); input.Text = "我的工作"; Add().Focus();
                Check($"{theme} blurring saves a custom name", input.Text == "我的工作" && Settings.Entry(id)!.Name == "我的工作");
                Capture(window.RootVisual, Path.Combine(output, $"settings-custom-name-{theme.ToString().ToLowerInvariant()}.png"));
                input.Focus(); input.Text = "   "; Add().Focus();
                Check($"{theme} clearing a custom name restores the default", input.Text == "Claude" && Settings.Entry(id)!.DisplayName == "");
                input.Focus(); input.Text = "未保存名称"; Key(input, System.Windows.Input.Key.Escape);
                Check($"{theme} Escape on an automatic name keeps the editor empty", input.Text == "" && Settings.Entry(id)!.DisplayName == "");
                Add().Focus();
            }

            var dragSettings = new DeckSettings { Theme = SettingsTheme.Dark, Animations = true, AnimationDuration = 600 };
            dragSettings.Apps[1].QuotaSource = ProviderId.Claude; // Repeated sources must still move independently.
            UpdateSettings(dragSettings); window.RebuildTheme(); window.ShowPage(3); window.Activate();
            await Task.Delay(80); window.UpdateLayout();
            var order = Settings.Apps.Select(a => a.InstanceId).ToArray();
            var first = Row(order[0]);
            var handle = (Border)((Grid)first.Child).Children[0];
            window.BeginAppDrag(0); window.UpdateAppDrag(124.5); // Row top is 90, between the second and third slots.
            Check("dragging does not save an unfinished order", Settings.Apps.Select(a => a.InstanceId).SequenceEqual(order));
            if (SystemParameters.ClientAreaAnimation)
            {
                await Task.Delay(90);
                Capture(window.RootVisual, Path.Combine(output, "settings-drag-moving.png"));
            }
            handle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Check("release commits only the dragged instance", Settings.Apps.Select(a => a.InstanceId).SequenceEqual(new[] { order[1], order[0], order[2], order[3] }) && !handle.IsMouseCaptured);
            if (SystemParameters.ClientAreaAnimation)
            {
                await Task.Delay(90);
                Capture(window.RootVisual, Path.Combine(output, "settings-drag-settling.png"));
            }
            await Task.Delay(650);
            Capture(window.RootVisual, Path.Combine(output, "settings-drag-finished.png"));
            window.MoveApp(1, 3);
            window.BeginAppDrag(3); window.UpdateAppDrag(44.5); // Reverse before the previous animation finishes.
            await Task.Delay(75);
            window.EndAppDrag(true);
            await Task.Delay(650);
            Check("dragging across multiple rows restores the requested order", Settings.Apps.Select(a => a.InstanceId).SequenceEqual(order));

            window.BeginAppDrag(0); window.UpdateAppDrag(172.5);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Task.Delay(650);
            Check("Escape cancels dragging and restores the saved order", !handle.IsMouseCaptured && Settings.Apps.Select(a => a.InstanceId).SequenceEqual(order));
            window.BeginAppDrag(0); window.UpdateAppDrag(172.5); Mouse.Capture(null);
            await Task.Delay(650);
            Check("losing capture cancels the drag without saving a partial order", Settings.Apps.Select(a => a.InstanceId).SequenceEqual(order));

            window.BeginAppDrag(0); window.UpdateAppDrag(172.5); window.ShowPage(0);
            Check("leaving the page releases capture and cancels the pending reorder", Mouse.Captured is null && Settings.Apps.Select(a => a.InstanceId).SequenceEqual(order));
        }
        finally
        {
            window.EndAppDrag(false, false); UpdateSettings(saved); window.RebuildTheme(); window.ShowPage(3);
        }
    }
}
