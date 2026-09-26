using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private async Task VerifyPreviewDismissalAsync(string output, List<string> checks)
    {
        var saved = Settings.Copy();
        SettingsWindow? window = null;
        Window? other = null;
        void Check(string name, bool value) => checks.Add((value ? "PASS " : "FAIL ") + name);
        T Named<T>(DependencyObject root, string name) where T : FrameworkElement => SettingsElements(root).OfType<T>()
            .Single(control => AutomationProperties.GetName(control) == name);
        void Click(DependencyObject root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Press(UIElement control) => control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        bool PreviewButtonReset() => !window!.IsPreviewing;
        // Changing a size starts the preview; the AI stepper comes before the music one.
        void Step(bool music)
        {
            var steppers = SettingsElements(window!.RootVisual).OfType<RepeatButton>().Where(b => AutomationProperties.GetName(b) == "增加宽度");
            var plus = music ? steppers.Last() : steppers.First();
            Press(plus); plus.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        void StartPreview()
        {
            Step(false);
            Deck.PointerChanged(false);
        }
        try
        {
            UpdateSettings(new DeckSettings { Width = 1080, Height = 330, MusicPage = true,
                Animations = false, OpenDelay = 40, CloseDelay = 150,
                Maximized = WindowBehavior.Normal, Borderless = WindowBehavior.Normal, Exclusive = WindowBehavior.Normal });
            Deck.Preview(false); Deck.TestContext(ScreenContext.Desktop);
            window = new SettingsWindow(this); window.Show(); window.ShowPage(2); window.Activate();
            Click(window.RootVisual, "展开 AI 用量尺寸"); Click(window.RootVisual, "展开音乐尺寸");
            await Task.Delay(100); window.UpdateLayout();
            StartPreview(); await Task.Delay(300);
            Check("preview remains expanded while inspecting dimensions outside the panel", Deck.IsExpanded);
            Capture(Deck.PanelVisual, Path.Combine(output, "preview-before-dismissal.png"));

            var width = SettingsElements(window.RootVisual).OfType<Slider>().First(s => AutomationProperties.GetName(s) == "宽度");
            var track = (Track)width.Template.FindName("PART_Track", width);
            Press(track.Thumb); width.Value = 1100; await Task.Delay(300);
            Check("slider thumb input keeps preview active while changing width", Deck.IsExpanded && Settings.Width == 1100 && !PreviewButtonReset());
            var height = SettingsElements(window.RootVisual).OfType<Slider>().First(s => AutomationProperties.GetName(s) == "高度");
            var heightTrack = (Track)height.Template.FindName("PART_Track", height);
            Press(heightTrack.Thumb);
            heightTrack.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            height.Value = 350;
            heightTrack.Thumb.RaiseEvent(new DragCompletedEventArgs(30, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            heightTrack.Thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Deck.PointerChanged(false); await Task.Delay(500);
            Check("releasing the height slider and moving away keeps preview expanded without another click",
                Deck.IsExpanded && Settings.Height == 350 && !PreviewButtonReset());
            var number = SettingsElements(window.RootVisual).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "宽度数值");
            Press(number); number.Focus(); await Task.Delay(250);
            Check("focusing a dimension editor preserves preview", Deck.IsExpanded && !PreviewButtonReset());
            var plus = SettingsElements(window.RootVisual).OfType<RepeatButton>().First(b => AutomationProperties.GetName(b) == "增加宽度");
            Press(plus); plus.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(250);
            Check("dimension stepper keeps preview active", Deck.IsExpanded);

            // This commit runs after mouse-down, reproducing the focus-loss ordering.
            number.Focus(); number.Text = "1120";
            Press(window.RootVisual);
            SettingsElements(window.RootVisual).OfType<Button>().Single(button => Equals(button.Content, "恢复默认")).Focus();
            await Task.Delay(80);
            Check("outside settings click ends preview after the dimension edit commits", Settings.Width == 1120 && PreviewButtonReset());
            await Task.Delay(250);
            Check("outside settings click restores automatic collapse", !Deck.IsExpanded);
            Capture(Deck.PanelVisual, Path.Combine(output, "preview-after-dismissal.png"));
            Deck.PointerChanged(true); await Task.Delay(100);
            bool hoverExpanded = Deck.IsExpanded;
            Deck.PointerChanged(false); await Task.Delay(250);
            Check("normal hover expands and collapses after preview dismissal", hoverExpanded && !Deck.IsExpanded);

            // The dismissal waits for an idle dispatcher, so a fixed delay can end before it runs. An operation queued
            // behind it at the same priority runs right after it; the close delay has only just started then.
            StartPreview(); Press(window.RootVisual); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check("preview dismissal preserves the configured close delay", Deck.IsExpanded && PreviewButtonReset());
            await Task.Delay(250);
            Check("clicking a blank settings area releases forced expansion", !Deck.IsExpanded);

            StartPreview();
            var preset = Named<RadioButton>(window.RootVisual, "快捷尺寸 紧凑");
            Press(preset); preset.Focus(); preset.IsChecked = true; await Task.Delay(250);
            Check("quick size selection survives the settings page rebuild", Deck.IsExpanded && !PreviewButtonReset());
            Press(window.RootVisual); await Task.Delay(250);
            Check("rebuilt settings controls still allow outside dismissal", !Deck.IsExpanded && PreviewButtonReset());

            Step(true); Deck.PointerChanged(false); await Task.Delay(100);
            Check("music panel starts its own settings preview", Deck.IsExpanded && Deck.IsMusicPage);
            Press(window.RootVisual); await Task.Delay(250);
            Check("music preview also ends on outside settings input", !Deck.IsExpanded);

            StartPreview(); Deck.PointerChanged(true); Press(window.RootVisual); await Task.Delay(250);
            Check("restoring detection retains an expanded panel while the pointer is inside", Deck.IsExpanded && PreviewButtonReset());
            Deck.PointerChanged(false); await Task.Delay(250);
            Check("leaving that panel then uses ordinary collapse", !Deck.IsExpanded);

            StartPreview();
            other = new Window { Title = "Preview dismissal test", Width = 200, Height = 100, Left = 20, Top = 650,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            other.Show(); other.Activate(); await Task.Delay(300);
            Check("settings deactivation ends preview and restores collapse", !Deck.IsExpanded && PreviewButtonReset());
            other.Close(); other = null; window.Activate(); await Task.Delay(100);

            StartPreview(); window.ShowPage(1); await Task.Delay(60);
            Check("leaving expanded appearance still ends preview", !Deck.IsExpanded);
        }
        finally
        {
            other?.Close(); window?.Close(); Deck.Preview(false); Deck.PointerChanged(false); UpdateSettings(saved);
        }
    }
}
