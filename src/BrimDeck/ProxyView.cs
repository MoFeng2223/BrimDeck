using System.Globalization;
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

// Expanded Proxy control page (DeckPage.Proxy): borderless, monochrome-first native BrimDeck design
// harmonized with the panel theme's accent and surface tokens, featuring read-only mode/Sys/TUN status,
// responsive Compact/Standard/Spacious layout tiers, smooth Motion-driven node scrolling, and progressive latency testing.
internal sealed class ProxyView : Grid
{
    private const string PositiveHex = "#5DC98C";

    // Clash Verge cat head silhouette with EvenOdd cutout almond eyes and nose (24x24 viewBox).
    internal const string CatHeadPathData =
        "F0 M 12,7.2 C 13.6,7.2 14.8,7.0 15.8,6.2 L 18.6,3.4 C 19.3,2.7 20.2,3.1 20.3,4.1 L 20.6,9.4 " +
        "C 21.7,11.0 22.1,12.8 21.8,14.8 C 21.4,17.4 19.3,19.4 16.2,20.3 C 14.7,20.8 13.4,21.0 12.0,21.0 " +
        "C 10.6,21.0 9.3,20.8 7.8,20.3 C 4.7,19.4 2.6,17.4 2.2,14.8 C 1.9,12.8 2.3,11.0 3.4,9.4 " +
        "L 3.7,4.1 C 3.8,3.1 4.7,2.7 5.4,3.4 L 8.2,6.2 C 9.2,7.0 10.4,7.2 12.0,7.2 Z " +
        "M 6.0,13.4 C 7.4,12.7 9.4,13.3 10.4,14.8 C 9.1,15.8 7.1,15.6 6.0,14.2 C 5.8,13.9 5.8,13.5 6.0,13.4 Z " +
        "M 18.0,13.4 C 16.6,12.7 14.6,13.3 13.6,14.8 C 14.9,15.8 16.9,15.6 18.0,14.2 C 18.2,13.9 18.2,13.5 18.0,13.4 Z " +
        "M 10.7,16.9 C 11.4,16.6 12.6,16.6 13.3,16.9 C 13.5,17.0 13.4,17.4 12.6,18.2 C 12.2,18.5 11.8,18.5 11.4,18.2 C 10.6,17.4 10.5,17.0 10.7,16.9 Z";

    private readonly ProxyController _controller;
    private readonly Action<string> _rememberGroup;
    private string? _selectedGroup;

    private enum ScreenKind { None, Stopped, Disconnected, Connected }
    private ScreenKind _screen = ScreenKind.None;
    private bool _hasSubRow;
    private bool _compactHeight;
    private bool _spacious;
    private string _groupTabsSignature = "";

    private Style? _pillStyle;
    private Style? _modeStyle;

    // Live in-place references (zero visual tree rebuilds on traffic, status, mode, selection or delay updates).
    private ColumnDefinition? _leftColDef;
    private ColumnDefinition? _dividerColDef;
    private TextBlock? _clientTitle;
    private StackPanel? _sysStatus, _tunStatus;
    private Ellipse? _sysDot, _tunDot;
    private TextBlock? _sysLabel, _tunLabel;
    private Border? _modeTrack;
    private Border? _modePill;
    private TranslateTransform? _modePillTransform;
    private int _modePillIndex = -1;
    private double _modePillTargetX;
    private double _modePillLastNow;
    private bool _modePillAnimating;
    private readonly Dictionary<ProxyMode, (Button Btn, Ellipse Dot, TextBlock Label)> _modeButtons = [];
    private Button? _activeNodeBtn;
    private Border? _activeNodeBar;
    private TextBlock? _activeNodePrefixLabel, _activeNodeNameLabel, _activeNodeDelayLabel;
    private TextBlock? _downRateLabel, _upRateLabel, _connLabel, _totalsLabel;
    private Grid? _trafficBottomRow;
    private ProxyTrafficChart? _trafficChart;
    private TextBlock? _subNameLabel, _subQuotaLabel;
    private ColumnDefinition? _subFillCol, _subRestCol;
    private Border? _subFillBar;
    private StackPanel? _tabsPanel;
    private Button? _moreGroupsBtn;
    private TextBlock? _moreGroupsLabel;
    private readonly List<(string Name, Button Btn, TextBlock Label)> _tabButtons = [];
    private Button? _testBtn;
    private TextBlock? _testBtnLabel;
    private ProxyNodeList? _nodeList;

    private PanelLook Look => PanelLook.Of(this);

    public ProxyView(ProxyController controller, string? selectedGroup, Action<string> rememberGroup)
    {
        _controller = controller;
        _selectedGroup = selectedGroup;
        _rememberGroup = rememberGroup;
        ClipToBounds = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public void UpdateState()
    {
        var snap = _controller.Snapshot;
        var nextScreen = !snap.Status.Running ? ScreenKind.Stopped
            : !snap.ApiConnected ? ScreenKind.Disconnected
            : ScreenKind.Connected;

        if (nextScreen != ScreenKind.Connected)
        {
            if (_screen != nextScreen || nextScreen == ScreenKind.Disconnected)
                BuildEmptyScreen(nextScreen, snap);
            return;
        }

        double w = ActualWidth > 0 ? ActualWidth : ProxySizes.Default.Width;
        double h = ActualHeight;
        bool wantSub = snap.Subscription is { HasTraffic: true } && h >= 196;
        bool wantCompact = h > 0 && h < 146;
        bool wantSpacious = w >= 740 && h >= 220;
        if (_screen != ScreenKind.Connected || wantSub != _hasSubRow || wantCompact != _compactHeight || wantSpacious != _spacious)
            BuildConnectedLayout(snap, wantSub, wantCompact, wantSpacious);
        else
            UpdateConnectedInPlace(snap);
    }

    public void UpdateTraffic()
    {
        if (_screen != ScreenKind.Connected) return;
        var snap = _controller.Snapshot;
        if (_downRateLabel is not null) _downRateLabel.Text = "↓ " + ProxyControlParsers.FormatRate(snap.Traffic.DownBytesPerSec);
        if (_upRateLabel is not null) _upRateLabel.Text = "↑ " + ProxyControlParsers.FormatRate(snap.Traffic.UpBytesPerSec);
        if (_connLabel is not null) _connLabel.Text = FormatConnAndMemory(snap.Connections);
        if (_totalsLabel is not null)
            _totalsLabel.Text = $"↑ {ProxyControlParsers.FormatSize(snap.Connections.UploadTotal)}  ↓ {ProxyControlParsers.FormatSize(snap.Connections.DownloadTotal)}";
        _trafficChart?.SetSamples(_controller.TrafficHistory);
    }

    private static double LeftColumnWidth(double totalW) =>
        totalW < 520 ? Math.Clamp(Math.Round(totalW * 0.42), 198, 208)
        : totalW >= 740 ? Math.Clamp(Math.Round(totalW * 0.34), 276, 304)
        : Math.Clamp(Math.Round(totalW * 0.39), 224, 260);

    private static double DividerWidth(double totalW) => totalW < 520 ? 15 : totalW >= 740 ? 25 : 21;

    private (int Limit, double TabLabelMax) GroupTabBudget()
    {
        double totalW = ActualWidth > 0 ? ActualWidth : ProxySizes.Default.Width;
        double rightW = Math.Max(140, totalW - LeftColumnWidth(totalW) - DividerWidth(totalW));
        int limit = rightW < 290 ? 2 : rightW < 410 ? 3 : rightW >= 510 ? 5 : 4;
        double reserved = rightW < 290 ? 108 : 114;
        double padPerTab = rightW < 290 ? 16 : 20;
        double labelMax = Math.Clamp(Math.Floor((rightW - reserved) / limit) - padPerTab, 48, 108);
        return (limit, labelMax);
    }

    private void ApplyResponsiveLayout()
    {
        if (_screen != ScreenKind.Connected || _leftColDef is null || _dividerColDef is null) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _leftColDef.Width = new GridLength(LeftColumnWidth(w));
        _dividerColDef.Width = new GridLength(DividerWidth(w));

        var snap = _controller.Snapshot;
        bool wantSub = snap.Subscription is { HasTraffic: true } && h >= 196;
        bool wantCompact = h < 146;
        bool wantSpacious = w >= 740 && h >= 220;
        if (wantSub != _hasSubRow || wantCompact != _compactHeight || wantSpacious != _spacious)
            BuildConnectedLayout(snap, wantSub, wantCompact, wantSpacious);
        else
            UpdateConnectedInPlace(snap);
    }

    private void ClearLayout()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        _leftColDef = _dividerColDef = null;
        _clientTitle = null;
        _sysStatus = _tunStatus = null;
        _sysDot = _tunDot = null;
        _sysLabel = _tunLabel = null;
        _modeTrack = null;
        _modePill = null;
        _modePillTransform = null;
        _modePillIndex = -1;
        _modePillAnimating = false;
        _modeButtons.Clear();
        _activeNodeBtn = null;
        _activeNodeBar = null;
        _activeNodePrefixLabel = _activeNodeNameLabel = _activeNodeDelayLabel = null;
        _downRateLabel = _upRateLabel = _connLabel = _totalsLabel = null;
        _trafficBottomRow = null;
        _trafficChart = null;
        _subNameLabel = _subQuotaLabel = null;
        _subFillCol = _subRestCol = null;
        _subFillBar = null;
        _tabsPanel = null;
        _moreGroupsBtn = null;
        _moreGroupsLabel = null;
        _tabButtons.Clear();
        _testBtn = null;
        _testBtnLabel = null;
        _nodeList = null;
        _groupTabsSignature = "";
    }

