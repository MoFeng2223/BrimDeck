using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;
namespace BrimDeck;
public partial class MainWindow
{
    private WeatherView? _weatherView;
    private WeatherLive? _liveWeather;
    private System.Windows.Threading.DispatcherTimer? _weatherRelease;
    private DateTime? _weatherHiddenSince;

    internal void WeatherSelectionChanged()
    {
        var previous = _liveWeather;
        UpdateWeatherDemand();
        if (previous != _liveWeather && IsVisible && !_expanded) RenderCompact();
    }
    private void UpdateWeatherDemand()
    {
        WeatherLive.Selection? key = null;
        if (!_app.Exiting && !_preview && IsLoaded && !_expanded && CompactRings.IsVisible && _alert is null &&
            !IsSuppressed && Settings.ShowsWeather(EffectiveStyle))
        {
            var settings = _app.Settings;
            var city = settings.WeatherCities.ElementAtOrDefault(settings.WeatherSelected);
            var source = settings.Weather.SourceFor(city);
            key = new(city, source, source == "hefeng" ? settings.Weather.HeFengHost : "", source == "hefeng" ? HeFengMonitor.Generation : 0);
        }
        if (_liveWeather?.Key == key) return;
        _liveWeather?.Release(CompactWeatherChanged);
        var app = _app;
        _liveWeather = key is null ? null : WeatherLive.Acquire(key, () => app.Settings.Weather, CompactWeatherChanged);
    }

