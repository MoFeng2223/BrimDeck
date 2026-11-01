using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BrimDeck;

// Hover and press feedback that fades in and out as the system monitor's does (Soft.Hover on Soft.Follow) instead of
// switching in one frame. A button style sets
// Feedback.Fade; its template has a "HoverLayer" and may have a "PressLayer", both transparent at rest, beneath the content,
// or, for a bare icon (PanelGlyphButton), a "Glyph" whose opacity carries the feedback instead.
// The press layer lies over the hover layer, which stays lit while pressed, so the two never dip in between. The state is
// read after the button's own handlers have run, so IsPressed is already current.
internal static class Feedback
{
    public static readonly DependencyProperty FadeProperty = DependencyProperty.RegisterAttached(
        "Fade", typeof(bool), typeof(Feedback), new PropertyMetadata(false, FadeChanged));
    public static bool GetFade(DependencyObject element) => (bool)element.GetValue(FadeProperty);
    public static void SetFade(DependencyObject element, bool value) => element.SetValue(FadeProperty, value);

    private static readonly MouseButtonEventHandler ButtonHandler = (sender, _) => Update(sender);
    private static readonly MouseEventHandler PointerHandler = (sender, _) => Update(sender);
    private static readonly MouseEventHandler MoveHandler = (sender, _) => { if (sender is UIElement { IsMouseCaptured: true }) Update(sender); };
    private static readonly DependencyPropertyChangedEventHandler EnabledHandler = (sender, _) => Update(sender);
    private static readonly RoutedEventHandler LoadedHandler = (sender, _) => Update(sender, initialize: true);

    private static void FadeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button) return;
        button.MouseEnter -= PointerHandler; button.MouseLeave -= PointerHandler; button.MouseMove -= MoveHandler;
        button.LostMouseCapture -= PointerHandler; button.IsEnabledChanged -= EnabledHandler; button.Loaded -= LoadedHandler;
        button.RemoveHandler(UIElement.MouseLeftButtonDownEvent, ButtonHandler); button.RemoveHandler(UIElement.MouseLeftButtonUpEvent, ButtonHandler);
        if (!(bool)e.NewValue) return;
        button.MouseEnter += PointerHandler; button.MouseLeave += PointerHandler; button.MouseMove += MoveHandler;
        button.LostMouseCapture += PointerHandler; button.IsEnabledChanged += EnabledHandler; button.Loaded += LoadedHandler;
        if (button.IsLoaded) Update(button, initialize: true);
        // The button handles its own presses; handledEventsToo lets the feedback see them after it has.
        button.AddHandler(UIElement.MouseLeftButtonDownEvent, ButtonHandler, true); button.AddHandler(UIElement.MouseLeftButtonUpEvent, ButtonHandler, true);
    }

    private static void Update(object sender, bool initialize = false)
    {
        if (sender is not ButtonBase { Template: { } template } button) return;
        bool enabled = button.IsEnabled, animate = !initialize && Soft.For(button) && button.IsVisible;
        bool over = enabled && button.IsMouseOver, pressed = enabled && button.IsPressed;
        Show(template.FindName("HoverLayer", button) as UIElement, over ? 1 : 0, animate, initialize);
        Show(template.FindName("PressLayer", button) as UIElement, pressed ? 1 : 0, animate, initialize);
        Show(template.FindName("Glyph", button) as UIElement, pressed ? GlyphPressed : over ? 1 : GlyphRest, animate, initialize);
    }
    private const double GlyphRest = .78, GlyphPressed = .6;

    private static void Show(UIElement? layer, double target, bool animate, bool initialize)
    {
        if (layer is null) return;
        double from = layer.Opacity;
        // Local values let the host own fading even when an imported template has instant opacity triggers.
        if (!initialize && Math.Abs(from - target) < .01 && !layer.HasAnimatedProperties) return;
        layer.BeginAnimation(UIElement.OpacityProperty, null);
        layer.Opacity = target;
        if (animate) layer.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(from, target,
            TimeSpan.FromMilliseconds(Soft.Hover)) { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
}
