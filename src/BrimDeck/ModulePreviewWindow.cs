using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

// A preview owns only its mount. It never changes the running plugin's activation or the application palette.
internal sealed class ModulePreviewWindow : Window
{
    private readonly ModuleViewHost _view;
    internal ModulePreviewWindow(ModuleHost host, Contribution<ModulePage> page, FrameworkElement owner)
    {
        Title = Loc.T("插件局部主题预览", "Plugin local theme preview"); Width = 680; Height = 400; MinWidth = 480; MinHeight = 260;
        var root = new DockPanel(); Content = root;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) }; DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var context = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        var dark = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!);
        _view = new(host, page.ModuleId, context, page.Value.CreateView, page.Value.CreateViewAsync, dark, preview: true) { Margin = new Thickness(12), IsHitTestVisible = false, Focusable = false };
        root.Children.Add(_view);
        foreach (var (id, label) in new[] { (PanelThemes.Dark, Loc.T("深色", "Dark")), (PanelThemes.Light, Loc.T("浅色", "Light")) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => _view.SetTheme(PanelThemes.Resolve(PanelThemes.BuiltIn(id)!)); toolbar.Children.Add(button);
        }
        toolbar.Children.Add(new TextBlock { Text = Loc.T("仅预览绘制，业务操作与导航不可用", "Drawing preview; business input and navigation disabled"), VerticalAlignment = VerticalAlignment.Center });
        SizeChanged += (_, _) => _view.SetViewport(new(Math.Max(1, ActualWidth - 48), Math.Max(1, ActualHeight - 106)) { DeviceScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX });
        IsVisibleChanged += (_, _) => _view.SetPresented(IsVisible);
        Closed += (_, _) => _view.Dispose();
    }
}
