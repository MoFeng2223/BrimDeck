using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public static partial class ModuleUi
{
    public static FontFamily PanelFont => UI.PanelFont;
    public static string Secondary => UI.Secondary;
    public static string Tertiary => UI.Tertiary;
    public static string OnAccent => PanelTheme.Hex("onAccent");
    public static string FillSelected => UI.FillSelected;
    public static Brush Brush(string color) => UI.Brush(color);
    public static System.Windows.Controls.TextBlock Centered(string text, double size, string color)
    {
        var result = UI.Centered(text, size, color);
        string? token = color == UI.Primary ? "PanelPrimary" : color == UI.Secondary ? "PanelSecondary" : color == UI.Tertiary ? "PanelTertiary" : color == UI.Accent ? "PanelAccent" : null;
        if (token is not null) result.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, token);
        return result;
    }
}

// These adapters share the host's implementation. Modules never need friend-assembly access.
public static class ModuleTheme
{
    public static bool Dark => PanelTheme.Current.IsDark;
    public static int Generation => PanelTheme.Generation;
}

public static class ModuleLocalization
{
    public static string Language => Loc.Language;
    public static string T(string chinese, string english) => Loc.T(chinese, english);
}

public static class ModuleMotion
{
    public static bool Enabled => Soft.Enabled;
    public const int Quick = Soft.Quick;
    public static void Animate(Animatable target, DependencyProperty property, double value, bool animate, int milliseconds)
        => ModuleControlButton.Animate(target, property, value, animate && Soft.Enabled, milliseconds);
}
