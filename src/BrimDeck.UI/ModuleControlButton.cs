using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BrimDeck;

public class ModuleControlButton : Button
{
    private bool _animationsEnabled;
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set { if (_animationsEnabled == value) return; _animationsEnabled = value; UpdateState(); }
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) UpdateState();
    }
    public override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateState(); }
    private void UpdateState()
    {
        if (Template?.FindName("Hover", this) is Ellipse hover)
        {
            double opacity = !IsEnabled ? 0 : IsPressed ? .16 : (bool)GetValue(IsMouseOverProperty) ? .10 : 0;
            Animate(hover, OpacityProperty, opacity, AnimationsEnabled, Soft.Hover);
        }
        if (Template?.FindName("ControlScale", this) is ScaleTransform scale)
        {
            double value = IsEnabled && IsPressed ? .94 : 1;
            Animate(scale, ScaleTransform.ScaleXProperty, value, false, 0);
            Animate(scale, ScaleTransform.ScaleYProperty, value, false, 0);
        }
    }
    internal static void Animate(Animatable target, DependencyProperty property, double value, bool animate, int milliseconds)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null); target.SetValue(property, value);
        if (animate && from != value) target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }
    private static void Animate(UIElement target, DependencyProperty property, double value, bool animate, int milliseconds)
    {
        double from = (double)target.GetValue(property);
        target.BeginAnimation(property, null); target.SetValue(property, value);
        if (animate && from != value) target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
    }
}

