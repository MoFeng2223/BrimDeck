using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BrimDeck;

// Uses the host's snapshot and motion curves. History changes immediately; fragments never retain a live label or input target.
internal sealed class ApplicationLabelMotion : IDisposable
{
    private readonly Grid _owner;
    private readonly Panel _labels;
    private readonly Canvas _layer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly List<(FrameworkElement Element, TranslateTransform Transform, Transform Previous)> _moving = [];
    private readonly DispatcherTimer _cleanup = new() { Interval = TimeSpan.FromMilliseconds(Soft.Move + 80) };
    private DispatcherOperation? _pending;
    private bool _disposed;
    internal int FragmentCount => _layer.Children.Count;

    internal ApplicationLabelMotion(Grid owner, Panel labels)
    {
        _owner = owner; _labels = labels;
        Grid.SetRowSpan(_layer, Math.Max(1, owner.RowDefinitions.Count)); Grid.SetColumnSpan(_layer, Math.Max(1, owner.ColumnDefinitions.Count));
        Panel.SetZIndex(_layer, 8); owner.Children.Add(_layer);
        AutomationProperties.SetAutomationId(_layer, "clipboard-application-fragments");
        owner.IsVisibleChanged += VisibilityChanged;
        owner.Unloaded += Unloaded; owner.SizeChanged += Resized;
        _cleanup.Tick += (_, _) => Clear();
    }
    private static string Id(FrameworkElement element) => AutomationProperties.GetAutomationId(element);
    private Point Position(FrameworkElement element) => element.TranslatePoint(new Point(), _owner);
    internal void Remove(FrameworkElement target, Action change)
    {
        Clear();
        if (_disposed || !Soft.For(_owner) || !_owner.IsVisible || !target.IsVisible || PresentationSource.FromVisual(target) is null) { change(); return; }
        var positions = _labels.Children.OfType<FrameworkElement>().Where(element => Id(element).StartsWith("clipboard-application-", StringComparison.Ordinal))
            .ToDictionary(Id, Position, StringComparer.Ordinal);
        Point at = Position(target); double width = target.ActualWidth, height = target.ActualHeight;
        var visible = Rect.Intersect(new Rect(at, new Size(width, height)), new Rect(_owner.RenderSize));
        for (DependencyObject? parent = VisualTreeHelper.GetParent(target); parent is not null && parent != _owner; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) visible.Intersect(new Rect(Position(scroll), scroll.RenderSize));
        if (width > 0 && height > 0 && !visible.IsEmpty)
        {
            var picture = PageHandover.Snapshot(target, width, height, VisualTreeHelper.GetDpi(target));
            const int columns = 7, rows = 3;
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var bounds = Rect.Intersect(new Rect(at.X + width * column / columns, at.Y + height * row / rows, width / columns, height / rows), visible);
                if (bounds.IsEmpty || bounds.Width < .5 || bounds.Height < .5) continue;
                var brush = new ImageBrush(picture) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect((bounds.X - at.X) / width, (bounds.Y - at.Y) / height, bounds.Width / width, bounds.Height / height), Stretch = Stretch.Fill };
                var shard = new Rectangle { Width = bounds.Width, Height = bounds.Height, Fill = brush, RenderTransformOrigin = new Point(.5, .5) };
                var shift = new TranslateTransform(); var turn = new RotateTransform(); var shrink = new ScaleTransform(1, 1);
                shard.RenderTransform = new TransformGroup { Children = { shrink, turn, shift } };
                Canvas.SetLeft(shard, bounds.X); Canvas.SetTop(shard, bounds.Y); _layer.Children.Add(shard);
                double spread = column - (columns - 1) / 2d, direction = ((row + column) % 2 == 0 ? 1 : -1);
                shift.BeginAnimation(TranslateTransform.XProperty, Soft.Fade(0, spread * 9 + direction * 5, Soft.Move, Soft.Out));
                shift.BeginAnimation(TranslateTransform.YProperty, Soft.Fade(0, (row - 1) * 12 + 12 + Math.Abs(spread) * 2, Soft.Move, Soft.Out));
                turn.BeginAnimation(RotateTransform.AngleProperty, Soft.Fade(0, direction * (12 + column * 4), Soft.Move, Soft.Out));
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, Soft.Fade(1, .25, Soft.Move, Soft.InOut));
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, Soft.Fade(1, .25, Soft.Move, Soft.InOut));
                shard.BeginAnimation(UIElement.OpacityProperty, Soft.Fade(1, 0, Soft.Move, Soft.InOut));
            }
        }
        change();
        _pending = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _pending = null;
            if (_disposed || !_owner.IsVisible || !Soft.For(_owner)) { Clear(); return; }
            foreach (var element in _labels.Children.OfType<FrameworkElement>())
            {
                if (!positions.TryGetValue(Id(element), out var old)) continue;
                Point next = Position(element); Vector delta = old - next;
                if (Math.Abs(delta.X) < .5 && Math.Abs(delta.Y) < .5) continue;
                var shift = new TranslateTransform(); var previous = element.RenderTransform;
                element.RenderTransform = new TransformGroup { Children = { previous, shift } }; _moving.Add((element, shift, previous));
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(Soft.Move)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(Soft.Move)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
            }
        });
        _cleanup.Start();
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!_owner.IsVisible) Clear(); }
    private void Unloaded(object sender, RoutedEventArgs args) => Clear();
    private void Resized(object sender, SizeChangedEventArgs args) => Clear();
    internal void Clear()
    {
        _pending?.Abort(); _pending = null; _cleanup.Stop();
        foreach (var shard in _layer.Children.OfType<Rectangle>())
        {
            shard.BeginAnimation(UIElement.OpacityProperty, null);
            if (shard.RenderTransform is TransformGroup group) foreach (var transform in group.Children)
            {
                if (transform is TranslateTransform shift) { shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null); }
                else if (transform is RotateTransform turn) turn.BeginAnimation(RotateTransform.AngleProperty, null);
                else if (transform is ScaleTransform scale) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); }
            }
            shard.Fill = null;
        }
        _layer.Children.Clear();
        foreach (var (element, transform, previous) in _moving)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null); transform.BeginAnimation(TranslateTransform.YProperty, null);
            if (element.RenderTransform is TransformGroup group && group.Children.Contains(transform)) element.RenderTransform = previous;
        }
        _moving.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Clear(); _owner.IsVisibleChanged -= VisibilityChanged;
        _owner.Unloaded -= Unloaded; _owner.SizeChanged -= Resized; _owner.Children.Remove(_layer);
    }
}
