// Edge-Drop stable keyed ItemList adapted to WPF: sort is a projection of the single history model.
// The fixed upstream has hitCount but no count-sort control; this menu is the requested host extension.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    internal static readonly DependencyProperty SortOrderProperty = DependencyProperty.Register(nameof(SortOrder), typeof(ClipboardSort), typeof(ClipboardView), new PropertyMetadata(ClipboardSort.Time));
    public ClipboardSort SortOrder { get => (ClipboardSort)GetValue(SortOrderProperty); set => SetValue(SortOrderProperty, value); }
    private readonly Action<ClipboardSort>? _setSort;
    private Button _sortButton = null!;
    private int _reorderGeneration;
    private void ShowSort()
    {
        var menu = Menu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_sortButton.ContextMenu, menu)) _sortButton.ContextMenu = null; };
        foreach (var order in Enum.GetValues<ClipboardSort>())
        {
            var item = new MenuItem { Header = order == ClipboardSort.Time ? Loc.T("按时间排序 · 最新优先", "Sort by time · Newest first") : Loc.T("按复制次数排序 · 最多优先", "Sort by copy count · Highest first"),
                IsCheckable = true, IsChecked = SortOrder == order, Icon = SortOrder == order ? "✓" : null, Style = (Style)FindResource("TrayMenuItem") };
            AutomationProperties.SetAutomationId(item, order == ClipboardSort.Time ? "clipboard-sort-time" : "clipboard-sort-count");
            item.Click += (_, e) => { menu.IsOpen = false; SetSort(order); e.Handled = true; }; menu.Items.Add(item);
        }
        _sortButton.ContextMenu = menu; menu.PlacementTarget = _sortButton; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void SetSort(ClipboardSort order)
    {
        if (SortOrder == order) return;
        SortOrder = order; Refresh(); _setSort?.Invoke(order);
    }
    private Dictionary<string, Point>? CaptureReorder(List<ClipboardEntry> results)
    {
        if (!IsVisible || results.Count != _visible.Count || results.Select(entry => entry.Id).SequenceEqual(_visible.Select(entry => entry.Id))) return null;
        var ids = results.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        if (_visible.Any(entry => !ids.Contains(entry.Id))) return null;
        return CaptureReorderPositions();
    }
    private Dictionary<string, Point>? CaptureReorderPositions()
    {
        if (!IsVisible || !Soft.For(this)) return null;
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var entry in _visible)
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement row) continue;
            positions[entry.Id] = row.TranslatePoint(new Point(), _list);
        }
        return positions;
    }
    private static void CancelReorderOnReuse(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((e.OldValue as ClipboardEntry)?.Id != (e.NewValue as ClipboardEntry)?.Id) ((FrameworkElement)sender).RenderTransform = Transform.Identity;
    }
    private void AnimateReorder(Dictionary<string, Point>? positions)
    {
        int generation = ++_reorderGeneration;
        if (!Soft.For(this))
        {
            foreach (var entry in _visible) if (_list.ItemContainerGenerator.ContainerFromItem(entry) is FrameworkElement row) row.RenderTransform = Transform.Identity;
            return;
        }
        if (positions is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_disposed || generation != _reorderGeneration || !IsVisible) return;
            _list.UpdateLayout();
            foreach (var entry in _visible)
            {
                if (_list.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement row) continue;
                // Recycled containers must not inherit another entry's unfinished movement.
                row.RenderTransform = Transform.Identity;
                row.DataContextChanged -= CancelReorderOnReuse; row.DataContextChanged += CancelReorderOnReuse;
                if (!Soft.For(this) || !positions.TryGetValue(entry.Id, out var before)) continue;
                var after = row.TranslatePoint(new Point(), _list); var distance = before - after;
                if (distance.Length < .5) continue;
                var shift = new TranslateTransform(); row.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(distance.X, 0, TimeSpan.FromMilliseconds(Soft.Move)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance.Y, 0, TimeSpan.FromMilliseconds(Soft.Move)) { EasingFunction = Soft.Follow, FillBehavior = FillBehavior.Stop });
            }
        });
    }
}
