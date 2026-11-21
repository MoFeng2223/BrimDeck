using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrimDeck.Core;
using City = BrimDeck.City;
using State = BrimDeck.WeatherData.State;

namespace BrimDeck;

// Open-Meteo weather, stored separately from account and device settings.
internal sealed class WeatherView : UserControl
{
    private State state = new();
    // 第一行为「今日天气」区域：Star 弹性行 + MinHeight，高度足够时内容拉伸居中，压到最小高度后不再往下压缩。
    private readonly RowDefinition summaryRow = new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 112 };
    private readonly Grid summary = new();
    private readonly UniformGrid stats = new() { Rows = 2, Columns = 2 };
    private readonly UniformGrid forecast = new() { Rows = 1, Columns = 7 };
    private readonly Grid days = new();
    private readonly Canvas chart = new() { Height = 68, Width = 588, Background = Brushes.Transparent };
    private bool _hourlyMode;    // 底部曲线视图：false=每日高低温，true=当日 24h 逐时
    // 底部「看什么」：卡片点一下换成该指标的逐时曲线，再点一次回到气温。逐日只有气温，所以非气温镜头一律切到逐时。
    private enum Lens { Temp, Feels, Humidity, Wind, Sun }
    private Lens _lens = Lens.Temp;
    // 每列纵向顺序：星期/日期/图标/降雨/高温 →【折线图】→ 低温。低温独立成行夹在折线下方，折线正好插在高温与低温之间。
    private readonly UniformGrid lowRow = new() { Rows = 1, Columns = 7, Height = 18 };
    private readonly List<TextBlock> tempLabels = new();   // 高/低温文本，供“隐藏高低温”档统一收起
    // 「今天」列选择态高亮：垫在 days 底部、跨三行（预报列上段/折线/低温），从列顶贯穿到最底部，折线与低温行都被它包住。
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
    private double _measuredAvail = -1;   // 上次做档位试探时的可用高度，高度没变就不用再测
    private string _autoCityName = "";   // 最近一次自动定位到的城市名，工具栏按钮据此显示「自动定位 · 城市」
    private int _columns = 7;
    private double _chartWidth;
    private int generation;
    // 视口即内容区，宽度与高度随 Fit 传入值整体填充。底部 days 纵向为 预报列上段(星期/日期/图标/降雨)→折线(高低温曲线)→低温，
    // 按「能否完整展示」从最高档向下逐级收纳：完整档(折线代表温度，隐藏高低温文本) → 藏折线(恢复高低温文本) → 藏高低温(仅基础列) → 连基础列都不够就整行隐藏、只留今日天气。
    internal void Fit(double width, double height, bool force = false)
    {
        Width = width; Height = height;
        double avail = Math.Max(0, height - 16);                      // 根 Grid 上边距 0 + 下边距 16
        summaryRow.MinHeight = Math.Min(summaryMinHeight, avail);     // 今日天气最小高度封顶，避免自身被压坏
        // 列数随宽度在 7（窄）到 24（满宽 440–1200 线性插值）之间自适应；24 列正好是历史 10 + 今天 + 未来 13。
        // 折线画布与预报网格同一套几何：左右边距同为 6、内容宽度同为 width-40，点坐标按列中心计算，保证每个点对准列正中。
        double daysW = Math.Max(0, width - 28);
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
        bool heightChanged = Math.Abs(avail - _measuredAvail) > 0.5;
        bool redraw = force || columnsChanged || Math.Abs(chartWidth - _chartWidth) > 0.5;
        _columns = columns; _chartWidth = chartWidth; forecast.Columns = shownValue; lowRow.Columns = shownValue; chart.Width = chartWidth;
        // 拖动调宽时这里每帧都会进来。画布宽度与列数立刻生效，重建则压到 30fps：
        // 一帧里重建 24 列的列元素加整条折线要几毫秒，120Hz 下就是持续掉帧，看起来正是「卡」。
        if (redraw && _last is { } f)
        {
            long now = Environment.TickCount64;
            if (force || now - _drawnAt >= 33) { _drawnAt = now; _redrawPending = false; DrawForecast(f); DrawChart(f); }
            else { _redrawPending = true; FlushLater(); }
        }
        // 列数、列内数据量、可用高度都没变，档位就不可能变：跳过下面三次实测。
        // 调高度只走这条早退路径，所以它一直不卡；调宽度以前每帧都要重推列数 + 重测 + 重画，卡就卡在这。
        if (!(force || columnsChanged || shownChanged || heightChanged)) return;
        _measuredAvail = avail;
        double room = avail - summaryRow.MinHeight;                   // 实际可让给预报区的空间
        // 从完整档往下逐级收纳：每一步都按当前可见性实地测量需求量，绝不靠估算档位。先试完整档。
        SetForecastLevel(3);
        if (MeasureDays(daysW) > room)
        {
            SetForecastLevel(2);                                      // 装不下 → 藏折线
            if (MeasureDays(daysW) > room)
            {
                SetForecastLevel(1);                                  // 还装不下 → 藏高低温，保留星期/日期/图标/降雨
                days.Visibility = MeasureDays(daysW) <= room ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        SyncSummaryAlignment();
    }
    // 今日天气这块的对齐跟着「下方有没有预报条」走：
    // 有预报条时顶对齐，多出来的高度就落回预报条上方（居中会把几十像素平分到页头下面，正是上边留白过大的由来）；
    // 只剩今日天气时（面板矮到放不下预报条）居中，否则内容贴着页头、下面空一大片。
    private void SyncSummaryAlignment()
    {
        var place = days.Visibility == Visibility.Visible ? VerticalAlignment.Top : VerticalAlignment.Center;
        foreach (UIElement child in summary.Children)
        {
            if (child is FrameworkElement element && element.VerticalAlignment != place) element.VerticalAlignment = place;
        }
    }
    // 3 完整档(基础列+折线)：折线本身画出高低温曲线，低温行与高/低温文本收起，不再由文本重复表达温度。2 无折线(基础列+高低温两列)：折线放不下时高低温由文本两列表达。
    // 1 无高低温(仅基础列)：保留星期/日期/图标/降雨，高低温与折线全部收起。1 档连基础列都装不下则由调用方整行折叠。
    private void SetForecastLevel(int level)
    {
        chart.Visibility = level >= 3 ? Visibility.Visible : Visibility.Collapsed;
        bool showTemp = level >= 2;
        // 逐时与逐日必须同进退：lowRow 在逐日放低温、在逐时放时刻，行高都是 18px。
        // 只要两种模式下这一行的显隐规则一致，days 的总高就恒等，切模式永远不会触发重排或降档。
        lowRow.Visibility = level >= 2 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in tempLabels) p.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = level >= 1 ? Visibility.Visible : Visibility.Collapsed;
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
    private double MeasureDays(double w)
    {
        days.Measure(new Size(w, double.PositiveInfinity));
        return days.DesiredSize.Height;
    }
    private const double summaryMinHeight = 112;
    private readonly Grid host = new();
    private bool fetching;
    private CancellationTokenSource? _token;
    private DateTimeOffset _cycleStart = DateTime.UtcNow;
    internal bool IsFetching => fetching;
    internal DateTimeOffset CycleStart => _cycleStart;
    internal event Action? StateChanged;
    private void RaiseState() => StateChanged?.Invoke();

    public WeatherView()
    {
        Width = 620; Height = 290;
        Background = Brushes.Transparent; Foreground = Brushes.White;
        try { state = WeatherData.Load(); } catch { }
        // Grid 两行：今日天气(Star 弹性行+MinHeight) / 底部预报(Auto 贴地)，宽度与高度都随 Fit 传入值整体填充。
        // 底边距 16 与用量页列表底部一致（UsageContent 的 layout.Bottom），预报区与窗口底部保持相同的留白。
        // 上边距 0：页头那一行（36）本身就是内容的上沿，用量页也是把内容直接贴在页头下沿的
        // （UsageContent.Margin 顶边为 0，列内 ColumnTopInset = 2）。这里再留 6 会和用量页形成两套基准。
        var root = new Grid { Margin = new Thickness(14, 0, 14, 16) };
        root.RowDefinitions.Add(summaryRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0); Grid.SetRow(days, 1);
        root.Children.Add(summary); root.Children.Add(days);
        host.Children.Add(root); Content = host;
        // days 内部三行上下排列：预报列上段(星期/日期/图标/降雨/高温)在上、折线图居中、低温行在下，
        // 折线正好插在高温与低温之间，各自独立成行、互不叠加、不遮挡文字。
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 当前位置高亮垫底：跨三行(上段/折线/底部行)，Forecast、折线、低温/时刻行都画在它上面，选择态从列顶贯穿到列底。
        // 逐日高亮「今天」那一列，逐时高亮「现在」那一列——同一块 Border、同一套定位公式，两种模式共用。
        Grid.SetRow(todayGlow, 0); Grid.SetRowSpan(todayGlow, 3); days.Children.Add(todayGlow);
        // 「现在」分界线垫在同一层：竖线在两格之间、文字在列区上方那两行空位里，都不参与命中测试。
        Grid.SetRow(nowRule, 0); Grid.SetRowSpan(nowRule, 3); days.Children.Add(nowRule);
        forecast.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(forecast, 0); days.Children.Add(forecast);
        // 折线画布与预报网格左边距同为 6px 显式左对齐，画布原点与列起点完全重合，点坐标按列中心计算。
        chart.Margin = new Thickness(6, 8, 6, 0); chart.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(chart, 1); days.Children.Add(chart);
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
        lowRow.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(lowRow, 2); days.Children.Add(lowRow);
        Grid.SetRow(nowText, 0); Grid.SetRowSpan(nowText, 2); days.Children.Add(nowText);
        IsVisibleChanged += async (_, _) => { if (IsVisible) { try { state = WeatherData.Load(); } catch { } await Refresh(); } };
        Loaded += async (_, _) => { await Refresh(); timer.Start(); }; timer.Tick += async (_, _) => await Refresh(); Unloaded += (_, _) => { timer.Stop(); generation++; };
    }

    internal Task ManualRefresh()
    {
        if (fetching) { Cancel(); return Task.CompletedTask; }
        return Refresh();
    }
    internal void Cancel() => _token?.Cancel();
    private async Task Refresh()
    {
        _token?.Cancel();
        var cts = new CancellationTokenSource();
        _token = cts;
        var token = cts.Token;
        int request = ++generation; var selected = state.Selected; string key = selected >= 0 && selected < state.Cities.Count ? state.Cities[selected].ToString() : "auto";
        if (state.Cache.TryGetValue(key, out var cached)) Render(cached);
        fetching = true; RaiseState();
        try {
            City city;
            if (selected >= 0 && selected < state.Cities.Count) city = state.Cities[selected];
            else city = await WeatherData.LocateAsync(state.Schedule, token) ?? new City("当前位置", 0, 0);
            // 历史单独一轮：先画不含历史的一版，历史到了再整体替换。首屏不必等 10 个时光机请求。
            var snapshot = await WeatherData.CurrentAsync(city, state.Schedule, token, partial => { if (request == generation) Render(partial); });
            if (request != generation) return;
            if (token.IsCancellationRequested) return;
            Render(snapshot); state.Cache[key] = snapshot; WeatherData.Save(state); _cycleStart = DateTime.UtcNow;
        } catch (OperationCanceledException) {
            if (request != generation) return;
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
            DrawSummary(s); DrawForecast(s); DrawChart(s);
        } catch { }
    }
    // 左上：大图标 + 大字温度 + 天气描述 + 高低温 + 来源/详情；右上：体感 / 湿度 / 风速 / 日照入口四张小卡。
    private void DrawSummary(WeatherSnapshot s)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        summary.RowDefinitions.Clear();
        int today = WeatherData.TodayIndex(s);    // 正序数组中“今天”的位置
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 今日天气那一块顶对齐，不在星号行里居中：面板拉高时星号行会剩下几十像素，
        // 居中会把它们平分到上方，看起来就是「页头上方留白过大」。顶对齐后大图标上沿
        // 落在 36 + 2 = 38，和用量页第一行（页头 36 + ColumnTopInset 2）同一个基准，
        // 多出来的高度全部让给今日天气与底部预报条之间。
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top };
        hero.Children.Add(new WeatherGlyph(s.WeatherCode, s.IsDay) { Width = 56, Height = 56, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(new TextBlock { Text = Degree(s.Temperature), FontSize = 44, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = $"{Degree(s.DayHighC[today])} / {Degree(s.DayLowC[today])}", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(205, 224, 247)), VerticalAlignment = VerticalAlignment.Center });
        side.Children.Add(bottom);
        // 来源与详情：来源只显示主每日来源，完整分域来源在详情浮层里逐项列出。
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 0, 0) };
        footer.Children.Add(new TextBlock { Text = SourceLabel(s), FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(174, 196, 220)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
        var detail = new Button { Content = Loc.T("详情", "Details"), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(143, 208, 255)), BorderThickness = new Thickness(0), Padding = new Thickness(6, 0, 0, 0), FontSize = 9.5, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        WeatherMotion.Attach(detail); detail.Click += (_, e) => { e.Handled = true; ShowDetails(s); };
        footer.Children.Add(detail);
        side.Children.Add(footer); hero.Children.Add(side);
        summary.Children.Add(hero);
        // 四张卡与左侧大图标同一基准顶对齐，两列的上沿才会齐平（居中的话只要两列高度不同就会错开）。
        stats.Children.Clear(); stats.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(stats, 1); summary.Children.Add(stats);
        string rise = Clock(s.SunriseTimes, today), set = Clock(s.SunsetTimes, today);
        // 四张卡既是读数也是镜头：点一下底部换成该指标的逐时曲线，再点一次回到气温（卡片自身高亮表示当前镜头）。
        var tiles = new[] { (WeatherMetaKind.Thermo, Color.FromRgb(255, 176, 102), Loc.T("体感", "Feels like"), Num(s.FeelsLike, "°"), Lens.Feels), (WeatherMetaKind.Droplet, Color.FromRgb(112, 178, 255), Loc.T("湿度", "Humidity"), s.HumidityPercent >= 0 ? $"{s.HumidityPercent}%" : "--", Lens.Humidity), (WeatherMetaKind.Wind, Color.FromRgb(140, 200, 220), Loc.T("风速", "Wind"), Num(s.WindKmh, " km/h"), Lens.Wind), (WeatherMetaKind.Sunrise, Color.FromRgb(255, 200, 90), Loc.T($"日出 {rise}", $"Sunrise {rise}"), Loc.T($"日落 {set}", $"Sunset {set}"), Lens.Sun) };
        foreach (var (kind, ink, label, value, lens) in tiles)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // 字体跟面板一致（UI.PanelFont = Segoe UI Variable Text + 微软雅黑），此前这里是系统默认族，
            // 和时钟、按钮上的字不是同一套。字号按实测宽度定：格宽固定 125，减内边距 18、描边 2、
            // 图标 17 + 间距 7 后，文字可用 75；最宽的是英文「Sunset 17:24」@12.5 = 70.3、中文「日落 17:24」= 57.4。
            text.Children.Add(new TextBlock { Text = label, FontFamily = UI.PanelFont, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(169, 189, 214)), Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { Text = value, FontFamily = UI.PanelFont, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap });
            var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            body.Children.Add(new WeatherMetaIcon(kind, ink) { Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            body.Children.Add(text);
            bool active = _lens == lens;
            // 四张卡是同一组读数，底色统一用「白色低透明」叠在天空渐变上：哪一张都不抢眼，
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
                CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Padding = new Thickness(9, 4, 9, 4), Child = body,
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1)
            };
            // 悬浮只换底色与描边，就是普通按钮的反馈：不缩放、不位移。
            // 卡片在 2×2 里彼此只隔 6px，整体放大 1.035 会顶到邻卡、看着像在互相推挤；
            // 里面的图标也被同一个放大带着抖，所以这里连 WeatherMotion 都不挂。
            void Ink(Color toFill, Color toEdge)
            {
                fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toFill, TimeSpan.FromMilliseconds(110)));
                edge.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(toEdge, TimeSpan.FromMilliseconds(110)));
            }
            tile.MouseEnter += (_, _) => Ink(fillHot, edgeHot);
            tile.MouseLeave += (_, _) => Ink(fillIdle, edgeIdle);
            tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; PickLens(lens); };
            stats.Children.Add(tile);
        }
        // 刚建出来的两列默认按「有预报条」处理；预报条此刻被收起时要跟着改成居中，规则只在 SyncSummaryAlignment 一处。
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
    private static string SourceLabel(WeatherSnapshot s)
    {
        string? main = s.Sources.TryGetValue("Daily", out var daily) && daily.Length > 0 ? daily : s.Sources.TryGetValue("Current", out var current) && current.Length > 0 ? current : null;
        if (main is null) return "";
        string text = Loc.T("来源 ", "Source ") + main;
        return text.Length > 46 ? text[..45] + "…" : text;
    }
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

        // 标题行：城市完整层级（洪山区 · 武汉市 · 湖北省）、天气、更新时间、来源，右侧关闭。
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        string condition = s.ConditionText.Length > 0 ? s.ConditionText : WeatherCodes.Text(s.WeatherCode);
        title.Children.Add(new TextBlock
        {
            Text = s.FullName.Length > 0
                ? (s.FullName == "当前位置" ? Loc.T("当前位置", "Current location") : s.FullName)
                : (s.Name == "当前位置" ? Loc.T("当前位置", "Current location") : DisplayName(s.Name)),
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
        });
        title.Children.Add(new TextBlock { Text = $"{condition} · {Num(s.Temperature, "°")} · " + Loc.T($"更新于 {s.FetchedAt:HH:mm}", $"Updated {s.FetchedAt:HH:mm}"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(158, 178, 200)), Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(title);
        var close = new Button { Content = new WeatherMetaIcon(WeatherMetaKind.Close, Colors.White) { Width = 13, Height = 13 }, Width = 28, Height = 28, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), Foreground = Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = System.Windows.Input.Cursors.Hand };
        WeatherMotion.Attach(close); close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        body.Children.Add(header);

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
        string dirText = s.WindDirection.Length > 0 ? s.WindDirection : Num(s.WindDirectionDeg, "°");
        Metric(Loc.T("风向", "Wind direction"), dirText + (scale >= 0 ? $" · {Loc.T("风级 ", "Bft ")}{scale}" : ""), src("wind_dir_text") ?? src("wind_dir") ?? src("wind_scale"));
        Metric(Loc.T("气压", "Pressure"), Num(s.PressureHpa, " hPa"), src("pressure"));
        Metric(Loc.T("能见度", "Visibility"), Num(s.VisibilityKm, " km", 1), src("visibility"));
        Metric(Loc.T("露点", "Dew point"), Num(s.DewPointC, "°"), src("dew"));
        Metric(Loc.T("紫外线", "UV index"), Num(s.UvIndex, ""), src("uv"));
        Metric(Loc.T("降水", "Precipitation"), Num(s.PrecipitationMm, " mm", 1) + (s.PrecipType.Length > 0 && s.PrecipType != "none" ? (s.PrecipType == "rain" ? Loc.T(" 雨", " rain") : s.PrecipType == "snow" ? Loc.T(" 雪", " snow") : "") : ""), src("precip"));
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

        // 未来 24 小时：每格显示时间、图标、温度、降水概率与风速。
        body.Children.Add(SectionLabel(Loc.T("未来 24 小时", "Next 24 hours")));
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

        // 24 天表格：日期、星期、图标、天气、高低温、降水、风、UV、日出日落。
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
                Cell(6, Loc.T("降水", "Rain"), align: TextAlignment.Center); Cell(7, Loc.T("风", "Wind")); Cell(8, Loc.T("UV", "UV"), align: TextAlignment.Center); Cell(9, Loc.T("日出 / 日落", "Sunrise / sunset"));
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
                // 行悬停展示这一天的全部字段：表体只放日期/天气/高低温/降水/风/UV/日出日落，其余进 tooltip。
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
                if (moonPhase.Length > 0) moonLine += $" · {moonPhase}";
                dayTip.Add(moonLine);
                // 晨昏蒙影与太阳正午/午夜（和风独有，有值才显示）。
                string astroLine = $"{Loc.T("天光 ", "Astro ")}{T(s.AstronomicalDawnTimes, index)}–{T(s.AstronomicalDuskTimes, index)} · {Loc.T("航海 ", "Nautical ")}{T(s.NauticalDawnTimes, index)}–{T(s.NauticalDuskTimes, index)} · {Loc.T("民用 ", "Civil ")}{T(s.CivilDawnTimes, index)}–{T(s.CivilDuskTimes, index)}";
                if (T(s.AstronomicalDawnTimes, index).Length > 0) dayTip.Add(astroLine);
                string sunLine = $"{Loc.T("日出 ", "Sunrise ")}{Clock(s.SunriseTimes, index)} · {Loc.T("日落 ", "Sunset ")}{Clock(s.SunsetTimes, index)} · {Loc.T("太阳正午 ", "Noon ")}{T(s.SolarNoonTimes, index)} · {Loc.T("太阳午夜 ", "Midnight ")}{T(s.SolarMidnightTimes, index)}";
                if (T(s.SolarNoonTimes, index).Length > 0) dayTip.Add(sunLine);
                var dayTipText = string.Join("\n", dayTip.Where(line => line.Length > 0 && line.Trim().Length > 0));
                if (dayTipText.Length > 0) row.ToolTip = dayTipText;
                row.Background = isToday ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) : Brushes.Transparent;
            }
            table.Children.Add(row);
        }

        // 扩展区：分钟降水 / 预警 / 指数 / 天文月相。台风来袭由预警覆盖，不再单列台风/辐照/潮汐。
        ShowExtraSections(body, s);

        // 数据来源图例：列出所有声明支持的来源色点，提供数据的是专属色，未提供/未启用的一律灰色圆点，不再逐域说明谁不提供。
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 16, 0, 8) };
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(dim), VerticalAlignment = VerticalAlignment.Center });
            List<string> ids;
            if (fieldPrefixes.Length > 0)
                ids = s.FieldSources.Where(kv => kv.Value.Length > 0 && fieldPrefixes.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal))).Select(kv => kv.Value).Distinct().ToList();
            else if (s.DomainSources.TryGetValue(domain, out var declared))
                ids = declared.Where(id => s.SourceProvided.TryGetValue(id, out var ok) && ok).ToList();
            else ids = [];
            foreach (var id in ids)
            {
                var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = new SolidColorBrush(SourceColor(id)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                System.Windows.Controls.ToolTipService.SetToolTip(dot, Loc.T($"来自 {SourceName(id)}", $"From {SourceName(id)}"));
                row.Children.Add(dot);
            }
            body.Children.Add(row);
        }

        // 1) 分钟降水：未来数小时的逐点降水条，按降水量映射高度；条色跟该域来源走，不再用固定蓝。
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
        // 标题颜色跟该域实际贡献的来源走（指数目前只有和风提供 → 和风紫），不再用固定蓝，
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

        // 4) 天文：今天的全部天文时刻——月相月升月落、日出日落、三级晨昏蒙影、太阳正午/午夜、月过中天/月下点。
        int today = WeatherData.TodayIndex(s);
        string moonrise = s.MoonriseTimes.Length > today && s.MoonriseTimes[today].Length >= 16 ? s.MoonriseTimes[today][11..16] : "--:--";
        string moonset = s.MoonsetTimes.Length > today && s.MoonsetTimes[today].Length >= 16 ? s.MoonsetTimes[today][11..16] : "--:--";
        string phase = s.DayMoonPhase.Length > today ? s.DayMoonPhase[today] : "";
        // 取当天 ISO 时刻的 HH:mm；数组缺省返回空串，展示时跳过。
        string HHmm(string[] arr) => arr.Length > today && arr[today].Length >= 16 ? arr[today][11..16] : "";
        string sunrise = HHmm(s.SunriseTimes), sunset = HHmm(s.SunsetTimes);
        bool hasAstro = moonrise != "--:--" || moonset != "--:--" || phase.Length > 0 || sunrise.Length > 0;
        if (hasAstro)
        {
            // 天文混在 Daily 域里，但晨昏蒙影/月过中天等扩展时刻只有和风提供，按实际字段归源。
            Header(Loc.T("天文", "Astronomy"), "Daily", "d_sun:", "d_astro:", "d_moon:");
            // 月相组：升落/相位/过中天/下点。
            var moonParts = new List<string> { $"{Loc.T("月升", "Moonrise")} {moonrise} · {Loc.T("月落", "Moonset")} {moonset}" };
            if (phase.Length > 0) moonParts.Add(phase);
            string transit = HHmm(s.MoonTransitTimes), underfoot = HHmm(s.MoonUnderfootTimes);
            if (transit.Length > 0) moonParts.Add($"{Loc.T("月过中天", "Transit")} {transit}");
            if (underfoot.Length > 0) moonParts.Add($"{Loc.T("月下点", "Underfoot")} {underfoot}");
            body.Children.Add(Pill(string.Join(" · ", moonParts), ink, 11));
            // 太阳组：日出日落 + 太阳正午/午夜（正午/午夜仅和风提供）。
            var sunParts = new List<string> { $"{Loc.T("日出", "Sunrise")} {sunrise} · {Loc.T("日落", "Sunset")} {sunset}" };
            string solarNoon = HHmm(s.SolarNoonTimes), solarMidnight = HHmm(s.SolarMidnightTimes);
            if (solarNoon.Length > 0) sunParts.Add($"{Loc.T("太阳正午", "Solar noon")} {solarNoon}");
            if (solarMidnight.Length > 0) sunParts.Add($"{Loc.T("太阳午夜", "Solar midnight")} {solarMidnight}");
            body.Children.Add(Pill(string.Join(" · ", sunParts), ink, 11));
            // 晨昏蒙影三级：天光（天文）→ 航海 → 民用，由暗到亮；仅和风提供。
            string civilDawn = HHmm(s.CivilDawnTimes), civilDusk = HHmm(s.CivilDuskTimes);
            string nauticalDawn = HHmm(s.NauticalDawnTimes), nauticalDusk = HHmm(s.NauticalDuskTimes);
            string astroDawn = HHmm(s.AstronomicalDawnTimes), astroDusk = HHmm(s.AstronomicalDuskTimes);
            if (astroDawn.Length > 0 || nauticalDawn.Length > 0 || civilDawn.Length > 0)
                body.Children.Add(Pill($"{Loc.T("天光", "Astro")} {astroDawn}–{astroDusk} · {Loc.T("航海", "Nautical")} {nauticalDawn}–{nauticalDusk} · {Loc.T("民用", "Civil")} {civilDawn}–{civilDusk}", dim, 10.5));
        }
    }

    private static TextBlock SectionLabel(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), Margin = new Thickness(2, 16, 0, 8) };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(143, 166, 192)), Margin = new Thickness(2, 0, 0, 6) };
    // 来源主题色：每家在详情页读数卡与图例中使用专属颜色，一眼看出该字段数据来自哪个来源。
    private static Color SourceColor(string id) => id switch
    {
        "openmeteo" => Color.FromRgb(102, 194, 255),   // 天蓝
        "tencent" => Color.FromRgb(87, 199, 133),      // 腾讯绿
        "weathercomcn" => Color.FromRgb(255, 163, 72), // 中国天气网橙
        "hefeng" => Color.FromRgb(188, 139, 255),      // 和风紫
        _ => Color.FromRgb(158, 178, 200)
    };
    private static string SourceName(string id) => id switch
    {
        "openmeteo" => "Open-Meteo", "tencent" => Loc.T("腾讯天气", "Tencent"), "weathercomcn" => Loc.T("中国天气网", "China Weather"),
        "hefeng" => Loc.T("和风天气", "QWeather"), _ => id
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
        forecast.Children.Clear(); lowRow.Children.Clear(); tempLabels.Clear();
        nowRule.Visibility = Visibility.Collapsed; nowText.Visibility = Visibility.Collapsed;   // 「现在」分界线只属于逐时
        if (_hourlyMode) { DrawHourlyForecast(s); return; }
        DrawDailyForecast(s);
    }
    // 逐时与逐日共用同一套列骨架：5 行固定高度（星期 / 日期 / 图标 / 降水 / 温度），合计 98px。
    // 两种模式行数与行高完全一致，切换只换文字不换几何：图标、降水、温度三行像素级对齐，
    // days 整块高度因此恒定，既不会切换时上下跳，也不会让 Fit() 按不同高度误判档位。
    // 逐时把「星期 / 日期」两行留空——不是缺内容，是占位对齐（时刻统一在下方时刻行表达）。
    private static readonly double[] ColumnRowHeights = { 20, 14, 30, 14, 20 };
    // 列内尺寸的唯一来源：逐时与逐日共用同一套骨架，就必须共用同一套字号与图标尺寸，
    // 否则切换模式时同样位置的图标/数字会大小跳变。任一处要改，只改这里。
    private const double ColGlyph = 28;    // 第 3 行天气图标（行高 30，留 1px 上下呼吸）
    private const double ColNote = 10;     // 第 2 行日期、第 4 行降水概率
    private const double ColValue = 15;    // 第 5 行主数值：逐日高温 / 逐时温度
    private const double ColFoot = 12;     // 底行：逐日低温 / 逐时时刻
    private static Grid Column()
    {
        var grid = new Grid();
        foreach (var h in ColumnRowHeights) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        return grid;
    }
    private static void CellAt(Grid grid, UIElement child, int row)
    {
        Grid.SetRow(child, row); grid.Children.Add(child);
    }
    // 逐时模式：列里放「风向+风速 / 天气现象 / 图标 / 降雨 / 温度」，时刻统一挪到最底部那一行，与折线共用同一个列几何。
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
            var column = Column();
            // 与逐日同一套 5 行骨架，但前两行不空着——它们和逐日的「星期 / 日期」严格同高，填的都是逐时独有的信息。
            // 第 1 行：风向箭头 + 风速。气象学的风向是「来向」，箭头加 180° 指向风吹去的方向，看上去就是「风往这边吹」；
            // 逐日只能给一个聚合风向，小时级的风向变化正是逐时独有的信息。缺风向时只留风速，两个都缺就留空。
            double deg = p < s.HourWindDirDeg.Length ? s.HourWindDirDeg[p] : double.NaN;
            double wind = p < s.HourWindKmh.Length ? s.HourWindKmh[p] : double.NaN;
            bool arrow = !double.IsNaN(deg), speed = !double.IsNaN(wind);
            if (arrow || speed)
            {
                var mark = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (arrow) mark.Children.Add(WindMark(deg));
                if (speed) mark.Children.Add(new TextBlock { Text = $"{wind:0}", FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 205, 235)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(arrow ? 2 : 0, 0, 0, 0) });
                CellAt(column, mark, 0);
            }
            // 第 2 行：天气现象。61/63/65 这几档在图标上几乎长得一样，文字补的正是图标说不出的那一级。
            CellAt(column, new TextBlock { Text = HourCondition(code), FontSize = ColNote, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 232)) }, 1);
            CellAt(column, new WeatherGlyph(code, day) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 2);
            string rain = s.HourRainProb.Length > p && s.HourRainProb[p] >= 0 ? $"{s.HourRainProb[p]}%" : "–";
            CellAt(column, new TextBlock { Text = rain, Foreground = new SolidColorBrush(Color.FromRgb(99, 179, 244)), FontSize = ColNote, TextAlignment = TextAlignment.Center }, 3);
            CellAt(column, new TextBlock { Text = HourValue(s, p), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 4);
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
            int i = order[p]; var date = DateTime.Parse(s.DayDates[i]); var column = Column();
            string label = i == today ? Loc.T("今天", "Today") : i == today + 1 ? Loc.T("明天", "Tomorrow") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]);
            bool isToday = i == today;
            CellAt(column, new TextBlock { Text = label, FontSize = ColValue, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Foreground = isToday ? Brushes.White : new SolidColorBrush(Color.FromRgb(205, 224, 247)) }, 0);
            CellAt(column, new TextBlock { Text = date.ToString("MM/dd"), Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)), FontSize = ColNote, TextAlignment = TextAlignment.Center }, 1);
            CellAt(column, new WeatherGlyph(s.DayCodes[i], true) { Width = ColGlyph, Height = ColGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 2);
            CellAt(column, new TextBlock { Text = s.DayRainProb[i] >= 0 ? $"{s.DayRainProb[i]}%" : "–", Foreground = new SolidColorBrush(Color.FromRgb(99, 179, 244)), FontSize = ColNote, TextAlignment = TextAlignment.Center }, 3);
            // 高温在预报列第 5 行、低温在下方独立低温行，折线夹在两者之间；两个温度都登记进 tempLabels 供低档位统一收起。
            var high = new TextBlock { Text = Degree(s.DayHighC[i]), FontSize = ColValue, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(high); CellAt(column, high, 4);
            // 今天列的选择态高亮由垫底的 todayGlow 贯穿整列承担，列自身保持透明。
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            var low = new TextBlock { Text = Degree(s.DayLowC[i]), Foreground = new SolidColorBrush(Color.FromRgb(182, 201, 226)), FontSize = ColFoot, TextAlignment = TextAlignment.Center, Width = 64, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tempLabels.Add(low); lowRow.Children.Add(low);
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
            case Lens.Sun when s.SunriseTimes.Length > 0: DrawSunBand(s); return;
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
        foreach (var (series, brush) in new[] { (high, (Brush)Brushes.Goldenrod), (low, (Brush)Brushes.DodgerBlue) })
        {
            var line = new System.Windows.Shapes.Polyline { Stroke = brush, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            for (int p = 0; p < n; p++) { int i = order[p]; if (double.IsNaN(series[i])) continue; double x = X(p), yy = 48 - (series[i] - min) / range * 33; line.Points.Add(new Point(x, yy)); }
            chart.Children.Add(line);
            for (int p = 0; p < n; p++)
            {
                int i = order[p]; if (double.IsNaN(series[i])) continue; double x = X(p), yy = 48 - (series[i] - min) / range * 33;
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush }; Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, yy - 3); chart.Children.Add(dot);
            }
        }
    }
    // 当日 24 小时逐时温度曲线：缩放几何同每日折线（48-33 高度带），横轴按逐时列数均匀分列，
    // 与上段预报网格同为 forecast.Columns 列，每个采样点对准对应时刻列的正中心；画布内不再打时间，
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
        var line = new System.Windows.Shapes.Polyline { Stroke = brush, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        for (int p = 0; p < n; p++) if (!double.IsNaN(s.HourTempsC[p])) line.Points.Add(new Point(X(p), 48 - (s.HourTempsC[p] - min) / range * 33));
        chart.Children.Add(line);
        for (int p = 0; p < n; p++)
        {
            if (double.IsNaN(s.HourTempsC[p])) continue;
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush }; Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, 48 - (s.HourTempsC[p] - min) / range * 33 - 2.5); chart.Children.Add(dot);
            // 每个点都标温度，不再隔点抽样：列宽最小约 48px，单个「7°」只占 12–18px，相邻两点间隔 48px，不会压字。
            var probe = new TextBlock { Text = $"{s.HourTempsC[p]:0}°", FontSize = 9, Foreground = brush };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(probe, X(p) - probe.DesiredSize.Width / 2); Canvas.SetTop(probe, 48 - (s.HourTempsC[p] - min) / range * 33 - 14); chart.Children.Add(probe);
        }
    }
    // 通用逐时折线：与 DrawHourly 同一套几何（48-33 高度带、列中心取点），供体感镜头复用。
    // 与气温曲线一致，每个点都标数值（体感只在逐时里有逐点数据）。
    private void DrawHourlyLine(double[] data, Brush brush, bool degree)
    {
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length < 2) return;
        double min = valid.Min(i => data[i]), range = Math.Max(1, valid.Max(i => data[i]) - min);
        var line = new System.Windows.Shapes.Polyline { Stroke = brush, StrokeThickness = 2 };
        foreach (var p in valid) line.Points.Add(new Point(X(p), 48 - (data[p] - min) / range * 33));
        chart.Children.Add(line);
        foreach (var p in valid)
        {
            double yy = 48 - (data[p] - min) / range * 33;
            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush };
            Canvas.SetLeft(dot, X(p) - 2.5); Canvas.SetTop(dot, yy - 2.5); chart.Children.Add(dot);
            var label = new TextBlock { Text = degree ? $"{data[p]:0}°" : $"{data[p]:0}", FontSize = 9, Foreground = brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, yy - 14); chart.Children.Add(label);
        }
    }
    // 逐时柱状：湿度（0–100 固定量程）与风速（按最大值自适应）共用，柱底压在 50 这条基线上。
    private void DrawBars(double[] data, double max, string suffix)
    {
        int n = Math.Max(1, forecast.Columns); if (n < 2) return;
        double w = Math.Max(30, chart.Width), col = Math.Max(2, w / n);
        double X(int p) => (p + 0.5) * col;
        var valid = Enumerable.Range(0, Math.Min(n, data.Length)).Where(i => !double.IsNaN(data[i])).ToArray();
        if (valid.Length == 0) return;
        if (double.IsNaN(max) || max <= 0) max = Math.Max(1, valid.Max(i => data[i]));
        var brush = new SolidColorBrush(Color.FromRgb(112, 178, 255));
        foreach (var p in valid)
        {
            double h = Math.Max(2, data[p] / max * 40);
            var bar = new System.Windows.Shapes.Rectangle { Width = Math.Max(3, col * 0.42), Height = h, Fill = brush, RadiusX = 2, RadiusY = 2, Opacity = 0.85 };
            Canvas.SetLeft(bar, X(p) - bar.Width / 2); Canvas.SetTop(bar, 50 - h); chart.Children.Add(bar);
            if (p % 4 != 0) continue;
            var label = new TextBlock { Text = $"{data[p]:0}{suffix}", FontSize = 9, Foreground = brush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, X(p) - label.DesiredSize.Width / 2); Canvas.SetTop(label, 50 - h - 13); chart.Children.Add(label);
        }
    }
    // 日照镜头：一条 24 小时轨道，金色段是白昼，白点是此刻位置；比原来的整屏弧线更省空间，也不打断其它读数。
    private void DrawSunBand(WeatherSnapshot s)
    {
        int today = WeatherData.TodayIndex(s);
        double w = Math.Max(30, chart.Width);
        static double Fraction(string[] times, int i)
        {
            if (i < 0 || i >= times.Length || times[i].Length < 16) return double.NaN;
            return (int.Parse(times[i][11..13]) * 60 + int.Parse(times[i][14..16])) / 1440.0;
        }
        double a = Fraction(s.SunriseTimes, today), b = Fraction(s.SunsetTimes, today);
        if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return;
        var track = new System.Windows.Shapes.Rectangle { Width = w, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)) };
        Canvas.SetTop(track, 30); chart.Children.Add(track);
        var lit = new System.Windows.Shapes.Rectangle { Width = w * (b - a), Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Color.FromRgb(255, 201, 77)) };
        Canvas.SetLeft(lit, w * a); Canvas.SetTop(lit, 30); chart.Children.Add(lit);
        for (int hh = 0; hh <= 24; hh += 6)
        {
            var tick = new TextBlock { Text = $"{hh:00}", FontSize = 8.5, Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)) };
            tick.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tick, Math.Max(0, Math.Min(w - tick.DesiredSize.Width, w * hh / 24.0 - tick.DesiredSize.Width / 2))); Canvas.SetTop(tick, 41); chart.Children.Add(tick);
        }
        double now = (DateTime.Now.Hour * 60 + DateTime.Now.Minute) / 1440.0;
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
        Canvas.SetLeft(dot, w * now - 3.5); Canvas.SetTop(dot, 29); chart.Children.Add(dot);
        var cap = new TextBlock { Text = $"日出 {Clock(s.SunriseTimes, today)} · 日落 {Clock(s.SunsetTimes, today)}", FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(230, 240, 251)) };
        Canvas.SetLeft(cap, 2); Canvas.SetTop(cap, 6); chart.Children.Add(cap);
    }
    // 逐时列底部那行跟着镜头走：气温/体感出温度，湿度出百分比，风速出 km/h，日照仍用气温（白昼由轨道表达）。
    private string HourValue(WeatherSnapshot s, int p)
    {
        string pick(double[] a) => p >= 0 && p < a.Length ? Degree(a[p]) : "--";
        return _lens switch
        {
            Lens.Feels => pick(s.HourFeelsLikeC),
            Lens.Humidity => p < s.HourHumidity.Length && s.HourHumidity[p] >= 0 ? $"{s.HourHumidity[p]}%" : "–",
            Lens.Wind => p < s.HourWindKmh.Length && !double.IsNaN(s.HourWindKmh[p]) ? $"{s.HourWindKmh[p]:0}" : "–",
            _ => pick(s.HourTempsC)
        };
    }
    // 风向箭头：几何按「指向上」画，再旋到风吹去的方向（气象风向是来向，所以 +180°）。
    private static UIElement WindMark(double deg) => new Path
    {
        Data = Geometry.Parse("M0,-4.6 L3.6,3.4 L0,1.4 L-3.6,3.4 Z"),
        Fill = new SolidColorBrush(Color.FromRgb(160, 205, 235)),
        Width = 7, Height = 7, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        RenderTransformOrigin = new Point(.5, .5),
        RenderTransform = new RotateTransform(deg + 180)
    };
    // 码值已由 WeatherCodes 归一到同一套区间，这里只翻成最短的一档文字：列宽最小约 44px，
    // 中文最多三个字，英文最多六个字母，超出就会被 TextBlock 直接裁掉。
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

    // 城市名展示：新版城市存的是结构化层级（Name 是区县/城市主体，Full 是「区县 · 市 · 省」完整名）。
    // 工具栏按钮只放得下短名；下拉菜单用 Full，同名区县要靠省市区分。
    internal static string DisplayName(string name)
    {
        var parts = name.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return name;
        if (parts.Length >= 3) parts = parts[..^1];   // 三段以上，最后一段一定是国家
        return parts.Length > 1 && parts[0] == parts[1] ? parts[0] : string.Join(Loc.T("·", " · "), parts);
    }
    // 只要最前面那一段城市名：下拉的自动定位行写「自动定位·武汉」，后面不再跟省份与国家。
    private static string ShortName(string name)
    {
        int split = name.IndexOf(" · ", StringComparison.Ordinal);
        return split > 0 ? name[..split] : name;
    }
    private bool AutoSelected => state.Selected < 0 || state.Selected >= state.Cities.Count;
    // 定位失败时快照名是「当前位置」这个占位（数据层写死的中文串，不随界面语言变），那不是城市名，按钮上不重复写。
    private string AutoCityName => _autoCityName == "当前位置" ? "" : _autoCityName;
    // 按钮常态显示「城市·省份」（武汉·湖北 / 伦敦·安大略）：自动定位前缀只在菜单里，国家不显示。
    // 工具栏是横条，再多一段就挤掉别的按钮。定位结果还没回来时退回「自动定位」。
    internal string CityLabel =>
        DisplayName(AutoSelected ? AutoCityName : state.Cities[state.Selected].Name) is { Length: > 0 } name
            ? name : Loc.T("自动定位", "Auto location");
    // 下拉第一行要和其它行区分开：写清这是自动定位，后面只跟城市名，不跟省份与国家。
    internal string AutoMenuLabel => AutoCityName.Length > 0
        ? Loc.T($"自动定位·{ShortName(AutoCityName)}", $"Auto · {ShortName(AutoCityName)}")
        : Loc.T("自动定位", "Auto location");
    internal List<CityChoice> CityChoices()
    {
        var choices = new List<CityChoice> { new(AutoMenuLabel, AutoSelected, -1) };
        for (int i = 0; i < state.Cities.Count; i++) choices.Add(new CityChoice(state.Cities[i].Full, !AutoSelected && state.Selected == i, i));
        return choices;
    }
    internal async Task ChooseCityAsync(int index)
    {
        if (state.Selected == index) return;
        state.Selected = index; WeatherData.Save(state);
        await Refresh();
    }
    internal Action? SettingsRequested;
}