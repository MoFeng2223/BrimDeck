using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The power plan switcher page: shows all Windows power schemes in a responsive grid of theme-aware cards
// and switches the active scheme immediately on click.
internal sealed class PowerPlanView : Grid
{
    private PanelLook Look => PanelLook.Of(this);
    private readonly ScrollViewer _scroll;
    private readonly UniformGrid _grid;
    private readonly TextBlock _statusText;
    private readonly List<(Guid Id, Border Card, Border Dot, Path Icon, TextBlock Name, TextBlock State)> _cards = [];
    private IReadOnlyList<PowerPlanInfo> _plans = [];
    private Guid? _hovered;
    private string? _statusMessage;
    private bool _statusIsError;

    private static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformanceGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public PowerPlanView()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _grid = new UniformGrid { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
        _scroll = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Padding = new Thickness(0, 2, 0, 2)
        };
        Children.Add(_scroll);

        _statusText = new TextBlock
        {
            FontFamily = UI.PanelFont,
            FontSize = 10.5,
            Margin = new Thickness(2, 4, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        SetRow(_statusText, 1);
        Children.Add(_statusText);

        SizeChanged += (_, _) => UpdateColumns();
        Loaded += (_, _) => RefreshPlans();
    }

    public void RefreshPlans()
    {
        IReadOnlyList<PowerPlanInfo> plans;
        try
        {
            plans = PowerSchemes.List();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _plans = [];
            _grid.Children.Clear();
            _cards.Clear();
            ShowStatus(Loc.T("读取电源方案失败：", "Failed to read power plans: ") + ex.Message, isError: true);
            return;
        }

        bool sameRoster = plans.Count == _plans.Count
            && plans.Select((p, i) => p.Id == _plans[i].Id && p.Name == _plans[i].Name && p.Description == _plans[i].Description).All(x => x);
        _plans = plans;
        if (!sameRoster || _cards.Count != plans.Count)
            RebuildCards();
        else
            UpdateCardStates();
    }

    private void UpdateColumns()
    {
        double width = ActualWidth;
        if (width <= 0) return;
        int count = _plans.Count;
        int cols = width >= 640 ? 4
            : width >= 400 ? (count <= 4 ? 2 : 3)
            : 2;
        if (count > 0) cols = Math.Min(cols, count);
        if (cols < 1) cols = 1;
        if (_grid.Columns != cols) _grid.Columns = cols;
    }

    private void RebuildCards()
    {
        _grid.Children.Clear();
        _cards.Clear();
        UpdateColumns();

        if (_plans.Count == 0)
        {
            _grid.Columns = 1;
            _grid.Children.Add(new TextBlock
            {
                Text = Loc.T("未找到可用的系统电源方案", "No Windows power plans found"),
                FontFamily = UI.PanelFont,
                FontSize = 12,
                Foreground = UI.Brush(Look.Secondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 24)
            });
            return;
        }

        double radius = Look.Corner(8);
        foreach (var plan in _plans)
        {
            var card = new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = true,
                SnapsToDevicePixels = true
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconPath = new Path
            {
                Data = Geometry.Parse(IconDataFor(plan)),
                StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            var iconBox = new Viewbox
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Canvas { Width = 24, Height = 24, Children = { iconPath } }
            };
            top.Children.Add(iconBox);

            var nameBlock = new TextBlock
            {
                Text = plan.Name,
                FontFamily = UI.PanelFont,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            SetColumn(nameBlock, 1);
            top.Children.Add(nameBlock);

            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetColumn(dot, 2);
            top.Children.Add(dot);

            layout.Children.Add(top);

            var stateBlock = new TextBlock
            {
                FontFamily = UI.PanelFont,
                FontSize = 10.5,
                Margin = new Thickness(20, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetRow(stateBlock, 1);
            layout.Children.Add(stateBlock);

            card.Child = layout;
            card.ToolTip = string.IsNullOrWhiteSpace(plan.Description)
                ? plan.Name
                : $"{plan.Name}\n{plan.Description}";
            AutomationProperties.SetName(card, plan.Name);

            var planId = plan.Id;
            card.MouseEnter += (_, _) => { _hovered = planId; UpdateCardStates(); };
            card.MouseLeave += (_, _) => { if (_hovered == planId) { _hovered = null; UpdateCardStates(); } };
            card.MouseLeftButtonUp += (_, e) => { ActivatePlan(planId); e.Handled = true; };
            card.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) { ActivatePlan(planId); e.Handled = true; }
            };

            _grid.Children.Add(card);
            _cards.Add((plan.Id, card, dot, iconPath, nameBlock, stateBlock));
        }

        UpdateCardStates();
    }

    private void ActivatePlan(Guid id)
    {
        var plan = _plans.FirstOrDefault(p => p.Id == id);
        var result = plan is not null ? PowerSchemes.SetActive(plan) : new PowerPlanSwitchResult(PowerSchemes.SetActive(id).Success, null, null);
        if (!result.Success)
        {
            ShowStatus(Loc.T("切换电源方案失败：", "Failed to switch power plan: ") + (result.Error ?? ""), isError: true);
            return;
        }
        if (!string.IsNullOrEmpty(result.HardwareMode))
            ShowStatus(result.HardwareMode, isError: false);
        else
            ClearStatus();
        RefreshPlans();
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusMessage = message;
        _statusIsError = isError;
        _statusText.Text = message;
        _statusText.Foreground = isError
            ? UI.Brush(Look.Readable(Look.Hex("danger"), 4.5))
            : UI.Brush(Look.Tertiary);
        _statusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        _statusMessage = null;
        _statusIsError = false;
        _statusText.Visibility = Visibility.Collapsed;
    }

    private void UpdateCardStates()
    {
        var look = Look;
        string accent = look.Readable(look.Hex("accent"), 4.5);
        string accentGraphic = look.Readable(look.Hex("accent"));
        var activeFill = UI.Brush(look.Hex("fillSelected"));
        var hoverFill = UI.Brush(look.Hex("hover"));
        var normalFill = UI.Brush(look.Hex("fill"));
        var hairline = UI.Brush(look.Hex("hairline"));
        var activeBorder = UI.Brush(accentGraphic);
        var primary = UI.Brush(look.Primary);
        var secondary = UI.Brush(look.Secondary);
        var tertiary = UI.Brush(look.Tertiary);
        var accentBrush = UI.Brush(accent);
        var accentDotBrush = UI.Brush(accentGraphic);

        for (int i = 0; i < _cards.Count && i < _plans.Count; i++)
        {
            var (id, card, dot, icon, name, state) = _cards[i];
            var plan = _plans[i];
            bool active = plan.IsActive;
            bool hovered = _hovered == id;

            card.Background = active ? activeFill : hovered ? hoverFill : normalFill;
            card.BorderBrush = active ? activeBorder : hovered ? UI.Brush(look.Hex("outline")) : hairline;

            icon.Stroke = active ? accentDotBrush : hovered ? primary : secondary;
            name.Foreground = active ? primary : hovered ? primary : secondary;
            name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            dot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            dot.Background = accentDotBrush;

            state.Text = active
                ? Loc.T("当前使用中", "Active")
                : hovered ? Loc.T("点击切换", "Click to switch") : SubtitleFor(plan);
            state.Foreground = active ? accentBrush : tertiary;
        }

        if (_statusMessage is null) _statusText.Visibility = Visibility.Collapsed;
        else _statusText.Foreground = _statusIsError
            ? UI.Brush(look.Readable(look.Hex("danger"), 4.5))
            : tertiary;
    }

    private static string SubtitleFor(PowerPlanInfo plan)
    {
        if (plan.Id == PowerSaverGuid || Matches(plan.Name, "silent", "saver", "eco", "quiet", "节能", "静音"))
            return Loc.T("节能静音", "Quiet / Saver");
        if (plan.Id == BalancedGuid || Matches(plan.Name, "balanced", "平衡"))
            return Loc.T("均衡模式", "Balanced");
        if (plan.Id == UltimatePerformanceGuid || Matches(plan.Name, "turbo", "ultimate", "卓越", "狂暴", "野兽"))
            return Loc.T("极致性能", "Turbo / Max");
        if (plan.Id == HighPerformanceGuid || Matches(plan.Name, "performance", "high", "性能"))
            return Loc.T("高性能", "Performance");
        return Loc.T("电源方案", "Power plan");
    }

    private static string IconDataFor(PowerPlanInfo plan)
    {
        if (plan.Id == PowerSaverGuid || Matches(plan.Name, "silent", "saver", "eco", "quiet", "节能", "静音"))
            return "M 5,19 C 5,10 11,5 19,5 C 19,13 14,19 5,19 Z M 5,19 L 13,11";
        if (plan.Id == BalancedGuid || Matches(plan.Name, "balanced", "平衡"))
            return "M 4,16 A 8,8 0 0 1 20,16 M 12,16 L 16,10 M 12,16 A 1.5,1.5 0 1 0 12,16.01";
        return "M 13.5,2.5 L 5.5,13.5 H 11.5 L 10.5,21.5 L 18.5,10.5 H 12.5 Z";
    }

    private static bool Matches(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