    private void WeatherVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _weatherHiddenSince = _weatherView?.IsVisible == false ? DateTime.UtcNow : null;
        ScheduleWeatherRelease();
    }

    internal void ScheduleWeatherRelease()
    {
        _weatherRelease?.Stop();
        if (_weatherHiddenSince is not { } hidden || _app.Exiting) return;
        _weatherRelease ??= new(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Dispatcher);
        _weatherRelease.Tick -= ReleaseWeatherTick;
        _weatherRelease.Tick += ReleaseWeatherTick;
        var remaining = hidden + _app.MemoryPlan.ReleaseAfter - DateTime.UtcNow;
        _weatherRelease.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _weatherRelease.Start();
    }

    private void ReleaseWeatherTick(object? sender, EventArgs args)
    {
        _weatherRelease?.Stop();
        if (_weatherView?.IsVisible == false && ReleaseWeatherView()) _app.WeatherReleased();
    }

    private bool ReleaseWeatherView()
    {
        _weatherRelease?.Stop();
        _weatherHiddenSince = null;
        if (_weatherView is null) return false;
        _weatherView.IsVisibleChanged -= WeatherVisibilityChanged;
        _weatherView.StateChanged -= RenderWeatherChrome;
        _weatherView.Cancel();
        WeatherContent.Children.Clear();
        _weatherView = null;
        _weatherRingTimer?.Stop();
        _weatherRingTimer = null;
        _weatherSpin = "";
        return true;
    }
    private void CompactWeatherChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!_expanded && IsVisible && Settings.ShowsWeather(EffectiveStyle)) RenderCompact();
    });

    private FrameworkElement? CompactWeather(double scale)
    {
        if (!Settings.ShowsWeather(EffectiveStyle)) return null;
        if (_liveWeather?.Latest is not { } snapshot) return null;
        var age = DateTimeOffset.UtcNow - snapshot.At;
        var badge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            ToolTip = $"{snapshot.Name} · {snapshot.At.ToLocalTime():MM-dd HH:mm}",
            Opacity = age > _liveWeather.Interval ? 0.65 : 1
        };
        string temperature = _liveWeather.Stale ? "—" : $"{snapshot.Temperature:0}°";
        if (!_liveWeather.Stale && snapshot.Code is { } code && code >= 0)
            badge.Children.Add(new WeatherGlyph(code, snapshot.Day) { Width = 16 * scale, Height = 16 * scale, Margin = new Thickness(0, 0, 3 * scale, 0) });
        badge.Children.Add(UI.Fixed(temperature, 13 * scale, 18 * scale, Look.Primary));
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        badge.Width = Math.Ceiling(badge.DesiredSize.Width);
        badge.Height = badge.DesiredSize.Height;
        return badge;
    }
    private string _weatherSpin = "";
    private PromptTimer? _weatherRingTimer;
    private async void WeatherRefresh_Click(object sender, RoutedEventArgs e)
    {
        // 停止还是开始刷新由 ManualRefresh 统一判断，视图层不再重复读 IsFetching。
        if (_weatherView is null) return;
        await _weatherView.ManualRefresh();
        RenderWeatherRefreshButton();
    }
    private void WeatherPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Weather);
    private void RenderWeather()
    {
        RefreshStatus();
        UsageContent.Visibility = MusicContent.Visibility = MonitorContent.Visibility = ShelfContent.Visibility = DevicesContent.Visibility = Visibility.Collapsed;
        WeatherContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = ApplicationPages.Visibility = Visibility.Collapsed;
        ClockLabel.Visibility = Visibility.Visible;
        ApplyHeader(PanelHeader.For(36, 20));
        if (_weatherView is null)
        {
            if (!_expanded && !_preview) return;
            _weatherView = new WeatherView(_app, Look) { SettingsRequested = () => _app.OpenSettingsPage(SettingsWindow.WeatherSettingsPage) };
            _weatherView.StateChanged += RenderWeatherChrome;
            _weatherView.IsVisibleChanged += WeatherVisibilityChanged;
            WeatherContent.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _weatherView });
        }
        FitWeather();
        RenderWeatherChrome();
    }

    // 拖动调尺寸时每帧只让面板内容跟上新尺寸：页头、刷新环、城市按钮、状态栏都与尺寸无关，
    // 留给 RenderWeather 按节流重建，否则一次拖宽要把它们连同自动化信息重算上百遍。
    internal void FitWeather() => _weatherView?.Fit(ContentCanvas.Width, ContentCanvas.Height - 36);

    // 城市选择与播放来源同一个控件：胶囊按钮 + 下拉浮层，第一个是自动定位（带定位到的城市），中间是已存城市，
    // 最后是「添加城市」——它跳到设置页，新增/删除城市仍只有设置页一个入口。
    private void WeatherCity_Click(object sender, RoutedEventArgs e)
    {
        if (_weatherView is null) return;
        if (ReferenceEquals(WeatherCityButton.ContextMenu, _toolbarMenu) && ToolbarMenuOpen) { CloseSourceMenu(); return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        menu.SetValue(SuppressMusicToolTipsProperty, true);
        foreach (var choice in _weatherView.CityChoices())
        {
            var label = new TextBlock
            {
                Text = choice.Label, FontSize = 12.5, Foreground = UI.Brush(Look.Primary),
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            // Tag "single" 让勾选列只画对勾、不变成图钉（图钉是播放来源专用的固定播放器功能）。
            var item = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = label, Tag = "single", IsCheckable = true, IsChecked = choice.Selected };
            AutomationProperties.SetName(item, choice.Label);
            int index = choice.Index;
            item.Click += (_, _) => { CloseSourceMenu(); _ = ChooseCity(index); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
        var addLabel = new TextBlock
        {
            Text = Loc.T("添加城市…", "Add a city…"), FontSize = 12.5, Foreground = UI.Brush(Look.Primary),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var add = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = addLabel, Tag = "action" };
        AutomationProperties.SetName(add, addLabel.Text);
        add.Click += (_, _) => { CloseSourceMenu(); _weatherView?.SettingsRequested?.Invoke(); };
        menu.Items.Add(add);
        ShowToolbarMenu(WeatherCityButton, menu);
    }
    private async Task ChooseCity(int index)
    {
        if (_weatherView is null) return;
        await _weatherView.ChooseCityAsync(index);
        RenderWeatherChrome();
    }
    // 页头天气控件只在天气页且已建视图时出现；两处按钮共用同一判据，避免条件漂移。
    private bool WeatherChromeVisible => Settings.WeatherPage && IsWeatherPage && _weatherView is not null;
    private void RenderWeatherChrome()
    {
        RenderWeatherRefreshButton();
        RenderWeatherCityButton();
    }
    // 按钮文字跟着选择与定位结果走；抓取中、定位到的城市还没回来时保持上一版文字，不闪成空。
    private void RenderWeatherCityButton()
    {
        if (WeatherCityButton is null) return;
        bool show = WeatherChromeVisible;
        WeatherCityButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        string label = _weatherView!.CityLabel;
        WeatherCityLabel.Text = label;
        var tip = Loc.T("切换城市", "Switch city");
        if (WeatherCityButton.ToolTip is System.Windows.Controls.ToolTip shown) shown.Content = tip; else WeatherCityButton.ToolTip = UI.LeftwardToolTip(tip);
        AutomationProperties.SetName(WeatherCityButton, label);
    }

    // The weather button shares the sync button's look (ring, shrinking arrows, spin); only the click differs, as the weather
    // page always refreshes itself and a second click stops the refresh under way instead of toggling anything.
    private void RenderWeatherRefreshButton()
    {
        if (WeatherRefreshButton is null || _weatherView is null) return;
        bool page = IsWeatherPage;
        bool onScreen = _expanded && page && !IsSuppressed;
        bool fetching = _weatherView.IsFetching;
        WeatherRefreshButton.Visibility = WeatherChromeVisible ? Visibility.Visible : Visibility.Collapsed;
        var tip = fetching ? Loc.T("正在刷新天气… 点击停止", "Refreshing weather… Click to stop") : Loc.T("刷新天气", "Refresh weather");
        if (WeatherRefreshButton.ToolTip is System.Windows.Controls.ToolTip shown) shown.Content = tip; else WeatherRefreshButton.ToolTip = UI.LeftwardToolTip(tip);
        AutomationProperties.SetName(WeatherRefreshButton, fetching ? Loc.T("停止刷新天气", "Stop refreshing weather") : Loc.T("刷新天气", "Refresh weather"));

        // The ring is about the automatic 15-minute cycle and is always on screen with the weather page: while a refresh
        // runs it reads as full, just as the sync button's ring does, and its arrow spins inside it.
        bool countdown = onScreen;
        WeatherRefreshTrack.Visibility = WeatherRefreshProgress.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
        double glyph = (countdown ? 13 : 18) / 24d;
        WeatherRefreshGlyph.Width = WeatherRefreshGlyph.Height = 24; WeatherRefreshGlyph.RenderTransformOrigin = new Point(.5, .5);
        if (WeatherRefreshGlyph.RenderTransform is ScaleTransform { IsFrozen: false } scale) scale.ScaleX = scale.ScaleY = glyph;
        else WeatherRefreshGlyph.RenderTransform = new ScaleTransform(glyph, glyph);
        WeatherRefreshArrows.StrokeThickness = countdown ? 1.65 * 18 / 13 : 1.65;
        UpdateWeatherRing();
        var ringTimer = _weatherRingTimer ??= new PromptTimer(Dispatcher, UpdateWeatherRing) { Interval = TimeSpan.FromSeconds(1) };
        if (countdown && onScreen) { if (!ringTimer.IsEnabled) ringTimer.Start(); }
        else ringTimer.Stop();

        // A refresh spins the arrows until it ends, whether asked for by hand or by the 15-minute cycle.
        bool animations = Animates;
        string spin = !onScreen || !animations || !fetching ? "" : "refreshing";
        if (_weatherSpin == spin) return;
        _weatherSpin = spin;
        WeatherRefreshRotation.BeginAnimation(RotateTransform.AngleProperty, spin.Length == 0 ? null : new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop
        });
    }

    private void UpdateWeatherRing()
    {
        if (_weatherView is null) return;
        if (!_expanded || !IsWeatherPage || IsSuppressed) { WeatherRefreshProgress.Data = null; return; }
        double fraction = _weatherView.IsFetching ? 1 : Math.Clamp((DateTime.UtcNow - _weatherView.CycleStart).TotalSeconds / WeatherView.AutoInterval.TotalSeconds, 0, 1);
        WeatherRefreshProgress.Data = RingArc(fraction);
    }
}
