using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using City = BrimDeck.Core.City;
using State = BrimDeck.WeatherData.State;

namespace BrimDeck;

// Open-Meteo weather, stored separately from account and device settings.
internal sealed class WeatherView : UserControl
{
    private readonly App _app;
    private State state;
    // 今日天气按内容高度贴顶、不拉伸；预报区占满余下高度贴底，两者首尾相接，中间不留空白。
    // MinHeight 贴近卡片两行的真实高度（约 86px），不再人为抬高到 112——省下的高度留给折线，
    // 避免下方信息行加高后 room 不足被降到「藏折线」档。折线判断用的就是这个值，必须与内容高度相符。
    private readonly RowDefinition summaryRow = new() { Height = GridLength.Auto, MinHeight = 86 };
    private readonly RowDefinition daysRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    // 页脚行占用原本的下边距 16px：刷新时刻与主来源靠右下对齐，既不挤占上方的读数卡与预报区，
    // 也不改变整页总高度（Fit 里从可用高度扣掉的仍是这 16px）。
    private readonly RowDefinition footerRow = new() { Height = new GridLength(16) };
    private readonly TextBlock footerNote = new()
    {
        FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(174, 196, 220)),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 2, 0)
    };
    private readonly Grid summary = new();
    private readonly UniformGrid stats = new() { Rows = 2, Columns = 2 };
    private readonly UniformGrid forecast = new() { Rows = 1, Columns = 7 };
    private readonly Grid days = new();
    // 折线是预报区里唯一的弹性带：所在行是 Star、画布拉伸填充（MinHeight 保底），多余高度全部由折线吸收。
    // 高度变化被折线连续伸缩消化，而不是靠收起/展开内容行——骨架恒定，抖动在结构上无从产生。
    private readonly Canvas chart = new() { MinHeight = ChartMinHeight, Width = 588, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
    private bool _hourlyMode;    // 底部曲线视图：false=每日高低温，true=当日 24h 逐时
    // 底部「看什么」：卡片点一下换成该指标的逐时曲线，再点一次回到气温。逐日只有气温，所以非气温镜头一律切到逐时。
    private enum Lens { Temp, Feels, Humidity, Wind, Gust, Precip, Pressure, DewPoint, Uv, Cloud, Visibility, Aqi, Celestial }
    private Lens _lens = Lens.Temp;
    // 逐日列内的纵向顺序：星期/日期/转后图标/天气名/高温 →【折线图】→ 低温 →（转前名/同尺寸图标/风级）。折线插在高低温之间。
    private readonly UniformGrid lowRow = new() { Rows = 1, Columns = 7, Height = LowRowHeight };
    // 逐日专用的折线下方信息行，与折线上方镜像：转前天气名在上、28px 转前（白天）图标居中、风向箭头 + 风级在下。
    // 转前转后同天气时上下图标一样，但两行都必须画，保持折线两侧对称；逐时模式折叠，时刻仍留在 lowRow。
    private readonly UniformGrid dayInfoRow = new() { Rows = 1, Columns = 7, Height = 60 };
    private readonly List<TextBlock> tempLabels = new();   // 高/低温文本，供“隐藏高低温”档统一收起
    // 天气名称文本与其所在列的第 4 行：收纳到档 3 时上下两处名称同时收起（行高同步归零），折线保留。
    private readonly List<TextBlock> conditionLabels = new();
    private readonly List<RowDefinition> conditionRows = new();
    // 当前预报区收纳状态：4 完整 / 3 藏天气文字 / 2 藏折线 / 0 整行折叠。
    // 除「天气文字」和「折线」外，图标、风、温度、时刻等一律常驻；连这些也放不下时才整行折叠。
    // 档 1 与档 2 高度相同（StateHeight 对 1/2 同值），降档会直接穿过 1 落到 0，等效于只有两档收纳。
    // 每一档需要的高度是骨架常量之和（StateHeight），决策是 room→状态 的纯函数；
    // 列每次重绘都会重建行定义，状态必须独立保存，重建后原样贴回，否则天气名称会闪回。
    private int _forecastLevel = 4;
    // 「今天」列选择态高亮：垫在 days 底部、跨四行（预报列上段/折线/低温/逐日信息行），从列顶贯穿到最底部，折线与下方各行都被它包住。
    // 显式左对齐 + 左边距定位，与预报网格列几何完全一致，避免 Stretch+Width 时被居中导致的横向偏移。
    private readonly Border todayGlow = new() { CornerRadius = new CornerRadius(15), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
    // 逐时的「现在」不是某一列，而是「过去那一格」与「当前那一格」之间的分界：
    // 一条竖线贯穿列区到底行，线顶标「现在」。这样 22:00 那一格仍然写「22:00」，当前落在两格之间。
    private readonly Border nowRule = new() { Width = 2, CornerRadius = new CornerRadius(1), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly TextBlock nowText = new() { Text = "", Width = 40, FontSize = 9.5, TextAlignment = TextAlignment.Center, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    internal static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(15);
    private readonly DispatcherTimer timer = new() { Interval = AutoInterval };
    private WeatherSnapshot? _last;
    // 重画节流：拖动改宽度时 Fit 每帧都会被调用，而重建 24 列的列元素与折线并不便宜。
    // 画布宽度与列数当场更新，真正重建最多 30fps，落后的一帧由 _flush 补上。
    private long _drawnAt;
    private bool _redrawPending;
    private DispatcherTimer? _flush;
    private double _lastAvail = -1;       // 上次参与状态决策的可用高度，高度没变就跳过整段决策
    private string _autoCityName = "";   // 最近一次自动定位到的城市
    private int _columns = 7;
    private double _chartWidth;
    private int _summaryCols = 2;
    private int generation;
    // 视口即内容区，宽度与高度随 Fit 传入值整体填充。预报区骨架恒定（各行行高固定、图标恒定 28px），
    // 折线行是唯一弹性带；高度不足按 4→3→2→1→0 逐级收纳：4 完整 → 3 藏上下天气名称 → 2 藏折线 → 1 藏高低温 → 0 整行折叠。
    internal void Fit(double width, double height, bool force = false)
    {
        Width = width; Height = height;
        // 顶部读数卡随宽度在 2–6 列之间逐列增减；跨档时重建摘要。
        int summaryCols = SummaryColumns(width);
        if (summaryCols != _summaryCols)
        {
            _summaryCols = summaryCols;
            if (_last is { } summarySnap) DrawSummary(summarySnap);
        }
        double avail = Math.Max(0, height - 16);                      // 根 Grid 上边距 0 + 底部页脚行 16
        summaryRow.MinHeight = Math.Min(summaryMinHeight, avail);     // 今日天气最小高度封顶，避免自身被压坏
        // 列数随宽度在 7（窄）到 24（满宽 440–1200 线性插值）之间自适应；24 列正好是历史 10 + 今天 + 未来 13。
        // 折线画布与预报网格同一套几何：左右边距同为 6、内容宽度同为 width-40，点坐标按列中心计算，保证每个点对准列正中。
        double chartWidth = Math.Max(30, width - 40);
        int columns = Math.Clamp(7 + (int)Math.Round((width - 440) / 760.0 * 17), 7, 24);
        // 列数收敛：每日模式随宽度在 7–24 天内自适应，24 列正好是历史 10 + 今天 + 未来 13；
        // 24h 模式固定 24 列对应 24 个逐时数据点（数据不足时按实际逐时数收敛），保证「24 个日期 ↔ 24 个时刻」一一对应。
        // 逐时与逐日共用同一套列数阶梯：宽度压缩到 400 以下就只留 7 列，绝不把 24 个时刻硬挤进去。
        // 列数定了之后，预报列、折线采样点、逐时/逐日两种模式全部按同一个列宽几何排，点永远落在列正中。
        int shown = _last is { } available
            ? (_hourlyMode ? Math.Min(columns, Math.Max(1, available.HourTimes.Length)) : Math.Min(columns, available.DayDates.Length))
            : columns;
        int shownValue = Math.Max(1, shown);
        bool columnsChanged = columns != _columns, shownChanged = shownValue != forecast.Columns;
        bool heightChanged = Math.Abs(avail - _lastAvail) > 0.5;
        bool redraw = force || columnsChanged || Math.Abs(chartWidth - _chartWidth) > 0.5;
        _columns = columns; _chartWidth = chartWidth; forecast.Columns = shownValue; lowRow.Columns = shownValue; dayInfoRow.Columns = shownValue; chart.Width = chartWidth;
        // 拖动调宽时这里每帧都会进来。画布宽度与列数立刻生效，重建则压到 30fps：
        // 一帧里重建 24 列的列元素加整条折线要几毫秒，120Hz 下就是持续掉帧，看起来正是「卡」。
        if (redraw && _last is { } f)
        {
            long now = Environment.TickCount64;
            if (force || now - _drawnAt >= 33) { _drawnAt = now; _redrawPending = false; DrawForecast(f); DrawChart(f); }
            else { _redrawPending = true; FlushLater(); }
        }
        // 列数、列内数据量、可用高度都没变，状态就不可能变：跳过整段决策。
        if (!(force || columnsChanged || shownChanged || heightChanged)) return;
        _lastAvail = avail;
        // 今日天气高度：days 可见时是 Auto 行，ActualHeight 就是内容高度；days 折叠（档 0）时 summary 接管
        // 弹性行被拉伸居中，ActualHeight 等于整块可用高度、不是内容高度——改用 DesiredSize（布局测量产物=内容高）。
        // 若仍用 ActualHeight，room≈0，档 0 永远等不到升档排期，窗口再拉大预报区也回不来。
        // room 按 4px 向下量化：亚 4px 的高度振荡不参与状态判断。
        double summaryH = days.Visibility == Visibility.Visible ? summary.ActualHeight : summary.DesiredSize.Height;
        double room = Math.Floor((avail - Math.Max(summaryRow.MinHeight, summaryH)) / 4) * 4;   // 实际可让给预报区的空间
        UpdateForecastState(room);
        SyncSummaryAlignment();
    }
    // 今日天气内容不多，按内容高度贴顶即可，不跟着面板拉伸；多出来的高度全部交给下方预报区去填。
    // 只有当面板矮到放不下预报条、只剩今日天气时，才让今日天气接管弹性行并居中，避免贴着页头、下面空一大片。
    private void SyncSummaryAlignment()
    {
        bool showDays = days.Visibility == Visibility.Visible;
        summaryRow.Height = showDays ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        daysRow.Height = showDays ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var place = showDays ? VerticalAlignment.Top : VerticalAlignment.Center;
        foreach (UIElement child in summary.Children)
        {
            if (child is FrameworkElement element && element.VerticalAlignment != place) element.VerticalAlignment = place;
        }
    }
    // 把当前状态原样贴到列元素上。列在每次重绘时都会 Clear + 新建行定义，行高与可见性回到默认值；
    // 重绘后必须重贴，否则天气名称/折线会闪回，直到下一次 Fit 才收起——拖动高度时看到的就是这个闪动。
    // 图标尺寸恒定 28px 不随档位缩放：骨架只有「名称行的有无」一个变量，其余全部交给折线行伸缩。
    private void ApplyForecastLevel()
    {
        int level = _forecastLevel;
        bool showCondition = level >= 4;
        // 天气名行高按模式取：逐时列的名称行与逐日不是同一行号。
        var rows = _hourlyMode ? HourlyRowHeights : ColumnRowHeights;
        double conditionHeight = showCondition ? rows[_hourlyMode ? HourlyConditionRow : DailyConditionRow] : 0;
        foreach (var row in conditionRows) row.Height = new GridLength(conditionHeight);
        foreach (var label in conditionLabels) label.Visibility = showCondition ? Visibility.Visible : Visibility.Collapsed;

        var chartVisibility = level >= 3 ? Visibility.Visible : Visibility.Collapsed;
        bool chartToggled = chart.Visibility != chartVisibility;
        chart.Visibility = chartVisibility;
        bool showTemp = level >= 2 && _lens != Lens.Celestial;   // 日月镜头有自己的底部时刻行，低温/时刻行让位
        // 逐时与逐日必须同进退：lowRow 在逐日放低温、在逐时放时刻，只要显隐规则一致，days 的骨架就恒定。
        lowRow.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        // 逐日信息行只在「有低温行 + 逐日模式」出现；切到逐时后时刻独占 lowRow，这一行收起。
        dayInfoRow.Visibility = showTemp && !_hourlyMode ? Visibility.Visible : Visibility.Collapsed;
        // 信息行只有两档：完整（天气名 + 28px 图标 + 风）与紧凑（图标 + 风）。
        dayInfoRow.Height = showCondition ? DayInfoFull : DayInfoCompact;
        foreach (var p in tempLabels) p.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = level >= 1 ? Visibility.Visible : Visibility.Collapsed;
        // 折线行重新出现时行高从 0 展开，画布还没按新高度画过：立刻补一版，布局后 SizeChanged 会再校准一次。
        if (chartToggled && chartVisibility == Visibility.Visible && _last is { } snap) DrawChart(snap);
    }
    // 各收纳状态需要的高度：骨架行高全是常量（ColumnRowHeights 与各行固定高），状态高度因此也是常量。
    // 估算里加了少量安全余量：宁可早一档收起，也不让内容溢出弹性行——决策不需要任何实测。
    private double StateHeight(int level)
    {
        if (level <= 0) return 0;
        // 预报列上段：两套骨架按模式取，合计去掉天气名行（档 3 起收起），外加列 Border 与 forecast 边距。
        // 逐时列多一行降水概率（+14px），收档阈值相应更高；切逐时后 room 不足会先于逐日收档，属预期取舍。
        var rows = _hourlyMode ? HourlyRowHeights : ColumnRowHeights;
        int condRow = _hourlyMode ? HourlyConditionRow : DailyConditionRow;
        double h = ColumnInset + BandSafety;
        for (int i = 0; i < rows.Length; i++) h += i == condRow && level < 4 ? 0 : rows[i];
        if (level >= 3) h += ChartMinHeight + ChartTopMargin + BandSafety;  // 折线行按最小高度计，富余高度由它吸收
        if (level >= 1)
        {
            h += LowRowHeight + BandSafety;                                 // 低温/时刻行：除整行折叠外常驻
            if (!_hourlyMode) h += (level >= 4 ? DayInfoFull : DayInfoCompact) + BandSafety;   // 逐日信息行：名称随天气文字同进退，图标与风常驻
        }
        return h;
    }
    // 状态决策：room 的纯函数，双向即时、阈值不对称。降档在 room < StateHeight(level) 时立即执行
    // （破版比早收更糟）；升档要求 room ≥ StateHeight(target) + UpgradeMargin 也立即执行——两条阈值
    // 之间的 8px 死区隔开了升降边界，手抖与量化噪声在死区里不会引起任何状态变化。
    // 旧版升档靠 160ms 去抖定时器，那是测量反馈回路（已删除）时代必须的保险；恒定骨架下 room 不依赖
    // 预报状态、无回路可振荡，去抖只剩「拖动期间每帧重启定时器、必须停手才回升」的副作用，故弃用。
    private void UpdateForecastState(double room)
    {
        while (_forecastLevel > 0 && StateHeight(_forecastLevel) > room) _forecastLevel--;
        while (_forecastLevel < 4 && StateHeight(_forecastLevel + 1) + UpgradeMargin <= room) _forecastLevel++;
        ApplyForecastLevel();
    }
    // 节流窗口里被跳过的那一次重画：33ms 后补上。拖到一半停手、或拖动在节流窗口内结束，都不会留下半截折线。
    private void FlushLater()
    {
        if (_flush is not null) return;
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _flush = timer;
        timer.Tick += (_, _) =>
        {
            if (_redrawPending)
            {
                _redrawPending = false; _drawnAt = Environment.TickCount64;
                if (_last is { } s) { DrawForecast(s); DrawChart(s); }
            }
            if (_redrawPending) return;
            _flush = null; timer.Stop();
        };
        timer.Start();
    }
    // 弹性折线的随高重画：画布高度被拉伸改变（拖高度、升降收纳档）时按新几何重画，33ms 节流与整页重画同一档。
    private void ScheduleChartRedraw()
    {
        if (_last is null) return;
        long now = Environment.TickCount64;
        if (now - _drawnAt >= 33) { _drawnAt = now; DrawChart(_last); }
        else { _redrawPending = true; FlushLater(); }
    }
    private const double summaryMinHeight = 86;   // 今日天气卡片两行的真实高度，与 summaryRow 初始值一致，让 room 估算准确
    private const double ChartMinHeight = 46;     // 折线画布最小高度：上下各留标签与圆点的空间，低于此折线档装不下
    // —— 骨架尺寸的唯一事实来源：ApplyForecastLevel 贴行高、StateHeight 估状态高度，两边引用同一组常量。
    // 估算 = 真实开销 + BandSafety。要改任何一行尺寸只改这里（及列 Border 的 Padding），决策与呈现永远不会漂移。
    private const double LowRowHeight = 20;     // 低温/时刻行（低温与高温同字号 15px，行高相应加大）
    private const double DayInfoFull = 58;      // 逐日信息行·完整（天气名 + 28px 图标 + 风）
    private const double DayInfoCompact = 44;   // 逐日信息行·紧凑（图标 + 风）
    private const double ChartTopMargin = 2;    // 折线画布上边距
    private const double ColumnInset = 6;       // 预报列上段真实开销：forecast 上边距 2 + 列 Border 上下 Padding 4
    private const double BandSafety = 2;        // 每条横带的安全余量：宁可早一档收起，也不让内容溢出弹性行
    private const double UpgradeMargin = 8;     // 升档死区：升档要求比该档自身需求再多 8px 富余，与降档阈值隔开，吸收手抖与 4px 量化噪声
    // 折线画布的当前实际高度：由弹性行拉伸决定；尚未完成首帧布局时退回最小高度。
    private double ChartBand() { double h = chart.ActualHeight; return !double.IsNaN(h) && h >= 20 ? h : ChartMinHeight; }
    private readonly Grid host = new();
    private bool fetching;
    private CancellationTokenSource? _token;
    private DateTimeOffset _cycleStart = DateTime.UtcNow;
    internal bool IsFetching => fetching;
    internal DateTimeOffset CycleStart => _cycleStart;
    internal event Action? StateChanged;
    private void RaiseState() => StateChanged?.Invoke();

    public WeatherView(App app)
    {
        _app = app;
        Width = 620; Height = 290;
        Background = Brushes.Transparent; Foreground = Brushes.White;
        state = WeatherData.Load(() => _app.Settings);
        // Grid 两行：今日天气(Auto 按内容贴顶) / 底部预报(Star 填满余下高度)，宽度与高度都随 Fit 传入值整体填充。
        // 今日天气内容不多，按内容高度就够；多出来的高度全部给下方预报区，让它上沿紧接天气、下沿贴住页脚，
        // 中间不再留一大块空白。底部 16px 由页脚行占住（刷新时刻与来源），与用量页列表底部基准一致。
        // 上边距 0：页头那一行（36）本身就是内容的上沿，用量页也是把内容直接贴在页头下沿的
        // （UsageContent.Margin 顶边为 0，列内 ColumnTopInset = 2）。这里再留 6 会和用量页形成两套基准。
        var root = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        root.RowDefinitions.Add(summaryRow);
        root.RowDefinitions.Add(daysRow);
        root.RowDefinitions.Add(footerRow);
        Grid.SetRow(summary, 0); Grid.SetRow(days, 1); Grid.SetRow(footerNote, 2);
        root.Children.Add(summary); root.Children.Add(days); root.Children.Add(footerNote);
        host.Children.Add(root); Content = host;
        // days 内部四行上下排列：预报列上段(星期/日期/图标/天气名称/高温)贴顶、折线图占据中间弹性行、
        // 低温/时刻行在下、逐日再看一行的「转前天气名 + 同尺寸图标 + 风向风级」贴底，与折线上方镜像。折线行吸收预报区多出来的高度，
        // 让底部信息行紧贴窗口下沿，而不是所有内容堆在顶部、下面空一大块。
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 当前位置高亮垫底：跨四行(上段/折线/低温/逐日信息行)，Forecast、折线与底部各行都画在它上面。
        // 逐日高亮「今天」那一列，逐时高亮「现在」那一列——同一块 Border、同一套定位公式，两种模式共用。
        Grid.SetRow(todayGlow, 0); Grid.SetRowSpan(todayGlow, 4); days.Children.Add(todayGlow);
        // 「现在」分界线垫在同一层：竖线在两格之间、文字在列区上方那两行空位里，都不参与命中测试。
        Grid.SetRow(nowRule, 0); Grid.SetRowSpan(nowRule, 4); days.Children.Add(nowRule);
        forecast.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(forecast, 0); days.Children.Add(forecast);
        // 折线画布与预报网格左边距同为 6px 显式左对齐，画布原点与列起点完全重合，点坐标按列中心计算。
        chart.Margin = new Thickness(6, 2, 6, 0); chart.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(chart, 1); days.Children.Add(chart);
        // 弹性折线：画布被拉伸改变高度时（拖高度、升降收纳档）按新高度重画，节流 33ms 与整页重画同一档。
        chart.SizeChanged += (_, _) => ScheduleChartRedraw();
        // 整块底部（列 + 折线 + 低温行）就是一个切换区：鼠标移上去变手型，点一下在「逐时」与「逐日」之间互换。
        // 不设开关按钮——底部本身就是开关，和卡片镜头互不干扰（卡片改看什么，底部改看多久）。
        days.Cursor = System.Windows.Input.Cursors.Hand;
        days.Background = Brushes.Transparent;                       // 透明也要有画刷，否则空白处收不到命中测试
        days.MouseLeftButtonUp += (_, e) =>
        {
            if (_last is not { } snap || snap.HourTimes.Length == 0) return;
            _hourlyMode = !_hourlyMode;
            _lens = Lens.Temp;                                        // 切回逐日时没有非气温镜头，顺手复位
            Fit(Width, Height, true); DrawSummary(snap);
            e.Handled = true;
        };
        lowRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(lowRow, 2); days.Children.Add(lowRow);
        dayInfoRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(dayInfoRow, 3); days.Children.Add(dayInfoRow);
        Grid.SetRow(nowText, 0); Grid.SetRowSpan(nowText, 2); days.Children.Add(nowText);
        // 首帧与切页都走同一个入口：有请求在飞时直接返回，数据还在自动周期内就跳过，
        // 不再让 Loaded 与 IsVisibleChanged 各发一次、后者把前者取消。
        IsVisibleChanged += async (_, _) => { if (IsVisible) { state = WeatherData.Load(() => _app.Settings); await EnsureFreshAsync(); } };
        Loaded += async (_, _) => { await EnsureFreshAsync(); timer.Start(); };
        // 15 分钟周期是硬触发；正在手动刷新时让它跑完，不打断。
        timer.Tick += async (_, _) => { if (!fetching) await Refresh(); };
        Unloaded += (_, _) => { timer.Stop(); generation++; };
    }

    private async Task EnsureFreshAsync()
    {
        if (fetching) return;
        if (_last is { } snapshot && DateTimeOffset.Now - snapshot.FetchedAt < AutoInterval) return;
        await Refresh();
    }

    internal Task ManualRefresh()
    {
        if (fetching) { Cancel(); return Task.CompletedTask; }
        return Refresh(force: true);
    }
    internal void Cancel() => _token?.Cancel();
    // force：手动刷新要真的发请求。自动轮询照常吃缓存（实况 10 分钟、逐时 30 分钟、逐日 2 小时各自过期），
    // 否则点下去只是在 TTL 内再把同一份缓存画一遍，看起来就是「刷新没反应」。
    private async Task Refresh(bool force = false)
    {
        _token?.Cancel();
        var cts = new CancellationTokenSource();
        _token = cts;
        var token = cts.Token;
        int request = ++generation; var selected = state.Selected;
        // 缓存键一律是实际城市键：手动选城市用所选城市的键，自动定位用 AutoKey 指向的键；
        // "auto" 只在 AutoKey 里做映射，不是数据键。AutoKey 为空（首启动）时跳过缓存预渲染。
        string key = selected >= 0 && selected < state.Cities.Count ? state.Cities[selected].ToString() : state.AutoKey;
        if (key.Length > 0 && state.Cache.TryGetValue(key, out var cached)) Render(cached);
        fetching = true; RaiseState();
        try {
            using var fresh = force ? WeatherCache.Fresh() : null;
            City city;
            if (selected >= 0 && selected < state.Cities.Count) city = state.Cities[selected];
            else city = await WeatherData.LocateAsync(state.Schedule, token) ?? new City("当前位置", 0, 0);
            // 旧请求交给 token 中断；generation 只在部分结果回写时挡一次，避免迟到的首屏覆盖新请求。
            var snapshot = await WeatherData.CurrentAsync(city, state.Schedule, token, partial => { if (request == generation) Render(partial); });
            Render(snapshot);
            // 定位/选择出的实际城市即缓存键：自动定位的快照也按位置存，AutoKey 记下映射。
            key = city.ToString();
            state.Cache[key] = snapshot;
            if (AutoSelected) state.AutoKey = key;
            WeatherData.Save(state); _cycleStart = DateTime.UtcNow;
        } catch (OperationCanceledException) {
            // 取消即本轮作废，界面与状态由新的 Refresh 接管。
        } catch { if (request == generation) { summary.Children.Clear(); summary.Children.Add(new TextBlock { Text = Loc.T("天气数据不可用", "Weather data unavailable"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center }); } }
        finally { if (ReferenceEquals(_token, cts)) { fetching = false; _token = null; } RaiseState(); }
    }
    internal static string Condition(int code) => code switch
    {
        0 => Loc.T("晴", "Sunny"), 1 or 2 => Loc.T("多云", "Cloudy"), 3 => Loc.T("阴", "Overcast"),
        45 or 48 => Loc.T("雾", "Foggy"), >= 51 and <= 67 => Loc.T("雨", "Rain"), >= 71 and <= 77 => Loc.T("雪", "Snow"),
        >= 80 and <= 82 => Loc.T("阵雨", "Showers"), 85 or 86 => Loc.T("阵雪", "Snow showers"), >= 95 => Loc.T("雷雨", "Thunderstorm"), _ => Loc.T("多云", "Cloudy")
    };
    // 天空渐变随昼夜与天气切换：晴昼蓝、阴霾灰、雨夜深蓝灰、雷雨紫，夜晚统一深邃蓝黑。
    internal static LinearGradientBrush Sky(int code, bool day)
    {
        var (a, b) = day ? code switch {
            <= 2 => (Color.FromRgb(31, 77, 143), Color.FromRgb(85, 127, 175)),
            3 => (Color.FromRgb(74, 86, 104), Color.FromRgb(113, 128, 150)),
            45 or 48 => (Color.FromRgb(96, 102, 114), Color.FromRgb(140, 148, 160)),
            >= 95 => (Color.FromRgb(59, 46, 86), Color.FromRgb(110, 91, 138)),
            >= 71 and <= 77 or 85 or 86 => (Color.FromRgb(93, 122, 154), Color.FromRgb(176, 196, 222)),
            _ => (Color.FromRgb(44, 58, 77), Color.FromRgb(93, 109, 126))
        } : (Color.FromRgb(13, 18, 48), Color.FromRgb(35, 40, 74));
        var brush = new LinearGradientBrush(a, b, 150);
        brush.Freeze(); return brush;
    }
    private void Render(WeatherSnapshot s)
    {
        try {
            Background = Brushes.Transparent;
            _last = s;
            if (AutoSelected) _autoCityName = s.Name;
            DrawFooter(s); DrawSummary(s); DrawForecast(s); DrawChart(s);
        } catch { }
    }
    // 顶部读数卡列数：随宽度在 2–6 列之间逐列增减，每加宽约 120px 多一列。
    // 两行 × N 列，同一列上下是一对相关读数；6 列时铺满 current 非元数据字段。
    private static int SummaryColumns(double width) => width >= 1000 ? 6 : width >= 880 ? 5 : width >= 760 ? 4 : width >= 640 ? 3 : 2;

    // 风速卡的标题直接带上 8 方位风向与风级，省一张卡；没有风向文字（如 Open-Meteo）时按度数归一。
    private string WindTitle(WeatherSnapshot s)
    {
        string dir = s.WindDirection;
        if (string.IsNullOrWhiteSpace(dir) && !double.IsNaN(s.WindDirectionDeg))
            dir = WeatherCodes.DegreeToCompass(s.WindDirectionDeg) + Loc.T("风", "");
        if (string.IsNullOrWhiteSpace(dir)) return Loc.T("风速", "Wind");
        return s.WindScale >= 0 ? $"{dir} {s.WindScale}{Loc.T("级", " Bft")}" : dir;
    }

    // 降水类型只显示类型名本身，图标按类型区分；未知/空保留通用“降水”，无降水用云朵而非水滴。
    private static string PrecipName(string type) => type switch
    {
        "rain" => Loc.T("雨", "Rain"),
        "snow" => Loc.T("雪", "Snow"),
        "ice" => Loc.T("冰粒", "Ice pellets"),
        "mixed" => Loc.T("雨夹雪", "Mixed"),
        "none" => Loc.T("无降水", "Dry"),
        _ => Loc.T("降水", "Precip")
    };
    private static WeatherMetaKind PrecipIcon(string type) => type switch
    {
        "rain" => WeatherMetaKind.Rain,
        "snow" => WeatherMetaKind.Snow,
        "ice" => WeatherMetaKind.Ice,
        "mixed" => WeatherMetaKind.Mixed,
        "none" => WeatherMetaKind.NoPrecip,
        _ => WeatherMetaKind.Precipitation
    };
    private static string PrecipTitle(WeatherSnapshot s) => PrecipName(s.PrecipType);

    // 空气卡：标题行放等级文字（优/良/轻度污染…），主数值放 AQI。和风给 category 就直接用，
    // 没有时按国标数值兜底算出等级；AQI 缺失时标题退回「空气」、主数值显示 --。
    private static string AirTitle(WeatherSnapshot s)
        => s.Aqi < 0 ? Loc.T("空气", "Air") : s.AqiText.Length > 0 ? s.AqiText : WeatherCodes.AqiText(s.Aqi);
    private static string AirValue(WeatherSnapshot s) => s.Aqi >= 0 ? $"AQI: {s.Aqi}" : "--";

    private string? PrecipTip(WeatherSnapshot s)
        => double.IsNaN(s.PrecipRateMmH) && string.IsNullOrWhiteSpace(s.PrecipType)
            ? null
            : Loc.T($"强度 {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}", $"Rate {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}");
    // 左上：大图标 + 大字温度 + 天气描述 + 高低温 + 来源；右上：随宽度扩展的实况读数卡（体感/湿度/风速…日月入口）。
    private void DrawSummary(WeatherSnapshot s)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        summary.RowDefinitions.Clear();
        int today = WeatherData.TodayIndex(s);    // 正序数组中“今天”的位置
        int cols = _summaryCols = SummaryColumns(Width);
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cols * 105) });
        summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 今日天气内容顶对齐：内容不多，不跟着面板拉伸，多出的高度留给下方预报区填充。
        // 垂直基准由 SyncSummaryAlignment 统一管理（只剩今日天气时才改为居中）。
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top };
        // 左上角大图标是详情入口：悬浮播放天气动效，点击打开详情浮层。
        var heroGlyph = new WeatherGlyph(s.WeatherCode, s.IsDay, animated: true)
        {
            Width = 56, Height = 56, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T("点击查看天气详情", "Click for weather details")
        };
        AutomationProperties.SetName(heroGlyph, Loc.T("天气详情", "Weather details"));
        WeatherMotion.Attach(heroGlyph);
        heroGlyph.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowDetails(s); };
        hero.Children.Add(heroGlyph);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(new TextBlock { Text = Degree(s.Temperature), FontSize = 44, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        // 今日高低温取自 daily 数组（当天域），与实况温度分属两个数据域：实况来源成功而当天来源
        // 失败时数组可能为空，直接索引会越界异常、被 Render 的 catch 吞成整页空白。缺当天就显示
        // 占位符，实况大温度/图标/四张卡照常渲染——两个域互不牵连。
        bool hasDay = today < s.DayHighC.Length && today < s.DayLowC.Length;
        string range = hasDay ? $"{Degree(s.DayHighC[today])} / {Degree(s.DayLowC[today])}" : "-- / --";
        bottom.Children.Add(new TextBlock { Text = range, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(205, 224, 247)), VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(bottom);
        // 来源与刷新时刻统一放在右下角页脚，摘要卡不再单占一行，左侧因此更紧凑。
        hero.Children.Add(side);
        summary.Children.Add(hero);
        // 读数卡与左侧大图标同一基准顶对齐，多列时上沿才会齐平。
        stats.Rows = 2; stats.Columns = cols;
        stats.Children.Clear(); stats.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(stats, 1); summary.Children.Add(stats);
        string rise = Clock(s.SunriseTimes, today), set = Clock(s.SunsetTimes, today);
        // 同一列上下放相关读数：风速/阵风、体感/露点、湿度/降水、气压/能见度、空气/紫外线、云量/日月。
        // 风向/风级并入风速卡，降水类型/强度并入降水卡；日升/日落是镜头入口，始终放在最右下角。
        // UniformGrid 按行填充：先写满上排再写下排，同一列的上下两张才会成对。
        (WeatherMetaKind kind, Color ink, string label, string value, Lens? lens, string? tip)[] upper =
        [
            (WeatherMetaKind.WindSock, Color.FromRgb(140, 200, 220), WindTitle(s), Num(s.WindKmh, " km/h"), Lens.Wind, null),
            (WeatherMetaKind.Thermo, Color.FromRgb(255, 176, 102), Loc.T("体感", "Feels like"), Num(s.FeelsLike, "°"), Lens.Feels, null),
            (WeatherMetaKind.Droplet, Color.FromRgb(112, 178, 255), Loc.T("湿度", "Humidity"), s.HumidityPercent >= 0 ? $"{s.HumidityPercent}%" : "--", Lens.Humidity, null),
            (WeatherMetaKind.Gauge, Color.FromRgb(168, 186, 208), Loc.T("气压", "Pressure"), Num(s.PressureHpa, " hPa"), Lens.Pressure, null),
            (WeatherMetaKind.AirQuality, Color.FromRgb(126, 208, 154), AirTitle(s), AirValue(s), Lens.Aqi, null),
            (WeatherMetaKind.Cloud, Color.FromRgb(196, 214, 232), Loc.T("云量", "Cloud"), Num(s.CloudPercent, "%"), Lens.Cloud, null)
        ];
        (WeatherMetaKind kind, Color ink, string label, string value, Lens? lens, string? tip)[] lower =
        [
            (WeatherMetaKind.Gust, Color.FromRgb(126, 212, 226), Loc.T("阵风", "Gusts"), Num(s.WindGustKmh, " km/h"), Lens.Gust, null),
            (WeatherMetaKind.DewPoint, Color.FromRgb(88, 146, 220), Loc.T("露点", "Dew point"), Num(s.DewPointC, "°"), Lens.DewPoint, null),
            (PrecipIcon(s.PrecipType), Color.FromRgb(96, 168, 246), PrecipTitle(s), Num(s.PrecipitationMm, " mm", 1), Lens.Precip, PrecipTip(s)),
            (WeatherMetaKind.Eye, Color.FromRgb(150, 204, 232), Loc.T("能见度", "Visibility"), Num(s.VisibilityKm, " km", 1), Lens.Visibility, null),
            (WeatherMetaKind.Uv, Color.FromRgb(255, 206, 112), Loc.T("紫外线", "UV"), Num(s.UvIndex, ""), Lens.Uv, null),
            (WeatherMetaKind.Sunrise, Color.FromRgb(255, 200, 90), Loc.T($"日升 {rise}", $"Sunrise {rise}"), Loc.T($"日落 {set}", $"Sunset {set}"), Lens.Celestial, null)
        ];
        var tiles = new List<(WeatherMetaKind kind, Color ink, string label, string value, Lens? lens, string? tip)>(2 * cols);
        for (int i = 0; i < cols; i++) tiles.Add(upper[i]);
        // 最右列下排始终是日月入口；其余下排按同一序号与上排配对。
        for (int i = 0; i < cols; i++) tiles.Add(i == cols - 1 ? lower[^1] : lower[i]);
        foreach (var (kind, ink, label, value, lens, tip) in tiles)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 字号按实测宽度定：格宽 105，减内边距 14、描边 2、图标 15 + 间距 5 后，文字可用约 69。
            text.Children.Add(new TextBlock { Text = label, FontFamily = UI.PanelFont, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(169, 189, 214)), Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = value, FontFamily = UI.PanelFont, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap });
            var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            // 风速卡用 8 方位箭头指示风向；箭头优先跟标题文字同档，文字缺失时才按角度分档。
            // 其余卡用各自的元信息图标。
            double windAngle = WeatherCodes.Wind8Angle(s.WindDirection, s.WindDirectionDeg);
            if (lens == Lens.Wind && !double.IsNaN(windAngle))
            {
                var windArrow = WindArrowIcon(windAngle, ink);
                windArrow.Margin = new Thickness(0, 0, 5, 0);
                body.Children.Add(windArrow);
            }
            else
                body.Children.Add(new WeatherMetaIcon(kind, ink) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            body.Children.Add(text);
            bool active = lens is { } chosen && _lens == chosen;
            // 这些卡是同一组读数，底色统一用「白色低透明」叠在天空渐变上：哪一张都不抢眼，
            // 天空换季（晴/雨/夜）时也不用为每张卡单独配深色底。选中态才是唯一的例外——蓝色淡底 + 蓝描边。
            // 圆角 14 在只有 44px 高的格子里几乎成了胶囊，收到 10 才像卡片而不是药丸。
            Color fillIdle = active ? Color.FromArgb(44, 116, 172, 255) : Color.FromArgb(20, 255, 255, 255);
            Color fillHot = active ? Color.FromArgb(62, 128, 186, 255) : Color.FromArgb(36, 255, 255, 255);
            Color edgeIdle = active ? Color.FromArgb(140, 120, 178, 255) : Color.FromArgb(22, 255, 255, 255);
            Color edgeHot = active ? Color.FromArgb(196, 142, 196, 255) : Color.FromArgb(54, 255, 255, 255);
            var fill = new SolidColorBrush(fillIdle); var edge = new SolidColorBrush(edgeIdle);
            var tile = new Border
            {
                Background = fill, BorderBrush = edge,
                CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Padding = new Thickness(7, 4, 7, 4), Child = body,
                BorderThickness = new Thickness(1)
            };
            if (tip is not null) tile.ToolTip = tip;
            // 悬浮只换底色与描边，就是普通按钮的反馈：不缩放、不位移。
            // 卡片在窄窗口 2×2 里彼此只隔 6px，整体放大 1.035 会顶到邻卡、看着像在互相推挤；
            // 里面的图标也被同一个放大带着抖，所以这里连 WeatherMotion 都不挂。
            void Ink(Color toFill, Color toEdge)
            {
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toFill, TimeSpan.FromMilliseconds(110)));
                edge.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toEdge, TimeSpan.FromMilliseconds(110)));
            }
            // 只有绑定镜头的卡可点；其余卡保持静态，避免“能点却没反应”的错觉。
            if (lens is { } pick)
            {
                tile.Cursor = System.Windows.Input.Cursors.Hand;
                tile.MouseEnter += (_, _) => Ink(fillHot, edgeHot);
                tile.MouseLeave += (_, _) => Ink(fillIdle, edgeIdle);
                tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; PickLens(pick); };
            }
            stats.Children.Add(tile);
        }
        // 对齐规则集中在 SyncSummaryAlignment：有预报条时顶对齐，收起时居中。
        SyncSummaryAlignment();
    }

    // 卡片镜头：同一张卡再点一次收回气温。逐日只有高低温，其余指标只有逐时数据，所以选非气温镜头时一并切到逐时。
    private void PickLens(Lens lens)
    {
        if (_last is not { } snap) return;
        _lens = _lens == lens ? Lens.Temp : lens;
        if (_lens != Lens.Temp) _hourlyMode = true;
        Fit(Width, Height, true);
        DrawSummary(snap);
    }
    // 数值/时间的容错格式化：来源没有提供的字段显示为 --，不让 NaN 或空串进入界面。
    private static string Num(double value, string suffix, int digits = 0) => double.IsNaN(value) ? "--" : value.ToString($"F{digits}", System.Globalization.CultureInfo.InvariantCulture) + suffix;
    private static string Degree(double value) => double.IsNaN(value) ? "--" : $"{value:0}°";
    private static string Clock(string[] times, int index) => index >= 0 && index < times.Length && times[index].Length >= 16 ? times[index][11..16] : "--:--";
    // 右下角页脚：主来源与本次刷新时刻，用底部原有的 16px 空边，不向上挤占摘要卡与预报区。
    private void DrawFooter(WeatherSnapshot s)
    {
        string time = s.FetchedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        string source = SourceName(s);
        footerNote.Text = source.Length > 0
            ? Loc.T($"{time} 更新 · {source}", $"Updated {time} · {source}")
            : Loc.T($"{time} 更新", $"Updated {time}");
    }
    // 主来源：优先每日域，其次实况域；完整的分域来源在详情浮层里逐项列出。
    private static string SourceName(WeatherSnapshot s)
        => s.Sources.TryGetValue("Daily", out var daily) && daily.Length > 0 ? daily
         : s.Sources.TryGetValue("Current", out var current) && current.Length > 0 ? current : "";
    // 详情浮层：当前多维读数、未来 24 小时、24 天（历史 10 + 今天 + 未来 13）、空气质量与分域来源。
    // 浮层盖住整块天气视图并可纵向滚动，关闭后回到摘要；不改变主视图的紧凑布局。
    private void ShowDetails(WeatherSnapshot s)
    {
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(168, 6, 9, 16)) };
        host.Children.Add(overlay);
        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 20) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled, Content = body };
        var card = new Border { Margin = new Thickness(12), CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(24, 27, 34)), BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255)), BorderThickness = new Thickness(1), Child = scroll };
        overlay.Children.Add(card);
        void Close() => host.Children.Remove(overlay);
        overlay.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, overlay)) Close(); };
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        overlay.Focusable = true; overlay.Focus();

        // 标题行：城市完整名称（洪山区 · 武汉市 · 湖北省 · 中国）、天气、更新时间、来源，右侧关闭。
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        string condition = s.ConditionText.Length > 0 ? s.ConditionText : WeatherCodes.Text(s.WeatherCode);
        title.Children.Add(new TextBlock
        {
            Text = s.Name == "当前位置" ? Loc.T("当前位置", "Current location") : s.Name,
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
        });
        title.Children.Add(new TextBlock { Text = $"{condition} · {Num(s.Temperature, "°")} · " + Loc.T($"更新于 {s.FetchedAt:HH:mm}", $"Updated {s.FetchedAt:HH:mm}"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(158, 178, 200)), Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(title);
        var close = new Button { Content = new WeatherMetaIcon(WeatherMetaKind.Close, Colors.White) { Width = 13, Height = 13 }, Width = 28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = System.Windows.Input.Cursors.Hand };
        WeatherMotion.Attach(close); close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        body.Children.Add(header);

        // 天文卡放最前面：月相/太阳/晨昏蒙影三张卡，标题带来源色点（按字段级归源）。
        AstronomyCards(body, s);

        // 当前多维读数：两行六列的小卡。来源只用色点表达（名字收进悬浮提示与图例），
        // 13 张卡各写一遍「和风天气」太吵；卡片描边也随来源着色，一眼分辨数据归属。
        var metrics = new UniformGrid { Columns = 6, Margin = new Thickness(-3, 0, -3, 0) };
        string? src(string field) => s.FieldSources.TryGetValue(field, out var id) ? id : null;
        void Metric(string label, string value, string? sourceId)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)) });
            // 来源色点放在数值左侧同一行（不是单独一行），悬停整行显示来源名。
            var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (sourceId is { Length: > 0 })
            {
                var color = SourceColor(sourceId);
                valueRow.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 1) });
                System.Windows.Controls.ToolTipService.SetToolTip(valueRow, Loc.T($"来自 {SourceName(sourceId)}", $"From {SourceName(sourceId)}"));
            }
            valueRow.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(valueRow);
            var cellBox = new Border { Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Padding = new Thickness(10, 7, 10, 7), Child = text };
            if (sourceId is { Length: > 0 }) { var c = SourceColor(sourceId); cellBox.BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)); } cellBox.BorderThickness = new Thickness(1, 1, 1, 0);
            metrics.Children.Add(cellBox);
        }
        Metric(Loc.T("体感", "Feels like"), Num(s.FeelsLike, "°"), src("feels") ?? src("temp"));
        Metric(Loc.T("湿度", "Humidity"), s.HumidityPercent >= 0 ? $"{s.HumidityPercent}%" : "--", src("humidity"));
        Metric(Loc.T("风速", "Wind"), Num(s.WindKmh, " km/h"), src("wind"));
        Metric(Loc.T("阵风", "Gusts"), Num(s.WindGustKmh, " km/h"), src("gust"));
        // 风向卡带风级（和风 wind.scale；无数据时按风速换算蒲福级数兜底）。
        int scale = s.WindScale >= 0 ? s.WindScale : WeatherCodes.FromWindSpeed(s.WindKmh);
        string dirText = s.WindDirection.Length > 0 ? s.WindDirection
            : !double.IsNaN(s.WindDirectionDeg) ? WeatherCodes.DegreeToCompass(s.WindDirectionDeg) + Loc.T("风", "") : "--";
        Metric(Loc.T("风向", "Wind direction"), dirText + (scale >= 0 ? $" · {Loc.T("风级 ", "Bft ")}{scale}" : ""), src("wind_dir_text") ?? src("wind_dir") ?? src("wind_scale"));
        Metric(Loc.T("气压", "Pressure"), Num(s.PressureHpa, " hPa"), src("pressure"));
        Metric(Loc.T("能见度", "Visibility"), Num(s.VisibilityKm, " km", 1), src("visibility"));
        Metric(Loc.T("露点", "Dew point"), Num(s.DewPointC, "°"), src("dew"));
        Metric(Loc.T("紫外线", "UV index"), Num(s.UvIndex, ""), src("uv"));
        Metric(Loc.T("降水", "Precipitation"), Num(s.PrecipitationMm, " mm", 1) + (s.PrecipType.Length > 0 && s.PrecipType != "none" ? " " + PrecipName(s.PrecipType) : ""), src("precip"));
        Metric(Loc.T("降水强度", "Precip rate"), Num(s.PrecipRateMmH, " mm/h", 2), src("precip_rate"));
        Metric(Loc.T("云量", "Cloud cover"), Num(s.CloudPercent, "%"), src("cloud"));
        // 空气质量：AQI / PM2.5 / PM10 三个字段在同一张卡内各自带来源色点，和风速、降水等读数并列显示。
        {
            var airText = new StackPanel();
            airText.Children.Add(new TextBlock { Text = Loc.T("空气质量", "Air quality"), FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)) });
            void AirLine(string text, string? sourceId, string? tooltip)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                var lineColor = sourceId is { Length: > 0 } ? SourceColor(sourceId) : Color.FromRgb(143, 166, 192);
                line.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(lineColor), VerticalAlignment = VerticalAlignment.Center });
                var valueBlock = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                if (tooltip is { Length: > 0 }) System.Windows.Controls.ToolTipService.SetToolTip(line, tooltip);
                line.Children.Add(valueBlock);
                airText.Children.Add(line);
            }
            AirLine(s.Aqi >= 0 ? $"AQI {s.Aqi} · {s.AqiText}" : "--", src("aqi"), src("aqi") is { } aq ? SourceName(aq) : null);
            // 通用国际标准（qaqi）：与中国标准（cn-mee）并列展示，便于国内外对照。
            AirLine(s.AqiGlobal >= 0 ? Loc.T("国际 AQI ", "Global AQI ") + s.AqiGlobal : Loc.T("国际 AQI --", "Global AQI --"), src("aqi"), src("aqi") is { } ag ? SourceName(ag) : null);
            AirLine(s.Pm25 >= 0 ? $"PM2.5 {s.Pm25}" : "--", src("pm25"), src("pm25") is { } p2 ? SourceName(p2) : null);
            AirLine(s.Pm10 >= 0 ? $"PM10 {s.Pm10}" : "--", src("pm10"), src("pm10") is { } p1 ? SourceName(p1) : null);
            var airBox = new Border { Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Padding = new Thickness(10, 7, 10, 7), Child = airText };
            if (src("aqi") is { } aid) { var ac = SourceColor(aid); airBox.BorderBrush = new SolidColorBrush(Color.FromArgb(110, ac.R, ac.G, ac.B)); }
            airBox.BorderThickness = new Thickness(1, 1, 1, 0);
            // 污染物全表、首要污染物与健康建议放悬浮提示：卡片本体仍是三行，不会撑高指标网格。
            var details = new List<string>();
            if (s.AirPrimary.Length > 0) details.Add(Loc.T("首要污染物：", "Primary pollutant: ") + s.AirPrimary);
            foreach (var (name, value) in s.AirPollutants) details.Add($"{name}  {value}");
            if (s.AirEffect.Length > 0) details.Add(s.AirEffect);
            if (s.AirAdvice.Length > 0) details.Add(Loc.T("一般人群：", "General population: ") + s.AirAdvice);
            if (s.AirAdviceSensitive.Length > 0 && s.AirAdviceSensitive != s.AirAdvice) details.Add(Loc.T("敏感人群：", "Sensitive population: ") + s.AirAdviceSensitive);
            if (details.Count > 0) System.Windows.Controls.ToolTipService.SetToolTip(airBox, string.Join("\n", details));
            metrics.Children.Add(airBox);
        }
        body.Children.Add(metrics);

        // 逐时详情标题与实际窗口一致：主视图窗口含「上一小时 + 当前 + 未来 22 小时」，
        // 过去列由本地历史补齐，缺失时窗口会从更晚的小时开始——按数据实际覆盖动态生成标题，不写死「未来 24 小时」。
        int totalHours = s.HourTimes.Length, pastHours = 0;
        if (totalHours > 0)
        {
            var cityNow = CityNow(s);
            string nowKey = $"{cityNow.Year:0000}-{cityNow.Month:00}-{cityNow.Day:00}T{cityNow.Hour:00}";
            for (int i = 0; i < totalHours; i++)
                if (s.HourTimes[i].Length >= 13 && string.CompareOrdinal(s.HourTimes[i][..13], nowKey) < 0) pastHours++;
        }
        string hourlyTitle = totalHours == 0 ? Loc.T("未来 24 小时", "Next 24 hours")
            : pastHours > 0 ? Loc.T($"过去 {pastHours} 小时至今后 {totalHours - pastHours} 小时", $"Past {pastHours} h to next {totalHours - pastHours} h")
            : Loc.T($"未来 {totalHours} 小时", $"Next {totalHours} hours");
        body.Children.Add(SectionLabel(hourlyTitle));
        if (s.HourTimes.Length == 0) body.Children.Add(Note(Loc.T("暂无逐时数据", "No hourly data")));
        else
        {
            var hours = new UniformGrid { Columns = 12 };
            for (int i = 0; i < s.HourTimes.Length; i++)
            {
                string time = s.HourTimes[i].Length >= 16 ? s.HourTimes[i][11..16] : "--:--";
                bool day = int.TryParse(s.HourTimes[i].Length >= 13 ? s.HourTimes[i][11..13] : "", out int hour) && hour is >= 6 and < 18;
                int code = s.HourCodes.Length > i && s.HourCodes[i] >= 0 ? s.HourCodes[i] : 3;
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
                cell.Children.Add(new TextBlock { Text = time, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), TextAlignment = TextAlignment.Center });
                cell.Children.Add(new WeatherGlyph(code, day) { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 1) });
                cell.Children.Add(new TextBlock { Text = s.HourTempsC.Length > i ? Degree(s.HourTempsC[i]) : "--", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center });
                string rain = s.HourRainProb.Length > i && s.HourRainProb[i] >= 0 ? $"{s.HourRainProb[i]}%" : "";
                cell.Children.Add(new TextBlock { Text = rain, FontSize = 8.5, Foreground = new SolidColorBrush(Color.FromRgb(99, 179, 244)), TextAlignment = TextAlignment.Center, Height = 12 });
                cell.Children.Add(new TextBlock { Text = s.HourWindKmh.Length > i ? Num(s.HourWindKmh[i], "", 0) : "", FontSize = 8.5, Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 220)), TextAlignment = TextAlignment.Center });
                // 悬停展示这一小时的全部字段：格面只放时间/图标/温度/降水/风速，其余进 tooltip 不撑布局。
                var tip = new List<string>();
                string HourLine(string label, double value, string unit, int digits = 0) => double.IsNaN(value) ? "" : $"{label} {value.ToString("0" + (digits > 0 ? "." + new string('0', digits) : ""), CultureInfo.CurrentCulture)}{unit}";
                tip.Add(HourLine(Loc.T("体感", "Feels"), s.HourFeelsLikeC.Length > i ? s.HourFeelsLikeC[i] : double.NaN, "°"));
                if (s.HourHumidity.Length > i && s.HourHumidity[i] >= 0) tip.Add(Loc.T("湿度 ", "RH ") + s.HourHumidity[i] + "%");
                tip.Add(HourLine(Loc.T("降水", "Precip"), s.HourPrecipMm.Length > i ? s.HourPrecipMm[i] : double.NaN, "mm", 1));
                if (s.HourRainProb.Length > i && s.HourRainProb[i] >= 0) tip.Add(Loc.T("降水概率 ", "PoP ") + s.HourRainProb[i] + "%");
                tip.Add(HourLine(Loc.T("阵风", "Gust"), s.HourWindGustKmh.Length > i ? s.HourWindGustKmh[i] : double.NaN, "km/h"));
                if (s.HourWindDirDeg.Length > i && !double.IsNaN(s.HourWindDirDeg[i])) tip.Add(Loc.T("风向 ", "Dir ") + WeatherCodes.DegreeToCompass(s.HourWindDirDeg[i]));
                tip.Add(HourLine(Loc.T("气压", "Pressure"), s.HourPressureHpa.Length > i ? s.HourPressureHpa[i] : double.NaN, "hPa"));
                tip.Add(HourLine(Loc.T("紫外线", "UV"), s.HourUv.Length > i ? s.HourUv[i] : double.NaN, "", 1));
                tip.Add(HourLine(Loc.T("露点", "Dew"), s.HourDewPointC.Length > i ? s.HourDewPointC[i] : double.NaN, "°", 1));
                tip.Add(HourLine(Loc.T("云量", "Cloud"), s.HourCloudPercent.Length > i ? s.HourCloudPercent[i] : double.NaN, "%", 0));
                tip.Add(HourLine(Loc.T("能见度", "Vis"), s.HourVisibilityKm.Length > i ? s.HourVisibilityKm[i] : double.NaN, "km", 1));
                var hourTip = string.Join("\n", tip.Where(line => line.Length > 0));
                var hourBox = new Border { Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), CornerRadius = new CornerRadius(8), Margin = new Thickness(1), Padding = new Thickness(2, 5, 2, 5), Child = cell, ToolTip = hourTip.Length > 0 ? hourTip : null };
                if (src("h:" + s.HourTimes[i]) is { } hid)
                {
                    var hc = SourceColor(hid);
                    hourBox.BorderBrush = new SolidColorBrush(Color.FromArgb(150, hc.R, hc.G, hc.B));
                    hourBox.BorderThickness = new Thickness(1, 2, 1, 0);
                    // 来源名并入 tooltip 首行，不覆盖上面的字段明细。
                    if (hourTip.Length > 0) System.Windows.Controls.ToolTipService.SetToolTip(hourBox, $"{SourceName(hid)}\n{hourTip}");
                    else System.Windows.Controls.ToolTipService.SetToolTip(hourBox, SourceName(hid));
                }
                hours.Children.Add(hourBox);
            }
            body.Children.Add(hours);
        }

        // 24 天表格：日期、星期、图标、天气、高低温、降水、风、UV、日升日落。
        body.Children.Add(SectionLabel(Loc.T("24 天预报与历史", "24-day forecast and history")));
        var table = new StackPanel();
        int today = WeatherData.TodayIndex(s);
        AddDayRow(true, -1);
        for (int i = 0; i < s.DayDates.Length; i++) AddDayRow(false, i);
        body.Children.Add(table);
        void AddDayRow(bool head, int index)
        {
            var row = new Grid { MinHeight = 28, Margin = new Thickness(0, 0, 0, 1) };
            foreach (double width in new[] { 72d, 48d, 34d }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            for (int c = 3; c < 10; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c is 3 ? 1.3 : c is 9 ? 1.4 : 0.75, GridUnitType.Star) });
            void Cell(int column, string text, Brush? brush = null, double size = 11.5, TextAlignment align = TextAlignment.Left, string? sourceId = null, bool dim = false)
            {
                Color ink = head ? Color.FromRgb(143, 166, 192) : dim ? Color.FromRgb(172, 190, 210) : Color.FromRgb(226, 236, 248);
                var block = new TextBlock { Text = text, FontSize = head ? 10 : size, Foreground = brush ?? new SolidColorBrush(ink), TextAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                if (head) block.FontWeight = FontWeights.SemiBold;
                if (!head && sourceId is { Length: > 0 })
                {
                    var tag = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align == TextAlignment.Center ? HorizontalAlignment.Center : HorizontalAlignment.Left };
                    tag.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(SourceColor(sourceId)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                    tag.Children.Add(block);
                    System.Windows.Controls.ToolTipService.SetToolTip(tag, SourceName(sourceId));
                    Grid.SetColumn(tag, column); row.Children.Add(tag);
                    return;
                }
                Grid.SetColumn(block, column); row.Children.Add(block);
            }
            if (head)
            {
                Cell(0, Loc.T("日期", "Date")); Cell(1, Loc.T("星期", "Weekday")); Cell(2, "");
                Cell(3, Loc.T("天气", "Weather")); Cell(4, Loc.T("最高", "High"), align: TextAlignment.Center); Cell(5, Loc.T("最低", "Low"), align: TextAlignment.Center);
                Cell(6, Loc.T("降水", "Rain"), align: TextAlignment.Center); Cell(7, Loc.T("风", "Wind")); Cell(8, Loc.T("UV", "UV"), align: TextAlignment.Center); Cell(9, Loc.T("日升 / 日落", "Sunrise / sunset"));
            }
            else
            {
                if (index < 0 || index >= s.DayDates.Length) { table.Children.Add(row); return; }
                var date = DateTime.Parse(s.DayDates[index]);
                bool isToday = index == today, past = index < today;
                int code = s.DayCodes.Length > index && s.DayCodes[index] >= 0 ? s.DayCodes[index] : 3;
                string name = s.DayConditions.Length > index && s.DayConditions[index].Length > 0 ? s.DayConditions[index] : WeatherCodes.Text(code);
                // 白天/夜间天气并列展示（夜间未单独给时复用白天口径）。
                string nightName = s.DayNightConditions.Length > index && s.DayNightConditions[index].Length > 0 ? s.DayNightConditions[index] : name;
                string dayNight = nightName != name ? $"{name} / {nightName}" : name;
                Cell(0, date.ToString("MM/dd"), align: TextAlignment.Left, dim: past);
                Cell(1, isToday ? Loc.T("今天", "Today") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]), dim: past);
                string dkey = "d:" + s.DayDates[index];
                var glyph = new WeatherGlyph(code, true) { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(glyph, 2); row.Children.Add(glyph);
                Cell(3, dayNight, sourceId: src("d_cond:" + s.DayDates[index]) ?? src(dkey), dim: past);
                Cell(4, Degree(s.DayHighC.Length > index ? s.DayHighC[index] : double.NaN), new SolidColorBrush(Color.FromRgb(255, 176, 102)), align: TextAlignment.Center, sourceId: src("d_high:" + s.DayDates[index]) ?? src(dkey), dim: past);
                Cell(5, Degree(s.DayLowC.Length > index ? s.DayLowC[index] : double.NaN), new SolidColorBrush(Color.FromRgb(143, 200, 255)), align: TextAlignment.Center, sourceId: src("d_low:" + s.DayDates[index]) ?? src(dkey), dim: past);
                string rain = s.DayRainProb.Length > index && s.DayRainProb[index] >= 0 ? $"{s.DayRainProb[index]}%" : s.DayPrecipMm.Length > index && !double.IsNaN(s.DayPrecipMm[index]) ? $"{s.DayPrecipMm[index]:0.0}mm" : "--";
                Cell(6, rain, new SolidColorBrush(Color.FromRgb(99, 179, 244)), align: TextAlignment.Center, sourceId: src("d_rain:" + s.DayDates[index]) ?? src(dkey), dim: past);
                string wind = s.DayWindDir.Length > index && s.DayWindDir[index].Length > 0 ? s.DayWindDir[index] : "";
                if (s.DayWindMaxKmh.Length > index && !double.IsNaN(s.DayWindMaxKmh[index])) wind += (wind.Length > 0 ? " " : "") + $"{s.DayWindMaxKmh[index]:0}km/h";
                Cell(7, wind.Length > 0 ? wind : "--", sourceId: src("d_wind:" + s.DayDates[index]) ?? src(dkey), dim: past);
                Cell(8, s.DayUvMax.Length > index ? Num(s.DayUvMax[index], "") : "--", align: TextAlignment.Center, sourceId: src("d_uv:" + s.DayDates[index]) ?? src(dkey), dim: past);
                Cell(9, $"{Clock(s.SunriseTimes, index)} / {Clock(s.SunsetTimes, index)}", sourceId: src("d_sun:" + s.DayDates[index]) ?? src(dkey), dim: past);
                // 行悬停展示这一天的全部字段：表体只放日期/天气/高低温/降水/风/UV/日升日落，其余进 tooltip。
                string T(string[] arr, int idx) => arr.Length > idx && arr[idx].Length >= 16 ? arr[idx][11..16] : "";
                var dayTip = new List<string>();
                string Line(string label, double value, string unit, int digits = 0) => double.IsNaN(value) ? "" : $"{label} {value.ToString("0" + (digits > 0 ? "." + new string('0', digits) : ""), CultureInfo.CurrentCulture)}{unit}";
                dayTip.Add(Line(Loc.T("均温", "Avg"), s.DayAvgC.Length > index ? s.DayAvgC[index] : double.NaN, "°", 1));
                dayTip.Add(Line(Loc.T("体感高/低", "Feels hi/lo"), s.DayFeelsHighC.Length > index ? s.DayFeelsHighC[index] : double.NaN, "°") + (s.DayFeelsLowC.Length > index && !double.IsNaN(s.DayFeelsLowC[index]) ? $" / {s.DayFeelsLowC[index]:0}°" : ""));
                if (s.DayHumidity.Length > index && s.DayHumidity[index] >= 0) dayTip.Add(Loc.T("白天湿度 ", "Day RH ") + s.DayHumidity[index] + "%");
                dayTip.Add(Line(Loc.T("白天云量", "Day cloud"), s.DayCloudPercent.Length > index ? s.DayCloudPercent[index] : double.NaN, "%"));
                dayTip.Add(Line(Loc.T("气压", "Pressure"), s.DayPressureHpa.Length > index ? s.DayPressureHpa[index] : double.NaN, "hPa"));
                dayTip.Add(Line(Loc.T("阵风", "Gust"), s.DayGustMaxKmh.Length > index ? s.DayGustMaxKmh[index] : double.NaN, "km/h"));
                string nightTip = "";
                if (s.DayNightHumidity.Length > index && s.DayNightHumidity[index] >= 0) nightTip += Loc.T("夜间湿度 ", "Night RH ") + s.DayNightHumidity[index] + "%  ";
                nightTip += Line(Loc.T("夜间风", "Night wind"), s.DayNightWindMaxKmh.Length > index ? s.DayNightWindMaxKmh[index] : double.NaN, "km/h") + Line(Loc.T(" 夜间阵风", " gust"), s.DayNightGustMaxKmh.Length > index ? s.DayNightGustMaxKmh[index] : double.NaN, "km/h");
                string nightRain = s.DayNightRainProb.Length > index && s.DayNightRainProb[index] >= 0 ? Loc.T("夜间降水概率 ", "Night PoP ") + s.DayNightRainProb[index] + "%" : Line(Loc.T("夜间降水", "Night precip"), s.DayNightPrecipMm.Length > index ? s.DayNightPrecipMm[index] : double.NaN, "mm", 1);
                if (nightRain.Length > 0) nightTip += (nightTip.Length > 0 ? "  " : "") + nightRain;
                dayTip.Add(Line(Loc.T("夜间云量", "Night cloud"), s.DayNightCloudPercent.Length > index ? s.DayNightCloudPercent[index] : double.NaN, "%"));
                if (nightTip.Length > 0) dayTip.Add(nightTip.TrimEnd());
                // 月相与月升月落、月过中天/月下点。
                string moonPhase = s.DayMoonPhase.Length > index ? s.DayMoonPhase[index] : "";
                string moonLine = $"{Loc.T("月升 ", "Moonrise ")}{Clock(s.MoonriseTimes, index)} · {Loc.T("月落 ", "Moonset ")}{Clock(s.MoonsetTimes, index)}";
                if (T(s.MoonTransitTimes, index).Length > 0) moonLine += $" · {Loc.T("月过中天 ", "Transit ")}{T(s.MoonTransitTimes, index)}";
                if (T(s.MoonUnderfootTimes, index).Length > 0) moonLine += $" · {Loc.T("月下点 ", "Underfoot ")}{T(s.MoonUnderfootTimes, index)}";
                if (moonPhase.Length > 0) moonLine += $" · {MoonPhaseName(moonPhase)}";
                dayTip.Add(moonLine);
                // 晨昏蒙影与太阳正午/午夜（和风独有，有值才显示）。
                string astroLine = $"{Loc.T("天光 ", "Astro ")}{T(s.AstronomicalDawnTimes, index)}–{T(s.AstronomicalDuskTimes, index)} · {Loc.T("航海 ", "Nautical ")}{T(s.NauticalDawnTimes, index)}–{T(s.NauticalDuskTimes, index)} · {Loc.T("民用 ", "Civil ")}{T(s.CivilDawnTimes, index)}–{T(s.CivilDuskTimes, index)}";
                if (T(s.AstronomicalDawnTimes, index).Length > 0) dayTip.Add(astroLine);
                string sunLine = $"{Loc.T("日升 ", "Sunrise ")}{Clock(s.SunriseTimes, index)} · {Loc.T("日落 ", "Sunset ")}{Clock(s.SunsetTimes, index)} · {Loc.T("太阳正午 ", "Noon ")}{T(s.SolarNoonTimes, index)} · {Loc.T("太阳午夜 ", "Midnight ")}{T(s.SolarMidnightTimes, index)}";
                if (T(s.SolarNoonTimes, index).Length > 0) dayTip.Add(sunLine);
                var dayTipText = string.Join("\n", dayTip.Where(line => line.Length > 0 && line.Trim().Length > 0));
                if (dayTipText.Length > 0) row.ToolTip = dayTipText;
                row.Background = isToday ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : Brushes.Transparent;
            }
            table.Children.Add(row);
        }

        // 扩展区：分钟降水 / 预警 / 指数 / 天文月相；台风由预警覆盖。
        ShowExtraSections(body, s);

        // 数据来源图例：提供数据的来源用专属色，未提供/未启用的用灰色圆点。
        body.Children.Add(SectionLabel(Loc.T("数据来源", "Sources")));
        var info = new StackPanel();
        var allIds = s.DomainSources.Values.SelectMany(ids => ids).Distinct().ToArray();
        if (allIds.Length > 0)
        {
            var legend = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
            foreach (var id in allIds)
            {
                bool ok = s.SourceProvided.TryGetValue(id, out var yes) && yes;
                var color = ok ? SourceColor(id) : Color.FromRgb(108, 116, 128);
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 12, 2) };
                chip.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
                chip.Children.Add(new TextBlock { Text = SourceName(id), FontSize = 11, Foreground = new SolidColorBrush(color), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                legend.Children.Add(chip);
            }
            info.Children.Add(legend);
        }
        body.Children.Add(info);
        Soft.Enter(card);
    }

    // 扩展数据展示：全部收在详情浮层，主视图维持紧凑与掉帧级性能。
    // 每个分区各自判断有无数据，缺省只显示一行轻提示，不为没有数据的域留大片空白。
    private static void ShowExtraSections(StackPanel body, WeatherSnapshot s)
    {
        Color dim = Color.FromRgb(143, 166, 192), ink = Color.FromRgb(226, 236, 248), accent = Color.FromRgb(99, 179, 244);
        static TextBlock Pill(string text, Color color, double size = 11) => new()
        {
            Text = text, FontSize = size, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.NoWrap
        };

        // 区块标题行：标题右侧放该域实际贡献来源的色点，来源名收进悬浮提示；颜色含义由底部图例统一解释。
        // fieldPrefixes 非空时改从字段级来源取（天文等域混在 Daily 里，按实际字段归源才准确）。
        void Header(string text, string domain, params string[] fieldPrefixes)
        {
            List<string> ids;
            if (fieldPrefixes.Length > 0)
                ids = s.FieldSources.Where(kv => kv.Value.Length > 0 && fieldPrefixes.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal))).Select(kv => kv.Value).Distinct().ToList();
            else if (s.DomainSources.TryGetValue(domain, out var declared))
                ids = declared.Where(id => s.SourceProvided.TryGetValue(id, out var ok) && ok).ToList();
            else ids = [];
            SectionHeader(body, text, ids);
        }

        // 1) 分钟降水：未来数小时的逐点降水条，按降水量映射高度；条色跟该域来源走。
        Header(Loc.T("分钟降水", "Minute precipitation"), "Minutely");
        Color minuteColor = accent;
        if (s.DomainSources.TryGetValue("Minutely", out var minuteIds) && minuteIds.FirstOrDefault(id => s.SourceProvided.TryGetValue(id, out var ok) && ok) is { } minuteId) minuteColor = SourceColor(minuteId);
        if (s.MinuteTimes.Length == 0) body.Children.Add(Note(Loc.T("暂无分钟降水数据", "No minute precipitation")));
        else
        {
            var bars = new UniformGrid { Columns = 8, Margin = new Thickness(2, 0, 2, 4) };
            double max = s.MinutePrecip.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            for (int i = 0; i < s.MinuteTimes.Length; i++)
            {
                string time = s.MinuteTimes[i].Length >= 16 ? s.MinuteTimes[i][11..16] : "--:--";
                double value = s.MinutePrecip.Length > i ? s.MinutePrecip[i] : double.NaN;
                double h = double.IsNaN(value) || max <= 0 ? 0 : Math.Max(3, value / max * 34);
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
                cell.Children.Add(new Border { Width = 14, Height = h, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(double.IsNaN(value) ? dim : minuteColor), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
                cell.Children.Add(Pill(time, dim, 9.5));
                cell.Children.Add(Pill(double.IsNaN(value) ? "--" : $"{value:0.0}", ink, 10));
                bars.Children.Add(cell);
            }
            body.Children.Add(bars);
            // 和风分钟响应的文字总结（如「未来两小时无降水」）。
            if (s.MinuteSummary.Length > 0) body.Children.Add(Pill(s.MinuteSummary, ink, 10.5));
        }

        // 2) 预警：每条一行，等级着色，标题 + 正文 + 生效区间。来源响应（含「当前无预警」）都标色点。
        Header(Loc.T("气象预警", "Weather warnings"), "Warning");
        if (s.Warnings.Length == 0) body.Children.Add(Note(Loc.T("暂无预警", "No active warnings")));
        else foreach (var w in s.Warnings)
        {
            var line = new StackPanel { Margin = new Thickness(2, 0, 2, 6) };
            line.Children.Add(Pill((w.Level.Length > 0 ? $"[{w.Level}] " : "") + (w.Title.Length > 0 ? w.Title : w.Type), Color.FromRgb(242, 153, 74), 12));
            if (w.Text.Length > 0) { var msg = Pill(w.Text, ink, 11); msg.TextWrapping = TextWrapping.Wrap; line.Children.Add(msg); }
            if (w.Start.Length > 0 || w.End.Length > 0) line.Children.Add(Pill($"{w.Start} → {w.End}", dim, 10));
            body.Children.Add(line);
        }

        // 3) 生活指数：两列卡，名称 + 等级 + 说明。
        // 标题颜色跟该域实际贡献的来源走（指数由和风提供 → 和风紫），
        // 与上方读数卡/图例的来源配色保持一致；没有来源信息时才退回 accent。
        Header(Loc.T("天气指数", "Lifestyle indices"), "Indices");
        Color indexColor = accent;
        if (s.DomainSources.TryGetValue("Indices", out var indexIds) && indexIds.FirstOrDefault(id => s.SourceProvided.TryGetValue(id, out var ok) && ok) is { } indicesId) indexColor = SourceColor(indicesId);
        if (s.Indices.Length == 0) body.Children.Add(Note(Loc.T("暂无生活指数", "No lifestyle indices")));
        else
        {
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(2, 0, 2, 4) };
            foreach (var item in s.Indices)
            {
                var card = new StackPanel { Margin = new Thickness(3) };
                card.Children.Add(Pill((item.Name.Length > 0 ? item.Name : item.Category) + (item.Level.Length > 0 ? $" · {item.Level}" : ""), indexColor, 11.5));
                if (item.Text.Length > 0) { var msg = Pill(item.Text, ink, 10.5); msg.TextWrapping = TextWrapping.Wrap; card.Children.Add(msg); }
                grid.Children.Add(new Border { Child = card, Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6) });
            }
            body.Children.Add(grid);
        }
    }

    // 分区标题行（详情页共用）：标题右侧放该分区实际贡献来源的色点，名字收进悬浮提示，图例负责解释颜色。
    private static void SectionHeader(StackPanel body, string text, IReadOnlyCollection<string> ids)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 16, 0, 8) };
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), VerticalAlignment = VerticalAlignment.Center });
        foreach (var id in ids)
        {
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = new SolidColorBrush(SourceColor(id)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            System.Windows.Controls.ToolTipService.SetToolTip(dot, Loc.T($"来自 {SourceName(id)}", $"From {SourceName(id)}"));
            row.Children.Add(dot);
        }
        body.Children.Add(row);
    }

    // 天文卡：放在详情页最前面（标题行之下、读数卡之上）。三张卡——月相（升落/相位/过中天/下点）、
    // 太阳（日升日落/正午午夜）、晨昏蒙影三级。数据随 Daily 域返回但扩展时刻仅和风提供，色点按字段级来源归。
    private static void AstronomyCards(StackPanel body, WeatherSnapshot s)
    {
        Color dim = Color.FromRgb(143, 166, 192), ink = Color.FromRgb(226, 236, 248);
        int today = WeatherData.TodayIndex(s);
        // 取当天 ISO 时刻的 HH:mm；数组缺省返回空串，对应项跳过。
        string HHmm(string[] arr) => arr.Length > today && arr[today].Length >= 16 ? arr[today][11..16] : "";
        string moonrise = s.MoonriseTimes.Length > today && s.MoonriseTimes[today].Length >= 16 ? s.MoonriseTimes[today][11..16] : "--:--";
        string moonset = s.MoonsetTimes.Length > today && s.MoonsetTimes[today].Length >= 16 ? s.MoonsetTimes[today][11..16] : "--:--";
        string phase = s.DayMoonPhase.Length > today ? s.DayMoonPhase[today] : "";
        string sunrise = HHmm(s.SunriseTimes), sunset = HHmm(s.SunsetTimes);
        bool hasAstro = moonrise != "--:--" || moonset != "--:--" || phase.Length > 0 || sunrise.Length > 0;
        if (!hasAstro) return;
        var ids = s.FieldSources.Where(kv => kv.Value.Length > 0
            && (kv.Key.StartsWith("d_sun:", StringComparison.Ordinal) || kv.Key.StartsWith("d_astro:", StringComparison.Ordinal) || kv.Key.StartsWith("d_moon:", StringComparison.Ordinal)))
            .Select(kv => kv.Value).Distinct().ToList();
        SectionHeader(body, Loc.T("天文", "Astronomy"), ids);
        var cards = new List<(string Label, string Text)>();
        // 月相卡：升落/相位/过中天/下点。
        var moon = new List<string> { $"{Loc.T("月升", "Moonrise")} {moonrise} · {Loc.T("月落", "Moonset")} {moonset}" };
        if (phase.Length > 0) moon.Add(MoonPhaseName(phase));
        string transit = HHmm(s.MoonTransitTimes), underfoot = HHmm(s.MoonUnderfootTimes);
        if (transit.Length > 0) moon.Add($"{Loc.T("月过中天", "Transit")} {transit}");
        if (underfoot.Length > 0) moon.Add($"{Loc.T("月下点", "Underfoot")} {underfoot}");
        cards.Add((Loc.T("月相", "Moon"), string.Join(" · ", moon)));
        // 太阳卡：日升日落 + 太阳正午/午夜（正午/午夜仅和风提供）。
        var sun = new List<string> { $"{Loc.T("日升", "Sunrise")} {sunrise} · {Loc.T("日落", "Sunset")} {sunset}" };
        string solarNoon = HHmm(s.SolarNoonTimes), solarMidnight = HHmm(s.SolarMidnightTimes);
        if (solarNoon.Length > 0) sun.Add($"{Loc.T("太阳正午", "Solar noon")} {solarNoon}");
        if (solarMidnight.Length > 0) sun.Add($"{Loc.T("太阳午夜", "Solar midnight")} {solarMidnight}");
        cards.Add((Loc.T("太阳", "Sun"), string.Join(" · ", sun)));
        // 晨昏蒙影三级卡：天光（天文）→ 航海 → 民用，由暗到亮；仅和风提供。
        string civilDawn = HHmm(s.CivilDawnTimes), civilDusk = HHmm(s.CivilDuskTimes);
        string nauticalDawn = HHmm(s.NauticalDawnTimes), nauticalDusk = HHmm(s.NauticalDuskTimes);
        string astroDawn = HHmm(s.AstronomicalDawnTimes), astroDusk = HHmm(s.AstronomicalDuskTimes);
        if (astroDawn.Length > 0 || nauticalDawn.Length > 0 || civilDawn.Length > 0)
            cards.Add((Loc.T("晨昏蒙影", "Twilight"), $"{Loc.T("天光", "Astro")} {astroDawn}–{astroDusk} · {Loc.T("航海", "Nautical")} {nauticalDawn}–{nauticalDusk} · {Loc.T("民用", "Civil")} {civilDawn}–{civilDusk}"));
        // 先收集再定列数：有的城市缺晨昏蒙影，避免 UniformGrid 留空列。
        var grid = new UniformGrid { Columns = Math.Max(1, cards.Count), Margin = new Thickness(-1, 0, -1, 4) };
        foreach (var (label, content) in cards)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(dim) });
            stack.Children.Add(new TextBlock { Text = content, FontSize = 11, Foreground = new SolidColorBrush(ink), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(new Border { Child = stack, Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(4) });
        }
        body.Children.Add(grid);
    }
    // 和风 moonPhase 给的是英文枚举（如 waxing-gibbous），显示层映射成中文月相；未识别的值仍原样显示，
    // 避免接口以后增加相位时把数据吞掉。内部快照仍保存原始枚举值，不改来源口径。
    private static string MoonPhaseName(string phase)
    {
        if (phase.Length == 0) return "";
        string key = new(phase.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return key switch
        {
            "new" or "newmoon" => Loc.T("新月", "New moon"),
            "waxingcrescent" => Loc.T("蛾眉月", "Waxing crescent"),
            "firstquarter" or "waxingquarter" => Loc.T("上弦月", "First quarter"),
            "waxinggibbous" => Loc.T("盈凸月", "Waxing gibbous"),
            "full" or "fullmoon" => Loc.T("满月", "Full moon"),
            "waninggibbous" => Loc.T("亏凸月", "Waning gibbous"),
            "lastquarter" or "thirdquarter" or "waningquarter" => Loc.T("下弦月", "Last quarter"),
            "waningcrescent" => Loc.T("残月", "Waning crescent"),
            _ => phase.Replace('-', ' ')
        };
    }

    private static TextBlock SectionLabel(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), Margin = new Thickness(2, 16, 0, 8) };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), Margin = new Thickness(2, 0, 0, 6) };
    // 来源主题色：每家在详情页读数卡与图例中使用专属颜色，一眼看出该字段数据来自哪个来源。
    private static Color SourceColor(string id) => id switch
    {
        "openmeteo" => Color.FromRgb(102, 194, 255),   // 天蓝
        "weathercomcn" => Color.FromRgb(255, 163, 72), // 中国天气网橙
        "hefeng" => Color.FromRgb(188, 139, 255),      // 和风紫
        "local" => Color.FromRgb(158, 178, 200),       // 本地历史：灰，区别于任何在线来源
        _ => Color.FromRgb(158, 178, 200)
    };
    private static string SourceName(string id) => id switch
    {
        "openmeteo" => "Open-Meteo", "weathercomcn" => Loc.T("中国天气网", "China Weather"),
        "hefeng" => Loc.T("和风天气", "QWeather"),
        "local" => Loc.T("本地记录", "Local history"), _ => id
    };
    // 宽度自适应列数，按时间正序（过去→今天→未来）取窗口：最小 7 列=今天+未来6天，最大 24 列=过去10+今天+未来13，左右按同比例同步扩展。
    private static int[] Order(WeatherSnapshot s, int columns)
    {
        int len = s.DayDates.Length;
        int today = WeatherData.TodayIndex(s);          // 正序数组中“今天”的位置
        int maxPast = Math.Min(10, today), maxFuture = Math.Min(13, len - today - 1);
        columns = Math.Max(1, Math.Min(columns, len));
        int basic = Math.Min(7, 1 + maxFuture);  // 基础 7 列 = 今天 + 未来 6 天（不足时用全部）
        int extra = Math.Max(0, columns - basic);
        int leftMax = maxPast, rightMax = Math.Max(0, maxFuture - (basic - 1)); // 左右各自可扩展的上限
        int pool = Math.Min(extra, leftMax + rightMax);
        int left = 0, right = 0;
        for (int k = 0; k < pool; k++) { if (left < leftMax && (right >= rightMax || left <= right)) left++; else if (right < rightMax) right++; } // 左右交替同步扩展
        var order = new List<int>();
        for (int i = today - left; i < today; i++) order.Add(i);                                  // 过去最接近今天的 left 天
        for (int i = today; i < len && i <= today + basic - 1 + right; i++) order.Add(i);          // 今天 + 未来
        return order.ToArray();
    }
    private void DrawForecast(WeatherSnapshot s)
    {
        forecast.Children.Clear(); lowRow.Children.Clear(); dayInfoRow.Children.Clear(); tempLabels.Clear(); conditionLabels.Clear(); conditionRows.Clear();
        nowRule.Visibility = Visibility.Collapsed; nowText.Visibility = Visibility.Collapsed;   // 「现在」分界线只属于逐时
        // 日月镜头：预报列收起，弧线、色带与底部时刻行全部画进弹性的 chart 画布（由 DrawChart 统一绘制）。
        if (_lens == Lens.Celestial) { forecast.Visibility = Visibility.Collapsed; return; }
        forecast.Visibility = Visibility.Visible;
        // 列刚重建，行高与可见性回到默认（天气名称全显示）；立刻贴回当前档位，避免延迟重绘后闪回完整档。
        if (_hourlyMode) { DrawHourlyForecast(s); ApplyForecastLevel(); return; }
        DrawDailyForecast(s);
        ApplyForecastLevel();
    }
    // 逐日列骨架：5 行固定高度（星期 / 日期 / 图标 / 天气名 / 温度），合计 98px。
    // 逐时列骨架：6 行（风向风级 / 降水概率 / 空气等级 / 图标 / 天气名 / 温度），合计 112px——
    // 降水概率与空气等级是逐时独有的小时级信息，用户定夺只进逐时列、逐日不加行。
    // 两种模式行数因此不同，切换时上段高度差 14px，由弹性的折线行吸收（days 总高不变，只是折线带矮一截）；
    // 图标、天气名、温度三行在两套骨架里等高，切换时这三行像素级对齐。
    private static readonly double[] ColumnRowHeights = { 20, 14, 30, 14, 20 };
    private static readonly double[] HourlyRowHeights = { 20, 14, 14, 30, 14, 20 };
    // 天气名所在行：收纳档 3 收起的就是这一行；两套骨架行号不同，估算与贴回都必须按模式取。
    private const int DailyConditionRow = 3, HourlyConditionRow = 4;
    // 列内尺寸的唯一来源：逐时与逐日共用同一套骨架，就必须共用同一套字号与图标尺寸，
    // 否则切换模式时同样位置的图标/数字会大小跳变。任一处要改，只改这里。
    private const double ColGlyph = 28;    // 天气图标（行高 30，留 1px 上下呼吸）
    private const double ColNote = 10;     // 辅注小字：星期/日期、风级、降水概率、空气等级、天气名
    private const double ColValue = 15;    // 第 5 行主数值：逐日高温 / 逐时温度
    private const double ColFoot = 12;     // 底行：逐日低温 / 逐时时刻
    private static Grid Column(double[] heights)
    {
        var grid = new Grid();
        foreach (var h in heights) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        return grid;
    }
    private static void CellAt(Grid grid, UIElement child, int row)
    {
        Grid.SetRow(child, row); grid.Children.Add(child);
    }
    // 列第 3 行风级：快照没有逐时/逐日风级数组，统一由风速走蒲福换算；无风速返回空串，不留「–级」。
    private static string Bft(double speed)
        => !double.IsNaN(speed) && WeatherCodes.FromWindSpeed(speed) is { } bft && bft >= 0
            ? $"{bft}{Loc.T("级", "")}" : "";
    // 逐日昼夜：白天是「转前」，夜间是「转后」。折线两侧各画一个图标、各配自己的天气名，两个图标本身表达转折，文字不写「转」。
    private static int DayCodeAt(WeatherSnapshot s, int i)
        => s.DayCodes.Length > i && s.DayCodes[i] >= 0 ? s.DayCodes[i] : 3;
    private static int NightCodeAt(WeatherSnapshot s, int i)
        => s.DayNightCodes.Length > i && s.DayNightCodes[i] >= 0 ? s.DayNightCodes[i] : -1;
    // 上方画「转后」（夜间），夜间缺失或与白天相同就退回白天；下方固定画「转前」（白天）。同天气时两个图标一样也照画。
    private static int DayTopCode(WeatherSnapshot s, int i)
    {
        int night = NightCodeAt(s, i);
        return night >= 0 ? night : DayCodeAt(s, i);
    }
    // 逐时模式：列里放「风向+风速 / 风级 / 图标 / 天气名称 / 温度」，时刻放在最底部那一行，与折线共用同一个列几何。
    // 0 点那一列改标日期（如「11月20日」）。「现在」不占任何一列：它落在「过去那一格」与「当前那一格」
    // 之间的分界线上——竖线贯穿列区到底行、线顶写「现在」，当前这一格照常显示自己的时刻。
    private void DrawHourlyForecast(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns);
        int now = HourNow(s, n);
        double col = Math.Max(2, _chartWidth / n);
        todayGlow.Visibility = Visibility.Collapsed;
        // 分界线：第 now 列的左沿就是「过去 / 当前」的交界（列区左边距 6，与预报网格同一套几何）。
        double edge = 6 + now * col;
        nowRule.Visibility = Visibility.Visible;
        nowRule.Width = 2;
        nowRule.Margin = new Thickness(Math.Max(0, edge - 1), 14, 0, 0);   // 顶部 14px 让给「现在」两个字
        nowRule.Background = new SolidColorBrush(Color.FromArgb(150, 143, 208, 255));
        nowText.Visibility = Visibility.Visible;
        nowText.Text = Loc.T("现在", "Now");
        nowText.Foreground = new SolidColorBrush(Color.FromRgb(143, 208, 255));
        nowText.Margin = new Thickness(Math.Max(0, edge - nowText.Width / 2), 0, 0, 0);
        for (int p = 0; p < n && p < s.HourTimes.Length; p++)
        {
            bool day = int.TryParse(s.HourTimes[p].Length >= 13 ? s.HourTimes[p][11..13] : "", out int hour) && hour is >= 6 and < 18;
            int code = s.HourCodes.Length > p && s.HourCodes[p] >= 0 ? s.HourCodes[p] : 3;
            var column = Column(HourlyRowHeights);
            // 第 1 行：风向箭头 + 蒲福风级。气象学的风向是「来向」，箭头直接指向来向，所以北风朝上、东风朝右；
            // 逐日只能给一个聚合风向，小时级的风向变化正是逐时独有的信息。风速数值不进列（与逐日底部同款取舍：
            // 箭头+等级足够，km/h 属低频细节，逐时风速曲线已由风镜头承载）；箭头与等级都缺就留空。
            double deg = p < s.HourWindDirDeg.Length ? s.HourWindDirDeg[p] : double.NaN;
            string bft = p < s.HourWindKmh.Length ? Bft(s.HourWindKmh[p]) : "";
            bool arrow = !double.IsNaN(deg);
            if (arrow || bft.Length > 0)
            {
                var mark = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (arrow) mark.Children.Add(WindMark(deg));
                if (bft.Length > 0) mark.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 205, 235)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(arrow ? 2 : 0, 0, 0, 0) });
                CellAt(column, mark, 0);
            }
            // 第 2 行：降水概率。行高在骨架里常驻预留，空白并不省空间——有概率数据就显示（含 0%），
            // 「0%」本身是有效信息（该整点确实无降水）；概率缺失的来源退回降水量 ≥0.1mm 显示数值，两者都没有才留空。
            // 颜色与降水镜头的柱色同源，悬停 tooltip 给概率与降水量全文。
            int prob = p < s.HourRainProb.Length ? s.HourRainProb[p] : -1;
            double pmm = p < s.HourPrecipMm.Length ? s.HourPrecipMm[p] : double.NaN;
            if (prob >= 0 || (!double.IsNaN(pmm) && pmm >= 0.1))
            {
                var rain = new TextBlock
                {
                    Text = prob >= 0 ? $"{prob}%" : $"{pmm:0.#}", FontSize = ColNote, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(96, 168, 246))
                };
                var tip = new List<string>();
                if (prob >= 0) tip.Add(Loc.T("降水概率 ", "PoP ") + prob + "%");
                if (!double.IsNaN(pmm)) tip.Add(Loc.T("降水量 ", "Precip ") + pmm.ToString("0.#") + "mm");
                rain.ToolTip = string.Join(" · ", tip);
                CellAt(column, rain, 1);
            }
            // 第 3 行：空气质量等级。文字与颜色由该小时 AQI 按国标 HJ 633 换算（复用 AqiText/AqiColor，与空气卡同一套标准），
            // 列窄只写短名（去掉「污染」后缀：优/良/轻度/中度/重度/严重），悬停 tooltip 给全文与数值。
            // 逐时 AQI 仅和风提供，其他来源/城市该行留空——0 信息不占位。
            int aqi = p < s.HourAqi.Length ? s.HourAqi[p] : -1;
            if (aqi >= 0)
            {
                var level = new TextBlock
                {
                    Text = AqiLevelShort(aqi), FontSize = ColNote, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(WeatherCodes.AqiColor(aqi)))
                };
                level.ToolTip = $"{Loc.T("空气质量", "Air quality")} {WeatherCodes.AqiText(aqi)} · AQI {aqi}";
                CellAt(column, level, 2);
            }
            CellAt(column, new WeatherGlyph(code, day) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 3);
            // 第 5 行：天气名称。61/63/65 这几档在图标上几乎长得一样，文字补的正是图标说不出的那一级。
            var condition = new TextBlock { Text = HourCondition(code), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 232)) };
            CellAt(column, condition, 4);
            conditionLabels.Add(condition); conditionRows.Add(column.RowDefinitions[HourlyConditionRow]);
            // 第 6 行固定显示当时的气温，不跟着镜头变——切镜头后那个指标已由上方折线/柱状图体现。
            // NoWrap：App.xaml 默认 Wrap，窄列里数值会折成两行。
            string hourValue = p < s.HourTempsC.Length ? Degree(s.HourTempsC[p]) : "--";
            CellAt(column, new TextBlock
            {
                Text = hourValue, FontSize = hourValue.Length > 4 ? ColValue - 2 : ColValue,
                FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Width = 64, TextWrapping = TextWrapping.NoWrap
            }, 5);
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            // 时刻行：与列同宽同序，逐列对齐，统一压暗一档不与温度抢视线；「现在」由上方那条分界线表达，不占这一行。
            // 必须显式 NoWrap：App.xaml 里 TextBlock 全局默认 Wrap，列宽卡在 48px 上下时（7 列的小窗、24 列的大窗）
            // 「11月20日」比格子宽几个像素就会折成两行，把整行时刻撑高、连带上方的列一起错位。
            // 布局槽固定 64px：TextBlock 会把超出自身布局宽度的文字直接裁掉，而 24 列时格子只有约 48px、
            // 「11月20日」在 12px 下约 50px，按格子宽必然被截成「1月20」。槽放宽后按文字自身宽度居中，
            // 多出来的部分对称溢出到两侧空格里（邻列是居中的短时刻，两侧各留 8px 以上），看不见也不压字。
            lowRow.Children.Add(new TextBlock
            {
                Text = HourLabel(s, p), FontSize = ColFoot, TextAlignment = TextAlignment.Center,
                Width = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192))
            });
        }
    }
    // 当前整点所在列：窗口固定以「当前整点前 1 小时」为起点，所以正常就落在第 2 列（序号 1）。
    // 必须按整点比、不能按「离现在最近」比：22:40 时 23:00 只差 20 分钟，按最近取会把「现在」标到还没到的那一列。
    // 时钟按城市所在时区算（快照里带 UTC 偏移），查国外城市时不会拿本机时间硬套。
    private static int HourNow(WeatherSnapshot s, int n)
    {
        string now = CityNow(s).ToString("yyyy-MM-ddTHH:00", CultureInfo.InvariantCulture);
        int best = 0;
        for (int p = 0; p < Math.Min(n, s.HourTimes.Length); p++)
        {
            if (s.HourTimes[p].Length < 16) continue;
            if (string.CompareOrdinal(s.HourTimes[p], now) <= 0) best = p;   // 最后一个不晚于当前整点的列
            else break;
        }
        return best;
    }
    // 城市当地此刻；快照没带时区信息时退回本机时间。
    private static DateTimeOffset CityNow(WeatherSnapshot s)
    {
        if (s.UtcOffsetMinutes is > -1440 and < 1440)
        {
            try { return DateTimeOffset.Now.ToOffset(TimeSpan.FromMinutes(s.UtcOffsetMinutes)); } catch { }
        }
        return DateTimeOffset.Now;
    }
    // 底部时刻行文字：0 点标日期（如「11月20日」）/ 其余原样 HH:mm。直接从 ISO 串切，不依赖 DateTime.Parse 的区域设置。
    private static string HourLabel(WeatherSnapshot s, int p)
    {
        string time = p >= 0 && p < s.HourTimes.Length ? s.HourTimes[p] : "";
        if (time.Length < 13 || !int.TryParse(time[11..13], out int hour)) return "--";
        // 非 0 点照原样显示时刻（HH:mm，与逐时数组一致，不另造格式）；0 点改标日期。
        if (hour != 0) return time.Length >= 16 ? time[11..16] : $"{hour:00}:00";
        int month = int.TryParse(time[5..7], out int m) ? m : 0;
        int day = int.TryParse(time[8..10], out int d) ? d : 0;
        return $"{month}月{day}日";
    }
    private void DrawDailyForecast(WeatherSnapshot s)
    {
        int today = WeatherData.TodayIndex(s);
        var order = Order(s, forecast.Columns);
        // 「今天」列选择态高亮定位：与预报网格同一套几何（左 6px 边距、列宽 chartWidth/columns、中心对齐），
        // 跨三行覆盖 上段+折线+低温，从列顶贯穿到底部。找不到今天（不该发生）就收起。
        double col = forecast.Columns > 0 ? Math.Max(2, _chartWidth / forecast.Columns) : 0;
        int todayPos = -1;
        for (int p = 0; p < order.Length && todayPos < 0; p++) if (order[p] == today) todayPos = p;
        if (todayPos >= 0)
        {
            todayGlow.Visibility = Visibility.Visible;
            // 与列内留白(每列左右各 3px)一致：高亮宽度 = 列宽 - 6，圆角也向列内收进。
            todayGlow.Width = Math.Max(2, col - 6);
            todayGlow.Margin = new Thickness(6 + todayPos * col + 3, 2, 0, 0);
            todayGlow.Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
            todayGlow.BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));
            todayGlow.BorderThickness = new Thickness(1);
        }
        else todayGlow.Visibility = Visibility.Collapsed;
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p]; var date = DateTime.Parse(s.DayDates[i]); var column = Column(ColumnRowHeights);
            string label = i == today ? Loc.T("今天", "Today") : i == today + 1 ? Loc.T("明天", "Tomorrow") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]);
            bool isToday = i == today;
            CellAt(column, new TextBlock { Text = label, FontSize = ColValue, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Foreground = isToday ? Brushes.White : new SolidColorBrush(Color.FromRgb(205, 224, 247)) }, 0);
            CellAt(column, new TextBlock { Text = date.ToString("MM/dd"), Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)), FontSize = ColNote, TextAlignment = TextAlignment.Center }, 1);
            int before = DayCodeAt(s, i);
            int night = NightCodeAt(s, i);
            // 折线上方画「转后」（夜间），夜间缺失就退回白天；名称只写上方图标自己的天气，不写「转」。
            // 转前转后同天气时两个图标都画且完全一致（同为白天样式）；只有真变了才用夜间样式区分。
            int topCode = DayTopCode(s, i);
            bool changed = night >= 0 && night != before;
            CellAt(column, new WeatherGlyph(topCode, !changed) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 2);
            var condition = new TextBlock { Text = HourCondition(topCode), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 232)) };
            CellAt(column, condition, 3);
            conditionLabels.Add(condition); conditionRows.Add(column.RowDefinitions[3]);
            // 高温在预报列第 5 行、低温在下方独立低温行，折线夹在两者之间；两个温度都登记进 tempLabels 供低档位统一收起。
            var high = new TextBlock { Text = Degree(s.DayHighC[i]), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(high); CellAt(column, high, 4);
            // 今天列的选择态高亮由垫底的 todayGlow 贯穿整列承担，列自身保持透明。
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            // 低温与高温同格式（同字号同字重），只用颜色区分冷暖——镜像布局的上下两极读数规格一致。
            var low = new TextBlock { Text = Degree(s.DayLowC[i]), Foreground = new SolidColorBrush(Color.FromRgb(182, 201, 226)), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(low); lowRow.Children.Add(low);
            // 折线下方与上方镜像：上排转前天气名（对应下方图标）、中排与上方同尺寸的转前图标、下排风向箭头 + 风级。
            // 转前转后同天气时两个图标一样也照画；风速不展示，悬浮只补方位文字。
            var info = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // 下方天气名与上方天气名同进退：档 3 一起收起（折线保留），档 2 才收折线——
            // 名称作为整体先让位，折线是最后消失的那一个。
            var lowerName = new TextBlock { Text = HourCondition(before), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 232)) };
            conditionLabels.Add(lowerName);
            info.Children.Add(lowerName);
            var lowerGlyph = new WeatherGlyph(before, true) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center };
            info.Children.Add(lowerGlyph);
            string dirText = s.DayWindDir.Length > i ? s.DayWindDir[i] : "";
            double dir = s.DayWindDirDeg.Length > i ? s.DayWindDirDeg[i] : double.NaN;
            double wind = s.DayWindMaxKmh.Length > i ? s.DayWindMaxKmh[i] : double.NaN;
            var windRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(dir))
            {
                var arrow = WindArrowIcon(WeatherCodes.Wind8Angle(dirText, dir), Color.FromRgb(160, 205, 235));
                arrow.Width = 12; arrow.Height = 12; arrow.Margin = new Thickness(0, 0, 2, 0);
                windRow.Children.Add(arrow);
            }
            string bft = Bft(wind);
            if (bft.Length > 0) windRow.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(160, 205, 235)) });
            if (windRow.Children.Count > 0) info.Children.Add(windRow);
            if (!double.IsNaN(dir)) info.ToolTip = !string.IsNullOrWhiteSpace(dirText) ? dirText : WeatherCodes.DegreeToCompass(dir) + Loc.T("风", "");
            dayInfoRow.Children.Add(info);
        }
    }
    // 与预报同序的高低温折线：金线高温、蓝线低温，每个采样点带圆点与温度数字注释，随预报宽度横向伸展。
    // 折线是整行一条连续曲线（不是按列各画一段）；点的 x 用与预报网格完全相同的列中心几何，
    // 因此每个点精确落在对应列的正中心，横跨整行时曲线自然连续。
    private void DrawChart(WeatherSnapshot s)
    {
        chart.Children.Clear();
        // 镜头优先：非气温镜头只存在于逐时（逐日只有高低温两列数），所以这里直接按指标画。
        // 对应数组整体缺失时退回气温曲线，宁可少一个镜头也不要给一张空图。
        static bool Has(double[] a) => a.Length > 0 && a.Any(v => !double.IsNaN(v));
        switch (_lens)
        {
            case Lens.Feels when Has(s.HourFeelsLikeC): DrawHourlyLine(s.HourFeelsLikeC, new SolidColorBrush(Color.FromRgb(255, 176, 102)), true); return;
            case Lens.Humidity when s.HourHumidity.Any(v => v >= 0): DrawBars(Array.ConvertAll(s.HourHumidity, v => v < 0 ? double.NaN : v), 100, "%"); return;
            case Lens.Wind when Has(s.HourWindKmh): DrawBars(s.HourWindKmh, double.NaN, ""); return;
            case Lens.Gust when Has(s.HourWindGustKmh): DrawBars(s.HourWindGustKmh, double.NaN, "", 0, Color.FromRgb(126, 212, 226)); return;
            case Lens.Precip when Has(s.HourPrecipMm): DrawBars(s.HourPrecipMm, double.NaN, "", 1, Color.FromRgb(96, 168, 246)); return;
            case Lens.Pressure when Has(s.HourPressureHpa): DrawHourlyLine(s.HourPressureHpa, new SolidColorBrush(Color.FromRgb(168, 186, 208)), false); return;
            case Lens.DewPoint when Has(s.HourDewPointC): DrawHourlyLine(s.HourDewPointC, new SolidColorBrush(Color.FromRgb(88, 146, 220)), true); return;
            case Lens.Uv when Has(s.HourUv): DrawBars(s.HourUv, double.NaN, "", 1, Color.FromRgb(255, 206, 112)); return;
            case Lens.Cloud when Has(s.HourCloudPercent): DrawBars(s.HourCloudPercent, 100, "%", 0, Color.FromRgb(196, 214, 232)); return;
            case Lens.Visibility when Has(s.HourVisibilityKm): DrawHourlyLine(s.HourVisibilityKm, new SolidColorBrush(Color.FromRgb(150, 204, 232)), false, 1); return;
            // 空气质量逐时预报：和风 /airquality/v1/hourly 的 AQI，按与 HourTimes 对齐的数组画柱状；-1 的小时跳过。
            case Lens.Aqi when s.HourAqi.Any(v => v >= 0):
                DrawBars(Array.ConvertAll(s.HourAqi, v => v < 0 ? double.NaN : v), double.NaN, "", 0, Color.FromRgb(126, 208, 154)); return;
            case Lens.Celestial when s.SunriseTimes.Length > 0: DrawCelestialView(s); return;
        }
        if (_hourlyMode) { DrawHourly(s); return; }
        var high = s.DayHighC; var low = s.DayLowC;
        var order = Order(s, forecast.Columns);
        var valid = order.Where(i => !double.IsNaN(high[i]) && !double.IsNaN(low[i])).ToArray();
        if (valid.Length < 2) return;                                     // 来源缺失温度时不出折线，避免 NaN 坐标
        double min = valid.Min(i => low[i]), range = Math.Max(1, valid.Max(i => high[i]) - min);
        int n = order.Length;
        double w = Math.Max(30, chart.Width), col = forecast.Columns > 0 ? Math.Max(2, w / forecast.Columns) : 0;   // 与预报网格同源：列宽按列数分，点数不足时右侧留空
        double X(int p) => (p + 0.5) * col;                              // 列中心：与 UniformGrid 每列几何一致
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩，上留标签、下留圆点半径
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        foreach (var (series, brush) in new[] { (high, (Brush)Brushes.Goldenrod), (low, (Brush)Brushes.DodgerBlue) })
        {
            DrawTrend(Enumerable.Range(0, n).Where(p => !double.IsNaN(series[order[p]])).Select(p => new Point(X(p), Y(series[order[p]]))).ToArray(), brush);
            for (int p = 0; p < n; p++)
            {
                int i = order[p]; if (double.IsNaN(series[i])) continue; double x = X(p), yy = Y(series[i]);
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush }; Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, yy - 3); chart.Children.Add(dot);
            }
        }
    }
    // 当日 24 小时逐时温度曲线：弹性高度带（随画布实际高度伸缩）、列中心取点，横轴按逐时列数均匀分列，
    // 与上段预报网格同为 forecast.Columns 列，每个采样点对准对应时刻列的正中心；时间由下方时刻行表达，
    // 时刻统一由下方时刻行（lowRow）逐列对齐表达，避免同一串时间在一屏里出现两次。
    private void DrawHourly(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, n).Where(i => s.HourTempsC.Length > i && !double.IsNaN(s.HourTempsC[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => s.HourTempsC[i]), range = Math.Max(1, valid.Max(i => s.HourTempsC[i]) - min);
        var brush = new SolidColorBrush(Color.FromRgb(102, 194, 255));
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        DrawTrend(valid.Select(p => new Point(X(p), Y(s.HourTempsC[p]))).ToArray(), brush);
        foreach (int p in valid)
        {
            double yy = Y(s.HourTempsC[p]);
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush }; Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            // 每个点都标温度：列宽最小约 48px，单个「7°」只占 12–18px，相邻两点间隔 48px，不会压字。
            var probe = new TextBlock { Text = $"{s.HourTempsC[p]:0}°", FontSize = 9, Foreground = brush };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(probe, X(p) - probe.DesiredSize.Width / 2); Canvas.SetTop(probe, yy - 14); chart.Children.Add(probe);
        }
    }
    // 通用逐时折线：与 DrawHourly 同一套几何（弹性高度带、列中心取点），供体感镜头复用。
    // 与气温曲线一致，每个点都标数值（体感只在逐时里有逐点数据）。
    private void DrawHourlyLine(double[] data, Brush brush, bool degree, int digits = 0)
    {
        string fmt = "0" + (digits > 0 ? "." + new string('0', digits) : "");
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => data[i]), range = Math.Max(1, valid.Max(i => data[i]) - min);
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        DrawTrend(valid.Select(p => new Point(X(p), Y(data[p]))).ToArray(), brush);
        foreach (var p in valid)
        {
            double yy = Y(data[p]);
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush };
            Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            var label = new TextBlock { Text = degree ? data[p].ToString(fmt, CultureInfo.InvariantCulture) + "°" : data[p].ToString(fmt, CultureInfo.InvariantCulture), FontSize = 9, Foreground = brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, yy - 14); chart.Children.Add(label);
        }
    }
    private void DrawTrend(Point[] points, Brush brush)
    {
        if (points.Length < 2) return;
        double Slope(int i) => (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        double Tangent(int i)
        {
            if (i == 0) return Slope(0);
            if (i == points.Length - 1) return Slope(i - 1);
            double before = Slope(i - 1), after = Slope(i);
            // Flatten at extrema and limit the slope so interpolation stays between samples.
            return Math.Sign(before) == Math.Sign(after)
                ? Math.CopySign(Math.Min(Math.Abs(before), Math.Abs(after)), after) : 0;
        }

        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(points[0], false, false);
            double startSlope = Tangent(0);
            for (int i = 1; i < points.Length; i++)
            {
                Point start = points[i - 1], end = points[i];
                double dx = (end.X - start.X) / 3, endSlope = Tangent(i);
                path.BezierTo(new Point(start.X + dx, start.Y + dx * startSlope),
                    new Point(end.X - dx, end.Y - dx * endSlope), end, true, true);
                startSlope = endSlope;
            }
        }
        curve.Freeze();
        chart.Children.Add(new System.Windows.Shapes.Path { Data = curve, Stroke = brush, StrokeThickness = 2 });
    }

    // 逐时柱状：湿度/云量用固定量程，风、阵风、降水、紫外线按最大值自适应，柱底压在 50 这条基线上。
    private void DrawBars(double[] data, double max, string suffix, int digits = 0, Color? barColor = null)
    {
        string fmt = "0" + (digits > 0 ? "." + new string('0', digits) : "");
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length == 0) return;
        if (double.IsNaN(max) || max <= 0) max = Math.Max(1, valid.Max(i => data[i]));
        var brush = new SolidColorBrush(barColor ?? Color.FromRgb(112, 178, 255));
        // 固定「每 4 格一个」在宽窗口太空、窄窗口又太挤：量一次最长标签，按列宽决定间隔。
        string labelText(int p) => data[p].ToString(fmt, CultureInfo.InvariantCulture) + suffix;
        var probe = new TextBlock { Text = valid.Select(labelText).OrderByDescending(t => t.Length).First(), FontSize = 9 };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int step = Math.Max(1, (int)Math.Ceiling((probe.DesiredSize.Width + 2) / col));
        // 弹性柱状：柱底贴近画布下沿，柱高随画布实际高度伸缩；标签画在柱顶上方。
        double baseY = ChartBand() - 8, span = Math.Max(12, ChartBand() - 30);
        int ordinal = 0;
        foreach (var p in valid)
        {
            double h = Math.Max(2, data[p] / max * span);
            var bar = new System.Windows.Shapes.Rectangle { Width = Math.Max(3, col * 0.42), Height = h, Fill = brush, RadiusX = 2, RadiusY = 2, Opacity = 0.85 };
            Canvas.SetLeft(bar, X(p) - bar.Width / 2); Canvas.SetTop(bar, baseY - h); chart.Children.Add(bar);
            if (ordinal++ % step != 0) continue;
            var label = new TextBlock { Text = labelText(p), FontSize = 9, Foreground = brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, baseY - h - 13); chart.Children.Add(label);
        }
    }
    // 日月镜头视口窗口左缘。窗口宽 24h，规则：当前时段所处天体弧以其中天为窗口中心——
    // 日弧以正午居中、月弧以月中天居中；两弧之间的晨昏间隙以「现在」居中。
    // 中天近似弧线时间中点，弧长 < 24h 时两侧余量各 (24−弧长)/2，当前曲线必落在视口内、不被 0/24 边界拆开；
    // 中天极端偏移时 clamp 进 [落端−12, 升端+12] 强制满足不跨条件，clamp 区间退化（弧长>24h）则退回弧线中点。
    private void DrawCelestialView(WeatherSnapshot s)
    {
        todayGlow.Visibility = Visibility.Collapsed;
        DateTime now = CityNow(s).DateTime;
        double width = Math.Max(30, chart.Width), height = ChartBand();
        double band = Math.Max(26, height - 26), horizon = Math.Max(26, height - 72);
        Color gold = Color.FromRgb(255, 201, 77), silver = Color.FromRgb(226, 236, 248);
        var marks = new List<(DateTime at, string name, bool sun)>();
        var arcs = new List<(DateTime rise, DateTime set, DateTime peak, bool sun)>();
        var estimatedRises = new HashSet<(DateTime rise, bool sun)>();
        void Collect(string[] rises, string[] sets, string[] peaks, bool sun)
        {
            DateTime[] Times(string[] raw, string name)
            {
                var dates = new SortedSet<DateTime>();
                foreach (var value in raw)
                    if (value?.Length >= 16 && DateTime.TryParseExact(value[..16], "yyyy-MM-dd'T'HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && dates.Add(at))
                        marks.Add((at, name, sun));
                return dates.ToArray();
            }
            var up = Times(rises, sun ? Loc.T("日升", "Sunrise") : Loc.T("月升", "Moonrise"));
            var down = Times(sets, sun ? Loc.T("日落", "Sunset") : Loc.T("月落", "Moonset"));
            var high = Times(peaks, sun ? Loc.T("正午", "Noon") : Loc.T("月中天", "Moon peak"));
            for (int i = 0; i < up.Length; i++)
            {
                var rise = up[i];
                var set = down.FirstOrDefault(t => t > rise);
                if (set == default || (i + 1 < up.Length && up[i + 1] <= set)) continue;
                var peak = high.FirstOrDefault(t => t > rise && t < set);
                arcs.Add((rise, set, peak == default ? rise + (set - rise) / 2 : peak, sun));
            }
            // Some forecasts begin with a moonset and omit the preceding evening.
            // Use the following complete cycle only to shape the partial track.
            if (!sun && down.Length > 0 && up.Length > 0 && down[0] < up[0])
            {
                var set = down[0];
                var next = arcs.FirstOrDefault(a => !a.sun && a.rise == up[0]);
                if (next != default && next.rise - set < TimeSpan.FromDays(1)
                    && next.set - next.rise < TimeSpan.FromDays(1))
                {
                    var rise = set - (next.set - next.rise);
                    var peak = high.FirstOrDefault(t => t > rise && t < set);
                    arcs.Add((rise, set, peak == default ? rise + (set - rise) / 2 : peak, false));
                    estimatedRises.Add((rise, false));
                }
            }
        }
        Collect(s.SunriseTimes, s.SunsetTimes, s.SolarNoonTimes, true);
        Collect(s.MoonriseTimes, s.MoonsetTimes, s.MoonTransitTimes, false);
        var active = arcs.Where(a => a.rise <= now && now < a.set).OrderByDescending(a => a.sun).FirstOrDefault();
        var center = active == default ? now : active.peak;
        var start = center.AddHours(-12);
        if (now < start || now >= start.AddHours(24)) start = now.AddHours(-12);
        var end = start.AddHours(24);
        double X(DateTime at) => (at - start).TotalHours * width / 24;
        void Line(double x1, double y1, double x2, double y2, Brush ink, double thickness)
            => chart.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = ink, StrokeThickness = thickness });
        var muted = new SolidColorBrush(Color.FromArgb(55, 155, 172, 194));
        Line(0, horizon, width, horizon, muted, 1);
        Line(0, band, width, band, muted, 5);
        var visible = arcs.Where(a => a.rise < end && a.set > start).ToArray();
        var boundaries = visible.SelectMany(a => new[] { a.rise, a.set })
            .Where(t => t > start && t < end).Append(start).Append(end).Distinct().Order().ToArray();
        for (int i = 1; i < boundaries.Length; i++)
        {
            var from = boundaries[i - 1];
            var to = boundaries[i];
            var sun = visible.FirstOrDefault(a => a.sun && a.rise < to && a.set > from);
            var moon = visible.FirstOrDefault(a => !a.sun && a.rise < to && a.set > from);
            if (sun == default && moon == default) continue;
            Brush fill;
            if (sun != default && moon != default)
            {
                var overlapStart = sun.rise > moon.rise ? sun.rise : moon.rise;
                var overlapEnd = sun.set < moon.set ? sun.set : moon.set;
                Color first = sun.rise <= moon.rise ? gold : silver;
                Color last = sun.set >= moon.set ? gold : silver;
                var gradient = new LinearGradientBrush(first, last, 0)
                {
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new Point(X(overlapStart), band),
                    EndPoint = new Point(X(overlapEnd), band)
                };
                if (first == last)
                    gradient.GradientStops.Add(new GradientStop(first == gold ? silver : gold, .5));
                fill = gradient;
            }
            else fill = new SolidColorBrush(sun != default ? gold : silver);
            Line(X(from), band, X(to), band, fill, 5);
        }
        foreach (var arc in visible)
        {
            var left = arc.rise < start ? start : arc.rise;
            var right = arc.set > end ? end : arc.set;
            var ink = new SolidColorBrush(arc.sun ? gold : silver);
            double Y(DateTime at)
            {
                double f = at <= arc.peak ? (at - arc.rise) / (arc.peak - arc.rise) : (arc.set - at) / (arc.set - arc.peak);
                return horizon - Math.Sin(f * Math.PI / 2) * (horizon - 14);
            }
            var curve = new System.Windows.Shapes.Polyline { Stroke = ink, StrokeThickness = 2 };
            if (estimatedRises.Contains((arc.rise, arc.sun)))
            {
                curve.StrokeDashArray = new DoubleCollection { 4, 2 };
                curve.ToolTip = Loc.T("缺少前一晚月升记录，轨迹按相邻周期估算", "Estimated track: previous moonrise unavailable");
            }
            for (int i = 0; i <= 80; i++)
            {
                var at = left + (right - left) * (i / 80.0);
                curve.Points.Add(new Point(X(at), Y(at)));
            }
            chart.Children.Add(curve);
            if (now < arc.rise || now >= arc.set) continue;
            var glyph = new WeatherGlyph(0, arc.sun) { Width = 24, Height = 24 };
            Canvas.SetLeft(glyph, X(now) - 12); Canvas.SetTop(glyph, Y(now) - 12); chart.Children.Add(glyph);
        }
        int today = Array.FindIndex(s.DayDates, date => date == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        bool daylight = arcs.Any(a => a.sun && a.rise.Date == now.Date) ? active.sun : s.IsDay;
        FrameworkElement? centerGlyph = daylight ? new WeatherGlyph(0, true)
            : today >= 0 && today < s.DayMoonPhase.Length && !string.IsNullOrWhiteSpace(s.DayMoonPhase[today])
                ? new MoonPhaseIcon(s.DayMoonPhase[today]) : null;
        if (centerGlyph is not null)
        {
            centerGlyph.Width = centerGlyph.Height = 52;
            centerGlyph.IsHitTestVisible = false;
            Canvas.SetLeft(centerGlyph, (width - centerGlyph.Width) / 2);
            Canvas.SetTop(centerGlyph, (14 + horizon - centerGlyph.Height) / 2);
            chart.Children.Add(centerGlyph);
        }
        var todayMoonrise = marks.FirstOrDefault(m => !m.sun && m.name == Loc.T("月出", "Moonrise") && m.at.Date == now.Date);
        if (todayMoonrise != default && (todayMoonrise.at < start || todayMoonrise.at >= end))
        {
            var caption = new TextBlock
            {
                Text = Loc.T("今日月出 ", "Moonrise today ") + todayMoonrise.at.ToString("HH:mm"),
                FontSize = 11, Foreground = new SolidColorBrush(silver)
            };
            Canvas.SetLeft(caption, 6); Canvas.SetTop(caption, 0); chart.Children.Add(caption);
        }
        var captions = new List<(TextBlock label, double x, double left)>();
        double nextLeft = 0;
        foreach (var m in marks.Where(m => m.at >= start && m.at < end).OrderBy(m => m.at))
        {
            var label = new TextBlock
            {
                Text = m.name + "\n" + m.at.ToString("HH:mm"), TextAlignment = TextAlignment.Center,
                FontSize = 9, Foreground = new SolidColorBrush(m.sun ? gold : silver)
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = X(m.at), left = Math.Max(nextLeft, x - label.DesiredSize.Width / 2);
            captions.Add((label, x, left));
            nextLeft = left + label.DesiredSize.Width + 4;
        }
        double captionRight = width;
        for (int i = captions.Count - 1; i >= 0; i--)
        {
            var (label, x, preferredLeft) = captions[i];
            double left = Math.Min(preferredLeft, captionRight - label.DesiredSize.Width);
            captionRight = left - 4;
            double top = horizon + 2;
            Canvas.SetLeft(label, left); Canvas.SetTop(label, top); chart.Children.Add(label);
            chart.Children.Add(new Line
            {
                X1 = left + label.DesiredSize.Width / 2, Y1 = top + label.DesiredSize.Height,
                X2 = x, Y2 = band - 2.5, Stroke = label.Foreground, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            });
        }
        int step = width >= 816 ? 1 : width >= 576 ? 2 : width >= 384 ? 3 : 4;
        for (var at = start.Date; at < end; at = at.AddHours(step))
        {
            if (at < start) continue;
            var tick = new TextBlock { Text = at.ToString(at.Hour == 0 ? "M/d" : "HH:mm"), FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)) };
            tick.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tick, Math.Clamp(X(at) - tick.DesiredSize.Width / 2, 0, Math.Max(0, width - tick.DesiredSize.Width)));
            Canvas.SetTop(tick, height - 12); chart.Children.Add(tick);
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
        Canvas.SetLeft(dot, X(now) - 3.5); Canvas.SetTop(dot, band - 3.5); chart.Children.Add(dot);
    }
    // 风向箭头：几何按「指向上」画，直接旋到风的来向；气象风向就是来向，北风指北（向上）。
    // 角度先归到 8 方位，逐时箭头只有 0/45/…/315 八种朝向，和风向文字同一档。
    private static UIElement WindMark(double deg) => new Path
    {
        Data = Geometry.Parse("M0,-4.6 L3.6,3.4 L0,1.4 L-3.6,3.4 Z"),
        Fill = new SolidColorBrush(Color.FromRgb(160, 205, 235)),
        Width = 7, Height = 7, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(double.IsNaN(deg) ? 0 : WeatherCodes.Compass8Angle(deg))
    };
    // 实况风速卡的方向图标：同一套 8 方位角度，颜色跟随卡片，尺寸与其他元信息图标一致。
    private static FrameworkElement WindArrowIcon(double deg, Color ink) => new Path
    {
        Data = Geometry.Parse("M8,1.5 L11.2,13 L8,10.2 L4.8,13 Z"),
        Fill = new SolidColorBrush(ink),
        Width = 15, Height = 15, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(double.IsNaN(deg) ? 0 : WeatherCodes.Compass8Angle(deg))
    };
    // 码值已由 WeatherCodes 归一到同一套区间，这里只翻成最短的一档文字：列宽最小约 44px，
    // 中文最多三个字，英文最多六个字母，超出就会被 TextBlock 直接裁掉。
    // 逐时列的 AQI 等级短名：列宽只有约 24px，完整的「轻度污染」放不下，去掉「污染」后缀即成标准短名
    // （优/良/轻度/中度/重度/严重）。分档与 AqiText/AqiColor（国标 HJ 633）严格一致，颜色仍取 AqiColor。
    private static string AqiLevelShort(int aqi) => aqi switch
    {
        < 0 => "", <= 50 => Loc.T("优", "Good"), <= 100 => Loc.T("良", "Fair"), <= 150 => Loc.T("轻度", "Light"),
        <= 200 => Loc.T("中度", "Med."), <= 300 => Loc.T("重度", "Heavy"), _ => Loc.T("严重", "Sev.")
    };
    private static string HourCondition(int code) => code switch
    {
        0 => Loc.T("晴", "Clear"),
        1 or 2 => Loc.T("多云", "Partly"),
        3 => Loc.T("阴", "Cloudy"),
        45 or 48 => Loc.T("雾", "Fog"),
        51 or 53 or 55 => Loc.T("毛毛雨", "Drizzle"),
        56 or 57 or 66 or 67 or 68 => Loc.T("冻雨", "Sleet"),
        61 => Loc.T("小雨", "Light"),
        63 => Loc.T("中雨", "Rain"),
        65 or 82 => Loc.T("大雨", "Heavy"),
        71 => Loc.T("小雪", "Light"),
        73 => Loc.T("中雪", "Snow"),
        75 or 77 => Loc.T("大雪", "Heavy"),
        80 => Loc.T("阵雨", "Showers"),
        81 => Loc.T("强阵雨", "Storm"),
        85 or 86 => Loc.T("阵雪", "Snow"),
        95 or 96 or 99 => Loc.T("雷雨", "Storm"),
        _ => ""
    };
    // ── 工具栏的城市选择（与右下角浮层同一份状态） ───────────────────────────────
    // 版本只暴露「有哪些城市 / 选中的是哪个 / 当前该显示什么」和一次选择，抓取与刷新仍留在视图内部：
    // 工具栏只负责画一个和播放来源同款的按钮，不碰天气数据。
    internal sealed record CityChoice(string Label, bool Selected, int Index);

    // 灵动岛空间有限：城市名只显示前两级（洪山区 · 武汉市），完整名称仍唯一地存在 City.Name 里。
    // 设置页、详情页、预览卡显示完整名称；这里只截取，不加工。
    internal static string BriefName(string full) => City.BriefOf(full);
    private bool AutoSelected => state.Selected < 0 || state.Selected >= state.Cities.Count;
    // 定位失败时快照名是「当前位置」这个占位（数据层写死的中文串，不随界面语言变），那不是城市名，按钮上不重复写。
    private string AutoCityName => _autoCityName == "当前位置" ? "" : _autoCityName;
    internal string CityLabel
    {
        get
        {
            string brief = AutoSelected
                ? BriefName(AutoCityName)
                : state.Selected >= 0 && state.Selected < state.Cities.Count ? state.Cities[state.Selected].Brief : "";
            return brief.Length > 0 ? brief : Loc.T("自动定位", "Auto location");
        }
    }
    // 下拉第一行写清是自动定位，后面跟前两级城市名；其余选择项同样只显示前两级。
    internal string AutoMenuLabel => AutoCityName.Length > 0
        ? Loc.T($"自动定位·{BriefName(AutoCityName)}", $"Auto · {BriefName(AutoCityName)}")
        : Loc.T("自动定位", "Auto location");
    internal List<CityChoice> CityChoices()
    {
        var choices = new List<CityChoice> { new(AutoMenuLabel, AutoSelected, -1) };
        for (int i = 0; i < state.Cities.Count; i++) choices.Add(new CityChoice(state.Cities[i].Brief, !AutoSelected && state.Selected == i, i));
        return choices;
    }
    internal async Task ChooseCityAsync(int index)
    {
        if (state.Selected == index) return;
        state.Selected = index; _app.QueueSave(); WeatherData.Save(state);
        await Refresh();
    }
    internal Action? SettingsRequested;
}