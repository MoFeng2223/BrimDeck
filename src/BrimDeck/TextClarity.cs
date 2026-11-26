using System.Windows;
using System.Windows.Media;

namespace BrimDeck;

// ClearType for text wherever it lies. WPF draws text in grayscale in a layered window (AllowsTransparency), in a pop-up (a layered
// window of its own) and wherever it lands in a layer of its own (under a clip, a scroll view, a text box, an opacity mask), unless
// the element it lies in carries the ClearType hint. The hint is honored only below that layer, so a hint on a window's content does
// not reach text inside a scroll view or a text box. Set on a root, this gives every element under it the hint as it joins the
// tree, so text keeps ClearType whatever holds it.
//
// Only for an opaque surface: a see-through one sets TextRenderingMode.Grayscale on the same root (MainWindow.ApplySurface),
// and a pixel theme Aliased (PanelTheme.Text); WPF takes either over the hint.
//
// The one way to give text ClearType; no element sets RenderOptions.ClearTypeHint itself (TextClarityTests):
// - Every window sets it on its root: an ordinary window through its frame (SettingsChrome.Apply), a layered one where its text
//   lies over an opaque surface (the island's content, the notice over the island, the panel's small windows). A window without
//   text is listed in TextClarityTests.
// - A pop-up needs nothing: WPF passes a pop-up the values its target inherits, so a menu, a drop-down or a list opened from
//   anything in a window has them too. A menu or tooltip also takes them as it opens (PanelTheme.Text), as the service opens a
//   tooltip without a target.
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

    // A menu's or tooltip's content takes the text of what it opens from: its grayscale (a see-through panel) or unsmoothed text
    // (a pixel theme), and its ClearType. Opened from nothing (the tray), it keeps WPF's own.
    public static void Follow(FrameworkElement content, DependencyObject? from)
    {
        var mode = from is null ? TextRenderingMode.Auto : TextOptions.GetTextRenderingMode(from);
        if (mode is TextRenderingMode.Aliased or TextRenderingMode.Grayscale) TextOptions.SetTextRenderingMode(content, mode);
        else content.ClearValue(TextOptions.TextRenderingModeProperty);
        if (from is not null && GetClearType(from)) SetClearType(content, true);
        else content.ClearValue(ClearTypeProperty);
    }
}
