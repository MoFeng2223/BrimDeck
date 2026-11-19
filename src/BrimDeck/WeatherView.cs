using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
    private readonly Canvas chart = new() { Height = 68, Width = 588 };
    // 每列纵向顺序：星期/日期/图标/降雨/高温 →【折线图】→ 低温。低温独立成行夹在折线下方，折线正好插在高温与低温之间。
    private readonly UniformGrid lowRow = new() { Rows = 1, Columns = 7 };
    private readonly List<TextBlock> tempLabels = new();   // 高/低温文本，供“隐藏高低温”档统一收起
    // 「今天」列选择态高亮：垫在 days 底部、跨三行（预报列上段/折线/低温），从列顶贯穿到最底部，折线与低温行都被它包住。
    // 显式左对齐 + 左边距定位，与预报网格列几何完全一致，避免 Stretch+Width 时被居中导致的横向偏移。
    private readonly Border todayGlow = new() { CornerRadius = new CornerRadius(15), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
    internal static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(15);
    private readonly DispatcherTimer timer = new() { Interval = AutoInterval };
    private WeatherSnapshot? _last;
    private int _columns = 7;
    private double _chartWidth;
    private int generation;
    // 视口即内容区，宽度与高度随 Fit 传入值整体填充。底部 days 纵向为 预报列上段(星期/日期/图标/降雨)→折线(高低温曲线)→低温，
    // 按「能否完整展示」从最高档向下逐级收纳：完整档(折线代表温度，隐藏高低温文本) → 藏折线(恢复高低温文本) → 藏高低温(仅基础列) → 连基础列都不够就整行隐藏、只留今日天气。
    internal void Fit(double width, double height)
    {
        Width = width; Height = height;
        double avail = Math.Max(0, height - 22);                      // 根 Grid 上边距 6 + 下边距 16
        summaryRow.MinHeight = Math.Min(summaryMinHeight, avail);     // 今日天气最小高度封顶，避免自身被压坏
        // 列数随宽度在 7（窄）到 24（满宽 440–1200 线性插值）之间自适应；宽度最小时保证 7 列，最大时保证 24 列。
        // 折线画布与预报网格同一套几何：左右边距同为 6、内容宽度同为 width-40，点坐标按列中心计算，保证每个点对准列正中。
        double daysW = Math.Max(0, width - 28);
        double chartWidth = Math.Max(30, width - 40);
        int columns = Math.Clamp(7 + (int)Math.Round((width - 440) / 760.0 * 17), 7, 24);
        bool redraw = columns != _columns || Math.Abs(chartWidth - _chartWidth) > 0.5;
        _columns = columns; _chartWidth = chartWidth; forecast.Columns = columns; lowRow.Columns = columns; chart.Width = chartWidth;
        if (redraw && _last is { } f) { DrawForecast(f); DrawChart(f); }
        double room = avail - summaryRow.MinHeight;                   // 实际可让给预报区的空间
        // 从完整档往下逐级收纳：每一步都按当前可见性实地测量需求量，绝不靠估算档位。先试完整档。
        SetForecastLevel(3);
        if (MeasureDays(daysW) <= room) return;
        SetForecastLevel(2);                                          // 装不下 → 藏折线
        if (MeasureDays(daysW) <= room) return;
        SetForecastLevel(1);                                          // 还装不下 → 藏高低温，保留星期/日期/图标/降雨
        days.Visibility = MeasureDays(daysW) <= room ? Visibility.Visible : Visibility.Collapsed;
    }
    // 3 完整档(基础列+折线)：折线本身画出高低温曲线，低温行与高/低温文本收起，不再由文本重复表达温度。2 无折线(基础列+高低温两列)：折线放不下时高低温由文本两列表达。
    // 1 无高低温(仅基础列)：保留星期/日期/图标/降雨，高低温与折线全部收起。1 档连基础列都装不下则由调用方整行折叠。
    private void SetForecastLevel(int level)
    {
        chart.Visibility = level >= 3 ? Visibility.Visible : Visibility.Collapsed;
        bool showTemp = level == 2;                                       // 折线缺席时才用文本表达高低温
        lowRow.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in tempLabels) p.Visibility = showTemp ? Visibility.Visible : Visibility.Collapsed;
        days.Visibility = level >= 1 ? Visibility.Visible : Visibility.Collapsed;
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
        var root = new Grid { Margin = new Thickness(14, 6, 14, 16) };
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
        // 今天列高亮垫底：跨三行(上段/折线/低温)，Forecast、折线、低温都画在它上面，选择态从列顶贯穿到列底。
        Grid.SetRow(todayGlow, 0); Grid.SetRowSpan(todayGlow, 3); days.Children.Add(todayGlow);
        forecast.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(forecast, 0); days.Children.Add(forecast);
        // 折线画布与预报网格左边距同为 6px 显式左对齐，画布原点与列起点完全重合，点坐标按列中心计算。
        chart.Margin = new Thickness(6, 8, 6, 0); chart.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetRow(chart, 1); days.Children.Add(chart);
        lowRow.Margin = new Thickness(6, 2, 6, 0); Grid.SetRow(lowRow, 2); days.Children.Add(lowRow);
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
        if (state.Cache.TryGetValue(key, out var cached)) try { Render(WeatherData.Decode(cached)); } catch { state.Cache.Remove(key); }
        fetching = true; RaiseState();
        try {
            City city;
            if (selected >= 0 && selected < state.Cities.Count) city = state.Cities[selected];
            else city = await WeatherData.LocateAsync(token) ?? new City("当前位置", 0, 0);
            var snapshot = await WeatherData.CurrentAsync(city, token);
            if (request != generation) return;
            if (token.IsCancellationRequested) return;
            Render(snapshot); state.Cache[key] = WeatherData.Encode(snapshot); WeatherData.Save(state); _cycleStart = DateTime.UtcNow;
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
            DrawSummary(s); DrawForecast(s); DrawChart(s);
        } catch { }
    }
    // 左上：大图标 + 大字温度 + 天气描述 + 高低温；右上：体感 / 湿度 / 风速 / 日照入口四张小卡。
    private void DrawSummary(WeatherSnapshot s)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        summary.RowDefinitions.Clear();
        int today = WeatherData.TodayIndex(s);    // 正序数组中“今天”的位置
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        hero.Children.Add(new WeatherGlyph(s.WeatherCode, s.IsDay) { Width = 56, Height = 56, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(new TextBlock { Text = $"{s.Temperature:0}°", FontSize = 44, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = $"{s.DayHighC[today]:0}° / {s.DayLowC[today]:0}°", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(205, 224, 247)), VerticalAlignment = VerticalAlignment.Center });
        void LocationGlyph(StackPanel row, Color ink) => row.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Location, ink) { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center,  Margin = new Thickness(0) });
        void ChevronGlyph(StackPanel row, Color ink) => row.Children.Add(new WeatherMetaIcon(WeatherMetaKind.ChevronDown, ink) { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center,  Margin = new Thickness(4, 0, 0, 0) });
        var placeRow = new StackPanel { Orientation = Orientation.Horizontal };
        LocationGlyph(placeRow, Colors.LightGray); placeRow.Children.Add(new TextBlock { Text = s.Name == "当前位置" ? Loc.T("当前位置", "Current location") : s.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }); ChevronGlyph(placeRow, Colors.LightGray);
        var location = new Button { Content = placeRow, Background = Brushes.Transparent, Foreground = Brushes.LightGray, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0, 4, 0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center }; WeatherMotion.Attach(location); location.Click += (_, e) => { e.Handled = true; ShowCityPicker(); }; bottom.Children.Add(location);
        side.Children.Add(bottom);hero.Children.Add(side);
        summary.Children.Add(hero);
        stats.Children.Clear(); stats.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(stats, 1); summary.Children.Add(stats);
         var tiles = new[] { (WeatherMetaKind.Thermo, Color.FromRgb(255, 176, 102), Loc.T("体感", "Feels like"), $"{s.FeelsLike}°"), (WeatherMetaKind.Droplet, Color.FromRgb(112, 178, 255), Loc.T("湿度", "Humidity"), $"{s.HumidityPercent}%"), (WeatherMetaKind.Wind, Color.FromRgb(140, 200, 220), Loc.T("风速", "Wind"), $"{s.WindKmh} km/h"), (WeatherMetaKind.Sunrise, Color.FromRgb(255, 200, 90), Loc.T($"日出 {s.SunriseTimes[today][11..16]}", $"Sunrise {s.SunriseTimes[today][11..16]}"), Loc.T($"日落 {s.SunsetTimes[today][11..16]}", $"Sunset {s.SunsetTimes[today][11..16]}")) };
        foreach (var (kind, ink, label, value) in tiles)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(205, 224, 247)), Margin = new Thickness(0, 0, 0, 3) });
            text.Children.Add(new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            body.Children.Add(new WeatherMetaIcon(kind, ink) { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            body.Children.Add(text);
            var tile = new Border { Background = new SolidColorBrush(Color.FromRgb(48, 48, 50)), CornerRadius = new CornerRadius(14), Margin = new Thickness(3), Padding = new Thickness(10, 4, 8, 4), Child = body };
            if (kind == WeatherMetaKind.Sunrise) { tile.Background = new SolidColorBrush(Color.FromRgb(48, 39, 16)); tile.Cursor = System.Windows.Input.Cursors.Hand; tile.ToolTip = Loc.T("查看日照时段", "View daylight hours"); WeatherMotion.Attach(tile); string rise = s.SunriseTimes[today][11..16], set = s.SunsetTimes[today][11..16]; tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowSun(rise, set); }; }
            stats.Children.Add(tile);
        }
    }
    // 右下角城市切换浮层：自动定位 + 已存城市 + 管理入口，入场用全应用统一的软曲线上坠，行项仅以背景层次反馈。
    private void ShowCityPicker()
    {
        var overlay = new Grid { Background = Brushes.Transparent }; host.Children.Add(overlay);
        var list = new StackPanel(); var box = new Border { Width = 300, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)), BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 66)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Child = list }; overlay.Children.Add(box);
        overlay.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == overlay) host.Children.Remove(overlay); };
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { host.Children.Remove(overlay); e.Handled = true; } }; overlay.Focusable = true; overlay.Focus();
        for (int i = -1; i < state.Cities.Count; i++)
        {
            int index = i; string key = i < 0 ? "auto" : state.Cities[i].ToString(); string temp = "";
            if (state.Cache.TryGetValue(key, out var json)) try { temp = $"   {WeatherData.Decode(json).Temperature:0}°"; } catch { }
            var rowLabel = new StackPanel { Orientation = Orientation.Horizontal };
            rowLabel.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Check, Colors.SkyBlue) { Width = 13, Height = 13,  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0), Visibility = i == state.Selected ? Visibility.Visible : Visibility.Hidden });
            rowLabel.Children.Add(new TextBlock { Text = i < 0 ? Loc.T("自动定位", "Auto location") : state.Cities[i].Name, VerticalAlignment = VerticalAlignment.Center });
            if (temp.Length > 0) rowLabel.Children.Add(new TextBlock { Text = temp, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)) });
            var row = new Button { Content = rowLabel, Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Foreground = i == state.Selected ? Brushes.SkyBlue : Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch };
            var rowBox = new Border { CornerRadius = new CornerRadius(8), Background = i == state.Selected ? new SolidColorBrush(Color.FromArgb(30, 135, 206, 235)) : Brushes.Transparent, Child = row }; row.MouseEnter += (_, _) => { if (i != state.Selected) rowBox.Background = new SolidColorBrush(Color.FromRgb(44, 44, 50)); }; row.MouseLeave += (_, _) => { rowBox.Background = i == state.Selected ? new SolidColorBrush(Color.FromArgb(30, 135, 206, 235)) : Brushes.Transparent; }; row.Click += async (_, _) => { host.Children.Remove(overlay); if (state.Selected == index) return; state.Selected = index; WeatherData.Save(state); await Refresh(); }; list.Children.Add(rowBox);
        }
        var manageLabel = new StackPanel { Orientation = Orientation.Horizontal };
        manageLabel.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Add, Colors.White) { Width = 13, Height = 13,  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        manageLabel.Children.Add(new TextBlock { Text = Loc.T("添加或管理城市", "Add or manage cities"), VerticalAlignment = VerticalAlignment.Center });
        var manage = new Button { Content = manageLabel, Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch };
        var manageBox = new Border { CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = manage }; manage.MouseEnter += (_, _) => manageBox.Background = new SolidColorBrush(Color.FromRgb(44, 44, 50)); manage.MouseLeave += (_, _) => manageBox.Background = Brushes.Transparent; manage.Click += async (_, _) => { host.Children.Remove(overlay); SettingsRequested?.Invoke(); await Task.CompletedTask; }; list.Children.Add(manageBox);
        Soft.Enter(box);
    }
    // 点击「日出 · 日落」切到日照视图：弧线轨道 + 太阳 + 返回天气。
    private void ShowSun(string rise, string set)
    {
        summary.Children.Clear(); summary.ColumnDefinitions.Clear();
        var canvas = new Canvas { Width = 590, Height = 112 }; summary.Children.Add(canvas);
        var arc = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 100,85 Q 290,-52 480,85"), Stroke = Brushes.Goldenrod, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 3 } }; canvas.Children.Add(arc);
        var sunIcon = new WeatherGlyph(0, true) { Width = 34, Height = 34, IsHitTestVisible = false }; Canvas.SetLeft(sunIcon, 272); Canvas.SetTop(sunIcon, 4); canvas.Children.Add(sunIcon);
        var text = new TextBlock { Text = Loc.T($"日出 {rise}                                    日落 {set}", $"Sunrise {rise}                                    Sunset {set}"), Foreground = Brushes.White, FontSize = 16 }; Canvas.SetLeft(text, 100); Canvas.SetTop(text, 84); canvas.Children.Add(text);
        var backLabel = new StackPanel { Orientation = Orientation.Horizontal };
        backLabel.Children.Add(new WeatherMetaIcon(WeatherMetaKind.ChevronLeft, Colors.White) { Width = 12, Height = 12,  VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        backLabel.Children.Add(new TextBlock { Text = Loc.T("返回天气", "Back to weather"), VerticalAlignment = VerticalAlignment.Center });
        var back = new Button { Content = backLabel, Padding = new Thickness(8, 4, 8, 4), Cursor = System.Windows.Input.Cursors.Hand }; WeatherMotion.Attach(back); back.Click += async (_, _) => await Refresh(); Canvas.SetLeft(back, 6); Canvas.SetTop(back, 6); canvas.Children.Add(back);
        canvas.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }
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
            int i = order[p]; var date = DateTime.Parse(s.DayDates[i]); var column = new StackPanel();
            string label = i == today ? Loc.T("今天", "Today") : i == today + 1 ? Loc.T("明天", "Tomorrow") : Loc.T(new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)date.DayOfWeek], new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)date.DayOfWeek]);
            bool isToday = i == today;
            column.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Foreground = isToday ? Brushes.White : new SolidColorBrush(Color.FromRgb(205, 224, 247)) });
            column.Children.Add(new TextBlock { Text = date.ToString("MM/dd"), Foreground = new SolidColorBrush(Color.FromRgb(150, 168, 192)), FontSize = 10, TextAlignment = TextAlignment.Center });
            column.Children.Add(new WeatherGlyph(s.DayCodes[i], true) { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center });
            column.Children.Add(new TextBlock { Text = $"{s.DayRainProb[i]}%", Foreground = new SolidColorBrush(Color.FromRgb(99, 179, 244)), FontSize = 10, TextAlignment = TextAlignment.Center, Height = 14 });
            // 高温在预报列内，低温在下方独立低温行，折线图夹在两者之间；两个温度都登记进 tempLabels 供“藏高低温”档统一收起。
            var high = new TextBlock { Text = $"{s.DayHighC[i]:0}°", FontSize = 15, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
            tempLabels.Add(high); column.Children.Add(high);
            // 今天列的选择态高亮由垫底的 todayGlow 贯穿整列承担，列自身保持透明。
            forecast.Children.Add(new Border { CornerRadius = new CornerRadius(15), Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 2, 4, 2), Background = Brushes.Transparent, Child = column });
            var low = new TextBlock { Text = $"{s.DayLowC[i]:0}°", Foreground = new SolidColorBrush(Color.FromRgb(182, 201, 226)), FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
            tempLabels.Add(low); lowRow.Children.Add(low);
        }
    }
    // 与预报同序的高低温折线：金线高温、蓝线低温，每个采样点带圆点与温度数字注释，随预报宽度横向伸展。
    // 折线是整行一条连续曲线（不是按列各画一段）；点的 x 用与预报网格完全相同的列中心几何，
    // 因此每个点精确落在对应列的正中心，横跨整行时曲线自然连续。
    private void DrawChart(WeatherSnapshot s)
    {
        chart.Children.Clear();
        var high = s.DayHighC; var low = s.DayLowC;
        double min = low.Min(), range = Math.Max(1, high.Max() - min);
        var order = Order(s, forecast.Columns);
        int n = order.Length;
        double w = Math.Max(30, chart.Width), col = forecast.Columns > 0 ? Math.Max(2, w / forecast.Columns) : 0;   // 与预报网格同源：列宽按列数分，点数不足时右侧留空
        double X(int p) => (p + 0.5) * col;                              // 列中心：与 UniformGrid 每列几何一致
        foreach (var (series, brush, isHigh) in new[] { (high, (Brush)Brushes.Goldenrod, true), (low, (Brush)Brushes.DodgerBlue, false) })
        {
            var line = new System.Windows.Shapes.Polyline { Stroke = brush, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            for (int p = 0; p < n; p++) { int i = order[p]; double x = X(p), yy = 48 - (series[i] - min) / range * 33; line.Points.Add(new Point(x, yy)); }
            chart.Children.Add(line);
            for (int p = 0; p < n; p++)
            {
                int i = order[p]; double x = X(p), yy = 48 - (series[i] - min) / range * 33;
                var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = brush }; Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, yy - 3); chart.Children.Add(dot);
                var probe = new TextBlock { Text = $"{series[i]:0}°", FontSize = 9, Foreground = brush };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var label = new TextBlock { Text = probe.Text, FontSize = 9, Foreground = brush, TextAlignment = TextAlignment.Center };
                // 温度数字注释：高温标在点上方、低温标在点下方，紧贴圆点、不遮挡预报列中的任何文字。
                Canvas.SetLeft(label, x - probe.DesiredSize.Width / 2);
                Canvas.SetTop(label, isHigh ? yy - 15 : yy + 9); chart.Children.Add(label);
            }
        }
    }
    internal Action? SettingsRequested;
}