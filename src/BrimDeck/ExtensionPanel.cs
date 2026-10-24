using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The extension pages' rendering: each panel keeps one view per occupied slot, built once per palette and language and
// stretched by the page from then on. The extension owns its view's content entirely; the panel only owns the chrome
// every page shares — the header, the margins, and which slot is showing.
public partial class MainWindow
{
    private readonly FrameworkElement?[] _extensionViews = new FrameworkElement?[4];
    private readonly string?[] _extensionKeys = new string?[4];

    private string ExtensionKey(int slot) => string.Join("|", Settings.MonitorPalette, Loc.Language);

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
        if (_extensionViews[slot] is not { } view || _extensionKeys[slot] != key)
        {
            view = IslandExtensions.Slots[slot]!.CreateView(IslandExtensions.Colors);
            _extensionViews[slot] = view;
            _extensionKeys[slot] = key;
            ExtensionContent.Children.Clear();
            ExtensionContent.Children.Add(view);
        }
        else if (!ExtensionContent.Children.Contains(view))
        {
            ExtensionContent.Children.Clear();
            ExtensionContent.Children.Add(view);
        }
    }
}
