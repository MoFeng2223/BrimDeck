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
        EnsureExtensionViews();
        ShowExtensionView(slot);
    }

    // A view is built once for the panel's life and never taken off the window: a page switch hides it (Visibility),
    // which keeps the page alive, so coming back is at once. What the page costs while nobody watches it is the host's
    // to reclaim — after the usual grace it navigates the page to blank, and the next visit simply loads it again.
    // Called when the panel expands, so every plugin page is ready before the first switch to it.
    internal void EnsureExtensionViews()
    {
        if (IslandExtensions.OccupiedCount == 0) return;
        for (int slot = 0; slot < IslandExtensions.Slots.Length; slot++)
        {
            if (!IslandExtensions.Occupied(slot)) continue;
            var key = ExtensionKey(slot);
            if (_extensionViews[slot] is { } kept && _extensionKeys[slot] == key && IslandExtensions.Alive(slot, kept)) continue;
            if (_extensionViews[slot] is { } old) ExtensionContent.Children.Remove(old);
            FrameworkElement view;
            try { view = IslandExtensions.Slots[slot]!.CreateView(IslandExtensions.Colors); }
            catch (Exception ex)
            {
                view = new System.Windows.Controls.TextBlock
                {
                    Text = Loc.T("插件页面无法显示：", "The plugin page could not be shown: ") + ex.Message.Split('\n')[0],
                    Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
                };
            }
            view.Visibility = Visibility.Hidden;
            _extensionViews[slot] = view;
            _extensionKeys[slot] = key;
            ExtensionContent.Children.Add(view);
            ExtensionLog.Write("panel: view built for slot " + slot);
        }
    }

    // The chosen slot's view is the one that shows; the others stay in the tree, hidden and costless to switch back to.
    private void ShowExtensionView(int slot)
    {
        for (int at = 0; at < _extensionViews.Length; at++)
            if (_extensionViews[at] is { } view)
                view.Visibility = at == slot ? Visibility.Visible : Visibility.Hidden;
    }
}
