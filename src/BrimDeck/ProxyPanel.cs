using System.Windows;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed proxy status icon (CompactPart.Proxy) and expanded Proxy control page (DeckPage.Proxy).
// The read-only watcher runs while any panel shows the compact icon or open page; the live Mihomo controller
// (traffic stream + groups/nodes) runs ONLY while the expanded Proxy page is actually open and visible.
public partial class MainWindow
{
    private bool _proxyStripShowing, _proxyPageShowing;
    private ProxyStrip? _compactProxy;
    private ProxyView? _proxyView;
    private ProxyController? _proxyViewController;
    private string _proxyViewKey = "";
    private string? _selectedProxyGroup;

    private void InitializeProxy()
    {
        _app.ProxyChanged += ProxyChanged;
        _app.ProxyControlChanged += ProxyControlChanged;
        _app.ProxyTrafficChanged += ProxyTrafficChanged;
        Closed += (_, _) =>
        {
            _app.ProxyChanged -= ProxyChanged;
            _app.ProxyControlChanged -= ProxyControlChanged;
            _app.ProxyTrafficChanged -= ProxyTrafficChanged;
            _app.PresentProxy(this, false, false);
        };
    }

    // Called alongside UpdateMonitorDemand / UpdateDevicesDemand whenever visibility, expansion, alert or settings change.
    private void UpdateProxyDemand()
    {
        bool seen = IsVisible && !IsSuppressed && !DisplayPower.Off;
        bool page = _expanded && CurrentPage == DeckPage.Proxy && seen;
        bool strip = !_expanded && Settings.ShowsProxy(EffectiveStyle) && seen && _alert is null;
        // An indicator line that peeks into the capsule or notch keeps proxy status warm so it appears immediately.
        bool ahead = !_expanded && LineIsland is { } island && Settings.ShowsProxy(island) && EffectiveStyle == CompactStyle.Line && seen && _alert is null;
        _app.PresentProxy(this, page, strip || ahead);
        if (page && !_proxyPageShowing) RenderProxy(handover: false);
        bool wasStrip = _proxyStripShowing;
        _proxyPageShowing = page;
        _proxyStripShowing = strip;
        if (strip && !wasStrip && !_inRenderCompact) UpdateCompactProxy();
    }

    private void UpdateCompactProxy()
    {
        var snapshot = _app.Proxy?.Snapshot ?? ProxySnapshot.Stopped;
        bool shows = snapshot.Running && Settings.ShowsProxy(EffectiveStyle);
        if (_compactProxy is { } strip && shows && strip.Holds(Settings.ProxyIndicators))
            strip.SetSnapshot(snapshot);
        else if (shows || _compactProxy is not null)
            RenderCompact();
    }

    private void ProxyChanged()
    {
        if (!_expanded)
        {
            if (!Settings.ShowsProxy(EffectiveStyle) || _alert is not null) return;
            UpdateCompactProxy();
            return;
        }
        if (CurrentPage == DeckPage.Proxy && _proxyView is not null)
            _proxyView.UpdateState();
    }

    private void ProxyControlChanged()
    {
        if (!_expanded || CurrentPage != DeckPage.Proxy || _proxyView is null) return;
        _proxyView.UpdateState();
    }

    private void ProxyTrafficChanged()
    {
        if (!_expanded || CurrentPage != DeckPage.Proxy || _proxyView is null) return;
        _proxyView.UpdateTraffic();
    }

    // Built with the collapsed row; returns null while switched off or no proxy client is running so the icon disappears cleanly.
    private ProxyStrip? CompactProxy(double scale)
    {
        _compactProxy = null;
        if (!Settings.ShowsProxy(EffectiveStyle)) return null;
        var snapshot = _app.Proxy?.Snapshot ?? ProxySnapshot.Stopped;
        return snapshot.Running ? _compactProxy = new ProxyStrip(snapshot, Settings.ProxyIndicators, scale) : null;
    }

    private string ProxyViewKey() => string.Join("|", Loc.Language, Look.Generation, SystemParameters.HighContrast, Settings.UiFont);

    private void RenderProxy(bool handover = true)
    {
        bool shown = handover && ProxyContent.Visibility == Visibility.Visible && _proxyView is not null && ProxyContent.Children.Contains(_proxyView);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed;
        MusicContent.Visibility = Visibility.Collapsed;
        MonitorContent.Visibility = Visibility.Collapsed;
        ShelfContent.Visibility = Visibility.Collapsed;
        DevicesContent.Visibility = Visibility.Collapsed;
        ProxyContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed;
        ApplicationPages.Visibility = Visibility.Collapsed;
        ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        ApplyPageHeader(PanelHeader.ForPanel(height));
        ProxyContent.Margin = new Thickness(_header.ContentSide, 0, _header.ContentSide, MonitorBottom(height));

        var controller = _app.EnsureProxyController();
        if (_expanded && IsVisible && !IsSuppressed && !DisplayPower.Off)
            _app.PresentProxy(this, true, _proxyStripShowing);

        var key = ProxyViewKey();
        if (_proxyView is not null && ReferenceEquals(_proxyViewController, controller) && key == _proxyViewKey && ProxyContent.Children.Contains(_proxyView))
        {
            _proxyView.UpdateState();
            return;
        }

        _proxyViewKey = key;
        _proxyViewController = controller;
        var old = shown ? _proxyView : null;
        _proxyView = new ProxyView(controller, _selectedProxyGroup, group => _selectedProxyGroup = group);
        _proxyView.UpdateState();
        if (old is not null) MonitorCrossfade.Swap(ProxyContent, _proxyView, old, MonitorMotion);
        else { ProxyContent.Children.Clear(); ProxyContent.Children.Add(_proxyView); }
    }
}
