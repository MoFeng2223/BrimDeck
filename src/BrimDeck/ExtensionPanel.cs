using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The extension pages' rendering: each panel keeps one view per occupied slot and stretches it with the page. The
// extension owns its view's content entirely; the panel only owns the chrome every page shares — the header, the
// margins, and which slot is showing.
public partial class MainWindow
{
    private readonly FrameworkElement?[] _extensionViews = new FrameworkElement?[4];
    private readonly string?[] _extensionKeys = new string?[4];

    // A web page restyles itself from the pushes the host sends on every theme, palette or language change, so a view
    // is built once per panel and kept: recreating it would throw away the page for no gain. The key stays a constant.
    private string ExtensionKey(int slot) => "web";

    // A scan replaced or retired some slots: their views and tabs are dropped, the tabs are rebuilt, and the panel
    // re-renders — the page it stood on resolves to another when its slot is gone, and the fresh extension learns the
    // demand of the page that survived.
    internal void OnExtensionsChanged(IReadOnlyList<int> slots)
    {
        foreach (var slot in slots)
        {
            if (_extensionViews[slot] is { } view) ExtensionContent.Children.Remove(view);
            _extensionViews[slot] = null;
            _extensionKeys[slot] = null;
            if (_extensionButtons[slot] is { } button) PageButtons.Children.Remove(button);
            _extensionButtons[slot] = null;
        }
        BuildExtensionButtons();
        RenderPageSwitch();
        UpdateMonitorDemand();
        if (_expanded) ApplySettings();
    }

    internal void RenderExtension()
    {
        int slot = ExtensionSlot;
        if (slot < 0 || !IslandExtensions.Occupied(slot)) return;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ExtensionContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the other pages do, so the switch does not move between pages.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        ExtensionContent.Margin = new Thickness(_header.Side, 0, _header.Side, height < 170 ? 8 : 10);
        var key = ExtensionKey(slot);
        // A cached view is only reused while its page is still there. Taking the view off the window (another tab, a
        // settings rebuild) disposes its WebView; the extension is asked before the old control goes back on screen.
        bool cachedAlive = _extensionViews[slot] is { } cached && IslandExtensions.Alive(slot, cached);
        if (_extensionViews[slot] is not { } view || _extensionKeys[slot] != key || !cachedAlive)
        {
            try { view = IslandExtensions.Slots[slot]!.CreateView(IslandExtensions.Colors); }
            catch (Exception ex)
            {
                view = new System.Windows.Controls.TextBlock
                {
                    Text = Loc.T("插件页面无法显示：", "The plugin page could not be shown: ") + ex.Message.Split('\n')[0],
                    Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
                };
            }
            _extensionViews[slot] = view;
            _extensionKeys[slot] = key;
            // The view is cached for the page it belongs to, but it must not outlive its WebView: when it leaves the
            // window the entry is dropped, and the next render builds a page instead of putting an empty control back.
            int builtSlot = slot;
            var builtView = view;
            view.Unloaded += (_, _) =>
            {
                if (!ReferenceEquals(_extensionViews[builtSlot], builtView)) return;
                ExtensionLog.Write("panel: view retired slot " + builtSlot);
                _extensionViews[builtSlot] = null;
                _extensionKeys[builtSlot] = null;
            };
            ExtensionLog.Write("panel: view built for slot " + slot);
            ExtensionContent.Children.Clear();
            ExtensionContent.Children.Add(view);
        }
        else if (!ExtensionContent.Children.Contains(view))
        {
            ExtensionLog.Write("panel: view reused for slot " + slot);
            ExtensionContent.Children.Clear();
            ExtensionContent.Children.Add(view);
        }
    }
}
