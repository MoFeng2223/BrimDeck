using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// Add an entry here to introduce a new animation without changing scrolling. Its place in All says which TransferScrollEffectId it is.
internal sealed record TransferScrollEffect(string Name, int Duration, bool CenterEdges,
    Func<IEasingFunction> Ease, Action<Border, double, bool> Paint);
internal static class TransferScrollEffects
{
    public static readonly IReadOnlyList<TransferScrollEffect> All =
    [
        new("中心聚焦（默认）", 480, true, () => new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
            (card, distance, enabled) =>
            {
                double d = enabled ? Math.Min(1, Math.Abs(distance)) : 0;
                var transforms = new TransformGroup();
                // Only height shrinks at the edges; width stays full so the gap between items never grows unevenly.
                transforms.Children.Add(new ScaleTransform(1, 1 - d * .5));
                transforms.Children.Add(new TranslateTransform(0, d * 6));
                card.RenderTransform = transforms;
                card.Opacity = 1 - d * .5;
            }),
        new("磁吸卡片", 440, false, () => new BackEase { Amplitude = .4, EasingMode = EasingMode.EaseOut },
            (_, _, _) => { }),
        new("视差深度", 620, false, () => new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
            (card, distance, enabled) =>
            {
                if (card.Child is FrameworkElement body)
                    body.RenderTransform = new TranslateTransform(enabled ? Math.Clamp(distance, -1, 1) * -7 : 0, 0);
            })
    ];
    public static TransferScrollEffect For(TransferScrollEffectId id) => All[(int)id];
}

internal sealed class TransferScrollViewer : ScrollViewer
{
    private readonly TransferScrollEffect _effect;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private static readonly DependencyProperty PositionProperty = DependencyProperty.Register("Position", typeof(double),
        typeof(TransferScrollViewer), new PropertyMetadata(0d, (o, e) =>
        {
            var view = (TransferScrollViewer)o;
            view.ScrollToHorizontalOffset(Math.Clamp((double)e.NewValue, 0, view.ScrollableWidth));
        }));
    internal Func<bool>? IsLayoutResizing { get; init; }
    private bool _moving, _pressed;
    private double _target;
    public TransferScrollViewer(TransferScrollEffectId effectId)
    {
        _effect = TransferScrollEffects.For(effectId);
        Focusable = true;
        PanningMode = PanningMode.HorizontalOnly;
        Loaded += (_, _) => Refresh();
        SizeChanged += (_, _) => { Stop(); Refresh(); };
        ScrollChanged += (_, e) =>
        {
            Refresh();
            bool geometryChanged = e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0
                || e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0;
            if (geometryChanged || IsLayoutResizing?.Invoke() == true) Stop();
            else if (!_moving && !_pressed && e.HorizontalChange != 0) { _settle.Stop(); _settle.Start(); }
        };
        _settle.Tick += (_, _) => { _settle.Stop(); if (!_pressed && IsLayoutResizing?.Invoke() != true) Move(Nearest(HorizontalOffset)); };
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (e.Delta == 0) return;
            Step(e.Delta < 0 ? 1 : -1);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) { Step(e.Key == Key.Right ? 1 : -1); e.Handled = true; }
        };
        PreviewMouseDown += (_, _) => { _pressed = true; Stop(); };
        PreviewMouseUp += (_, _) => { _pressed = false; _settle.Start(); };
        LostMouseCapture += (_, _) => { _pressed = false; _settle.Start(); };
        PreviewTouchDown += (_, _) => { _pressed = true; Stop(); };
        PreviewTouchUp += (_, _) => { _pressed = false; _settle.Start(); };
        Unloaded += (_, _) => { Stop(); _pressed = false; };
    }
    private List<double> Stops()
    {
        if (Content is not StackPanel row || !row.IsArrangeValid) return [];
        return row.Children.OfType<Border>().Select(card =>
            Math.Clamp(card.TranslatePoint(new Point(card.ActualWidth / 2, 0), row).X
                + row.Margin.Left - ViewportWidth / 2, 0, ScrollableWidth)).Distinct().Order().ToList();
    }
    private double Nearest(double value) => Stops().OrderBy(stop => Math.Abs(stop - value)).FirstOrDefault(value);
    private void Step(int direction)
    {
        var stops = Stops();
        if (stops.Count == 0) return;
        double at = _moving ? _target : HorizontalOffset;
        double next = direction > 0 ? stops.FirstOrDefault(stop => stop > at + .5, stops[^1])
            : stops.LastOrDefault(stop => stop < at - .5, stops[0]);
        Move(next);
    }
    private void Move(double target)
    {
        double from = HorizontalOffset;
        Stop();
        _target = Math.Clamp(target, 0, ScrollableWidth);
        if (Math.Abs(from - _target) < .5 || !Soft.Enabled) { ScrollToHorizontalOffset(_target); return; }
        SetValue(PositionProperty, from);
        _moving = true;
        var motion = new DoubleAnimation(from, _target, TimeSpan.FromMilliseconds(_effect.Duration))
        { EasingFunction = _effect.Ease() };
        motion.Completed += (_, _) => { double end = _target; Stop(); ScrollToHorizontalOffset(end); };
        BeginAnimation(PositionProperty, motion);
    }
    private void Stop()
    {
        _settle.Stop();
        double at = HorizontalOffset;
        BeginAnimation(PositionProperty, null);
        SetValue(PositionProperty, at);
        _moving = false;
    }
    private void Refresh()
    {
        if (Content is not StackPanel row || ViewportWidth <= 0) return;
        var cards = row.Children.OfType<Border>().ToList();
        if (cards.Count == 0) return;
        double pad = _effect.CenterEdges ? Math.Max(0, (ViewportWidth - cards[0].ActualWidth - 12) / 2) : 0;
        var margin = new Thickness(pad, 0, pad, 0);
        if (row.Margin != margin) row.Margin = margin;
        if (!row.IsArrangeValid) return;
        foreach (var card in cards)
        {
            // Use layout coordinates; animated transforms must not feed back into distance.
            double center = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(card).X
                + card.Margin.Left + card.ActualWidth / 2 + pad
                + Math.Max(0, (ViewportWidth - row.ActualWidth - 2 * pad) / 2) - HorizontalOffset;
            double distance = (center - ViewportWidth / 2) / Math.Max(1, ViewportWidth / 2);
            card.RenderTransformOrigin = new Point(.5, .5);
            _effect.Paint(card, distance, Soft.Enabled);
        }
    }
}