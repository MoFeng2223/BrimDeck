using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BrimDeck;

// One timer/generation per surface. Replacing a message never queues an older dismissal over the newer result.
internal sealed class QuietNotice : Border
{
    internal TextBlock Label { get; } = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36 };
    internal DispatcherTimer Timer { get; } = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _generation;
    internal QuietNotice(string id)
    {
        HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(14, 0, 14, 12); Padding = new Thickness(12, 6, 12, 6); BorderThickness = new Thickness(1);
        Visibility = Visibility.Collapsed; IsHitTestVisible = false; Focusable = false;
        SetResourceReference(BackgroundProperty, "PanelPopup"); SetResourceReference(BorderBrushProperty, "PanelPopupBorder"); SetResourceReference(CornerRadiusProperty, "PanelR16");
        Label.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary"); Child = Label;
        AutomationProperties.SetAutomationId(this, id); AutomationProperties.SetLiveSetting(Label, AutomationLiveSetting.Polite);
        Timer.Tick += (_, _) => Hide(true);
        IsVisibleChanged += (_, _) => { if (!IsVisible) Hide(); };
        Unloaded += (_, _) => Hide();
    }
    internal void Fit(double width) => MaxWidth = Math.Max(0, Math.Min(520, width - Margin.Left - Margin.Right));
    internal void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ++_generation; Timer.Stop(); BeginAnimation(OpacityProperty, null); Opacity = 1; RenderTransform = Transform.Identity;
        Label.Text = message; Visibility = Visibility.Visible;
        if (IsVisible) Soft.Enter(this, 4);
        Timer.Start();
    }
    internal void Hide(bool animate = false)
    {
        Timer.Stop(); int generation = ++_generation;
        void Clear()
        {
            if (generation != _generation) return;
            Visibility = Visibility.Collapsed; Label.Text = ""; BeginAnimation(OpacityProperty, null); Opacity = 1; RenderTransform = Transform.Identity;
        }
        if (animate && Soft.For(this) && IsVisible) Soft.Leave(this, Clear); else Clear();
    }
}
