using Lens = BrimDeck.Core.WeatherMetric;
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

// 天气视图：按域聚合 Open-Meteo、和风与中国天气网的数据，与账号/设备设置分开存放。
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
    // 未来 2 小时降水窄条：有降水才展开，无数据整行归零，不占预报区的空间。
    private readonly RowDefinition minuteRow = new() { Height = new GridLength(0) };
    private readonly UniformGrid minuteBars = new()
    {
        Rows = 1, Columns = 24, Width = 252, Height = 22,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0)
    };
    private readonly TextBlock minuteTitle = new()
    {
        Text = Loc.T("未来 2 小时降雨", "Next 2h Precipitation"), FontSize = 9.5,
        Foreground = new SolidColorBrush(Color.FromRgb(174, 196, 220)), VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock minuteNote = new()
    {
        FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(206, 220, 238)),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly Border minuteStrip;
    private bool _minuteHasRain;
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
    // 底部区域第二套视图：生活指数。与 days 互斥占据同一行，点天气图标切换。
    private readonly ScrollViewer indicesView = new() { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
    private readonly StackPanel indicesBody = new();
    private bool _indicesMode;
    // 折线是预报区里唯一的弹性带：所在行是 Star、画布拉伸填充（MinHeight 保底），多余高度全部由折线吸收。
    // 高度变化被折线连续伸缩消化，而不是靠收起/展开内容行——骨架恒定，抖动在结构上无从产生。
    private readonly Canvas chart = new() { MinHeight = ChartMinHeight, Width = 588, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
    private bool _hourlyMode = true;    // 底部曲线视图：true=当日 24h 逐时（默认），false=每日高低温
    // 底部「看什么」：卡片点一下换成该指标的逐时曲线，再点一次回到气温。逐日只有气温，所以非气温镜头一律切到逐时。
    private WeatherChip[] _drawnChips = [];
    private Lens _lens = Lens.Temp;
    private readonly UniformGrid lowRow = new() { Rows = 1, Columns = 7, Height = LowRowHeight };
    private readonly UniformGrid lowNote = new() { Rows = 1, Columns = 7, Height = 0 };
    private readonly UniformGrid dayInfoRow = new() { Rows = 1, Columns = 7, Height = 60 };
    private readonly List<TextBlock> tempLabels = new();   // 高/低温文本，供“隐藏高低温”档统一收起
    // 天气名称文本与其所在列的第 4 行：收纳到档 3 时上下两处名称同时收起（行高同步归零），折线保留。
    private readonly List<TextBlock> conditionLabels = new();
    private readonly List<RowDefinition> conditionRows = new();
    // 预报列里的天气图标：档 1 连同图标行一起收起（只留数值与时刻），行高归零前先把图标藏掉，
    // 否则 0 高的行不会裁切子元素，图标会溢到相邻行上。
    private readonly List<UIElement> columnGlyphs = new();
    // 当前预报区收纳状态：4 完整 / 3 藏天气文字 / 2 藏折线 / 1 藏图标 / 0 整行折叠。
    // 五档各有独立高度：档 1 藏图标但保留温度与时刻，所以矮面板不会从「有图标」直接跌到「整行消失」。
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
    // 上一次成功绘制的内容签名（不含 FetchedAt）与失败标记：签名一致且没画全过时只更新页脚。
    // 签名为 FNV-1a 64 位哈希：整份快照先序列化成 UTF-8 字节再滚动哈希，不再产生几十 KB 的字符串。
    private ulong _renderSignature;
    private bool _renderFailed;
    private string _shownKey = "";   // 当前画面显示的是哪座城市（缓存键）：显示数据与城市绑定，换城不残留
    // 排队中的部分结果：历史单独一轮时 partial 与终版只隔几十毫秒，合并到一次渲染。
    private WeatherSnapshot? _pendingPartial;
    private bool _partialQueued;
    // 重画节流：拖动改宽度时 Fit 每帧都会被调用，而重建 24 列的列元素与折线并不便宜。
    // 画布宽度与列数当场更新，真正重建最多 30fps，落后的一帧由 _flush 补上。
    private long _drawnAt;
    private bool _redrawPending;
    private DispatcherTimer? _flush;
    // 折线画布最近一次真正绘制时的宽度与「拖宽结束」计时器：宽度变化先用缩放顶住，
    // 停手 120ms 后再按最终宽度重建一次，把拉伸的文字恢复成正常比例。
    private double _chartDrawnWidth;
    private DispatcherTimer? _chartSettle;
    // 主题配色只有几种，画刷冻结后共享：一次重画会碰上百个元素，逐个 new 只会白白制造垃圾。
    private readonly PanelLook _look;
    private Brush ColText => UI.Brush(_look.Secondary);
    private Brush ColMuted => UI.Brush(_look.Tertiary);
    private Brush ColCondition => UI.Brush(_look.Primary);
    private Brush ColWind => Ink(Color.FromRgb(160,205,235));
    private Brush ChartTemp => Ink(Color.FromRgb(102,194,255));
    private Brush Ink(Color color) => new SolidColorBrush(_look.Readable(color, PanelThemes.TextContrast));
    internal void ApplyWeatherTheme()
    {
        Foreground = UI.Brush(_look.Primary);
        minuteTitle.Foreground = footerNote.Foreground = ColMuted;
        minuteNote.Foreground = ColText;
        _renderSignature = 0;
        if (_last is not { } snap) return;
        Render(snap);
    }
    private static readonly Brush NowRuleBrush = Frozen(150, 143, 208, 255);
    private Brush NowTextBrush => Ink(Color.FromRgb(143, 208, 255));
    private static readonly Brush TodayFill = Frozen(26, 255, 255, 255);
    private static readonly Brush TodayEdge = Frozen(56, 255, 255, 255);
    private static Brush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static Brush Frozen(byte a, byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }
    private double _lastAvail = -1;       // 上次参与状态决策的可用高度，高度没变就跳过整段决策
    private string _autoCityName = "";   // 最近一次自动定位到的城市
    private int _columns = 7;
    private double _chartWidth;
    private int _summaryCols = 2;
    private double _summaryIcon = 48;   // Hero 图标当前档位；只在跨档时重建摘要，宽度连续变化由弹性 Star 列吸收
    private int generation;
    // 视口即内容区，宽度与高度随 Fit 传入值整体填充。预报区骨架恒定（各行行高固定、图标恒定 28px），
    // 折线行是唯一弹性带；高度不足按 4→3→2→1→0 逐级收纳：4 完整 → 3 藏上下天气名称 → 2 藏折线 → 1 藏图标 → 0 整行折叠。
    internal void Fit(double width, double height, bool force = false)
    {
        Width = width; Height = height;
        UpdateMinuteVisibility();
        // 顶部读数卡按可用宽度取列数（2–6）、Hero 分三档尺寸；只有跨档才重建。
        // 列宽本身由 Star 列弹性分配，连续拖宽时不需要重建：卡片跟着变宽变窄即可。
        if (_indicesMode && _last is { } indicesSnap && !_drawnIndices.SequenceEqual(_app.Settings.Weather.LifestyleIndices))
            DrawIndices(indicesSnap);
        var summaryPlan = PlanSummary(width);
        if (summaryPlan.Columns != _summaryCols || summaryPlan.Icon != _summaryIcon || !_drawnChips.SequenceEqual(_app.Settings.Weather.Chips))
        {
            _summaryCols = summaryPlan.Columns; _summaryIcon = summaryPlan.Icon;
            if (_last is { } summarySnap) DrawSummary(summarySnap);
        }
        double avail = Math.Max(0, height - 16 - minuteRow.Height.Value);   // 根 Grid 上边距 0 + 底部页脚行 16 + 分钟窄条
        summaryRow.MinHeight = Math.Min(summaryMinHeight, avail);     // 今日天气最小高度封顶，避免自身被压坏
        double chartWidth = Math.Max(30, width - 40);
        int columns = LayoutColumns(width);
        // 24h 模式固定 24 列对应 24 个逐时数据点，保证「24 个日期 ↔ 24 个时刻」一一对应。
        // 逐时与逐日共用同一套列数阶梯：宽度压缩到 400 以下就只留 7 列。
        int shownValue = LayoutShown(columns, _last);
        bool columnsChanged = columns != _columns, shownChanged = shownValue != forecast.Columns;
        bool heightChanged = Math.Abs(avail - _lastAvail) > 0.5;
        // 列数或实际列数变了必须重画：改 Columns 而不重建子元素会让 UniformGrid 换行，旧列残影落在左侧。
        bool layoutChanged = force || columnsChanged || shownChanged;
        bool widthChanged = Math.Abs(chartWidth - _chartWidth) > 0.5;
        _columns = columns; _chartWidth = chartWidth; forecast.Columns = shownValue; lowRow.Columns = shownValue; lowNote.Columns = shownValue; dayInfoRow.Columns = shownValue; chart.Width = chartWidth;
        if (_last is { } f)
        {
            // 列数跨档：列元素与折线一起重建。跨档次数少，直接重画比节流更稳。
            if (layoutChanged) { _drawnAt = Environment.TickCount64; _redrawPending = false; DrawForecast(f); DrawChart(f); }
            // 只有宽度变了：列内容与列宽无关，不重建列，只把锚点按新列宽重贴；
            // 折线先横向缩放顶住，停手 120ms 后按最终宽度重建一次，文字恢复原始比例。
            else if (widthChanged) { UpdateAnchors(f); ScaleChart(); }
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
    // 只有当面板矮到放不下预报条、只剩今日天气时，才让摘要区整体居中接管这一行，避免贴着页头、下面空一大片。
    private void SyncSummaryAlignment()
    {
        bool showDays = _indicesMode || days.Visibility == Visibility.Visible;   // 指数视图与 days 共用这一行，任一可见就占满
        summaryRow.Height = showDays ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        daysRow.Height = showDays ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // 对齐作用在整块摘要上：Grid 自身按内容高度居中/贴顶，Hero 与指标区在内部各自对齐，
        // 不再逐个改子元素的对齐（子元素一旦换行或换列，逐个设置就会互相打架）。
        summary.VerticalAlignment = showDays ? VerticalAlignment.Top : VerticalAlignment.Center;
    }
    // 把当前状态原样贴到列元素上。列在每次重绘时都会 Clear + 新建行定义，行高与可见性回到默认值；
    // 重绘后必须重贴，否则天气名称/折线会闪回，直到下一次 Fit 才收起——拖动高度时看到的就是这个闪动。
    // 图标尺寸恒定 28px 不随档位缩放：骨架只有「天气名行」「图标行」两个开关，其余全部交给折线行伸缩。
    private void ApplyForecastLevel()
    {
        int level = _forecastLevel;
        bool showCondition = level >= 4;
        bool showGlyph = level >= 2;     // 档 1：图标整行收起，只留风/概率/空气/温度与时刻
        // 天气名行高按模式取：逐时列的名称行与逐日不是同一行号。
        var rows = _hourlyMode ? HourlyRowHeights : ColumnRowHeights;
        int conditionIndex = _hourlyMode ? HourlyConditionRow : DailyConditionRow;
        int glyphIndex = _hourlyMode ? HourlyGlyphRow : DailyGlyphRow;
        double conditionHeight = showCondition ? rows[conditionIndex] : 0;
        double glyphHeight = showGlyph ? rows[glyphIndex] : 0;
        foreach (var row in conditionRows) row.Height = new GridLength(conditionHeight);
        foreach (var label in conditionLabels) label.Visibility = showCondition ? Visibility.Visible : Visibility.Collapsed;
        foreach (var glyph in columnGlyphs) glyph.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;

        var chartVisibility = level >= 3 ? Visibility.Visible : Visibility.Collapsed;
        bool chartToggled = chart.Visibility != chartVisibility;
        chart.Visibility = chartVisibility;
        // 折线行是唯一的弹性带。折线收起后不再把每一行都拉成 Star——那会把图标、温度、时刻均匀撑开，
        // 行距全乱、越拉越丑；改为整块保持骨架原高、贴住底部下沉，多出来的高度由它上方那片区域吸收。
        bool pinned = level < 3 && _lens != Lens.Celestial;
        days.VerticalAlignment = pinned ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        var flexible = new GridLength(1, GridUnitType.Star);
        days.RowDefinitions[0].Height = GridLength.Auto;
        days.RowDefinitions[1].Height = chartVisibility == Visibility.Visible ? flexible : new GridLength(0);
        foreach (var border in forecast.Children.OfType<Border>())
        {
            if (border.Child is not Grid column) continue;
            for (int i = 0; i < column.RowDefinitions.Count; i++)
            {
                double height = i == conditionIndex ? conditionHeight : i == glyphIndex ? glyphHeight : rows[i];
                column.RowDefinitions[i].Height = new GridLength(height);
            }
        }
        bool showTemp = level >= 1 && _lens != Lens.Celestial;   // 天文镜头有自己的底部时刻行，低温/时刻行让位
        // 底部三行各管各的显隐与定高，总高与拆开前完全一致（20 + 14 + 44 = 20 + 58）：
        //   lowRow 逐日低温 / 逐时时刻，档 1 起常驻；
        //   lowNote 逐日转前天气名，只在档 4 出现（与上方天气名同进退），逐时恒为 0；
        //   dayInfoRow 转前图标 + 风，逐日档 2 起常驻，与折线互不牵连。
        lowRow.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        bool showNote = showTemp && !_hourlyMode && showCondition;
        lowNote.Visibility = showNote ? Visibility.Visible : Visibility.Collapsed;
        bool showInfo = showTemp && !_hourlyMode && level >= 2;
        dayInfoRow.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        lowRow.Height = LowRowHeight;
        lowNote.Height = showNote ? DayNoteHeight : 0;
        dayInfoRow.Height = DayInfoCompact;
        days.RowDefinitions[2].Height = GridLength.Auto;
        days.RowDefinitions[3].Height = GridLength.Auto;
        days.RowDefinitions[4].Height = GridLength.Auto;
        foreach (var p in tempLabels) p.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = !_indicesMode && level >= 1 ? Visibility.Visible : Visibility.Collapsed;
        // 折线行重新出现时行高从 0 展开，画布还没按新高度画过：立刻补一版，布局后 SizeChanged 会再校准一次。
        if (chartToggled && chartVisibility == Visibility.Visible && _last is { } snap) DrawChart(snap);
    }
    // 各收纳状态需要的高度：骨架行高全是常量（ColumnRowHeights 与各行固定高），状态高度因此也是常量。
    // 估算里加了少量安全余量：宁可早一档收起，也不让内容溢出弹性行——决策不需要任何实测。
    // 档位语义：4 完整 / 3 藏天气文字 / 2 藏折线 / 1 藏图标（留温度与时刻）/ 0 整行折叠。
    // 逐日比逐时多一条「转前图标 + 风」镜像行，逐时比逐日多两行小时级数据（降水概率、空气等级）：
    // 两套骨架各自估算后取更高的那套，同一尺寸下两种模式的档位一致，切换不会跳档。
    // 镜像行在档 2 起常驻（折线收起不影响它），只有连图标一起收起的档 1 才随之下线。
    private double StateHeight(int level) => Math.Max(SkeletonHeight(level, true), SkeletonHeight(level, false));
    private double SkeletonHeight(int level, bool hourly)
    {
        if (level <= 0) return 0;
        // 天文镜头：预报列、天气名、温度与时刻行全部收起，整块只画一条折线画布。
        // 若仍按普通档位估算，中等偏矮的面板会把「折线档」判为装不下，画布被藏、列又被镜头收起，
        // 预报区于是整块空白——需求降到画布本身，它就能占满余下高度。
        if (_lens == Lens.Celestial) return CelestialMinHeight;
        var rows = hourly ? HourlyRowHeights : ColumnRowHeights;
        int condRow = hourly ? HourlyConditionRow : DailyConditionRow;
        int glyphRow = hourly ? HourlyGlyphRow : DailyGlyphRow;
        double h = ColumnInset + BandSafety;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == condRow && level < 4) continue;
            if (i == glyphRow && level < 2) continue;
            h += rows[i];
        }
        if (level >= 3) h += ChartMinHeight + ChartTopMargin + BandSafety;  // 折线行按最小高度计，富余高度由它吸收
        if (level >= 2) h += LowRowHeight + BandSafety;                     // 低温/时刻行：档 2 起常驻
        if (!hourly && level >= 4) h += DayNoteHeight + BandSafety;         // 转前天气名行：档 4 才有
        if (!hourly && level >= 2) h += DayInfoCompact + BandSafety;        // 转前图标 + 风：档 2 起常驻
        return h;
    }
    // 状态决策：room 的纯函数，双向即时、阈值不对称。降档在 room < 档高 时立即执行
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
                // 这里只可能是折线随高重画（宽度变化已改走缩放 + 停手重建）：列元素与高度无关，不重建。
                if (_last is { } s) DrawChart(s);
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
    // 宽度变化时的过渡：列内容不重建，折线先横向缩放顶住（文字随之轻微拉伸），
    // 停手 120ms 后按最终宽度真正重画一次，恢复原始字宽。
    private void ScaleChart()
    {
        double target = Math.Max(30, chart.Width);
        double baseWidth = _chartDrawnWidth > 1 ? _chartDrawnWidth : target;
        chart.RenderTransform = Math.Abs(target - baseWidth) < 0.5 ? null : new ScaleTransform(target / baseWidth, 1);
        SettleChart();
    }
    private void SettleChart()
    {
        if (_chartSettle is null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _chartSettle = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop(); _chartSettle = null;
                if (_last is { } s) { _drawnAt = Environment.TickCount64; DrawChart(s); }
            };
        }
        _chartSettle.Stop(); _chartSettle.Start();
    }
    private const double summaryMinHeight = 86;   // 今日天气卡片两行的真实高度，与 summaryRow 初始值一致，让 room 估算准确
    // —— 摘要区骨架：指标卡列宽在 84–120 之间弹性浮动，列数 2–6 随可用宽度取整。
    // 理想列距 102（卡片内容约 96 + 左右外边距 6）。指标区占 Star 列，宽度变化直接由卡片伸缩吸收，
    // 不重建、不留右侧固定空档——旧版「列数 × 105 固定宽」正是宽窗口右侧一大块空白与窄窗口挤扁 Hero 的来源。
    private const double TilePitch = 102;
    private const double ChartMinHeight = 46;     // 折线画布最小高度：上下各留标签与圆点的空间，低于此折线档装不下
    private const double CelestialMinHeight = 96; // 天文镜头的最低需求：整块只画画布本身（弧线带 + 地平线 + 时刻刻度）
    // 天文镜头画完弧线、大天体与事件标注所需的最小高度：弧线区至少 52px（容得下大天体），
    // 再加地平线以下的标注、色带与刻度。低于它才切矮版（只留地平线以下），画布高约 160、面板高约 300。
    private const double CelestialArcHeight = 160;
    // —— 骨架尺寸的唯一事实来源：ApplyForecastLevel 贴行高、StateHeight 估状态高度，两边引用同一组常量。
    // 估算 = 真实开销 + BandSafety。要改任何一行尺寸只改这里（及列 Border 的 Padding），决策与呈现永远不会漂移。
    private const double LowRowHeight = 20;     // 低温/时刻行（低温与高温同字号 15px，行高相应加大）
    private const double DayInfoFull = 58;      // 逐日信息行·完整（天气名 14 + 28px 图标 + 风 16），拆行后 = DayNoteHeight + DayInfoCompact
    private const double DayInfoCompact = 44;   // 逐日信息行·紧凑（图标 + 风）
    private const double DayNoteHeight = DayInfoFull - DayInfoCompact;   // 辅注行：转前天气名（10px 字，14px 行高）
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

    public WeatherView(App app, PanelLook look)
    {
        _app = app;
        _look = look;
        Width = 620; Height = 290;
        Background = Brushes.Transparent; ApplyWeatherTheme();
        // 内容整块填满视口：UserControl 默认按内容自然尺寸贴左上，预报区取 Star 行时整块内容只剩自然高度，
        // 上下会被留出等量空白（页头下沿到摘要之间那一大条黑带就是这么来的）。显式拉伸后视图上沿紧贴页头。
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        state = WeatherData.Load(() => _app.Settings);
        // Grid 两行：今日天气(Auto 按内容贴顶) / 底部预报(Star 填满余下高度)，宽度与高度都随 Fit 传入值整体填充。
        // 今日天气内容不多，按内容高度就够；多出来的高度全部给下方预报区，让它上沿紧接天气、下沿贴住页脚，
        // 中间不再留一大块空白。底部 16px 由页脚行占住（刷新时刻与来源），与用量页列表底部基准一致。
        // 上边距 0：页头那一行（36）本身就是内容的上沿，用量页也是把内容直接贴在页头下沿的
        // （UsageContent.Margin 顶边为 0，列内 ColumnTopInset = 2）。这里再留 6 会和用量页形成两套基准。
        var minutePanel = new DockPanel { LastChildFill = true };
        minutePanel.Children.Add(minuteTitle);
        minutePanel.Children.Add(minuteBars);
        minutePanel.Children.Add(minuteNote);
        minuteStrip = new Border
        {
            Child = minutePanel, Margin = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(8, 2, 8, 2), Visibility = Visibility.Collapsed
        };
        var root = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        root.RowDefinitions.Add(summaryRow);
        root.RowDefinitions.Add(minuteRow);
        root.RowDefinitions.Add(daysRow);
        root.RowDefinitions.Add(footerRow);
        Grid.SetRow(summary, 0); Grid.SetRow(minuteStrip, 1); Grid.SetRow(days, 2); Grid.SetRow(footerNote, 3);
        root.Children.Add(summary); root.Children.Add(minuteStrip); root.Children.Add(days); root.Children.Add(footerNote);
        // 生活指数视图与 days 占同一行、同一层；默认折叠，点天气图标时与 days 互换可见性。
        Grid.SetRow(indicesView, 2);
        indicesView.Content = indicesBody;
        indicesView.SetResourceReference(Control.TemplateProperty, "WeatherIndicesScrollTemplate");
        indicesView.Margin = new Thickness(0, 0, -root.Margin.Right, 0);
        indicesView.Padding = new Thickness(6, 2, 1, 2);
        root.Children.Add(indicesView);
        host.Children.Add(root); Content = host;
        // days 内部四行上下排列：预报列上段(星期/日期/图标/天气名称/高温)贴顶、折线图占据中间弹性行、
        // 折线下方再分三行独立排布（主值 低温/时刻、辅注 转前天气名/时刻说明、信息 转前图标+风），
        // 与折线上方镜像。折线行吸收预报区多出来的高度，让底部各行紧贴窗口下沿。
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 当前位置高亮垫底：跨五行(上段/折线/低温/辅注/信息行)，Forecast、折线与底部各行都画在它上面。
        // 逐日高亮「今天」那一列，逐时高亮「现在」那一列——同一块 Border、同一套定位公式，两种模式共用。
        Grid.SetRow(todayGlow, 0); Grid.SetRowSpan(todayGlow, 5); days.Children.Add(todayGlow);
        // 「现在」分界线垫在同一层：竖线在两格之间、文字在列区上方那两行空位里，都不参与命中测试。
        Grid.SetRow(nowRule, 0); Grid.SetRowSpan(nowRule, 5); days.Children.Add(nowRule);
        forecast.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(forecast, 0); days.Children.Add(forecast);
        // 折线画布与预报网格左边距同为 6px 显式左对齐，画布原点与列起点完全重合，点坐标按列中心计算。
        chart.Margin = new Thickness(6, 2, 6, 0); chart.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(chart, 1); days.Children.Add(chart);
        // 弹性折线：画布被拉伸改变高度时（拖高度、升降收纳档）按新高度重画，节流 33ms 与整页重画同一档。
        // 宽度变化不再走这里：宽度只做横向缩放，等拖宽停手后由 _chartSettle 统一重建。
        chart.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) ScheduleChartRedraw(); };
        // 整块底部（列 + 折线 + 低温行）就是一个切换区：鼠标移上去变手型，点一下在「逐时」与「逐日」之间互换。
        // 不设开关按钮——底部本身就是开关，和卡片镜头互不干扰（卡片改看什么，底部改看多久）。
        // 键盘用户同样可达：可聚焦，Enter/空格与鼠标点按共用同一入口。
        days.Cursor = System.Windows.Input.Cursors.Hand;
        days.Background = Brushes.Transparent;                       // 透明也要有画刷，否则空白处收不到命中测试
        void ToggleMode()
        {
            if (_last is not { } snap || snap.HourTimes.Length == 0) return;
            _hourlyMode = !_hourlyMode;
            _lens = Lens.Temp;                                        // 切回逐日时没有非气温镜头，顺手复位
            Fit(Width, Height, true); DrawSummary(snap);
        }
        days.MouseLeftButtonUp += (_, e) => { ToggleMode(); e.Handled = true; };
        days.Focusable = true;
        days.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { ToggleMode(); e.Handled = true; } };
        AutomationProperties.SetName(days, Loc.T("切换逐时/逐日预报", "Toggle hourly or daily forecast"));
        lowRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(lowRow, 2); days.Children.Add(lowRow);
        lowNote.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(lowNote, 3); days.Children.Add(lowNote);
        dayInfoRow.Margin = new Thickness(6, 0, 6, 0); Grid.SetRow(dayInfoRow, 4); days.Children.Add(dayInfoRow);
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
        var key = AutoSelected ? state.AutoKey : state.KeyFor(state.Cities[state.Selected]);
        if (_shownKey == key && _last is { } snapshot && DateTimeOffset.Now - snapshot.FetchedAt < AutoInterval) return;
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
        string key = selected >= 0 && selected < state.Cities.Count ? state.KeyFor(state.Cities[selected]) : state.AutoKey;
        if (key.Length > 0 && state.Cache.TryGetValue(key, out var cached)) { Render(cached); _shownKey = key; }
        // 显示数据与城市绑定：换到没有缓存的城市就清掉上一城的残留画面，刷新失败或取消时也不会把
        // 别的城市的数据挂在新城市标签下；有该城市自己的缓存则直接渲染缓存。
        else if (_last is not null && _shownKey != key) ShowEmpty();
        fetching = true; RaiseState();
        try {
            using var fresh = force ? WeatherCache.Fresh() : null;
            City city;
            if (selected >= 0 && selected < state.Cities.Count) city = state.Cities[selected];
            else
            {
                var located = await WeatherData.LocateAsync(state.Schedule, token);
                token.ThrowIfCancellationRequested();
                if (selected != state.Selected || request != generation) return;
                if (located is null)
                {
                    if (_last is null) ShowEmpty();
                    return;
                }
                city = located;
            }
            // 旧请求交给 token 中断；generation 只在部分结果回写时挡一次，避免迟到的首屏覆盖新请求。
            var sourceId = state.Schedule.SourceFor(selected < 0 ? null : city);
            key = WeatherData.CacheKey(city, sourceId);
            if (_shownKey != key && state.Cache.TryGetValue(key, out var locatedCache))
            {
                Render(locatedCache);
                _shownKey = key;
            }
            var snapshot = await WeatherData.CurrentAsync(city, state.Schedule, sourceId, token, partial => { if (request == generation && selected == state.Selected && sourceId == state.Schedule.SourceFor(selected < 0 ? null : city) && (_shownKey != key || _last is null)) QueuePartial(partial, key, request); });
            if (request != generation || selected != state.Selected || token.IsCancellationRequested || sourceId != state.Schedule.SourceFor(selected < 0 ? null : city)) return;
            // 终版马上就到：作废还没执行的 partial，避免一次刷新把整页重建两遍。
            _pendingPartial = null;
            Render(snapshot); _shownKey = key;
            // 定位/选择出的实际城市即缓存键：自动定位的快照也按位置存，AutoKey 记下映射。
            // 只回写天气快照与定位指针，不走整体 Save：设置页刚保存的城市/来源改动不被刷新流程覆盖。
            WeatherData.SaveSnapshot(key, snapshot, AutoSelected ? key : null);
            _cycleStart = DateTime.UtcNow;
            // 下一次自动刷新从本次成功刷新重新起算，页头倒计时环与实际自动刷新时刻保持同步。
            timer.Stop(); timer.Start();
        } catch (OperationCanceledException) {
            // 取消即本轮作废，界面与状态由新的 Refresh 接管（画面已是目标城市自己的缓存或空态）。
        } catch { if (request == generation && (_last is null || _shownKey != key)) { _pendingPartial = null; _renderSignature = 0; summary.Children.Clear(); summary.Children.Add(new TextBlock { Text = Loc.T("天气数据不可用", "Weather data unavailable"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center }); } }
        finally { if (ReferenceEquals(_token, cts)) { fetching = false; _token = null; } cts.Dispose(); RaiseState(); }
    }
    // 空态：换到没有缓存的城市后、刷新尚未成功时清掉上一城的残留画面（预报列、折线、页脚一并清）。
    private void ShowEmpty()
    {
        _last = null; _shownKey = ""; _pendingPartial = null;
        // 区块已被清空：作废内容签名，否则换回同一份缓存时会被「无变化」判定跳过、只剩页脚。
        _renderSignature = 0; _renderFailed = false;
        summary.Children.Clear();
        summary.Children.Add(new TextBlock { Text = Loc.T("暂无天气数据", "No weather data"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        forecast.Children.Clear(); lowRow.Children.Clear(); lowNote.Children.Clear(); dayInfoRow.Children.Clear(); chart.Children.Clear();
        todayGlow.Visibility = Visibility.Collapsed; nowRule.Visibility = nowText.Visibility = Visibility.Collapsed;
        footerNote.Text = "";
    }
    // 部分结果（不含历史）与终版通常只隔几十毫秒：合并到一次渲染，避免一次刷新把整页重建两遍。
    // 终版到达时会清空 _pendingPartial，排队中的这帧于是直接空转，不会用旧数据盖掉新画面。
    private void QueuePartial(WeatherSnapshot snapshot, string key, int request)
    {
        _pendingPartial = snapshot;
        if (_partialQueued) return;
        _partialQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _partialQueued = false;
            if (_pendingPartial is not { } s) return;
            _pendingPartial = null;
            if (request != generation) return;
            Render(s); _shownKey = key;
        }));
    }
    // 列数几何的唯一计算点：宽度→列数、快照→实际列数，Fit（判变）与 Render（重画）共用。
    private static int LayoutColumns(double width) => Math.Clamp(7 + (int)Math.Round((width - 440) / 760.0 * 17), 7, 24);
    private int LayoutShown(int columns, WeatherSnapshot? s) => Math.Max(1, s is { } available
        ? (_hourlyMode ? Math.Min(columns, Math.Max(1, available.HourTimes.Length)) : Math.Min(columns, available.DayDates.Length))
        : columns);
    private void ApplyLayout(WeatherSnapshot s)
    {
        int columns = LayoutColumns(Width), shown = LayoutShown(columns, s);
        _columns = columns; forecast.Columns = shown; lowRow.Columns = shown; dayInfoRow.Columns = shown;
    }
    // 单个区块绘制失败不拖累其余区块；异常进调试输出，避免整页静默变空白。返回值用于标记这一版没画全，
    // 让下一次相同的快照不会因为「内容签名没变」被跳过，而是重试一遍。
    private bool DrawPart(string name, Action draw)
    {
        try { draw(); return true; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WeatherView] {name}: {ex}"); return false; }
    }
    // 内容签名不含 FetchedAt：自动刷新拿回同一份数据时只需换页脚时刻，整页重建没有意义。
    private static readonly System.Text.Json.JsonSerializerOptions SignatureJson = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static ulong Signature(WeatherSnapshot s)
    {
        var fetched = s.FetchedAt;
        s.FetchedAt = default;
        try
        {
            // 直接序列化成 UTF-8 字节后做 FNV-1a。
            // 覆盖视图用到的全部字段（含数组与字典），30KB 量级下哈希只要几十微秒，分配量减半。
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s, SignatureJson);
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes) { hash ^= b; hash *= 1099511628211UL; }
            return hash;
        }
        catch { return 0; }
        finally { s.FetchedAt = fetched; }
    }
    private void Render(WeatherSnapshot s)
    {
        if (_hourlyMode && s.HourTimes.Length == 0) { _hourlyMode = false; _lens = Lens.Temp; }
        _last = s;
        ApplyLayout(s);
        if (AutoSelected) _autoCityName = s.Name;
        ulong signature = Signature(s);
        if (signature != 0 && signature == _renderSignature && !_renderFailed && forecast.Children.Count == forecast.Columns)
        {
            DrawPart("页脚", () => DrawFooter(s));
            return;
        }
        _renderSignature = signature;
        bool drawn = true;
        drawn &= DrawPart("页脚", () => DrawFooter(s));
        drawn &= DrawPart("摘要", () => DrawSummary(s));
        drawn &= DrawPart("预报列", () => DrawForecast(s));
        drawn &= DrawPart("折线", () => DrawChart(s));
        drawn &= DrawPart("分钟降水", () => DrawMinute(s));
        if (_indicesMode) drawn &= DrawPart("生活指数", () => DrawIndices(s));
        _renderFailed = !drawn;
    }
    // 把任意粒度的分钟点（和风 5min×24、Open-Meteo 15min×8）重采样成 24 格 × 5 分钟，
    // 每格取区间峰值，两种来源画出来形状一致。
    private void DrawMinute(WeatherSnapshot s)
    {
        minuteBars.Children.Clear();
        var points = s.MinutePrecip;
        if (points.Length == 0)
        {
            _minuteHasRain = false;
            minuteNote.Text = "";
            UpdateMinuteVisibility();
            return;
        }
        const int slots = 24;
        var values = new double[slots];
        for (int i = 0; i < slots; i++)
        {
            int from = i * points.Length / slots;
            int to = Math.Max(from + 1, (i + 1) * points.Length / slots);
            double peak = 0;
            for (int j = from; j < Math.Min(to, points.Length); j++)
                // 缺失点现在是 null；旧快照可能仍存着 NaN，两种都跳过。
                if (points[j] is { } value && !double.IsNaN(value)) peak = Math.Max(peak, value);
            values[i] = peak;
        }
        _minuteHasRain = values.Any(v => v > 0);
        int first = Array.FindIndex(values, v => v > 0);
        // 和风在同一响应里带整段总结（如「5分钟后开始下小雨，50分钟后就停了」），直接用原文；
        // Open-Meteo 没有这段总结，退回到按第一个有雨格估算。
        minuteNote.Text = s.MinuteSummary is { Length: > 0 } summary ? summary : first switch
        {
            0 => Loc.T("正在下雨", "Raining now"),
            > 0 => Loc.T($"{first * 5} 分钟后开始下雨", $"Rain in {first * 5} min"),
            _ => Loc.T("未来 2 小时无降水", "No rain in the next 2 hours")
        };
        double peakValue = Math.Max(0.2, values.Max());
        // 无雨时整行本来就隐藏，不重建 24 个柱子。
        if (!_minuteHasRain) { UpdateMinuteVisibility(); return; }
        foreach (double v in values)
        {
            double ratio = Math.Min(1, v / peakValue);
            minuteBars.Children.Add(new Border
            {
                Width = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Height = v <= 0 ? 2 : 2 + ratio * 20,
                Background = new SolidColorBrush(v <= 0
                    ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                    : ratio > 0.66 ? Color.FromRgb(0x4F, 0x93, 0xFF) : ratio > 0.33 ? Color.FromRgb(0x7F, 0xB4, 0xFF) : Color.FromRgb(0xA9, 0xCD, 0xFF))
            });
        }
        UpdateMinuteVisibility();
    }
    // 无数据或无降水整行归零（不占位）；只在逐时模式、真会下雨且高度够时展开；
    // 逐日模式优先保证 24 天预报的可用高度，不显示窄条；窄窗口只留文字不画柱。
    private void UpdateMinuteVisibility()
    {
        bool show = !_indicesMode && _hourlyMode && _last is { MinutePrecip.Length: > 0 } && _minuteHasRain && Height >= 200;
        // 窄条按 34px 从预报区里直接扣走：扣完如果连档 2（有图标、有温度）都装不下，就先不显示窄条。
        // 旧版只看「有雨且高度 ≥ 200」，这 34px 常把整块预报压到整行折叠，摘要被迫居中、上下空出一大片。
        if (show)
        {
            double measured = summary.ActualHeight > 8 ? summary.ActualHeight : summaryMinHeight;
            show = Height - 16 - 34 - measured >= StateHeight(2);
        }
        minuteRow.Height = show ? new GridLength(34) : new GridLength(0);
        minuteStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        minuteBars.Visibility = Width >= 420 ? Visibility.Visible : Visibility.Collapsed;
    }
    // 摘要区构型：指标卡列数 2–6，Hero 三档尺寸。Fit（判要不要重建）与 DrawSummary（建控件）共用这一处，
    // 两侧永远不会漂移。指标区拿 Star 列，所以列数只决定「分几格」，格子本身永远铺满余宽。
    private readonly record struct SummaryPlan(int Columns, double Icon, double Temp, double HeroWidth);
    private static SummaryPlan PlanSummary(double width)
    {
        double inner = Math.Max(160, width - 28);       // 去掉 root 左右各 14 的外边距
        var (icon, temp, heroWidth) = inner >= 1000 ? (64d, 52d, 178d)
            : inner >= 700 ? (56d, 46d, 158d) : (48d, 38d, 142d);
        // 按理想列距取整：剩给指标区的宽度 ÷ 102 四舍五入，列宽因此落在 84–120 的弹性区间。
        int columns = Math.Clamp((int)Math.Round((inner - heroWidth) / TilePitch), 2, 6);
        return new SummaryPlan(columns, icon, temp, heroWidth);
    }

    // 预警等级归一：和风 color.code 是小写英文（red/amber/blue…），severity 用英文词；中文旧写法也一并认。
    private static int WarnRank(string level) => level.Trim().ToLowerInvariant() switch
    {
        "红色" or "red" or "purple" or "black" or "extreme" => 4,
        "橙色" or "orange" or "amber" or "severe" => 3,
        "黄色" or "yellow" or "moderate" => 2,
        "蓝色" or "blue" or "green" or "minor" => 1,
        "white" or "gray" or "unknown" => 0,
        _ => 2
    };
    private static string WarnLevelShort(string level) => level.Trim().ToLowerInvariant() switch
    {
        "红色" or "red" or "purple" or "black" or "extreme" => "红",
        "橙色" or "orange" or "amber" or "severe" => "橙",
        "黄色" or "yellow" or "moderate" => "黄",
        "蓝色" or "blue" or "green" or "minor" => "蓝",
        _ => level
    };
    // 摘要卡里的预警胶囊：只留「类型 · 等级」，完整标题/正文放 ToolTip，点击切到生活指数视图。
    private Border WarningBadge(WarningItem w, int total)
    {
        (Color bg, Color fg) = WarnRank(w.Level) switch
        {
            4 => (Color.FromArgb(0x38, 0xFF, 0x6B, 0x6B), Color.FromRgb(255, 176, 176)),
            3 => (Color.FromArgb(0x38, 0xFF, 0xA9, 0x4D), Color.FromRgb(255, 208, 156)),
            2 => (Color.FromArgb(0x34, 0xFF, 0xD6, 0x5E), Color.FromRgb(255, 230, 158)),
            _ => (Color.FromArgb(0x34, 0x7F, 0xB8, 0xFF), Color.FromRgb(184, 216, 255))
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "⚠", FontSize = 10, Foreground = Ink(fg), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        string name = w.Type.Length > 0
            ? (w.Level.Length > 0 ? $"{w.Type} · {WarnLevelShort(w.Level)}" : w.Type)
            : (w.Title.Length > 0 ? w.Title : w.Level);
        row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Ink(fg), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (total > 1) row.Children.Add(new TextBlock { Text = $"+{total - 1}", FontSize = 10, Foreground = Ink(fg), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border
        {
            Child = row, Background = new SolidColorBrush(bg), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = w.Text.Length > 0
                ? (w.Title.Length > 0 ? w.Title + "\n" + w.Text : w.Text)
                : (w.Title.Length > 0 ? w.Title : null)
        };
        AutomationProperties.SetName(badge, Loc.T("气象预警", "Weather warning") + " " + name);
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!_indicesMode) ToggleView(); };
        return badge;
    }
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

    // 空气卡：标题固定「空气质量」，主值显示「等级 数值」（如「优 35」），空格分割。
    // 和风给了 category 就直接用，否则按国标 HJ 633 由 AQI 换算。
    private static string AirLevel(WeatherSnapshot s)
    {
        if (s.Aqi < 0) return "--";
        string level = s.AqiText.Length > 0 ? s.AqiText : WeatherCodes.AqiText(s.Aqi);
        return $"{level} {s.Aqi}";
    }

    private string? PrecipTip(WeatherSnapshot s)
        => double.IsNaN(s.PrecipRateMmH) && string.IsNullOrWhiteSpace(s.PrecipType)
            ? null
            : Loc.T($"强度 {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}", $"Rate {Num(s.PrecipRateMmH, " mm/h", 1)} · {PrecipName(s.PrecipType)}");
    // 左上：大图标 + 大字温度 + 高低温/预警；右上：随宽度扩展的实况读数卡（体感/湿度/风速…天文入口）。来源统一放右下角页脚。
    private void DrawSummary(WeatherSnapshot s)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        summary.RowDefinitions.Clear();
        int today = WeatherClock.LiveIndex(s);    // 按城市当前日期在正序数组中定位“今天”；-1 = 跨日缓存对不上今天
        var plan = PlanSummary(Width);
        int cols = _summaryCols = plan.Columns; _summaryIcon = plan.Icon;
        // 左列 Hero 取自然宽（图标 + 温度 + 高低温），右列指标区吃掉全部余宽：
        // 卡片随面板连续伸缩，宽窗口不再在右侧留固定空档，窄窗口也不会把 Hero 挤成一条缝。
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 今日天气内容顶对齐：内容不多，不跟着面板拉伸，多出的高度留给下方预报区填充。
        // 垂直基准由 SyncSummaryAlignment 统一管理（只剩今日天气时才改为居中）。
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        // 左上角大图标是视图切换入口：悬浮播放天气动效，点击在「逐时/逐日预报」与「生活指数」间互换。
        var heroGlyph = new WeatherGlyph(s.WeatherCode, s.IsDay, animated: true)
        {
            Width = plan.Icon, Height = plan.Icon, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T("点击切换生活指数 / 天气预报", "Click to toggle lifestyle indices / forecast")
        };
        AutomationProperties.SetName(heroGlyph, Loc.T("切换生活指数与天气预报", "Toggle lifestyle indices and forecast"));
        WeatherMotion.Attach(heroGlyph);
        heroGlyph.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleView(); };
        hero.Children.Add(heroGlyph);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tempRow = new StackPanel { Orientation = Orientation.Horizontal };
        tempRow.Children.Add(new TextBlock { Text = Degree(s.Temperature), FontSize = plan.Temp, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(tempRow);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        // 今日高低温取自 daily 数组（当天域），与实况温度分属两个数据域：实况来源成功而当天来源
        // 失败时数组可能为空，直接索引会越界异常、被 Render 的 catch 吞成整页空白。缺当天就显示
        // 占位符，实况大温度/图标/四张卡照常渲染——两个域互不牵连。
        bool hasDay = today >= 0 && today < s.DayHighC.Length && today < s.DayLowC.Length;   // 日期轴过期时不拿旧日期冒充今天的高低温
        string range = hasDay ? $"{Degree(s.DayHighC[today])} / {Degree(s.DayLowC[today])}" : "-- / --";
        // 有预警：高低温缩成小字挪到温度右侧，把温度下方这行让给最高等级的预警；无预警维持原样。
        var warn = s.Warnings.OrderByDescending(w => WarnRank(w.Level)).FirstOrDefault();
        if (warn is not null)
        {
            tempRow.Children.Add(new TextBlock { Text = range, FontSize = 11, Margin = new Thickness(8, 0, 0, 6), Foreground = ColText, VerticalAlignment = VerticalAlignment.Bottom });
            bottom.Children.Add(WarningBadge(warn, s.Warnings.Length));
        }
        else bottom.Children.Add(new TextBlock { Text = range, FontSize = 13, Foreground = ColText, VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(bottom);
        // 来源与刷新时刻统一放在右下角页脚，摘要卡不再单占一行，左侧因此更紧凑。
        hero.Children.Add(side);
        summary.Children.Add(hero);
        // 读数卡与左侧大图标同一基准垂直居中，多列时上下沿才会齐平。
        stats.Rows = 2; stats.Columns = cols;
        stats.Children.Clear(); stats.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(stats, 1); summary.Children.Add(stats);
        // 卡片密度按实际格宽微调：格宽不足 98 时图标与间距各收一档，把宽度让给文字，
        // 84–120 的弹性列宽下标签才不至于整段被省略号吃掉。
        double cell = (Math.Max(160, Width - 28) - plan.HeroWidth) / Math.Max(1, cols);
        bool tight = cell < 98;
        double metaGlyph = tight ? 16 : 18, metaGap = tight ? 4 : 5;
        double valueFont = tight ? 11.5 : 12;
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
            (WeatherMetaKind.AirQuality, WeatherVisuals.AqiInk(s.Aqi), Loc.T("空气质量", "Air quality"), AirLevel(s), Lens.Aqi, null),
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
        _drawnChips = [.. _app.Settings.Weather.Chips];
        var available = upper.Concat(lower).ToDictionary(tile => tile.lens!.Value);
        foreach (var row in _app.Settings.Weather.ChipRows(cols))
        {
            var metrics = row.Where(chip => chip.Visible).Take(cols).ToArray();
            for (int i = metrics.Length; i < cols; i++)
                stats.Children.Add(new Border { IsHitTestVisible = false });
            foreach (var chip in metrics) AddTile(chip.Metric);
        }

        void AddTile(Lens metric)
        {
            var (kind, ink, label, value, lens, tip) = available[metric];
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 字号按实测宽度定：格宽 84–120 浮动，减内边距 14、描边 2、图标 16–18 + 间距 4–5 后，文字可用约 40–75。
            text.Children.Add(new TextBlock { Text = label, FontFamily = UI.PanelFont, FontSize = 9.5, Foreground = ColText, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = value, FontFamily = UI.PanelFont, FontSize = valueFont, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(_look.Primary), TextWrapping = TextWrapping.NoWrap });
            var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            // 风速卡用 8 方位箭头指示风向；箭头优先跟标题文字同档，文字缺失时才按角度分档。
            // 其余卡用各自的元信息图标。
            double windAngle = WeatherCodes.Wind8Angle(s.WindDirection, s.WindDirectionDeg);
            if (lens == Lens.Wind && !double.IsNaN(windAngle))
            {
                var windArrow = WeatherVisuals.WindArrowIcon(windAngle, ink);
                windArrow.Margin = new Thickness(0, 0, metaGap, 0);
                body.Children.Add(windArrow);
            }
            else
                body.Children.Add(new WeatherMetaIcon(kind, ink) { Width = metaGlyph, Height = metaGlyph, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, metaGap, 0) });
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
            // 可聚焦 + Enter/空格触发，读数卡对键盘用户同样可达。
            if (lens is { } pick)
            {
                tile.Cursor = System.Windows.Input.Cursors.Hand;
                tile.Focusable = true;
                tile.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { PickLens(pick); e.Handled = true; } };
                AutomationProperties.SetName(tile, label);
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
        // 指数视图里点读数卡：镜头已经选好，顺手切回逐时预报——曲线画在下方的 days 里，
        // 当前显示的却是指数视图，不切回来就只是「点了没反应」。ToggleView 已含一次 force Fit。
        if (_indicesMode) { ToggleView(); DrawSummary(snap); return; }
        Fit(Width, Height, true);
        DrawSummary(snap);
    }
    // 数值/时间的容错格式化：来源没有提供的字段显示为 --，不让 NaN 或空串进入界面。
    private static string Num(double value, string suffix, int digits = 0) => double.IsNaN(value) ? "--" : value.ToString($"F{digits}", System.Globalization.CultureInfo.InvariantCulture) + suffix;
    private static string Degree(double value) => double.IsNaN(value) ? "--" : $"{value:0}°";
    private static string Clock(string[] times, int index) => index >= 0 && index < times.Length && times[index].Length >= 16 ? times[index][11..16] : "--:--";
    // 右下角页脚：主来源与本次刷新时刻，用底部原有的 16px 空边，不向上挤占摘要卡与预报区。
    // 跨日缓存（日期轴对不上城市的今天）标注缓存时刻与「缓存」字样，不冒充刚更新。
    private void DrawFooter(WeatherSnapshot s)
    {
        bool stale = WeatherClock.LiveIndex(s) < 0;
        string time = s.FetchedAt.ToLocalTime().ToString(stale ? "MM/dd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
        string verb = stale ? Loc.T("缓存", "Cached") : Loc.T("更新", "Updated");
        string source = SourceName(s);
        footerNote.Text = source.Length > 0
            ? Loc.T($"{time} {verb} · {source}", $"{verb} {time} · {source}")
            : Loc.T($"{time} {verb}", $"{verb} {time}");
    }
    // 页脚主来源：优先每日域，其次实况域；完整分域来源由快照的 Sources 字典保存。
    private static string SourceName(WeatherSnapshot s)
        => s.Sources.TryGetValue("Daily", out var daily) && daily.Length > 0 ? daily
         : s.Sources.TryGetValue("Current", out var current) && current.Length > 0 ? current : "";
    // 视图切换：点天气图标时，底部 days（逐时/逐日预报）与 indicesView（生活指数）互换。
    // 两者共用同一行；指数视图有数据时才值得切过去，因此这里只翻转状态，内容由 DrawIndices 负责。
    private void ToggleView()
    {
        if (_last is not { } snap) return;
        _indicesMode = !_indicesMode;
        if (_indicesMode) DrawIndices(snap);
        indicesView.Visibility = _indicesMode ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = !_indicesMode && _forecastLevel >= 1 ? Visibility.Visible : Visibility.Collapsed;
        SyncSummaryAlignment();
        UpdateMinuteVisibility();
        Fit(Width, Height, true);
    }

    // 生活指数视图：清空后重建卡片，只展示指数本身，无数据时给一行轻提示。
    // 高频项排前、冷门项在后；等级语义色见 IndexTone。
    private LifestyleSlot[] _drawnIndices = [];
    private void DrawIndices(WeatherSnapshot s)
    {
        var body = indicesBody;
        body.Children.Clear();
        _drawnIndices = [.. _app.Settings.Weather.LifestyleIndices];
        var available = s.Indices.DistinctBy(item => item.Type.Length > 0 ? item.Type : item.Name).ToArray();
        var byType = available.ToLookup(item => int.TryParse(item.Type, out int type) ? type : 0);
        var items = _drawnIndices.Where(slot => slot.Visible).SelectMany(slot => byType[(int)slot.Type])
            .Concat(available.Where(item => !int.TryParse(item.Type, out int type) || !Enum.IsDefined((LifestyleIndex)type))).ToArray();
        if (items.Length == 0)
            body.Children.Add(Note(available.Length == 0 ? Loc.T("暂无生活指数", "No lifestyle indices")
                : Loc.T("可在天气设置中开启生活指数", "Enable lifestyle indices in weather settings")));
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            body.Children.Add(grid);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                string name = item.Name.EndsWith("指数") && item.Name.Length > 2 ? item.Name[..^2] : item.Name;
                string value = item.Category.Length > 0 ? item.Category : item.Level;
                string icon = item.Type switch
                {
                    "1" => "🏃", "2" => "🚗", "3" => "👕", "4" => "🎣",
                    "5" => "☀", "6" => "🧳", "7" => "🌼", "8" => "🛋",
                    "9" => "💊", "10" => "☁", "11" => "❄", "12" => "🕶",
                    "13" => "💄", "14" => "👓", "15" => "☂", "16" => "🌬", _ => "📋"
                };
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                header.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                    Foreground = ColText, FontSize = 15, Width = 24, VerticalAlignment = VerticalAlignment.Center });
                var title = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = UI.Brush(_look.Primary), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1);
                header.Children.Add(title);
                var level = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = Ink(IndexTone(item)), TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetColumn(level, 2);
                header.Children.Add(level);

                var card = new StackPanel();
                card.Children.Add(header);
                if (item.Text.Length > 0)
                    card.Children.Add(new TextBlock { Text = item.Text, FontSize = 11.5, Foreground = ColText,
                        Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
                var tile = new Border { Child = card, Margin = new Thickness(2, 0, 2, 5), Padding = new Thickness(10, 8, 10, 8),
                    Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), CornerRadius = new CornerRadius(7) };
                if (i % 2 == 0)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(tile, i / 2);
                Grid.SetColumn(tile, i % 2);
                grid.Children.Add(tile);
            }
        }
    }

    // 指数等级文字色：绿=适合、黄=中性/过渡、红=不宜/极端、灰=纯建议或无等级。
    // 和风的 level 数字是语言无关判据，但方向按 type 而定：多数「数字越大越差」，
    // 穿衣(3) 反而是 5=舒适最好、两端寒冷/炎热差；空调(11)/太阳镜(12)/化妆(13) 纯建议无好坏，一律中性灰。
    private static Color IndexTone(IndexItem item)
    {
        int level = int.TryParse(item.Level, out var n) ? n : 0;
        bool neutral = item.Type is "11" or "12" or "13";
        bool bad, warn;
        if (neutral) { bad = warn = false; }
        else if (item.Type == "3") { bad = level is 1 or 2 or 7; warn = level is 3 or 6; }
        else if (level > 0)
        {
            bad = level >= 4 || item.Category.Contains("不宜") || item.Category.Contains("很差") || item.Category.Contains("极易") || item.Category.Contains("不舒适");
            warn = !bad && (level == 3 || item.Category.Contains("较") || item.Category.Contains("一般") || item.Category.Contains("中"));
        }
        else
        {
            bad = item.Category.Contains("不宜") || item.Category.Contains("很差") || item.Category.Contains("极易") || item.Category.Contains("不舒适");
            warn = !bad && (item.Category.Contains("较") || item.Category.Contains("一般") || item.Category.Contains("中"));
        }
        Color tone = bad ? Color.FromRgb(255, 148, 148) : warn ? Color.FromRgb(255, 202, 112) : Color.FromRgb(138, 216, 158);
        return neutral || (level == 0 && !bad && !warn) ? Color.FromRgb(152, 169, 187) : tone;
    }

    private TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = ColMuted, Margin = new Thickness(2, 0, 0, 6) };
    // 宽度自适应列数，按时间正序（过去→今天→未来）取窗口：最小 7 列=今天+未来6天，最大 24 列=过去10+今天+未来13，左右按同比例同步扩展。
    private static int[] Order(WeatherSnapshot s, int columns)
    {
        int len = s.DayDates.Length;
        int today = WeatherClock.LiveIndex(s);          // 按城市当前日期定位“今天”
        // 缓存跨日、城市的今天不在轴上时退回快照自带锚点选窗口：窗口只是决定取哪些天，
        // 「今天」标签与高亮由调用方按日期轴是否过期另行处理。
        if (today < 0) today = WeatherClock.SnapshotIndex(s);
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
        forecast.Children.Clear(); lowRow.Children.Clear(); lowNote.Children.Clear(); dayInfoRow.Children.Clear(); tempLabels.Clear(); conditionLabels.Clear(); conditionRows.Clear(); columnGlyphs.Clear();
        nowRule.Visibility = Visibility.Collapsed; nowText.Visibility = Visibility.Collapsed;   // 「现在」分界线只属于逐时
        // 天文镜头：预报列收起，弧线、色带与底部时刻行全部画进弹性的 chart 画布（由 DrawChart 统一绘制）。
        if (_lens == Lens.Celestial) { forecast.Visibility = Visibility.Collapsed; todayGlow.Visibility = Visibility.Collapsed; return; }
        forecast.Visibility = Visibility.Visible;
        // 锚点只依赖列宽，宽度变化时可以单独重贴，不必重建列。
        UpdateAnchors(s);
        // 列刚重建，行高与可见性回到默认（天气名称全显示）；立刻贴回当前档位，避免延迟重绘后闪回完整档。
        if (_hourlyMode) DrawHourlyForecast(s); else DrawDailyForecast(s);
        ApplyForecastLevel();
    }
    // 逐日列骨架：5 行固定高度（星期 / 日期 / 图标 / 天气名 / 温度），合计 98px。
    // 逐时列骨架：6 行（风向风级 / 降水概率 / 空气等级 / 图标 / 天气名 / 温度），合计 112px——
    // 降水概率与空气等级是逐时独有的小时级信息，只进逐时列、逐日不加行。
    // 两种模式行数因此不同，切换时上段高度差 14px，由弹性的折线行吸收（days 总高不变，只是折线带矮一截）；
    // 图标、天气名、温度三行在两套骨架里等高，切换时这三行像素级对齐。
    private static readonly double[] ColumnRowHeights = { 20, 14, 30, 14, 20 };
    private static readonly double[] HourlyRowHeights = { 20, 14, 14, 30, 14, 20 };
    // 天气名所在行：收纳档 3 收起的就是这一行；两套骨架行号不同，估算与贴回都必须按模式取。
    private const int DailyConditionRow = 3, HourlyConditionRow = 4;
    // 图标所在行：收纳档 1 连图标一起收起（只留数值与时刻），两套骨架都是第 3 行之前那一行。
    private const int DailyGlyphRow = 2, HourlyGlyphRow = 3;
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
    // 逐时模式：列里放「风向风级 / 降水概率 / 空气等级 / 图标 / 天气名称 / 温度」，时刻放在最底部那一行，与折线共用同一个列几何。
    // 0 点那一列改标日期（如「11月20日」）。「现在」不占任何一列：它落在「过去那一格」与「当前那一格」
    // 之间的分界线上——竖线贯穿列区到底行、线顶写「现在」，当前这一格照常显示自己的时刻。
    private void DrawHourlyForecast(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns);
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
                if (bft.Length > 0) mark.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, Foreground = ColWind, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(arrow ? 2 : 0, 0, 0, 0) });
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
                    Foreground = Ink(Color.FromRgb(96, 168, 246))
                };
                var tip = new List<string>();
                if (prob >= 0) tip.Add(Loc.T("降水概率 ", "PoP ") + prob + "%");
                if (!double.IsNaN(pmm)) tip.Add(Loc.T("降水量 ", "Precip ") + pmm.ToString("0.#") + "mm");
                rain.ToolTip = string.Join(" · ", tip);
                CellAt(column, rain, 1);
            }
            // 第 3 行：空气质量等级。文字与颜色由该小时 AQI 按国标 HJ 633 换算（复用 AqiText/AqiInk，与空气卡同一套标准），
            // 列窄只写短名（去掉「污染」后缀：优/良/轻度/中度/重度/严重），悬停 tooltip 给全文与数值。
            // 逐时 AQI 仅和风提供，其他来源/城市该行留空——0 信息不占位。
            int aqi = p < s.HourAqi.Length ? s.HourAqi[p] : -1;
            if (aqi >= 0)
            {
                var level = new TextBlock
                {
                    Text = AqiLevelShort(aqi), FontSize = ColNote, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                    Foreground = Ink(WeatherVisuals.AqiInk(aqi))
                };
                level.ToolTip = $"{Loc.T("空气质量", "Air quality")} {WeatherCodes.AqiText(aqi)} · AQI {aqi}";
                CellAt(column, level, 2);
            }
            var hourGlyph = new WeatherGlyph(code, day) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            CellAt(column, hourGlyph, HourlyGlyphRow);
            columnGlyphs.Add(hourGlyph);
            // 第 5 行：天气名称。61/63/65 这几档在图标上几乎长得一样，文字补的正是图标说不出的那一级。
            var condition = new TextBlock { Text = HourCondition(code), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
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
                Foreground = ColMuted
            });
        }
    }
    // 当前整点所在列：窗口固定以「当前整点前 1 小时」为起点，所以正常就落在第 2 列（序号 1）。
    // 必须按整点比、不能按「离现在最近」比：22:40 时 23:00 只差 20 分钟，按最近取会把「现在」标到还没到的那一列。
    // 时钟按城市所在时区算（快照里带 UTC 偏移），查国外城市时不会拿本机时间硬套。
    private static int HourNow(WeatherSnapshot s, int n)
    {
        string now = WeatherClock.Now(s).ToString("yyyy-MM-ddTHH:00", CultureInfo.InvariantCulture);
        int best = 0;
        for (int p = 0; p < Math.Min(n, s.HourTimes.Length); p++)
        {
            if (s.HourTimes[p].Length < 16) continue;
            if (string.CompareOrdinal(s.HourTimes[p], now) <= 0) best = p;   // 最后一个不晚于当前整点的列
            else break;
        }
        return best;
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
    // 只依赖列宽的两块覆盖层：逐日的「今天」高亮、逐时的「现在」分界线。
    // 列内容与列宽无关，拖动改宽时只重贴这两块，不重建整列。
    private void UpdateAnchors(WeatherSnapshot s)
    {
        if (_lens == Lens.Celestial) { todayGlow.Visibility = nowRule.Visibility = nowText.Visibility = Visibility.Collapsed; return; }
        double col = forecast.Columns > 0 ? Math.Max(2, _chartWidth / forecast.Columns) : 0;
        if (_hourlyMode)
        {
            // 第 now 列的左沿就是「过去 / 当前」的交界（列区左边距 6，与预报网格同一套几何）。
            int now = HourNow(s, Math.Max(1, forecast.Columns));
            double edge = 6 + now * col;
            todayGlow.Visibility = Visibility.Collapsed;
            nowRule.Visibility = Visibility.Visible;
            nowRule.Width = 2;
            nowRule.Margin = new Thickness(Math.Max(0, edge - 1), 14, 0, 0);   // 顶部 14px 让给「现在」两个字
            nowRule.Background = NowRuleBrush;
            nowText.Visibility = Visibility.Visible;
            nowText.Text = Loc.T("现在", "Now");
            nowText.Foreground = NowTextBrush;
            nowText.Margin = new Thickness(Math.Max(0, edge - nowText.Width / 2), 0, 0, 0);
            return;
        }
        // 「今天」列高亮：跨四行覆盖上段+折线+低温+逐日信息行，与列内左右各 3px 留白对齐。找不到今天（跨日缓存）就收起。
        int today = WeatherClock.LiveIndex(s);
        var order = Order(s, forecast.Columns);
        int todayPos = -1;
        for (int p = 0; p < order.Length && todayPos < 0; p++) if (order[p] == today) todayPos = p;
        nowRule.Visibility = nowText.Visibility = Visibility.Collapsed;
        if (todayPos < 0) { todayGlow.Visibility = Visibility.Collapsed; return; }
        todayGlow.Visibility = Visibility.Visible;
        todayGlow.Width = Math.Max(2, col - 6);
        todayGlow.Margin = new Thickness(6 + todayPos * col + 3, 2, 0, 0);
        todayGlow.Background = TodayFill;
        todayGlow.BorderBrush = TodayEdge;
        todayGlow.BorderThickness = new Thickness(1);
    }
    private void DrawDailyForecast(WeatherSnapshot s)
    {
        int today = WeatherClock.LiveIndex(s);
        bool stale = today < 0;   // 跨日缓存：不把任何一天标成「今天/明天」，一律按星期+绝对日期渲染
        var order = Order(s, forecast.Columns);
        for (int p = 0; p < order.Length; p++)
        {
            int i = order[p]; var date = DateTime.Parse(s.DayDates[i]); var column = Column(ColumnRowHeights);
            string label = stale ? Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek])
                : i == today ? Loc.T("今天", "Today") : i == today + 1 ? Loc.T("明天", "Tomorrow") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]);
            bool isToday = i == today;
            CellAt(column, new TextBlock { Text = label, FontSize = ColValue, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Foreground = isToday ? UI.Brush(_look.Primary) : ColText }, 0);
            CellAt(column, new TextBlock { Text = date.ToString("MM/dd"), Foreground = ColMuted, FontSize = ColNote, TextAlignment = TextAlignment.Center }, 1);
            int before = DayCodeAt(s, i);
            int night = NightCodeAt(s, i);
            // 折线上方画「转后」（夜间），夜间缺失就退回白天；只有真变了才用夜间样式区分。
            int topCode = DayTopCode(s, i);
            bool changed = night >= 0 && night != before;
            var dayGlyph = new WeatherGlyph(topCode, !changed) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            CellAt(column, dayGlyph, DailyGlyphRow);
            columnGlyphs.Add(dayGlyph);
            var condition = new TextBlock { Text = HourCondition(topCode), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
            CellAt(column, condition, 3);
            conditionLabels.Add(condition); conditionRows.Add(column.RowDefinitions[3]);
            // 高温在预报列第 5 行、低温在下方独立低温行，折线夹在两者之间；两个温度都登记进 tempLabels 供低档位统一收起。
            var high = new TextBlock { Text = Degree(s.DayHighC[i]), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(high); CellAt(column, high, 4);
            // 今天列的选择态高亮由垫底的 todayGlow 贯穿整列承担，列自身保持透明。
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            var low = new TextBlock { Text = Degree(s.DayLowC[i]), Foreground = UI.Brush(_look.Primary), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(low); lowRow.Children.Add(low);
            // 转前天气名单独占辅注行（与上方天气名同字号同色），信息行只剩 28px 图标与风两行。
            var lowerName = new TextBlock { Text = HourCondition(before), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = ColCondition };
            conditionLabels.Add(lowerName);
            lowNote.Children.Add(lowerName);
            var info = new Grid();
            info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ColGlyph, GridUnitType.Star) });
            info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayInfoCompact - ColGlyph, GridUnitType.Star) });
            var lowerGlyph = new WeatherGlyph(before, true) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center };
            lowerGlyph.VerticalAlignment = VerticalAlignment.Center;
            CellAt(info, lowerGlyph, 0);
            string dirText = s.DayWindDir.Length > i ? s.DayWindDir[i] : "";
            double dir = s.DayWindDirDeg.Length > i ? s.DayWindDirDeg[i] : double.NaN;
            double wind = s.DayWindMaxKmh.Length > i ? s.DayWindMaxKmh[i] : double.NaN;
            var windRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(dir))
            {
                var arrow = WeatherVisuals.WindArrowIcon(WeatherCodes.Wind8Angle(dirText, dir), Color.FromRgb(160, 205, 235));
                arrow.Width = 12; arrow.Height = 12; arrow.Margin = new Thickness(0, 0, 2, 0);
                windRow.Children.Add(arrow);
            }
            string bft = Bft(wind);
            if (bft.Length > 0) windRow.Children.Add(new TextBlock { Text = bft, FontSize = ColNote, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ColWind });
            if (windRow.Children.Count > 0) CellAt(info, windRow, 1);
            if (!double.IsNaN(dir)) info.ToolTip = !string.IsNullOrWhiteSpace(dirText) ? dirText : WeatherCodes.DegreeToCompass(dir) + Loc.T("风", "");
            dayInfoRow.Children.Add(info);
        }
    }
    // 与预报同序的高低温折线：金线高温、蓝线低温，每个采样点带圆点与温度数字注释，随预报宽度横向伸展。

    private void DrawChart(WeatherSnapshot s)
    {
        chart.Children.Clear();
        // 真正按当前宽度画过了：撤掉拖宽期间的横向缩放，并记下这版几何的基准宽度。
        chart.RenderTransform = null;
        _chartDrawnWidth = Math.Max(30, chart.Width);
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
    private void DrawHourly(WeatherSnapshot s)
    {
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, n).Where(i => s.HourTempsC.Length > i && !double.IsNaN(s.HourTempsC[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => s.HourTempsC[i]), range = Math.Max(1, valid.Max(i => s.HourTempsC[i]) - min);
        var brush = ChartTemp;
        double bandTop = 17, bandBottom = ChartBand() - 11;              // 弹性高度带：随画布实际高度伸缩
        double Y(double v) => bandBottom - (v - min) / range * (bandBottom - bandTop);
        DrawTrend(valid.Select(p => new Point(X(p), Y(s.HourTempsC[p]))).ToArray(), brush);
        foreach (int p in valid)
        {
            double yy = Y(s.HourTempsC[p]);
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush }; Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            var probe = new TextBlock { Text = $"{s.HourTempsC[p]:0}°", FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
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
            var label = new TextBlock { Text = degree ? data[p].ToString(fmt, CultureInfo.InvariantCulture) + "°" : data[p].ToString(fmt, CultureInfo.InvariantCulture), FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
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
            var label = new TextBlock { Text = labelText(p), FontSize = 9, Foreground = brush is SolidColorBrush solid ? Ink(solid.Color) : brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, baseY - h - 13); chart.Children.Add(label);
        }
    }
    // 天文镜头视口窗口左缘。窗口宽 24h，规则：当前时段所处天体弧以其中天为窗口中心——
    // 日弧以正午居中、月弧以月中天居中；两弧之间的晨昏间隙以「现在」居中。
    // 中天近似弧线时间中点，弧长 < 24h 时两侧余量各 (24−弧长)/2，当前曲线必落在视口内、不被 0/24 边界拆开；
    // 中天极端偏移时 clamp 进 [落端−12, 升端+12] 强制满足不跨条件，clamp 区间退化（弧长>24h）则退回弧线中点。
    private void DrawCelestialView(WeatherSnapshot s)
    {
        todayGlow.Visibility = Visibility.Collapsed;
        DateTime now = WeatherClock.Now(s).DateTime;
        double width = Math.Max(30, chart.Width), height = ChartBand();
        bool showArcs = height >= CelestialArcHeight;
        double band = Math.Max(26, height - 26);
        double horizon = showArcs ? Math.Max(26, height - 72) : Math.Max(26, band - 34);
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
        foreach (var arc in showArcs ? visible : [])
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
        FrameworkElement? centerGlyph = !showArcs ? null
            : daylight ? new WeatherGlyph(0, true)
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
        var todayMoonrise = marks.FirstOrDefault(m => !m.sun && m.name == Loc.T("月升", "Moonrise") && m.at.Date == now.Date);
        if (todayMoonrise != default && (todayMoonrise.at < start || todayMoonrise.at >= end))
        {
            var caption = new TextBlock
            {
                Text = Loc.T("今日月升 ", "Moonrise today ") + todayMoonrise.at.ToString("HH:mm"),
                FontSize = 11, Foreground = Ink(silver)
            };
            Canvas.SetLeft(caption, 6); Canvas.SetTop(caption, showArcs ? 0 : Math.Max(0, horizon - 14)); chart.Children.Add(caption);
        }
        var captions = new List<(TextBlock label, double x, double left)>();
        double nextLeft = 0;
        foreach (var m in marks.Where(m => m.at >= start && m.at < end).OrderBy(m => m.at))
        {
            var label = new TextBlock
            {
                Text = m.name + "\n" + m.at.ToString("HH:mm"), TextAlignment = TextAlignment.Center,
                FontSize = 9, Foreground = Ink(m.sun ? gold : silver)
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
                Foreground = ColMuted };
            tick.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tick, Math.Clamp(X(at) - tick.DesiredSize.Width / 2, 0, Math.Max(0, width - tick.DesiredSize.Width)));
            Canvas.SetTop(tick, height - 12); chart.Children.Add(tick);
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
        Canvas.SetLeft(dot, X(now) - 3.5); Canvas.SetTop(dot, band - 3.5); chart.Children.Add(dot);
    }
    // 风向箭头：几何按「指向上」画，直接旋到风的来向；气象风向就是来向，北风指北（向上）。
    // 角度先归到 8 方位，逐时箭头只有 0/45/…/315 八种朝向，和风向文字同一档。
    private UIElement WindMark(double deg) => new Path
    {
        Data = Geometry.Parse("M0,-4.6 L3.6,3.4 L0,1.4 L-3.6,3.4 Z"),
        Fill = ColWind,
        Width = 7, Height = 7, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(double.IsNaN(deg) ? 0 : WeatherCodes.Compass8Angle(deg))
    };
    // 码值已由 WeatherCodes 归一到同一套区间，这里只翻成最短的一档文字：列宽最小约 44px，
    // 中文最多三个字，英文最多六个字母，超出就会被 TextBlock 直接裁掉。
    private static string AqiLevelShort(int aqi) => WeatherCodes.AqiBand(aqi) switch
    {
        0 => "", 1 => Loc.T("优", "Good"), 2 => Loc.T("良", "Fair"), 3 => Loc.T("轻度", "Light"),
        4 => Loc.T("中度", "Med."), 5 => Loc.T("重度", "Heavy"), _ => Loc.T("严重", "Sev.")
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
    // ── 工具栏的城市选择 ───────────────────────────────
    internal sealed record CityChoice(string Label, bool Selected, int Index);

    // 灵动岛空间有限：城市名只显示前两级（洪山区 · 武汉市）。
    internal static string BriefName(string full) => City.BriefOf(full);
    private bool AutoSelected => state.Selected < 0 || state.Selected >= state.Cities.Count;
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
        state.Selected = index; _app.QueueSave();
        await Refresh();
    }
    internal Action? SettingsRequested;
}