    private void BuildEmptyScreen(ScreenKind kind, ProxyControlSnapshot snap)
    {
        ClearLayout();
        _screen = kind;
        _hasSubRow = false;
        _compactHeight = false;
        _spacious = false;

        string title, detail;
        bool showRetry;
        if (kind == ScreenKind.Stopped)
        {
            title = Loc.T("未检测到运行中的代理客户端", "No running proxy client");
            detail = Loc.T("打开 Clash Verge 等兼容客户端后将自动显示控制面板。", "Start Clash Verge or a compatible Clash/Mihomo client to connect.");
            showRetry = false;
        }
        else
        {
            var clientName = string.IsNullOrWhiteSpace(snap.Status.Client) ? "Mihomo" : snap.Status.Client;
            title = Loc.T($"已识别到 {clientName}，等待控制接口响应", $"Detected {clientName}, waiting for controller API");
            detail = Loc.T("支持 Clash Verge 命名管道与本机回环外部控制器；如未响应请确认内核已就绪。", "Connects over the local named pipe or loopback controller once the core is ready.");
            showRetry = true;
        }

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440 };
        var cat = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(CatHeadPathData),
            Fill = ClearSecondaryBrush(),
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(cat);

        var t = MakeText(title, 13.5, PrimaryBrush(), FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        panel.Children.Add(t);

        var d = MakeText(detail, 12, ClearSecondaryBrush());
        d.HorizontalAlignment = HorizontalAlignment.Center;
        d.TextAlignment = TextAlignment.Center;
        d.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(d);

        if (showRetry)
        {
            var retry = BuildPillActionButton(Loc.T("重试连接", "Retry"), "M 2.5,7 A 4.5,4.5 0 1 1 4,10.5 M 2.5,3.5 V 7 H 6", _controller.RefreshNow);
            retry.HorizontalAlignment = HorizontalAlignment.Center;
            retry.Margin = new Thickness(0, 12, 0, 0);
            panel.Children.Add(retry);
        }
        Children.Add(panel);
    }

    private void BuildConnectedLayout(ProxyControlSnapshot snap, bool showSub, bool compactHeight, bool spacious)
    {
        ClearLayout();
        _screen = ScreenKind.Connected;
        _hasSubRow = showSub;
        _compactHeight = compactHeight;
        _spacious = spacious;
        _pillStyle = CreateRoundedButtonStyle(Look.Corner(8));
        _modeStyle = CreateModeButtonStyle(Look.Corner(6));

        double totalW = ActualWidth > 0 ? ActualWidth : ProxySizes.Default.Width;
        _leftColDef = new ColumnDefinition { Width = new GridLength(LeftColumnWidth(totalW)) };
        _dividerColDef = new ColumnDefinition { Width = new GridLength(DividerWidth(totalW)) };
        ColumnDefinitions.Add(_leftColDef);
        ColumnDefinitions.Add(_dividerColDef);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = BuildLeftColumn(snap, showSub, compactHeight, spacious);
        Grid.SetColumn(left, 0);
        Children.Add(left);

        // Quiet BrimDeck hairline divider between left overview and right node list (matches Shelf & Monitor).
        var divider = new Border
        {
            Width = 1,
            Background = UI.Brush(Look.Hairline),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            IsHitTestVisible = false
        };
        Grid.SetColumn(divider, 1);
        Children.Add(divider);

        var right = BuildRightColumn(snap, compactHeight, spacious);
        Grid.SetColumn(right, 2);
        Children.Add(right);

        UpdateConnectedInPlace(snap);
    }

    private FrameworkElement BuildLeftColumn(ProxyControlSnapshot snap, bool showSub, bool compactHeight, bool spacious)
    {
        var col = new Grid();
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header (Cat + Client title)
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Read-only status strip (Mode + Sys/TUN)
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Active node card (clickable locate)
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Live traffic
        if (showSub) col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Subscription bar

        double vGap = compactHeight ? 4 : spacious ? 8 : 6;

        // Row 0: Grid-based brand header with quiet inline Sys/TUN indicators so Clash Verge never clips even in Compact.
        var header = new Grid { Margin = new Thickness(0, 0, 0, vGap) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        double catSize = compactHeight ? 16 : spacious ? 21 : 19.5;
        var catIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(CatHeadPathData),
            Fill = PrimaryBrush(),
            Width = catSize,
            Height = catSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, compactHeight ? 5 : spacious ? 8 : 6, 0)
        };
        Grid.SetColumn(catIcon, 0);
        header.Children.Add(catIcon);

