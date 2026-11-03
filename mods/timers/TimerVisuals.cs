using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Loc = BrimDeck.ModuleLocalization;
using UI = BrimDeck.ModuleUi;
using Soft = BrimDeck.ModuleMotion;
using PanelTheme = BrimDeck.ModuleTheme;

namespace BrimDeck.Timers;

// Shared language for the configuration page, the activity strip and its compact presentation.
internal static class TimerVisuals
{
    internal static string ModeName(TimerMode mode) => mode switch
    { TimerMode.Pomodoro => Loc.T("番茄钟", "Pomodoro"), TimerMode.Countdown => Loc.T("倒计时", "Countdown"), _ => Loc.T("秒表", "Stopwatch") };
    internal static string PhaseName(TimerSession timer) => timer.Status switch
    {
        TimerStatus.Waiting => Loc.T("下一轮专注", "Next focus"),
        TimerStatus.Completed => Loc.T("计时完成", "Time's up"),
        _ => timer.Phase switch
        { TimerPhase.Focus => Loc.T("专注", "Focus"), TimerPhase.ShortBreak => Loc.T("短休息", "Short break"), TimerPhase.LongBreak => Loc.T("长休息", "Long break"), TimerPhase.Countdown => Loc.T("倒计时", "Countdown"), _ => Loc.T("秒表", "Stopwatch") }
    };
    internal static string LapTime(double seconds) => TimerSession.Format(seconds) + $".{(long)(seconds * 100) % 100:00}";
    internal static string Time(TimerSession timer) => timer.Mode == TimerMode.Stopwatch
        ? TimerSession.Format(timer.ElapsedSeconds) : TimerSession.Format(timer.RemainingSeconds, true);
    internal static string Accent(TimerSession timer) => timer.Phase is TimerPhase.ShortBreak or TimerPhase.LongBreak
        ? PanelTheme.Dark ? "#54C9B3" : "#167D69"
        : PanelTheme.Dark ? "#E09A12" : "#A85C00";
    internal static TextBlock Text(string text, double size = 13, string resource = "PanelPrimary")
    {
        var block = UI.Centered(text, size, UI.Primary);
        block.SetResourceReference(TextBlock.ForegroundProperty, resource); return block;
    }
    internal static Button Button(FrameworkElement owner, string label, Action action, string? tip = null)
    {
        var button = new Button { Content = label, Height = 32, Padding = new Thickness(12, 0, 12, 0), FontFamily = UI.PanelFont, FontSize = 12 };
        if (owner.TryFindResource("PanelTextButton") is Style style) button.Style = style;
        button.SetResourceReference(Control.ForegroundProperty, "PanelPrimary");
        button.Click += (_, e) => { e.Handled = true; action(); };
        AutomationProperties.SetName(button, tip ?? label);
        if (tip is not null) button.ToolTip = tip;
        return button;
    }
    internal static FrameworkElement Icon(string color, double size = 18)
    {
        var path = new Path { Data = Geometry.Parse("M 8,2 H 16 M 12,2 V 5 M 18,5 L 20,7 M 12,9 V 13 L 15,15 M 21,14 A 9,9 0 1 1 3,14 A 9,9 0 1 1 21,14"),
            Stroke = UI.Brush(color), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }
    internal static FrameworkElement Playback(bool running, string color)
    {
        var path = new Path { Fill = UI.Brush(color), Data = Geometry.Parse(running ? "M 6,4 H 10 V 20 H 6 Z M 14,4 H 18 V 20 H 14 Z" : "M 7,4 L 21,12 L 7,20 Z") };
        return new Viewbox { Width = 18, Height = 18, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }
}

