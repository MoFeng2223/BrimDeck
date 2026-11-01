// WakeSlider.tsx / WakeSlider.css, Edge-Drop 767b6d63 (Apache-2.0), translated to a native WPF Slider.
// Settings uses the upstream 28 bars, 28/8 DIP heights, 3 DIP gaps and velocity-driven asymmetric wake.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BrimDeck;

internal sealed class EdgeDropWakeSlider : Slider
{
    private const int Bars = 28;
    private const double HeightOfBar = 28, RestHeight = 8, Gap = 3;
    private const double Settle = 9.23, FullSpeed = 320, MinReach = 1.5, Reach = 6, Skew = .6, Glide = .3, Smoothing = 100;
    private readonly List<(Border Bar, Border Crest, ScaleTransform Scale)> _bars = [];
    private Grid? _track;
    private bool _dragging, _rendering;
    private TouchDevice? _touch;
    private double? _pendingRatio;
    private double _target, _head, _headVelocity, _speed, _speedVelocity, _frameAt;
    internal event Action<double>? ValueCommitted;

    internal EdgeDropWakeSlider()
    {
        Minimum = 0; Maximum = 100; TickFrequency = 1; SmallChange = 1; LargeChange = 10;
        Focusable = true; IsTabStop = true; Height = 44; MinHeight = 44; Cursor = Cursors.Hand;
        SetResourceReference(FocusVisualStyleProperty, "SettingsFocus");
        var track = new FrameworkElementFactory(typeof(Grid), "PART_WakeTrack");
        track.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        Template = new ControlTemplate(typeof(EdgeDropWakeSlider)) { VisualTree = track };
        Loaded += (_, _) => { _head = _target = Percentage(Value); Paint(); };
        Unloaded += (_, _) => { EndDrag(); StopFrames(); };
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : .5; if (!IsEnabled) EndDrag(); };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); _bars.Clear();
        _track = (Grid)GetTemplateChild("PART_WakeTrack"); _track.Children.Clear(); _track.ColumnDefinitions.Clear();
        for (int i = 0; i < Bars; i++)
        {
            if (i > 0) _track.ColumnDefinitions.Add(new() { Width = new GridLength(Gap) });
            _track.ColumnDefinitions.Add(new());
            var crest = new Border { CornerRadius = new CornerRadius(9999), Opacity = 0 };
            crest.SetResourceReference(Border.BackgroundProperty, "SettingsSliderAccent");
            var scale = new ScaleTransform(1, RestHeight / HeightOfBar);
            var bar = new Border { Child = crest, Height = HeightOfBar, CornerRadius = new CornerRadius(9999),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale, IsHitTestVisible = false };
            Grid.SetColumn(bar, i * 2); _track.Children.Add(bar); _bars.Add((bar, crest, scale));
        }
        Paint();
    }
    private double Percentage(double value) => Maximum > Minimum ? (value - Minimum) / (Maximum - Minimum) * 100 : 0;
    // The non-divisible upper endpoint is still reachable, as in upstream snap().
    internal static double Snap(double value, double min, double max, double step)
    {
        if (max <= min) return min;
        if (step <= 0) return Math.Clamp(value, min, max);
        double last = min + Math.Floor(Math.Round((max - min) / step, 6)) * step;
        double grid = Math.Clamp(Math.Floor((value - min) / step + .5) * step + min, min, last);
        return Math.Round(last < max && Math.Abs(value - max) <= Math.Abs(value - grid) ? max : grid, 6);
    }
    private void CommitFromRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1); _target = ratio * 100;
        SetCurrentValue(ValueProperty, Snap(Minimum + ratio * (Maximum - Minimum), Minimum, Maximum, TickFrequency));
        Paint(); StartFrames();
    }
    private double Ratio(Point point)
    {
        double ratio = _track is { ActualWidth: > 0 } ? point.X / _track.ActualWidth : 0;
        return FlowDirection == FlowDirection.RightToLeft ? 1 - ratio : ratio;
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled || _dragging) return;
        Focus(); if (!CaptureMouse()) return;
        _dragging = true; CommitFromRatio(Ratio(e.GetPosition(_track))); e.Handled = true;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_dragging || _touch is not null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        _pendingRatio = Ratio(e.GetPosition(_track)); StartFrames(); e.Handled = true;
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging || _touch is not null) return;
        _pendingRatio = Ratio(e.GetPosition(_track)); EndDrag(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if (_touch is null) EndDrag(); base.OnLostMouseCapture(e); }
    protected override void OnPreviewTouchDown(TouchEventArgs e)
    {
        if (!IsEnabled || _dragging) return;
        Focus(); if (!CaptureTouch(e.TouchDevice)) return;
        _touch = e.TouchDevice; _dragging = true; CommitFromRatio(Ratio(e.GetTouchPoint(_track).Position)); e.Handled = true;
    }
    protected override void OnPreviewTouchMove(TouchEventArgs e)
    {
        if (!_dragging || _touch != e.TouchDevice) return;
        _pendingRatio = Ratio(e.GetTouchPoint(_track).Position); StartFrames(); e.Handled = true;
    }
    protected override void OnPreviewTouchUp(TouchEventArgs e)
    {
        if (_touch != e.TouchDevice) return;
        _pendingRatio = Ratio(e.GetTouchPoint(_track).Position); EndDrag(); e.Handled = true;
    }
    protected override void OnLostTouchCapture(TouchEventArgs e) { if (_touch == e.TouchDevice) EndDrag(); base.OnLostTouchCapture(e); }
    private void EndDrag()
    {
        if (!_dragging) return;
        if (_pendingRatio is { } ratio) { _pendingRatio = null; CommitFromRatio(ratio); }
        _dragging = false; var touch = _touch; _touch = null;
        if (touch is not null) ReleaseTouchCapture(touch); else if (IsMouseCaptured) ReleaseMouseCapture();
        _head = _target; _headVelocity = 0; _target = Percentage(Value); StartFrames();
        ValueCommitted?.Invoke(Value);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        double direction = FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
        double? next = e.Key switch
        {
            Key.Right => Value + direction * SmallChange, Key.Left => Value - direction * SmallChange,
            Key.Up => Value + SmallChange, Key.Down => Value - SmallChange,
            Key.PageUp => Value + LargeChange, Key.PageDown => Value - LargeChange,
            Key.Home => Minimum, Key.End => Maximum, _ => null
        };
        if (next is null) { base.OnPreviewKeyDown(e); return; }
        SetCurrentValue(ValueProperty, Snap(next.Value, Minimum, Maximum, TickFrequency));
        _head = _target = Percentage(Value); _headVelocity = _speed = _speedVelocity = 0;
        StopFrames(); Paint(); e.Handled = true;
    }
    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (!_dragging) { _target = Percentage(newValue); StartFrames(); ValueCommitted?.Invoke(newValue); }
        Paint();
    }
    private void StartFrames()
    {
        if (!IsLoaded) return;
        if (!Soft.Enabled && _pendingRatio is null) { _head = _target; _headVelocity = _speed = _speedVelocity = 0; Paint(); StopFrames(); return; }
        if (_rendering) return;
        _rendering = true; _frameAt = Environment.TickCount64 / 1000d; CompositionTarget.Rendering += Frame;
    }
    private void StopFrames() { if (_rendering) CompositionTarget.Rendering -= Frame; _rendering = false; }
    private void Frame(object? sender, EventArgs args)
    {
        if (_pendingRatio is { } ratio) { _pendingRatio = null; CommitFromRatio(ratio); }
        double now = Environment.TickCount64 / 1000d, dt = Math.Clamp(now - _frameAt, 0, .1); _frameAt = now;
        // Same two springs as useSpring(target) and useSpring(useVelocity(head)); substeps bound stalled frames.
        while (dt > 0)
        {
            double h = Math.Min(dt, 1 / 240d), w = Settle / Glide, velocityW = 2000 / Smoothing;
            _headVelocity += (-w * w * (_head - _target) - 2 * w * _headVelocity) * h; _head += _headVelocity * h;
            _speedVelocity += (-velocityW * velocityW * (_speed - _headVelocity) - 2.5 * velocityW * _speedVelocity) * h; _speed += _speedVelocity * h;
            dt -= h;
        }
        if (!Soft.Enabled || Math.Abs(_head - _target) < .002 && Math.Abs(_headVelocity) < .02 && Math.Abs(_speed) < .02)
        { _head = _target; _headVelocity = _speed = _speedVelocity = 0; StopFrames(); }
        Paint();
    }
    private void Paint()
    {
        double head = (Soft.Enabled && !_dragging ? _head : _target) / 100 * (Bars - 1);
        double speed = Soft.Enabled ? _speed : 0, s = Math.Clamp(Math.Abs(speed) / FullSpeed, 0, 1);
        double amp = s * s * (3 - 2 * s), reach = MinReach + (Reach - MinReach) * amp;
        int lit = (int)Math.Floor(head + .5); double direction = speed < 0 ? -1 : 1;
        for (int i = 0; i < _bars.Count; i++)
        {
            var (bar, crest, scale) = _bars[i]; double distance = i - head;
            double radius = reach * (distance * direction < 0 ? 1 + Skew : 1 - .5 * Skew);
            double lift = Math.Abs(distance) < radius ? amp * Math.Pow(Math.Cos(Math.PI * distance / (2 * radius)), 2) : 0;
            scale.ScaleY = RestHeight / HeightOfBar + lift * (1 - RestHeight / HeightOfBar); crest.Opacity = lift;
            bool on = i <= lit;
            if (bar.Tag is not bool previous || previous != on) { bar.Tag = on; bar.SetResourceReference(Border.BackgroundProperty, on ? "SettingsTextPrimary" : "SettingsTrackBrush"); }
        }
    }
}