        _clientTitle = MakeText(string.IsNullOrWhiteSpace(snap.Status.Client) ? "Clash" : snap.Status.Client,
            compactHeight ? 13 : spacious ? 16 : 15, PrimaryBrush(), FontWeights.SemiBold);
        _clientTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _clientTitle.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_clientTitle, 1);
        header.Children.Add(_clientTitle);

        double indicatorSize = compactHeight ? 10 : spacious ? 11.5 : 10.5;
        var flagsStrip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(compactHeight ? 4 : 6, 0, 0, 0) };
        (_sysStatus, _sysDot, _sysLabel) = CreateInlineStatus(Loc.T("代理", "Sys"), indicatorSize);
        (_tunStatus, _tunDot, _tunLabel) = CreateInlineStatus("TUN", indicatorSize);
        _tunStatus.Margin = new Thickness(compactHeight ? 5.5 : spacious ? 10 : 7, 0, 0, 0);
        flagsStrip.Children.Add(_sysStatus);
        flagsStrip.Children.Add(_tunStatus);
        Grid.SetColumn(flagsStrip, 2);
        header.Children.Add(flagsStrip);
        Grid.SetRow(header, 0);
        col.Children.Add(header);

        // Row 1: Concentric borderless segmented mode switcher with a gliding active capsule and 4px inter-cell gaps
        // so hover states never collide with or over-brighten the active mode pill.
        double modeBtnHeight = compactHeight ? 20 : spacious ? 26 : 23;
        double modeFontSize = compactHeight ? 11.5 : spacious ? 13 : 12;
        _modeTrack = new Border
        {
            Background = UI.Brush(Look.Hex("switchFill")),
            CornerRadius = new CornerRadius(Look.Corner(9)),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 0, vGap)
        };
        var modeGrid = new Grid();
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _modePillTransform = new TranslateTransform();
        _modePill = new Border
        {
            Background = UI.Brush(Look.FillSelected),
            CornerRadius = new CornerRadius(Look.Corner(6)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransform = _modePillTransform
        };
        _modePill.SizeChanged += (_, _) => PlaceModePill(animate: false);
        Grid.SetColumn(_modePill, 0);
        modeGrid.Children.Add(_modePill);

        var ruleBtn = CreateModeButton(ProxyMode.Rule, Loc.T("规则", "Rule"), modeBtnHeight, modeFontSize);
        var globalBtn = CreateModeButton(ProxyMode.Global, Loc.T("全局", "Global"), modeBtnHeight, modeFontSize);
        var directBtn = CreateModeButton(ProxyMode.Direct, Loc.T("直连", "Direct"), modeBtnHeight, modeFontSize);
        Grid.SetColumn(ruleBtn, 0);
        Grid.SetColumn(globalBtn, 2);
        Grid.SetColumn(directBtn, 4);
        modeGrid.Children.Add(ruleBtn);
        modeGrid.Children.Add(globalBtn);
        modeGrid.Children.Add(directBtn);

        _modeTrack.Child = modeGrid;
        Grid.SetRow(_modeTrack, 1);
        col.Children.Add(_modeTrack);

        // Row 2: Borderless Active Node card (vertical theme accent mark + resolved node + delay; click to smoothly scroll).
        var activeRow = new Grid();
        activeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        activeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        activeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _activeNodeBar = new Border
        {
            Width = 3,
            Height = compactHeight ? 12 : spacious ? 16 : 14,
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        Grid.SetColumn(_activeNodeBar, 0);
        activeRow.Children.Add(_activeNodeBar);

        var nameGrid = new Grid();
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _activeNodePrefixLabel = MakeText("", spacious ? 11.5 : 11, MutedBrush());
        _activeNodePrefixLabel.Margin = new Thickness(0, 0, 5, 0);
        _activeNodePrefixLabel.TextWrapping = TextWrapping.NoWrap;
        _activeNodeNameLabel = MakeText("—", compactHeight ? 12 : spacious ? 13 : 12.5, PrimaryBrush(), FontWeights.SemiBold);
        _activeNodeNameLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _activeNodeNameLabel.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(_activeNodePrefixLabel, 0);
        Grid.SetColumn(_activeNodeNameLabel, 1);
        nameGrid.Children.Add(_activeNodePrefixLabel);
        nameGrid.Children.Add(_activeNodeNameLabel);
        Grid.SetColumn(nameGrid, 1);
        activeRow.Children.Add(nameGrid);

        _activeNodeDelayLabel = MakeMonoText("-", spacious ? 11.5 : 11, ClearSecondaryBrush(), FontWeights.SemiBold);
        _activeNodeDelayLabel.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_activeNodeDelayLabel, 2);
        activeRow.Children.Add(_activeNodeDelayLabel);

        _activeNodeBtn = new Button
        {
            Height = compactHeight ? 26 : spacious ? 37 : 32,
            Margin = new Thickness(0, 0, 0, vGap),
            Padding = new Thickness(compactHeight ? 8 : 10, 0, compactHeight ? 8 : 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = UI.Brush(Look.Fill),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = _pillStyle,
            Content = activeRow
        };
        _activeNodeBtn.Click += (_, _) =>
        {
            var g = CurrentGroup(_controller.Snapshot);
            if (g is not null && !string.IsNullOrEmpty(g.Now))
                _nodeList?.ScrollToNode(g.Now);
        };
        Grid.SetRow(_activeNodeBtn, 2);
        col.Children.Add(_activeNodeBtn);

        // Row 3: Live traffic sits directly on the panel surface with a quiet hairline separator (matches Monitor & Usage).
        var trafficHost = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = compactHeight ? Brushes.Transparent : UI.Brush(Look.Hairline),
            BorderThickness = compactHeight ? new Thickness(0) : new Thickness(0, 1, 0, 0),
            Padding = new Thickness(2, compactHeight ? 2 : spacious ? 9 : 7, 2, compactHeight ? 0 : 2)
        };
        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = compactHeight ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        cardGrid.RowDefinitions.Add(spacious
            ? new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MaxHeight = 76 }
            : new RowDefinition { Height = compactHeight ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var downThemeHex = DownAccentHex();
        var upThemeHex = UpAccentHex();

        double rateSize = compactHeight ? 11.5 : spacious ? 13.5 : 12.5;
        var rates = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _downRateLabel = MakeMonoText("↓ " + ProxyControlParsers.FormatRate(snap.Traffic.DownBytesPerSec), rateSize, UI.Brush(ReadableText(downThemeHex)), FontWeights.SemiBold);
        _downRateLabel.Margin = new Thickness(0, 0, spacious ? 14 : 12, 0);
        _upRateLabel = MakeMonoText("↑ " + ProxyControlParsers.FormatRate(snap.Traffic.UpBytesPerSec), rateSize, ClearSecondaryBrush(), FontWeights.Medium);
        rates.Children.Add(_downRateLabel);
        rates.Children.Add(_upRateLabel);
        Grid.SetRow(rates, 0);
        cardGrid.Children.Add(rates);

        _trafficChart = new ProxyTrafficChart(ReadableGraphic(downThemeHex), upThemeHex, Look.Hairline)
        {
            Margin = new Thickness(0, spacious ? 6 : 4, 0, spacious ? 5 : 3),
            MinHeight = compactHeight ? 0 : spacious ? 34 : 20,
            Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible
        };
        _trafficChart.SetSamples(_controller.TrafficHistory);
        Grid.SetRow(_trafficChart, 1);
        cardGrid.Children.Add(_trafficChart);

        _trafficBottomRow = new Grid { Visibility = compactHeight ? Visibility.Collapsed : Visibility.Visible };
        _trafficBottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _trafficBottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _connLabel = MakeText(FormatConnAndMemory(snap.Connections), spacious ? 11.5 : 11, MutedBrush());
        _connLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _connLabel.TextWrapping = TextWrapping.NoWrap;
        _trafficBottomRow.Children.Add(_connLabel);
        _totalsLabel = MakeMonoText($"↑ {ProxyControlParsers.FormatSize(snap.Connections.UploadTotal)}  ↓ {ProxyControlParsers.FormatSize(snap.Connections.DownloadTotal)}", spacious ? 11 : 10.5, MutedBrush());
        _totalsLabel.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_totalsLabel, 1);
        _trafficBottomRow.Children.Add(_totalsLabel);
        Grid.SetRow(_trafficBottomRow, 2);
        cardGrid.Children.Add(_trafficBottomRow);

        trafficHost.Child = cardGrid;
        Grid.SetRow(trafficHost, 3);
        col.Children.Add(trafficHost);

        if (showSub)
        {
            var subCard = BuildSubscriptionCard(spacious);
            subCard.Margin = new Thickness(0, vGap, 0, 0);
            Grid.SetRow(subCard, 4);
            col.Children.Add(subCard);
        }

        return col;
    }

    private Border BuildSubscriptionCard(bool spacious)
    {
        var card = new Border
        {
            Background = UI.Brush(Look.Fill),
            CornerRadius = new CornerRadius(Look.Corner(8)),
            Padding = new Thickness(spacious ? 10 : 9, spacious ? 7 : 5, spacious ? 10 : 9, spacious ? 8 : 6)
        };
        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _subNameLabel = MakeText("", spacious ? 11.5 : 11, PrimaryBrush(), FontWeights.Medium);
        _subNameLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _subNameLabel.TextWrapping = TextWrapping.NoWrap;
        top.Children.Add(_subNameLabel);

        _subQuotaLabel = MakeMonoText("", spacious ? 11 : 10.5, MutedBrush());
        _subQuotaLabel.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_subQuotaLabel, 1);
        top.Children.Add(_subQuotaLabel);
        stack.Children.Add(top);

        var track = new Border
        {
            Height = spacious ? 4 : 3.5,
            Margin = new Thickness(0, spacious ? 5 : 4, 0, 0),
            CornerRadius = new CornerRadius(spacious ? 2 : 1.75),
            Background = UI.Brush(Look.Track),
            ClipToBounds = true
        };
        var fillGrid = new Grid();
        _subFillCol = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
        _subRestCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        fillGrid.ColumnDefinitions.Add(_subFillCol);
        fillGrid.ColumnDefinitions.Add(_subRestCol);
        _subFillBar = new Border { CornerRadius = new CornerRadius(spacious ? 2 : 1.75) };
        fillGrid.Children.Add(_subFillBar);
        track.Child = fillGrid;
        stack.Children.Add(track);

        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildRightColumn(ProxyControlSnapshot snap, bool compactHeight, bool spacious)
    {
        var col = new Grid();
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Row 0: Clean borderless group pills + overflow picker + monochrome Test pill.
        double barHeight = spacious ? 30 : 28;
        var tabBar = new Grid { Margin = new Thickness(0, 0, 0, spacious ? 10 : 7) };
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftHeader = new Grid { ClipToBounds = true };
        leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tabsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftHeader.Children.Add(_tabsPanel);

        (_moreGroupsBtn, _moreGroupsLabel) = BuildMoreGroupsButton(barHeight, compactHeight);
        _moreGroupsBtn.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_moreGroupsBtn, 1);
        leftHeader.Children.Add(_moreGroupsBtn);
        tabBar.Children.Add(leftHeader);

        (_testBtn, _testBtnLabel) = BuildTestButton(barHeight, compactHeight);
        _testBtn.Margin = new Thickness(compactHeight ? 4 : 6, 0, 0, 0);
        Grid.SetColumn(_testBtn, 1);
        tabBar.Children.Add(_testBtn);
        Grid.SetRow(tabBar, 0);
        col.Children.Add(tabBar);

        // Row 1: Single-element custom-drawn node list with smooth Motion-driven scroll.
        _nodeList = new ProxyNodeList(
            onSelect: (group, node) => _ = _controller.SelectNodeAsync(group, node),
            onTestNode: node => _ = _controller.TestNodeDelayAsync(node),
            isTestingNode: _controller.IsTestingNode);
        _nodeList.SetSpacious(spacious);
        Grid.SetRow(_nodeList, 1);
        col.Children.Add(_nodeList);

        return col;
    }

    private void UpdateConnectedInPlace(ProxyControlSnapshot snap)
    {
        if (_clientTitle is not null)
            _clientTitle.Text = string.IsNullOrWhiteSpace(snap.Status.Client) ? "Clash" : snap.Status.Client;

        UpdateInlineStatus(_sysStatus, _sysDot, _sysLabel, snap.Status.SystemProxy,
            snap.Status.SystemProxy ? Loc.T("系统代理已开启（只读显示）", "System proxy on (read-only)") : Loc.T("系统代理已关闭（只读显示）", "System proxy off (read-only)"));
        UpdateInlineStatus(_tunStatus, _tunDot, _tunLabel, snap.Status.Tun,
            snap.Status.Tun ? Loc.T("TUN 模式已开启（只读显示）", "TUN mode on (read-only)") : Loc.T("TUN 模式已关闭（只读显示）", "TUN mode off (read-only)"));

        var primaryBrush = PrimaryBrush();
        var secondaryBrush = ClearSecondaryBrush();
        var accentBrush = UI.Brush(ThemeAccentHex());

        if (_modeTrack is not null) _modeTrack.IsEnabled = !_controller.BusyModeSwitch;
        foreach (var (mode, (btn, dot, label)) in _modeButtons)
        {
            bool selected = snap.Status.Mode == mode;
            btn.Tag = selected;
            btn.Cursor = selected ? Cursors.Arrow : Cursors.Hand;
            dot.Fill = accentBrush;
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        UpdateModePill(snap.Status.Mode);

        var activeGroup = CurrentGroup(snap);
        if (activeGroup is not null && !string.Equals(_selectedGroup, activeGroup.Name, StringComparison.Ordinal))
        {
            _selectedGroup = activeGroup.Name;
            _rememberGroup(activeGroup.Name);
        }

        var (limit, labelMax) = GroupTabBudget();
        var visibleTabs = SelectVisibleGroups(snap.Groups, activeGroup, limit);
        var tabsSig = $"{limit}|{(int)labelMax}|" + string.Join("\n", visibleTabs.Select(g => $"{g.Name}|{(g.CanSelect ? 1 : 0)}"));
        if (tabsSig != _groupTabsSignature && _tabsPanel is not null)
        {
            _groupTabsSignature = tabsSig;
            _tabsPanel.Children.Clear();
            _tabButtons.Clear();
            foreach (var group in visibleTabs)
            {
                var (btn, lbl) = CreateGroupTabButton(group, labelMax);
                _tabsPanel.Children.Add(btn);
                _tabButtons.Add((group.Name, btn, lbl));
            }
        }
        foreach (var (name, btn, lbl) in _tabButtons)
        {
            bool selected = activeGroup is not null && string.Equals(name, activeGroup.Name, StringComparison.Ordinal);
            btn.Background = selected ? UI.Brush(Look.FillSelected) : Brushes.Transparent;
            lbl.Foreground = selected ? primaryBrush : secondaryBrush;
            lbl.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            var matched = snap.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal));
            btn.ToolTip = matched is null || string.IsNullOrEmpty(matched.Now) ? name : $"{name} · {ProxyControlParsers.FormatNodeDisplayName(matched.Now)}";
        }

        if (_moreGroupsBtn is not null && _moreGroupsLabel is not null)
        {
            int hiddenCount = Math.Max(0, snap.Groups.Count - visibleTabs.Count);
            _moreGroupsBtn.Visibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (hiddenCount > 0)
            {
                _moreGroupsLabel.Text = $"+{hiddenCount}";
                _moreGroupsBtn.ToolTip = Loc.T($"切换其他策略组（共 {snap.Groups.Count} 个）", $"Switch proxy group ({snap.Groups.Count} total)");
            }
        }

        if (_testBtn is not null && _testBtnLabel is not null)
        {
            bool hasGroup = activeGroup is not null;
            bool testing = hasGroup && _controller.IsTestingGroup(activeGroup!.Name);
            _testBtn.Visibility = hasGroup ? Visibility.Visible : Visibility.Collapsed;
            _testBtn.IsEnabled = hasGroup && !testing;
            _testBtnLabel.Text = testing ? Loc.T("测速中", "Testing") : Loc.T("测速", "Test");
            if (hasGroup)
                _testBtn.ToolTip = Loc.T($"测试「{activeGroup!.Name}」全部节点延迟", $"Test latency for all nodes in {activeGroup!.Name}");
        }

        UpdateActiveNodeCard(snap, activeGroup);
        _nodeList?.SetSpacious(_spacious);
        _nodeList?.SetGroup(activeGroup);

        if (_hasSubRow && snap.Subscription is { HasTraffic: true } sub && _subNameLabel is not null && _subQuotaLabel is not null && _subFillCol is not null && _subRestCol is not null && _subFillBar is not null)
        {
            _subNameLabel.Text = string.IsNullOrWhiteSpace(sub.ProfileName) ? Loc.T("订阅流量", "Traffic") : sub.ProfileName;
            var expiry = ProxyControlParsers.FormatExpiry(sub.ExpireUnixSeconds, DateTimeOffset.UtcNow);
            _subQuotaLabel.Text = $"{ProxyControlParsers.FormatSize(sub.UsedBytes)} / {ProxyControlParsers.FormatSize(sub.TotalBytes)}" + (expiry.Length > 0 ? $" · {expiry}" : "");
            var barColor = sub.UsedFraction >= 0.9 ? ReadableGraphic(Look.CriticalDefault) : sub.UsedFraction >= 0.7 ? ReadableGraphic(Look.WarningDefault) : ThemeAccentHex();
            _subFillBar.Background = UI.Brush(barColor);
            _subFillCol.Width = new GridLength(Math.Max(0.001, sub.UsedFraction), GridUnitType.Star);
            _subRestCol.Width = new GridLength(Math.Max(0.001, 1 - sub.UsedFraction), GridUnitType.Star);
        }

        UpdateTraffic();
    }

    private void UpdateModePill(ProxyMode mode)
    {
        int nextIndex = mode switch
        {
            ProxyMode.Rule => 0,
            ProxyMode.Global => 1,
            ProxyMode.Direct => 2,
            _ => -1
        };
        bool hadPrevious = _modePillIndex >= 0;
        _modePillIndex = nextIndex;
        PlaceModePill(animate: hadPrevious);
    }

    private void PlaceModePill(bool animate)
    {
        if (_modePill is null || _modePillTransform is null) return;
        if (_modePillIndex < 0)
        {
            _modePill.Visibility = Visibility.Collapsed;
            _modePillAnimating = false;
            return;
        }
        _modePill.Visibility = Visibility.Visible;
        double cellW = _modePill.ActualWidth;
        if (cellW <= 0) return;
        double targetX = Math.Round(_modePillIndex * (cellW + 4));
        _modePillTargetX = targetX;
        if (!animate || !Motion.Enabled || !IsLoaded || Math.Abs(targetX - _modePillTransform.X) < 0.5)
        {
            _modePillTransform.X = targetX;
            _modePillAnimating = false;
            return;
        }
        _modePillLastNow = Eased.Now;
        if (!_modePillAnimating)
        {
            _modePillAnimating = true;
            Motion.Start(_modePill, StepModePill);
        }
    }

    private bool StepModePill(double now)
    {
        if (!_modePillAnimating || _modePillTransform is null || !IsLoaded) { _modePillAnimating = false; return false; }
        double dt = Math.Clamp(now - _modePillLastNow, 0.001, 0.05);
        _modePillLastNow = now;
        double factor = 1.0 - Math.Exp(-dt * 22.0);
        double next = _modePillTransform.X + (_modePillTargetX - _modePillTransform.X) * factor;
        if (Math.Abs(_modePillTargetX - next) < 0.35)
        {
            _modePillTransform.X = _modePillTargetX;
            _modePillAnimating = false;
            return false;
        }
        _modePillTransform.X = next;
        return true;
    }

    private Button CreateModeButton(ProxyMode mode, string label, double height, double fontSize)
    {
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = UI.Brush(ThemeAccentHex()),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4.5, 0),
            Visibility = Visibility.Collapsed
        };
        // Leave text Foreground unset so it inherits Button.Foreground and gently brightens on hover.
        var text = new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "FontPanel");
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(dot);
        content.Children.Add(text);

        var button = new Button
        {
            Height = height,
            Padding = new Thickness(3, 0, 3, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Tag = false,
            Cursor = Cursors.Hand,
            Content = content,
            Style = _modeStyle,
            ToolTip = Loc.T(
                $"切换 Mihomo 内核为「{label}」模式（立即生效；Clash Verge 自身窗口与托盘可能不会自动同步显示）",
                $"Switch Mihomo core to {label} mode (takes effect immediately; Clash Verge UI/tray may not sync automatically)")
        };
        AutomationProperties.SetName(button, Loc.T("代理模式 ", "Proxy mode ") + label);
        button.Click += (_, _) =>
        {
            if (_controller.Snapshot.Status.Mode != mode)
                _ = _controller.SetModeAsync(mode);
        };
        _modeButtons[mode] = (button, dot, text);
        return button;
    }

    private static List<ProxyGroupInfo> SelectVisibleGroups(IReadOnlyList<ProxyGroupInfo> all, ProxyGroupInfo? active, int limit)
    {
        if (all.Count <= limit) return [.. all];
        var shown = all.Take(limit).ToList();
        if (active is not null && !shown.Any(g => string.Equals(g.Name, active.Name, StringComparison.Ordinal)))
            shown[^1] = active;
        return shown;
    }

    private void UpdateActiveNodeCard(ProxyControlSnapshot snap, ProxyGroupInfo? activeGroup)
    {
        if (_activeNodeBtn is null || _activeNodeBar is null || _activeNodePrefixLabel is null || _activeNodeNameLabel is null || _activeNodeDelayLabel is null)
            return;

        if (activeGroup is null || string.IsNullOrWhiteSpace(activeGroup.Now))
        {
            _activeNodeBar.Background = UI.Brush(Look.Track);
            _activeNodePrefixLabel.Text = Loc.T("当前", "Now");
            _activeNodeNameLabel.Text = "—";
            _activeNodeDelayLabel.Text = "-";
            _activeNodeDelayLabel.Foreground = MutedBrush();
            _activeNodeBtn.ToolTip = null;
            return;
        }

        var (direct, leaf, delay) = ProxyControlParsers.ResolveActiveNode(snap.Groups, activeGroup);
        var formattedLeaf = ProxyControlParsers.FormatNodeDisplayName(leaf);
        _activeNodeBar.Background = UI.Brush(ThemeAccentHex());
        bool chained = !string.Equals(direct, leaf, StringComparison.Ordinal);
        _activeNodePrefixLabel.Text = chained ? ProxyControlParsers.FormatNodeDisplayName(direct) + " →" : Loc.T("当前", "Now");
        _activeNodeNameLabel.Text = formattedLeaf;
        bool leafTesting = _controller.IsTestingNode(leaf);
        bool hasDelay = !leafTesting && delay is > 0;
        _activeNodeDelayLabel.Text = hasDelay ? $"{delay} ms" : "-";
        _activeNodeDelayLabel.Foreground = hasDelay ? UI.Brush(DelayTextColor(delay)) : MutedBrush();

        var chainText = chained ? $"{activeGroup.Name} → {ProxyControlParsers.FormatNodeDisplayName(direct)} → {formattedLeaf}" : $"{activeGroup.Name} → {formattedLeaf}";
        var delayPart = hasDelay ? $" ({delay} ms)" : "";
        _activeNodeBtn.ToolTip = chainText + delayPart + "\n" + Loc.T("点击在右侧列表中定位当前节点", "Click to locate this node");
        AutomationProperties.SetName(_activeNodeBtn, Loc.T("当前节点 ", "Active node ") + formattedLeaf);
    }

    private ProxyGroupInfo? CurrentGroup(ProxyControlSnapshot snap)
    {
        if (snap.Groups.Count == 0) return null;
        return snap.Groups.FirstOrDefault(g => string.Equals(g.Name, _selectedGroup, StringComparison.Ordinal)) ?? snap.Groups[0];
    }

    private (Button Btn, TextBlock Label) CreateGroupTabButton(ProxyGroupInfo group, double labelMaxWidth)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = MakeText(group.Name, _spacious ? 12.5 : 12, ClearSecondaryBrush());
        label.TextWrapping = TextWrapping.NoWrap;
        label.MaxWidth = labelMaxWidth;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(label);
        if (!group.CanSelect && !_compactHeight)
        {
            var auto = MakeText(Loc.T("自动", "Auto"), 9.5, MutedBrush());
            auto.Margin = new Thickness(4, 1, 0, 0);
            auto.TextWrapping = TextWrapping.NoWrap;
            stack.Children.Add(auto);
        }

        double padX = _compactHeight ? 7 : _spacious ? 10 : 8.5;
        var btn = new Button
        {
            Height = _spacious ? 30 : 28,
            Padding = new Thickness(padX, 0, padX, 0),
            Margin = new Thickness(0, 0, _compactHeight ? 3 : 4, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Content = stack,
            Style = _pillStyle
        };
        AutomationProperties.SetName(btn, group.Name);
        var groupName = group.Name;
        btn.Click += (_, _) => SelectGroupTab(groupName);
        return (btn, label);
    }

    private void SelectGroupTab(string groupName)
    {
        if (string.Equals(_selectedGroup, groupName, StringComparison.Ordinal)) return;
        _selectedGroup = groupName;
        _rememberGroup(groupName);
        _nodeList?.ResetScroll();
        UpdateConnectedInPlace(_controller.Snapshot);
    }

    private (Button Btn, TextBlock Label) BuildMoreGroupsButton(double height, bool compact)
    {
        var label = MakeText("", 11.5, ClearSecondaryBrush(), FontWeights.Medium);
        label.TextWrapping = TextWrapping.NoWrap;
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label);
        stack.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,1 L 3.5,4.5 L 7,1"),
            Width = 7,
            Height = 6,
            Stroke = ClearSecondaryBrush(),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 1, 0, 0)
        });

        var btn = new Button
        {
            Height = height,
            Padding = new Thickness(compact ? 6 : 8, 0, compact ? 6 : 8, 0),
            Background = UI.Brush(Look.Fill),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = _pillStyle,
            Content = stack
        };
        AutomationProperties.SetName(btn, Loc.T("更多策略组", "More proxy groups"));
        btn.Click += (_, _) => OpenGroupPickerMenu(btn);
        return (btn, label);
    }

    private void OpenGroupPickerMenu(Button anchor)
    {
        var groups = _controller.Snapshot.Groups;
        if (groups.Count == 0) return;
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HasDropShadow = false,
            OverridesDefaultStyle = true,
            Background = UI.Brush(Look.Hex("popup")),
            BorderBrush = UI.Brush(Look.Hex("popupBorder")),
            Foreground = PrimaryBrush(),
            MaxHeight = 320
        };
        if (TryFindResource("ShelfMenu") is Style shelfMenu) menu.Style = shelfMenu;

        var itemStyle = TryFindResource("ShelfMenuItem") as Style;
        foreach (var group in groups)
        {
            bool selected = string.Equals(group.Name, _selectedGroup, StringComparison.Ordinal);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (selected)
            {
                var check = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 1,4.5 L 3.5,7 L 8,2"),
                    Stroke = PrimaryBrush(),
                    StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(check);
            }
            var title = MakeText(group.Name, 12, PrimaryBrush(), selected ? FontWeights.SemiBold : FontWeights.Normal);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.MaxWidth = 140;
            Grid.SetColumn(title, 1);
            row.Children.Add(title);

            var nowLabel = ProxyControlParsers.FormatNodeDisplayName(group.Now);
            var mi = new MenuItem
            {
                Header = row,
                InputGestureText = string.IsNullOrWhiteSpace(nowLabel) ? "" : (nowLabel.Length > 18 ? nowLabel[..18] + "…" : nowLabel)
            };
            if (itemStyle is not null) mi.Style = itemStyle;
            var name = group.Name;
            mi.Click += (_, _) => SelectGroupTab(name);
            menu.Items.Add(mi);
        }
        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private (Button Btn, TextBlock Label) BuildTestButton(double height, bool compact)
    {
        var label = MakeText(Loc.T("测速", "Test"), 12, PrimaryBrush(), FontWeights.Medium);
        label.TextWrapping = TextWrapping.NoWrap;
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 7.5,1.5 L 3.5,7.5 H 7 L 6,12.5 L 10.5,6.5 H 7 Z"),
            Width = 13,
            Height = 13,
            Stroke = ClearSecondaryBrush(),
            StrokeThickness = 1.45,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, compact ? 4 : 5, 0)
        });
        stack.Children.Add(label);

        var btn = new Button
        {
            Height = height,
            Padding = new Thickness(compact ? 8 : 10, 0, compact ? 8 : 10, 0),
            Background = UI.Brush(Look.Fill),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = _pillStyle,
            Content = stack
        };
        AutomationProperties.SetName(btn, Loc.T("测速", "Test"));
        btn.Click += (_, _) =>
        {
            var g = CurrentGroup(_controller.Snapshot);
            if (g is not null) _ = _controller.TestGroupDelayAsync(g.Name);
        };
        return (btn, label);
    }

    private Button BuildPillActionButton(string text, string iconData, Action onClick)
    {
        var style = CreateRoundedButtonStyle(Look.Corner(8));
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconData),
            Width = 13,
            Height = 13,
            Stroke = PrimaryBrush(),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        stack.Children.Add(MakeText(text, 12, PrimaryBrush(), FontWeights.Medium));

        var btn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = UI.Brush(Look.Fill),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = style,
            Content = stack
        };
        AutomationProperties.SetName(btn, text);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // Quiet, borderless inline status indicator (dot + label) that blends naturally with BrimDeck.
    private (StackPanel Host, Ellipse Dot, TextBlock Label) CreateInlineStatus(string text, double fontSize)
    {
        var dot = new Ellipse { Width = 5.5, Height = 5.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3.5, 0) };
        var label = MakeText(text, fontSize, MutedBrush());
        label.TextWrapping = TextWrapping.NoWrap;
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        host.Children.Add(dot);
        host.Children.Add(label);
        return (host, dot, label);
    }

    private void UpdateInlineStatus(StackPanel? host, Ellipse? dot, TextBlock? label, bool active, string tooltip)
    {
        if (host is null || dot is null || label is null) return;
        host.ToolTip = tooltip;
        dot.Fill = active ? UI.Brush(ReadableGraphic(PositiveHex)) : UI.Brush(Look.Track);
        label.Foreground = active ? ClearSecondaryBrush() : MutedBrush();
        label.FontWeight = active ? FontWeights.Medium : FontWeights.Normal;
    }

    // Dedicated segmented mode button style: suppresses hover/press rectangles on the already-selected mode (Tag == true),
    // and softly highlights unselected modes inside their own 4px-separated cell with gently brightened ink.
    private Style CreateModeButtonStyle(double cornerRadius)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ClearSecondaryBrush()));
        if (TryFindResource("PanelKeyboardFocus") is Style focusStyle)
            style.Setters.Add(new Setter(FocusVisualStyleProperty, focusStyle));
        var radius = new CornerRadius(cornerRadius);
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var hover = new FrameworkElementFactory(typeof(Border), "HoverLayer");
        hover.SetValue(Border.BackgroundProperty, UI.Brush(Look.Hex("hover")));
        hover.SetValue(Border.CornerRadiusProperty, radius);
        hover.SetValue(OpacityProperty, 0d);
        hover.SetValue(IsHitTestVisibleProperty, false);

        var press = new FrameworkElementFactory(typeof(Border), "PressLayer");
        press.SetValue(Border.BackgroundProperty, UI.Brush(Look.Hex("pressed")));
        press.SetValue(Border.CornerRadiusProperty, radius);
        press.SetValue(OpacityProperty, 0d);
        press.SetValue(IsHitTestVisibleProperty, false);

        var contentHost = new FrameworkElementFactory(typeof(Border), "ContentHost");
        contentHost.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        contentHost.AppendChild(presenter);

        root.AppendChild(hover);
        root.AppendChild(press);
        root.AppendChild(contentHost);
        template.VisualTree = root;

        var selectedTrigger = new Trigger { Property = TagProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, PrimaryBrush(), "ContentHost"));
        template.Triggers.Add(selectedTrigger);

        var hoverUnselected = new MultiTrigger();
        hoverUnselected.Conditions.Add(new System.Windows.Condition(IsMouseOverProperty, true));
        hoverUnselected.Conditions.Add(new System.Windows.Condition(TagProperty, false));
        hoverUnselected.Setters.Add(new Setter(OpacityProperty, 0.65, "HoverLayer"));
        hoverUnselected.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, PrimaryBrush(), "ContentHost"));
        template.Triggers.Add(hoverUnselected);

        var pressUnselected = new MultiTrigger();
        pressUnselected.Conditions.Add(new System.Windows.Condition(ButtonBase.IsPressedProperty, true));
        pressUnselected.Conditions.Add(new System.Windows.Condition(TagProperty, false));
        pressUnselected.Setters.Add(new Setter(OpacityProperty, 0.9, "PressLayer"));
        pressUnselected.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, PrimaryBrush(), "ContentHost"));
        template.Triggers.Add(pressUnselected);

        var disabledTrigger = new Trigger { Property = IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(OpacityProperty, 0.55));
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private Style CreateRoundedButtonStyle(double cornerRadius)
    {
        var style = new Style(typeof(Button));
        if (TryFindResource("PanelKeyboardFocus") is Style focusStyle)
            style.Setters.Add(new Setter(FocusVisualStyleProperty, focusStyle));
        var radius = new CornerRadius(cornerRadius);
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.CornerRadiusProperty, radius);

        var hover = new FrameworkElementFactory(typeof(Border), "HoverLayer");
        hover.SetValue(Border.BackgroundProperty, UI.Brush(Look.Hex("hover")));
        hover.SetValue(Border.CornerRadiusProperty, radius);
        hover.SetValue(OpacityProperty, 0d);
        hover.SetValue(IsHitTestVisibleProperty, false);

        var press = new FrameworkElementFactory(typeof(Border), "PressLayer");
        press.SetValue(Border.BackgroundProperty, UI.Brush(Look.Hex("pressed")));
        press.SetValue(Border.CornerRadiusProperty, radius);
        press.SetValue(OpacityProperty, 0d);
        press.SetValue(IsHitTestVisibleProperty, false);

        var contentHost = new FrameworkElementFactory(typeof(Border));
        contentHost.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        contentHost.AppendChild(presenter);

        root.AppendChild(chrome);
        root.AppendChild(hover);
        root.AppendChild(press);
        root.AppendChild(contentHost);
        template.VisualTree = root;

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(OpacityProperty, 1d, "HoverLayer"));
        template.Triggers.Add(hoverTrigger);

        var pressTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressTrigger.Setters.Add(new Setter(OpacityProperty, 1d, "PressLayer"));
        template.Triggers.Add(pressTrigger);

        var disabledTrigger = new Trigger { Property = IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(OpacityProperty, 0.55));
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    // Theme-harmonized colors drawn from the active PanelLook (matches Monitor, Usage, Devices & Shelf).
    private string ThemeAccentHex() => SystemParameters.HighContrast ? Look.Primary : Look.Readable(Look.Accent, PanelThemes.GraphicContrast);
    private string DownAccentHex() => SystemParameters.HighContrast ? Look.Primary : Look.Accent;
    private string UpAccentHex()
    {
        if (SystemParameters.HighContrast) return Look.Primary;
        var c = Look.ColorOf("textPrimary");
        return $"#88{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private string ReadableText(string hex) => SystemParameters.HighContrast ? Look.Primary : Look.Readable(hex, PanelThemes.TextContrast);
    private string ReadableGraphic(string hex) => SystemParameters.HighContrast ? Look.Primary : Look.Readable(hex, PanelThemes.GraphicContrast);

    private Brush PrimaryBrush() => UI.Brush(Look.Primary);

    private Brush ClearSecondaryBrush()
    {
        if (SystemParameters.HighContrast) return PrimaryBrush();
        var c = Look.ColorOf("textPrimary");
        var b = new SolidColorBrush(Color.FromArgb(0xCC, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private Brush MutedBrush()
    {
        if (SystemParameters.HighContrast) return PrimaryBrush();
        var c = Look.ColorOf("textPrimary");
        var b = new SolidColorBrush(Color.FromArgb(0x96, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private string DelayTextColor(int? delayMs) => delayMs is null or <= 0 ? Look.Secondary
        : delayMs < 150 ? ReadableText(PositiveHex)
        : delayMs < 400 ? ReadableText(Look.WarningDefault)
        : ReadableText(Look.CriticalDefault);

    private static string FormatConnAndMemory(ProxyConnectionsSummary conn)
    {
        var parts = new List<string>(2) { Loc.T($"{conn.ActiveCount} 连接", $"{conn.ActiveCount} conn") };
        if (conn.MemoryInUseBytes > 0) parts.Add(ProxyControlParsers.FormatSize(conn.MemoryInUseBytes));
        return string.Join(" · ", parts);
    }

    private TextBlock MakeText(string text, double size, Brush foreground, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = foreground,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "FontPanel");
        return block;
    }

    private TextBlock MakeMonoText(string text, double size, Brush foreground, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = foreground,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
        return block;
    }

    // Single-element custom-drawn virtualized node list with Motion-driven smooth wheel scrolling
    // and borderless BrimDeck frosted cards tailored to Standard vs Spacious tiers.
    private sealed class ProxyNodeList : FrameworkElement
    {
        private const double ScrollBarSlot = 9;

        private readonly Action<string, string> _onSelect;
        private readonly Action<string> _onTestNode;
        private readonly Func<string, bool> _isTestingNode;

        private ProxyGroupInfo? _group;
        private bool _spacious;
        private double _scroll;
        private double _targetScroll;
        private double _scrollLastNow;
        private bool _scrolling;
        private int _hoveredIndex = -1;
        private int _pressedIndex = -1;
        private bool _hoverScrollBar;
        private bool _draggingThumb;
        private double _dragStartY, _dragStartScroll;

        private readonly ToolTip _tip = new() { Placement = PlacementMode.Bottom };
        private readonly System.Windows.Threading.DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
        private int _tipIndex = -1;

        private PanelLook Look => PanelLook.Of(this);
        private double CardHeight => _spacious ? 49 : 42;
        private double GapX => _spacious ? 10 : 8;
        private double GapY => _spacious ? 9 : 7;

        public ProxyNodeList(Action<string, string> onSelect, Action<string> onTestNode, Func<string, bool> isTestingNode)
        {
            _onSelect = onSelect;
            _onTestNode = onTestNode;
            _isTestingNode = isTestingNode;
            SnapsToDevicePixels = true;
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            _tip.PlacementTarget = this;
            _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); ShowTip(); };
            Unloaded += (_, _) => { _tipTimer.Stop(); _tip.IsOpen = false; _scrolling = false; };
            SizeChanged += (_, _) => { ClampScroll(); InvalidateVisual(); };
        }

        public void SetSpacious(bool spacious)
        {
            if (_spacious == spacious) return;
            _spacious = spacious;
            ClampScroll();
            InvalidateVisual();
        }

        public void SetGroup(ProxyGroupInfo? group)
        {
            _group = group;
            ClampScroll();
            if (_hoveredIndex >= (_group?.Nodes.Count ?? 0)) _hoveredIndex = -1;
            InvalidateVisual();
        }

        public void ResetScroll()
        {
            _scroll = 0;
            _targetScroll = 0;
            _scrolling = false;
            _hoveredIndex = -1;
            HideTip();
            InvalidateVisual();
        }

        public void ScrollToNode(string nodeName)
        {
            if (_group is null || ActualWidth <= 0 || ActualHeight <= 0) return;
            int idx = -1;
            for (int i = 0; i < _group.Nodes.Count; i++)
            {
                if (string.Equals(_group.Nodes[i].Name, nodeName, StringComparison.Ordinal)) { idx = i; break; }
            }
            if (idx < 0) return;
            int cols = ColumnsFor(ContentWidth);
            int row = idx / cols;
            double rowTop = row * (CardHeight + GapY);
            double target = Math.Clamp(rowTop - Math.Max(0, (ActualHeight - CardHeight) * 0.38), 0, MaxScroll);
            AnimateScrollTo(target);
        }

        private void AnimateScrollTo(double target)
        {
            _targetScroll = Math.Clamp(target, 0, MaxScroll);
            if (!Motion.Enabled || Math.Abs(_targetScroll - _scroll) < 0.5)
            {
                _scroll = _targetScroll;
                _scrolling = false;
                InvalidateVisual();
                return;
            }
            _scrollLastNow = Eased.Now;
            if (!_scrolling)
            {
                _scrolling = true;
                Motion.Start(this, StepScroll);
            }
        }

        private bool StepScroll(double now)
        {
            if (!_scrolling || !IsLoaded) { _scrolling = false; return false; }
            double dt = Math.Clamp(now - _scrollLastNow, 0.001, 0.05);
            _scrollLastNow = now;
            double factor = 1.0 - Math.Exp(-dt * 18.0);
            _scroll += (_targetScroll - _scroll) * factor;
            if (Math.Abs(_targetScroll - _scroll) < 0.35)
            {
                _scroll = _targetScroll;
                _scrolling = false;
                InvalidateVisual();
                return false;
            }
            InvalidateVisual();
            return true;
        }

        private double ContentWidth => Math.Max(40, ActualWidth - (MaxScroll > 0 ? ScrollBarSlot : 0));
        private static int ColumnsFor(double width) => width >= 500 ? 2 : 1;

        private double TotalHeight
        {
            get
            {
                int count = _group?.Nodes.Count ?? 0;
                if (count == 0) return 0;
                double w = Math.Max(40, ActualWidth - ScrollBarSlot);
                int cols = ColumnsFor(w);
                int rows = (count + cols - 1) / cols;
                return rows * CardHeight + Math.Max(0, rows - 1) * GapY;
            }
        }

        private double MaxScroll => Math.Max(0, TotalHeight - ActualHeight);

        private void ClampScroll()
        {
            double max = MaxScroll;
            _scroll = Math.Clamp(_scroll, 0, max);
            _targetScroll = Math.Clamp(_targetScroll, 0, max);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Always absorb wheel scrolling over the node list so panel mouse-wheel page switching never fires here.
            e.Handled = true;
            if (MaxScroll <= 0) return;
            HideTip();
            AnimateScrollTo(_targetScroll - e.Delta * 0.58);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (_draggingThumb)
            {
                double max = MaxScroll;
                if (max > 0 && ThumbTrackHeight > 0)
                {
                    double deltaY = pos.Y - _dragStartY;
                    _scroll = _targetScroll = Math.Clamp(_dragStartScroll + deltaY / ThumbTrackHeight * max, 0, max);
                    _scrolling = false;
                    InvalidateVisual();
                }
                return;
            }
            UpdateHover(pos);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (_draggingThumb) return;
            bool changed = _hoveredIndex != -1 || _hoverScrollBar || _pressedIndex != -1;
            _hoveredIndex = -1;
            _pressedIndex = -1;
            _hoverScrollBar = false;
            HideTip();
            if (changed) InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (MaxScroll > 0 && pos.X >= ActualWidth - ScrollBarSlot)
            {
                _draggingThumb = true;
                _scrolling = false;
                _dragStartY = pos.Y;
                _dragStartScroll = _scroll;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            int idx = HitTestNode(pos);
            if (idx >= 0)
            {
                _pressedIndex = idx;
                e.Handled = true;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_draggingThumb)
            {
                _draggingThumb = false;
                ReleaseMouseCapture();
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            int wasPressed = _pressedIndex;
            _pressedIndex = -1;
            if (wasPressed >= 0 && _group is not null)
            {
                int idx = HitTestNode(e.GetPosition(this));
                if (idx == wasPressed && idx < _group.Nodes.Count)
                {
                    var node = _group.Nodes[idx];
                    if (_group.CanSelect)
                    {
                        if (!string.Equals(_group.Now, node.Name, StringComparison.Ordinal))
                            _onSelect(_group.Name, node.Name);
                    }
                    else
                    {
                        _onTestNode(node.Name);
                    }
                }
                e.Handled = true;
                InvalidateVisual();
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            int idx = HitTestNode(e.GetPosition(this));
            if (idx >= 0 && _group is not null && idx < _group.Nodes.Count)
            {
                e.Handled = true;
                _onTestNode(_group.Nodes[idx].Name);
            }
        }

        private void UpdateHover(Point pos)
        {
            bool overBar = MaxScroll > 0 && pos.X >= ActualWidth - ScrollBarSlot;
            int idx = overBar ? -1 : HitTestNode(pos);
            Cursor = overBar ? Cursors.Arrow : idx >= 0 ? Cursors.Hand : Cursors.Arrow;
            if (idx != _hoveredIndex || overBar != _hoverScrollBar)
            {
                _hoveredIndex = idx;
                _hoverScrollBar = overBar;
                if (idx >= 0)
                {
                    if (_tipIndex != idx) { HideTip(); _tipIndex = idx; _tipTimer.Start(); }
                }
                else
                {
                    HideTip();
                }
                InvalidateVisual();
            }
        }

        private void ShowTip()
        {
            if (_group is null || _tipIndex < 0 || _tipIndex >= _group.Nodes.Count) return;
            var node = _group.Nodes[_tipIndex];
            var hint = _group.CanSelect
                ? Loc.T("左键切换，右键测速", "Click to select, right-click to test")
                : Loc.T("自动策略组，点击测速", "Automatic group, click to test");
            var delay = _isTestingNode(node.Name) ? Loc.T("测速中…", "Testing…") : ProxyControlParsers.FormatDelay(node.DelayMs);
            var meta = string.IsNullOrWhiteSpace(node.Type) ? delay : $"{node.Type} · {delay}";
            _tip.Content = $"{ProxyControlParsers.FormatNodeDisplayName(node.Name)}\n{meta}\n{hint}";
            _tip.IsOpen = true;
        }

        private void HideTip()
        {
            _tipTimer.Stop();
            _tip.IsOpen = false;
            _tipIndex = -1;
        }

        private int HitTestNode(Point pos)
        {
            if (_group is null || _group.Nodes.Count == 0 || pos.X < 0 || pos.Y < 0) return -1;
            double cw = ContentWidth;
            if (pos.X > cw) return -1;
            int cols = ColumnsFor(cw);
            double cardW = (cw - (cols - 1) * GapX) / cols;
            double strideX = cardW + GapX, strideY = CardHeight + GapY;
            int col = (int)(pos.X / strideX);
            if (col < 0 || col >= cols || pos.X - col * strideX > cardW) return -1;
            double y = pos.Y + _scroll;
            int row = (int)(y / strideY);
            if (row < 0 || y - row * strideY > CardHeight) return -1;
            int idx = row * cols + col;
            return idx < _group.Nodes.Count ? idx : -1;
        }

        private double ThumbHeight => MaxScroll <= 0 ? 0 : Math.Max(24, ActualHeight * (ActualHeight / TotalHeight));
        private double ThumbTrackHeight => Math.Max(0, ActualHeight - ThumbHeight - 4);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;
            // Full-bounds transparent hit surface prevents wheel events in card gaps from bubbling to MainWindow.PanelWheel.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var panelFont = TryFindResource("FontPanel") as FontFamily ?? new FontFamily("Segoe UI");
            var monoFont = TryFindResource("FontMono") as FontFamily ?? new FontFamily("Cascadia Mono, Consolas");
            var primaryColor = Look.ColorOf("textPrimary");
            var primaryBrush = UI.Brush(Look.Primary);
            var crispSubBrush = SystemParameters.HighContrast ? primaryBrush : new SolidColorBrush(Color.FromArgb(0xD8, primaryColor.R, primaryColor.G, primaryColor.B));
            var tagBrush = SystemParameters.HighContrast ? primaryBrush : new SolidColorBrush(Color.FromArgb(0x94, primaryColor.R, primaryColor.G, primaryColor.B));

            if (_group is null || _group.Nodes.Count == 0)
            {
                var emptyText = new FormattedText(
                    Loc.T("当前配置中没有可显示的代理节点", "No proxy nodes available"),
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(panelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    12.5, crispSubBrush, dpi);
                dc.DrawText(emptyText, new Point((w - emptyText.Width) / 2, (h - emptyText.Height) / 2));
                return;
            }

            double cw = ContentWidth;
            int cols = ColumnsFor(cw);
            double cardW = Math.Max(20, (cw - (cols - 1) * GapX) / cols);
            double cardH = CardHeight;
            double strideX = cardW + GapX, strideY = cardH + GapY;
            double radius = Look.Corner(10);
            double padX = _spacious ? 13 : 12;
            double nameSize = _spacious ? 13 : 12.5;
            double delaySize = _spacious ? 12 : 11.5;

            int firstRow = Math.Max(0, (int)(_scroll / strideY));
            int totalRows = (_group.Nodes.Count + cols - 1) / cols;
            int lastRow = Math.Min(totalRows - 1, (int)((_scroll + h) / strideY) + 1);

            var fillBrush = UI.Brush(Look.Fill);
            var selectedFillBrush = UI.Brush(Look.FillSelected);
            var hoverBrush = UI.Brush(Look.Hex("hover"));
            var pressedBrush = UI.Brush(Look.Hex("pressed"));
            var accentHex = SystemParameters.HighContrast ? Look.Primary : Look.Readable(Look.Accent, PanelThemes.GraphicContrast);
            var accentBrush = UI.Brush(accentHex);

            var greenBrush = UI.Brush(SystemParameters.HighContrast ? Look.Primary : Look.Readable(PositiveHex, PanelThemes.TextContrast));
            var warnBrush = UI.Brush(SystemParameters.HighContrast ? Look.Primary : Look.Readable(Look.WarningDefault, PanelThemes.TextContrast));
            var badBrush = UI.Brush(SystemParameters.HighContrast ? Look.Primary : Look.Readable(Look.CriticalDefault, PanelThemes.TextContrast));

            var normalFace = new Typeface(panelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var semiBoldFace = new Typeface(panelFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var monoFace = new Typeface(monoFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            bool showProtocol = cardW >= 245;

            for (int r = firstRow; r <= lastRow; r++)
            {
                double y = r * strideY - _scroll;
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (idx >= _group.Nodes.Count) break;
                    var node = _group.Nodes[idx];
                    bool selected = string.Equals(_group.Now, node.Name, StringComparison.Ordinal);
                    bool hovered = idx == _hoveredIndex;
                    bool pressed = idx == _pressedIndex;
                    bool testing = _isTestingNode(node.Name);

                    double x = Math.Round(c * strideX);
                    var rect = new Rect(x, y, Math.Max(0, cardW), cardH);
                    // Clean borderless frosted card matching BrimDeck's native pages.
                    dc.DrawRoundedRectangle(selected ? selectedFillBrush : fillBrush, null, rect, radius, radius);
                    if (pressed) dc.DrawRoundedRectangle(pressedBrush, null, rect, radius, radius);
                    else if (hovered) dc.DrawRoundedRectangle(hoverBrush, null, rect, radius, radius);

                    double textLeft = x + padX;
                    if (selected)
                    {
                        double barH = _spacious ? 20 : 18;
                        dc.DrawRoundedRectangle(accentBrush, null, new Rect(x + 8, y + (cardH - barH) / 2, 3, barH), 1.5, 1.5);
                        textLeft = x + padX + 6;
                    }

                    // Cleared/testing and timed-out nodes both display "-", popping in colored ms once measured.
                    bool hasDelay = !testing && node.DelayMs is > 0;
                    string delayStr = hasDelay ? $"{node.DelayMs} ms" : "-";
                    var delayBrush = !hasDelay ? tagBrush
                        : node.DelayMs < 150 ? greenBrush
                        : node.DelayMs < 400 ? warnBrush
                        : badBrush;
                    var delayFt = new FormattedText(delayStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, monoFace, delaySize, delayBrush, dpi);
                    double rightLimit = x + cardW - padX - delayFt.Width;
                    dc.DrawText(delayFt, new Point(rightLimit, y + (cardH - delayFt.Height) / 2));

                    if (showProtocol && ShortProtocol(node.Type) is { Length: > 0 } proto)
                    {
                        var protoFt = new FormattedText(proto, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, normalFace, 10, tagBrush, dpi);
                        double protoX = rightLimit - 10 - protoFt.Width;
                        if (protoX > textLeft + 92)
                        {
                            dc.DrawText(protoFt, new Point(protoX, y + (cardH - protoFt.Height) / 2));
                            rightLimit = protoX;
                        }
                    }

                    var (regionCode, displayName) = ProxyControlParsers.SplitLeadingRegionFlag(node.Name);
                    double nameLeft = textLeft;
                    FormattedText? regionFt = null;
                    if (regionCode is not null)
                    {
                        regionFt = new FormattedText(regionCode, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            semiBoldFace, nameSize, primaryBrush, dpi);
                        nameLeft += regionFt.Width + 6.5;
                    }

                    double maxNameW = Math.Max(20, rightLimit - nameLeft - 8);
                    var nameFt = new FormattedText(displayName, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        selected ? semiBoldFace : normalFace, nameSize, selected ? primaryBrush : crispSubBrush, dpi)
                    {
                        MaxTextWidth = maxNameW,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis
                    };
                    double nameY = y + (cardH - nameFt.Height) / 2;
                    double baselineY = nameY + nameFt.Baseline;
                    if (regionFt is not null)
                        dc.DrawText(regionFt, new Point(textLeft, baselineY - regionFt.Baseline));
                    dc.DrawText(nameFt, new Point(nameLeft, nameY));
                }
            }

            if (MaxScroll > 0)
            {
                double thumbH = ThumbHeight;
                double thumbY = 2 + (_scroll / MaxScroll) * ThumbTrackHeight;
                double barW = _hoverScrollBar || _draggingThumb ? 5 : 3;
                double barX = w - barW - 1;
                var thumbColor = Look.ColorOf("scrollThumb");
                double opacity = _hoverScrollBar || _draggingThumb ? 0.95 : Look.Current.ThumbRestOpacity;
                var thumbBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(thumbColor.A * opacity), thumbColor.R, thumbColor.G, thumbColor.B));
                dc.DrawRoundedRectangle(thumbBrush, null, new Rect(barX, thumbY, barW, thumbH), barW / 2, barW / 2);
            }
        }

        private static string ShortProtocol(string type) => (type ?? "").Trim().ToLowerInvariant() switch
        {
            "shadowsocks" => "SS",
            "shadowsocksr" => "SSR",
            "hysteria2" => "Hy2",
            "hysteria" => "Hy",
            "vless" => "VLESS",
            "vmess" => "VMess",
            "trojan" => "Trojan",
            "tuic" => "TUIC",
            "wireguard" => "WG",
            "anytls" => "AnyTLS",
            "selector" => Loc.T("策略组", "Group"),
            "urltest" => Loc.T("自动", "Auto"),
            "fallback" => Loc.T("故障转移", "Fallback"),
            "loadbalance" => Loc.T("均衡", "LB"),
            "direct" => Loc.T("直连", "Direct"),
            "reject" => Loc.T("拒绝", "Reject"),
            _ => ""
        };
    }

    // Lightweight custom-rendered dual sparkline harmonized with the active panel theme.
    private sealed class ProxyTrafficChart : FrameworkElement
    {
        private readonly Pen _baselinePen, _downPen, _upPen;
        private readonly Brush _downFill;
        private IReadOnlyList<ProxyTrafficSample> _samples = [];

        public ProxyTrafficChart(string downHex, string upHex, string baselineHex)
        {
            _baselinePen = MakePen(baselineHex, 1);
            _downPen = MakePen(downHex, 1.55);
            _upPen = MakePen(upHex, 1.15);
            var downColor = (Color)ColorConverter.ConvertFromString(downHex);
            var fill = new LinearGradientBrush(Color.FromArgb(0x36, downColor.R, downColor.G, downColor.B), Color.FromArgb(0x04, downColor.R, downColor.G, downColor.B), 90);
            fill.Freeze();
            _downFill = fill;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            ClipToBounds = true;
        }

        private static Pen MakePen(string hex, double thickness)
        {
            var pen = new Pen(UI.Brush(hex), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }

        public void SetSamples(IReadOnlyList<ProxyTrafficSample> samples)
        {
            _samples = samples;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;
            dc.DrawLine(_baselinePen, new Point(0, h - 0.5), new Point(w, h - 0.5));
            if (_samples.Count < 2) return;

            long peak = 1024;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (_samples[i].DownBytesPerSec > peak) peak = _samples[i].DownBytesPerSec;
                if (_samples[i].UpBytesPerSec > peak) peak = _samples[i].UpBytesPerSec;
            }
            double topPad = 2, bottom = h - 1, usableH = Math.Max(2, bottom - topPad);
            int n = _samples.Count;
            double step = w / Math.Max(1, n - 1);

            var downLine = new StreamGeometry();
            var downArea = new StreamGeometry();
            var upLine = new StreamGeometry();

            using (var dl = downLine.Open())
            using (var da = downArea.Open())
            using (var ul = upLine.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double x = i * step;
                    double dy = bottom - usableH * Math.Clamp(_samples[i].DownBytesPerSec / (double)peak, 0, 1);
                    double uy = bottom - usableH * Math.Clamp(_samples[i].UpBytesPerSec / (double)peak, 0, 1);
                    var dp = new Point(x, dy);
                    var up = new Point(x, uy);
                    if (i == 0)
                    {
                        dl.BeginFigure(dp, false, false);
                        da.BeginFigure(new Point(0, bottom), true, true);
                        da.LineTo(dp, false, false);
                        ul.BeginFigure(up, false, false);
                    }
                    else
                    {
                        dl.LineTo(dp, true, true);
                        da.LineTo(dp, false, false);
                        ul.LineTo(up, true, true);
                    }
                }
                da.LineTo(new Point((n - 1) * step, bottom), false, false);
            }
            downLine.Freeze();
            downArea.Freeze();
            upLine.Freeze();

            dc.DrawGeometry(_downFill, null, downArea);
            dc.DrawGeometry(null, _upPen, upLine);
            dc.DrawGeometry(null, _downPen, downLine);
        }
    }
}
