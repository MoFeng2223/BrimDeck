using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

// The collapsed proxy status icon (CompactPart.Proxy). The watcher (App.Proxy) runs only while at least one panel
// shows it on its collapsed surface; when no supported proxy software is running, the icon hides automatically.
public partial class MainWindow
{
    private bool _proxyStripShowing;
    private ProxyStrip? _compactProxy;

    private void InitializeProxy()
    {
        _app.ProxyChanged += ProxyChanged;
        Closed += (_, _) => { _app.ProxyChanged -= ProxyChanged; _app.PresentProxy(this, false); };
    }

    // Called alongside UpdateMonitorDemand / UpdateDevicesDemand whenever visibility, expansion, alert or settings change.
    private void UpdateProxyDemand()
    {
        bool seen = IsVisible && !IsSuppressed && !DisplayPower.Off;
        bool strip = !_expanded && Settings.ShowsProxy(EffectiveStyle) && seen && _alert is null;
        // An indicator line that peeks into the capsule or notch keeps proxy status warm so it appears immediately.
        bool ahead = !_expanded && LineIsland is { } island && Settings.ShowsProxy(island) && EffectiveStyle == CompactStyle.Line && seen && _alert is null;
        _app.PresentProxy(this, strip || ahead);
        bool was = _proxyStripShowing;
        _proxyStripShowing = strip;
        if (strip && !was && !_inRenderCompact) UpdateCompactProxy();
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
        if (_expanded || !Settings.ShowsProxy(EffectiveStyle) || _alert is not null) return;
        UpdateCompactProxy();
    }

    // Built with the collapsed row; returns null while switched off or no proxy client is running so the icon disappears cleanly.
    private ProxyStrip? CompactProxy(double scale)
    {
        _compactProxy = null;
        if (!Settings.ShowsProxy(EffectiveStyle)) return null;
        var snapshot = _app.Proxy?.Snapshot ?? ProxySnapshot.Stopped;
        return snapshot.Running ? _compactProxy = new ProxyStrip(snapshot, Settings.ProxyIndicators, scale) : null;
    }
}
