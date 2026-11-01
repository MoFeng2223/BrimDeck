using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckWakeSlider(Action<string, bool> check)
    {
        var app = (App)Application.Current; bool animations = app.Settings.Animations; app.Settings.Animations = false;
        var settings = new ClipboardSettings(); var changes = new List<(int Value, bool Save)>();
        var control = ClipboardPreviewSensitivityControl.Create(settings.PreviewSensitivity, (value, save) => { settings.PreviewSensitivity = value; changes.Add((value, save)); });
        var window = new Window { Content = control, Width = 420, Height = 180, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var slider = Descendants<EdgeDropWakeSlider>(control).Single();
            var track = (Grid)slider.Template.FindName("PART_WakeTrack", slider);
            var bars = track.Children.OfType<Border>().ToArray();
            check("EdgeDrop WakeSlider: settings uses original twenty-eight rounded bars and forty-four DIP input target", bars.Length == 28 && slider.ActualHeight == 44 && bars.All(bar => bar.CornerRadius.TopLeft == 9999 && bar.Height == 28));
            check("EdgeDrop WakeSlider: rest bars use original eight DIP height and three DIP gaps", bars.All(bar => bar.RenderTransform is ScaleTransform scale && Math.Abs(scale.ScaleY * bar.Height - 8) < .01)
                && bars.Zip(bars.Skip(1)).All(pair => Math.Abs(pair.Second.TranslatePoint(new Point(), track).X - pair.First.TranslatePoint(new Point(), track).X - pair.First.ActualWidth - 3) < 1));
            var badge = Descendants<TextBlock>(control).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-preview-delay");
            check("preview sensitivity: default badge and persisted preference agree on seventy percent / three hundred milliseconds", badge.Text.Contains("70%") && badge.Text.Contains("300 ms") && settings.PreviewDelayMilliseconds == 300);
            var peer = new SliderAutomationPeer(slider); var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!;
            check("EdgeDrop WakeSlider: native UI Automation exposes an adjustable zero-to-one-hundred range", range.Minimum == 0 && range.Maximum == 100 && range.Value == 70 && !range.IsReadOnly);
            void Press(Key key) => slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Press(Key.End); Pump();
            check("EdgeDrop WakeSlider: End commits instant sensitivity and updates badge without rebuilding", settings.PreviewSensitivity == 100 && settings.PreviewDelayMilliseconds == 0 && changes[^1] == (100, true) && !badge.Text.Contains("ms") && Descendants<EdgeDropWakeSlider>(control).Single() == slider);
            Press(Key.Home); Press(Key.PageUp); Press(Key.Right); Pump();
            check("EdgeDrop WakeSlider: Home, PageUp and arrows preserve one-percent detents", slider.Value == 11 && settings.PreviewSensitivity == 11 && changes[^1] == (11, true));
            foreach (int preset in new[] { 0, 50, 100 })
            {
                Descendants<Button>(control).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-preview-preset-" + preset).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check("EdgeDrop WakeSlider: clickable preset " + preset + " commits once at the correct endpoint", slider.Value == preset && settings.PreviewSensitivity == preset && changes[^1] == (preset, true));
            }
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            void Call(string name, params object[] args) => typeof(EdgeDropWakeSlider).GetMethod(name, hidden)!.Invoke(slider, args);
            typeof(EdgeDropWakeSlider).GetField("_dragging", hidden)!.SetValue(slider, true);
            changes.Clear(); Call("CommitFromRatio", .41); Pump();
            check("EdgeDrop WakeSlider: drag updates preview immediately without writing preferences each frame", settings.PreviewSensitivity == 41 && changes.SequenceEqual(new[] { (41, false) }));
            typeof(EdgeDropWakeSlider).GetField("_pendingRatio", hidden)!.SetValue(slider, .67);
            Call("EndDrag"); Pump();
            check("EdgeDrop WakeSlider: release flushes the final pointer coordinate and commits exactly once", slider.Value == 67 && settings.PreviewSensitivity == 67 && changes.Count(change => change.Save) == 1 && changes[^1] == (67, true));
            check("EdgeDrop WakeSlider: snap preserves a non-divisible upper endpoint and clamps input", EdgeDropWakeSlider.Snap(1, 0, 1, .3) == 1 && EdgeDropWakeSlider.Snap(-2, 0, 100, 1) == 0 && EdgeDropWakeSlider.Snap(105, 0, 100, 1) == 100);
            var restored = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings.Copy()))!; restored.Normalize();
            check("preview sensitivity: clone and JSON round-trip retain preference without serializing derived delay", restored.PreviewSensitivity == 67 && restored.PreviewDelayMilliseconds == 330 && !JsonSerializer.Serialize(restored).Contains("PreviewDelayMilliseconds"));
            var legacy = JsonSerializer.Deserialize<ClipboardSettings>("{}")!; legacy.Normalize();
            check("preview sensitivity: old settings receive a responsive default without enabling capture", legacy.PreviewSensitivity == 70 && !legacy.Enabled);
            restored.PreviewSensitivity = -1; restored.Normalize(); bool low = restored.PreviewSensitivity == 0;
            restored.PreviewSensitivity = 101; restored.Normalize();
            check("preview sensitivity: stored bounds normalize at zero and one hundred", low && restored.PreviewSensitivity == 100);
            app.Settings.Animations = true; slider.Value = 0;
            // An offscreen HWND may not present on the timer's wall clock. Advance the actual render callback by 50 ms.
            typeof(EdgeDropWakeSlider).GetField("_frameAt", hidden)!.SetValue(slider, Environment.TickCount64 / 1000d - .05);
            Call("Frame", null!, EventArgs.Empty);
            if (Soft.Enabled) check("EdgeDrop WakeSlider: changing value creates a velocity-driven wave rather than translating a stock thumb", bars.Any(bar => ((ScaleTransform)bar.RenderTransform).ScaleY > 8d / 28 + .01));
            app.Settings.Animations = false; slider.Value = 20; Pump();
            check("EdgeDrop WakeSlider: disabled motion stops rendering and restores flat bars", !(bool)typeof(EdgeDropWakeSlider).GetField("_rendering", hidden)!.GetValue(slider)! && bars.All(bar => Math.Abs(((ScaleTransform)bar.RenderTransform).ScaleY - 8d / 28) < .01));
            slider.IsEnabled = false;
            check("EdgeDrop WakeSlider: disabled state follows upstream half opacity and native accessibility", slider.Opacity == .5 && !slider.IsEnabled);
        }
        finally { window.Close(); app.Settings.Animations = animations; }
    }
}
