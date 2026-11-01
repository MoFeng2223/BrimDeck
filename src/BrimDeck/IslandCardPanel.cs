using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// WPF adaptation of Edge-Drop's stable keyed ItemList: virtualize bounded rows rather than an unbounded WrapPanel.
// The items remain ClipboardEntry containers, preserving selection, accessibility and keyboard behavior.
internal sealed class IslandCardPanel : VirtualizingPanel, IScrollInfo
{
    internal static readonly RoutedEvent ReflowingEvent = EventManager.RegisterRoutedEvent("Reflowing", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(IslandCardPanel));
    public IslandCardPanel() { }
    internal static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(IslandCardPanel), new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    internal static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(nameof(CellHeight), typeof(double), typeof(IslandCardPanel), new FrameworkPropertyMetadata(150d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double CellWidth { get => (double)GetValue(CellWidthProperty); set => SetValue(CellWidthProperty, value); }
    public double CellHeight { get => (double)GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }
    private readonly List<Rect> _slots = [];
    private Size _extent, _viewport;
    private Point _offset;
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return default;
        double width = double.IsFinite(available.Width) ? Math.Max(1, available.Width) : CellWidth;
        double height = double.IsFinite(available.Height) ? Math.Max(1, available.Height) : CellHeight;
        bool reflow = _viewport.Width > 0 && Math.Abs(width - _viewport.Width) > .1;
        int anchor = reflow ? _slots.FindIndex(slot => slot.Bottom > _offset.Y + .1) : -1;
        double anchorTop = anchor >= 0 ? _slots[anchor].Top - _offset.Y : 0;
        if (reflow) RaiseEvent(new RoutedEventArgs(ReflowingEvent, this));
        // CellWidth is the minimum pitch. Share leftover space and compensate the trailing container margin,
        // so a full row ends at the viewport edge instead of leaving an unused fixed-width column.
        int columns = Math.Max(1, (int)Math.Floor((width + ClipboardDockLayout.CardGap) / CellWidth));
        double cellWidth = (width + ClipboardDockLayout.CardGap) / columns;
        _slots.Clear();
        // Dock cards have no date heading; hidden date boundaries must not leave holes in time order.
        for (int i = 0; i < owner.Items.Count; i++)
            _slots.Add(new Rect(i % columns * cellWidth, i / columns * CellHeight, cellWidth, CellHeight));
        _viewport = new Size(width, height); _extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * CellHeight);
        // A column change keeps the leading entry in view instead of reinterpreting its old row offset.
        if (anchor >= 0 && anchor < _slots.Count) _offset.Y = _slots[anchor].Top - anchorTop;
        _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();
        int first = _slots.FindIndex(slot => slot.Bottom >= _offset.Y - CellHeight);
        int last = _slots.FindLastIndex(slot => slot.Top <= _offset.Y + height + CellHeight);
        // WPF connects the generator when InternalChildren initializes the items host.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (int childIndex = children.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0); int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last && first >= 0) continue;
            if (generator is IRecyclingItemContainerGenerator recycler) recycler.Recycle(position, 1); else generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
        if (first >= 0 && last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            int childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool fresh);
                    if (fresh || childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                    { InsertInternalChild(childIndex, child); generator.PrepareItemContainer(child); }
                    child.Measure(new Size(cellWidth, CellHeight));
                }
        }
        return _viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _slots.Count) continue;
            var slot = _slots[index]; slot.Offset(-_offset.X, -_offset.Y); InternalChildren[i].Arrange(slot);
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
        {
            // A move's destination can be unrealized (Position.Index == -1); only its old containers still exist here.
            int index = args.Action == NotifyCollectionChangedAction.Move ? args.OldPosition.Index : args.Position.Index;
            if (args.ItemUICount > 0) RemoveInternalChildRange(index, args.ItemUICount);
        }
        else if (args.Action == NotifyCollectionChangedAction.Reset) RemoveInternalChildRange(0, InternalChildren.Count);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _slots.Count) return;
        var slot = _slots[index]; if (slot.Top < VerticalOffset) SetVerticalOffset(slot.Top); else if (slot.Bottom > VerticalOffset + ViewportHeight) SetVerticalOffset(slot.Bottom - ViewportHeight);
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        if (!double.IsFinite(offset)) offset = offset > 0 ? ExtentHeight : 0;
        double next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(next - _offset.Y) < .1) return; _offset.Y = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(VerticalOffset - CellHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + CellHeight);
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (ItemsControl.ContainerFromElement(ItemsControl.GetItemsOwner(this), visual) is DependencyObject container)
        { int index = ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(container); BringIndexIntoView(index); }
        return rectangle;
    }
}

internal sealed class ClipboardTimelineCard : Grid
{
    internal static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(nameof(HeaderHeight), typeof(double), typeof(ClipboardTimelineCard), new PropertyMetadata(20d, (d, e) => ((ClipboardTimelineCard)d)._heading.Height = (double)e.NewValue));
    public double HeaderHeight { get => (double)GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }
    private readonly TextBlock _heading = ClipboardPreview.Text("", 10.5, "PanelTertiary");
    public ClipboardTimelineCard()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        _heading.Height = HeaderHeight; _heading.Margin = new Thickness(4, 0, 4, 0); _heading.TextWrapping = TextWrapping.NoWrap; Children.Add(_heading);
        Loaded += (_, _) => RefreshHeading(); DataContextChanged += (_, _) => RefreshHeading();
    }
    internal void RefreshHeading()
    {
        if (DataContext is not ClipboardEntry entry) return;
        var list = FindList(this); int index = list?.Items.IndexOf(entry) ?? -1;
        bool first = index <= 0 || list!.Items[index - 1] is not ClipboardEntry before || before.CopiedAt.LocalDateTime.Date != entry.CopiedAt.LocalDateTime.Date;
        _heading.Text = first ? new ClipboardTimelineGroup(DateOnly.FromDateTime(entry.CopiedAt.LocalDateTime), []).Label(DateTime.Now) : "";
    }
    private static ListBox? FindList(DependencyObject node)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent)) if (parent is ListBox list) return list;
        return null;
    }
}
