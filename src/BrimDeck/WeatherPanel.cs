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
    private string _weatherSpin = "";
    private PromptTimer? _weatherRingTimer;
    private async void WeatherRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_weatherView is null) return;
        // While a refresh is under way a second click stops it; otherwise it starts one.
        if (_weatherView.IsFetching) { _weatherView.Cancel(); return; }
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
            _weatherView = new WeatherView { SettingsRequested = () => _app.OpenSettingsPage(SettingsWindow.WeatherSettingsPage) };
            _weatherView.StateChanged += RenderWeatherRefreshButton;
            WeatherContent.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _weatherView });
        }
        _weatherView.Fit(ContentCanvas.Width, ContentCanvas.Height - 36);
        RenderWeatherRefreshButton();
    }

    // The weather button shares the sync button's look (ring, shrinking arrows, spin); only the click differs, as the weather
    // page always refreshes itself and a second click stops the refresh under way instead of toggling anything.
    private void RenderWeatherRefreshButton()
    {
        if (_weatherView is null || WeatherRefreshButton is null) return;
        bool page = IsWeatherPage;
        bool onScreen = _expanded && page && !IsSuppressed;
        bool fetching = _weatherView.IsFetching;
        WeatherRefreshButton.Visibility = Settings.WeatherPage && page ? Visibility.Visible : Visibility.Collapsed;
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
        string spin = !onScreen || !animations || !fetching ? "" : "details";
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
