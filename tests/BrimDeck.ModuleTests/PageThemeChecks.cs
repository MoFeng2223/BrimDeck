using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void PageThemeChecks()
    {
        using var host = Host(); host.ReconcileAsync([Def("page-theme", Done)]).GetAwaiter().GetResult();
        var owner = new Grid(); owner.Resources["DirectOwnerValue"] = "copied";
        var context = new ModuleViewContext(owner, _ => { }, _ => { }, _ => { });
        var editor = new TextBox { Text = "synthetic composition draft" }; editor.Select(3, 8);
        var label = ModuleUi.Text("Semantic"); var root = new Grid(); root.Children.Add(editor); root.Children.Add(label);
        var view = new ProbeView(root); int changes = 0;
        int global = ModuleTheme.Generation;
        var dark = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!); var light = PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Light)!);
        using (var mount = new ModuleViewHost(host, "page-theme", context, _ => view, palette: dark))
        {
            using var watch = context.OnAppearanceChanged(_ => changes++);
            var first = (SolidColorBrush)label.Foreground;
            mount.SetViewport(new ModuleViewport(500, 220) { ContentScale = 1.5, DeviceScale = 2 });
            mount.SetTheme(light); mount.Refresh();
            Check("local appearance changes keep the original editor and selection", ReferenceEquals(editor.Parent, root) && editor.Text == "synthetic composition draft" && editor.SelectionStart == 3 && editor.SelectionLength == 8 && view.Disposals == 0);
            Check("semantic factory text follows the mount palette", ((SolidColorBrush)label.Foreground).Color != first.Color && ((SolidColorBrush)label.Foreground).Color == ((SolidColorBrush)mount.FindResource("PanelPrimary")).Color);
            Check("direct owner resources and styles remain available in the local mount", Equals(mount.FindResource("DirectOwnerValue"), "copied"));
            Check("appearance reports separated content and device scales", context.Appearance is { Dark: false, DeviceScale: 2, ContentScale: 1.5, Preview: false } && changes >= 3);
            int steady = changes; mount.Refresh(); Check("unchanged appearance does not emit repeated revisions", changes == steady);
            Check("local palette updates never replace the global theme", ModuleTheme.Generation == global);
            var ring = ModuleUi.Ring(50); root.Children.Add(ring);
            Check("public ring shares the host shape and semantic accent", ring.Children.Count == 2 && ((System.Windows.Shapes.Shape)ring.Children[1]).Stroke == mount.FindResource("PanelAccent"));
            var progress = ModuleUi.Progress(double.NaN); var card = ModuleUi.ValueCard("Synthetic", "42"); root.Children.Add(card);
            Check("public controls bound invalid progress and inherit card palette", progress.Value == 0 && card.Background == mount.FindResource("PanelFill"));
        }
        Check("appearance listeners and mount clean up once", context.Lifetime.IsCancellationRequested && view.Disposals == 1);
        var preview = new ModuleViewContext(owner, _ => throw new Exception("navigation executed"), _ => { }, _ => { });
        using (var mount = new ModuleViewHost(host, "page-theme", preview, _ => new ProbeView(), palette: dark, preview: true))
        {
            Check("preview context is explicit before factory execution", preview.Appearance.Preview);
            Throws("preview navigation is rejected before the host callback", () => preview.OpenPage("page-theme:page"));
        }
        host.ReconcileAsync([]).GetAwaiter().GetResult();
    }
}
