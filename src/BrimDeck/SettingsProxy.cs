using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private TextBlock? _proxyLiveStatus;
    private readonly HashSet<FrameworkElement> _openProxyPanels = [];
    private bool _proxyClosing;

    // The proxy status row in the collapsed content order list (SettingsMusic.StyleOptions):
    // toggle shows/hides it for this compact style; expanding reveals the System Proxy / TUN selector, unified icon legend and live status.
    private Border ProxyRow(CompactStyle style, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        return OptionsPartRow(style, CompactPart.Proxy, rows,
            Loc.T("代理状态", "Proxy status"),
            Loc.T("代理状态设置", "Proxy status settings"),
            S.ShowsProxy(style),
            on => Change(s => { if (notch) s.NotchProxy = on; else s.CapsuleProxy = on; }),
            ProxyOptions(style),
            new Thickness(16, 10, 16, 12));
    }

    private FrameworkElement ProxyOptions(CompactStyle compact)
    {
        var panel = new StackPanel();
        var legendLook = new PanelLook(null);
        legendLook.Use(new DeckSettings { PanelTheme = _palette.Dark ? PanelThemes.Dark : PanelThemes.Light });
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        void RebuildLegend()
        {
            legend.Children.Clear();
            var modeChoice = S.ProxyIndicators;
            if (modeChoice == ProxyIndicators.Both)
            {
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, true, false), modeChoice, legendLook, Loc.T("规则 · 系统代理开", "Rule · Sys proxy")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Global, true, false), modeChoice, legendLook, Loc.T("全局", "Global")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Direct, true, false), modeChoice, legendLook, Loc.T("直连", "Direct")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, false, true), modeChoice, legendLook, Loc.T("仅 TUN 开", "TUN only")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, true, true), modeChoice, legendLook, Loc.T("双开", "Both on")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, false, false), modeChoice, legendLook, Loc.T("均关闭", "Off"), last: true));
            }
            else if (modeChoice == ProxyIndicators.SystemProxy)
            {
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, true, false), modeChoice, legendLook, Loc.T("规则 · 已开启", "Rule · On")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Global, true, false), modeChoice, legendLook, Loc.T("全局 · 已开启", "Global · On")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Direct, true, false), modeChoice, legendLook, Loc.T("直连 · 已开启", "Direct · On")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, false, false), modeChoice, legendLook, Loc.T("系统代理关闭", "Sys proxy off"), last: true));
            }
            else
            {
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, false, true), modeChoice, legendLook, Loc.T("规则 · TUN 开", "Rule · TUN on")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Global, false, true), modeChoice, legendLook, Loc.T("全局 · TUN 开", "Global · TUN on")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Direct, false, true), modeChoice, legendLook, Loc.T("直连 · TUN 开", "Direct · TUN on")));
                legend.Children.Add(ProxyLegendItem(new ProxySnapshot(true, ProxyMode.Rule, false, false), modeChoice, legendLook, Loc.T("TUN 关闭", "TUN off"), last: true));
            }
        }

        var indicators = MusicChoice(
            Loc.T("状态图案", "Status indicators"),
            S.ProxyIndicators,
            [
                (ProxyIndicators.Both, Loc.T("系统代理与 TUN", "Both")),
                (ProxyIndicators.SystemProxy, Loc.T("仅系统代理", "System proxy")),
                (ProxyIndicators.Tun, Loc.T("仅 TUN 模式", "TUN only"))
            ],
            value =>
            {
                Change(s => s.ProxyIndicators = value);
                RebuildLegend();
                RefreshProxyLiveStatus();
            },
            help: Loc.T(
                "单个圆环内融合显示代理模式（分流=规则、地球=全局、箭头=直连）、外环亮灭与右下角翠绿盾牌徽章。代理软件未运行时自动隐藏。",
                "A single ring combines routing mode (branch=Rule, globe=Global, arrow=Direct), lit outer ring, and the lower-right green TUN shield badge. Automatically hides when no proxy client runs."),
            width: 372);
        foreach (var rb in FindVisualChildren<RadioButton>(indicators)) rb.GroupName = "ProxyIndicators_" + compact;
        panel.Children.Add(indicators);

        panel.Children.Add(new Border { Height = 1, Background = UI.Brush(Divider), Margin = new Thickness(0, 6, 0, 10) });

        var legendHost = new Border
        {
            Background = UI.Brush(legendLook.Hex("surface")),
            BorderBrush = UI.Brush(Divider),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            Child = legend
        };
        PanelLook.Set(legendHost, legendLook);
        RebuildLegend();
        panel.Children.Add(legendHost);

        var status = TextLine("", 11.5, TextSecondary);
        status.Margin = new Thickness(0, 9, 0, 0);
        status.TextWrapping = TextWrapping.Wrap;
        _proxyLiveStatus = status;
        panel.Children.Add(status);

        var note = TextLine(
            Loc.T("仅以只读方式识别运行中的 Clash Verge 等兼容代理软件，不会修改代理配置。",
                "Read-only detection of running Clash Verge and compatible Clash/Mihomo clients; never modifies proxy settings."),
            11, TextTertiary);
        note.Margin = new Thickness(0, 4, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(note);

        if (!_proxyClosing)
        {
            _proxyClosing = true;
            _app.ProxyChanged += RefreshProxyLiveStatus;
            Closed += (_, _) => { _app.ProxyChanged -= RefreshProxyLiveStatus; _openProxyPanels.Clear(); _app.WatchProxyForSettings(false); };
        }
        void UpdateWatch()
        {
            if (panel.IsVisible) _openProxyPanels.Add(panel); else _openProxyPanels.Remove(panel);
            _app.WatchProxyForSettings(_openProxyPanels.Count > 0);
            if (panel.IsVisible) { _proxyLiveStatus = status; RefreshProxyLiveStatus(); }
        }
        panel.IsVisibleChanged += (_, _) => UpdateWatch();
        panel.Unloaded += (_, _) => { _openProxyPanels.Remove(panel); _app.WatchProxyForSettings(_openProxyPanels.Count > 0); };
        RefreshProxyLiveStatus();
        return panel;
    }

    private void RefreshProxyLiveStatus()
    {
        if (_proxyLiveStatus is not { } label) return;
        var snapshot = _app.Proxy?.Snapshot ?? ProxySnapshot.Stopped;
        label.Text = snapshot.Running
            ? Loc.T("当前识别：", "Detected: ") + ProxyText.Summary(snapshot, S.ProxyIndicators)
            : Loc.T("当前状态：未检测到运行中的代理软件（岛上自动隐藏）", "Status: No running proxy client detected (hidden on island)");
    }

    private FrameworkElement ProxyLegendItem(ProxySnapshot sample, ProxyIndicators indicators, PanelLook look, string caption, bool last = false)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, last ? 0 : 12, 1) };
        var glyph = new ProxyStrip(sample, indicators, 1) { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        glyph.Width = glyph.PreferredWidth;
        item.Children.Add(glyph);
        var text = TextLine(caption, 11.5, look.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        item.Children.Add(text);
        return item;
    }

    private void ProxySizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.ProxyWidth, 440, 1200, value => Resize(s => s.ProxyWidth = value, DeckPage.Proxy)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.ProxyHeight, 140, 400, value => Resize(s => s.ProxyHeight = value, DeckPage.Proxy)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = ProxySizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "ProxySize", IsChecked = (S.ProxyWidth, S.ProxyHeight) == (w, h) };
            System.Windows.Automation.AutomationProperties.SetName(choice, Loc.T("代理控制快捷尺寸 ", "Proxy control quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.ProxyWidth = w; s.ProxyHeight = h; }, DeckPage.Proxy); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
        var hint = TextLine(
            Loc.T("仅在展开“代理控制”页时通过 Mihomo 本机接口读取并控制代理模式、策略组节点、延迟测试与实时网速；系统代理与 TUN 仅只读显示。",
                "Connects to the local Mihomo API only while the Proxy control page is open for mode, group/node selection, latency tests and live traffic; System Proxy and TUN are read-only."),
            11, TextTertiary);
        hint.Margin = new Thickness(0, 8, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;
        Target.Children.Add(hint);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is Panel panel)
            foreach (UIElement child in panel.Children)
                foreach (var found in FindLogicalOrVisual<T>(child)) yield return found;
        else if (root is Decorator decorator && decorator.Child is { } inner)
            foreach (var found in FindLogicalOrVisual<T>(inner)) yield return found;
        else if (root is ContentControl content && content.Content is DependencyObject innerObj)
            foreach (var found in FindLogicalOrVisual<T>(innerObj)) yield return found;
    }

    private static IEnumerable<T> FindLogicalOrVisual<T>(DependencyObject obj) where T : DependencyObject
    {
        if (obj is T matched) yield return matched;
        foreach (var child in FindVisualChildren<T>(obj)) yield return child;
    }
}
