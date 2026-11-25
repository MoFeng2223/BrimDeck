using System.Windows;
using System.Windows.Media;

namespace BrimDeck;

// ClearType for the text of a layered window. WPF draws text in grayscale wherever it lands in a layer of its own (under a clip,
// a scroll view, a text box, an opacity mask), and a layered window's text needs the hint even without one. The hint is honored
// only below that layer, so a hint on the window's content does not reach text inside a scroll view or a text box. Set on a
// root, this gives every element under it the hint as it joins the tree, so text keeps ClearType whatever holds it.
//
// Only for an opaque surface: a see-through one sets TextRenderingMode.Grayscale on the same root (MainWindow.ApplySurface),
// and a pixel theme Aliased (PanelTheme.Text); WPF takes either over the hint.
internal static class TextClarity
{
    public static readonly DependencyProperty ClearTypeProperty = DependencyProperty.RegisterAttached("ClearType", typeof(bool), typeof(TextClarity),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, ClearTypeChanged));

    public static bool GetClearType(DependencyObject element) => (bool)element.GetValue(ClearTypeProperty);
    public static void SetClearType(DependencyObject element, bool value) => element.SetValue(ClearTypeProperty, value);

    private static void ClearTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) RenderOptions.SetClearTypeHint(element, ClearTypeHint.Enabled);
        else element.ClearValue(RenderOptions.ClearTypeHintProperty);
    }
}
