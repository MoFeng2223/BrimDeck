using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DropShelf.Models;
using DropShelf.Services;

namespace DropShelf.Views.Controls;

/// <summary>不确定进度的环形指示器。</summary>
public class ProgressRing : Control
{
    private RotateTransform? _rotation;

    static ProgressRing()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(typeof(ProgressRing)));
    }

    public ProgressRing()
    {
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => ThemeService.ThemeChanged += OnMotionChanged;
        Unloaded += (_, _) => { ThemeService.ThemeChanged -= OnMotionChanged; _rotation?.BeginAnimation(RotateTransform.AngleProperty, null); };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rotation = GetTemplateChild("PART_Rotation") as RotateTransform;
        UpdateAnimation();
    }

    private void OnMotionChanged(object? sender, EventArgs e) => UpdateAnimation();

    private void UpdateAnimation()
    {
        if (_rotation is null) return;
        if (IsVisible && ShelfMotion.Enabled)
            _rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
