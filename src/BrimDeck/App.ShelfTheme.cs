using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
using DropShelf;
using DropShelf.Services;
using DropShelf.Views;

namespace BrimDeck;

public partial class App
{
    private readonly ConditionalWeakTable<Border, IslandBorder> _shelfSurfaces = new();

    private void InitializeShelfTheme()
    {
        HostedResources.MotionEase = Soft.Out;
        HostedResources.SquareCorners = () => PanelTheme.Current.Effects.Pixel > 0;
        HostedResources.DressShelf = DressShelf;
        HostedResources.AttachMotion = AttachShelfMotion;
        PanelTheme.Changed += PublishShelfTheme;
        PublishShelfTheme();
        InitializeThemeFiles();
    }

    private void PublishShelfTheme()
    {
        var palette = PanelTheme.Current;
        var resources = new ResourceDictionary();
        void Brush(string target, string token, bool opaque = false)
        {
            var color = palette[token];
            var brush = new SolidColorBrush(PanelTheme.ToColor(opaque ? color.Over(palette.Surface) : color));
            brush.Freeze(); resources[target] = brush;
        }
        foreach (var (target, token) in new (string, string)[]
        {
            ("AccentBrush", "accent"), ("AccentHoverBrush", "accent"), ("AccentPressedBrush", "accent"),
            ("AccentTextBrush", "accent"), ("AccentSubtleBrush", "rangeFill"), ("AccentFaintBrush", "fill"),
            ("TextPrimaryBrush", "textPrimary"), ("TextSecondaryBrush", "textSecondary"),
            ("TextTertiaryBrush", "textTertiary"), ("TextDisabledBrush", "textTertiary"), ("TextOnAccentBrush", "onAccent"),
            ("WindowBackgroundBrush", "surface"), ("LayerBrush", "fill"), ("CardBrush", "input"), ("CardHoverBrush", "hover"),
            ("CardBorderBrush", "outline"), ("StackEdgeBrush", "fill"), ("SheetGlowBrush", "rangeFill"), ("SheetBorderBrush", "outline"),
            ("ControlBrush", "input"), ("ControlHoverBrush", "hover"), ("ControlPressedBrush", "pressed"),
            ("ControlBorderBrush", "outline"), ("ControlStrongBorderBrush", "textSecondary"), ("ControlAltBrush", "fill"),
            ("SubtleHoverBrush", "hover"), ("SubtlePressedBrush", "pressed"), ("SubtleSelectedBrush", "fillSelected"),
            ("DividerBrush", "hairline"), ("FlyoutBrush", "popup"), ("FlyoutBorderBrush", "popupBorder"),
            ("TooltipBrush", "tooltip"), ("TooltipBorderBrush", "tooltipBorder"),
            ("ShelfSolidBrush", "surface"), ("ShelfAcrylicTintBrush", "surface"), ("ShelfTileBorderBrush", "outline"),
            ("ScrollThumbBrush", "scrollThumb"), ("SuccessBrush", "accent"), ("CautionBrush", "danger"),
            ("CriticalBrush", "danger"), ("SmokeBrush", "popup")
        }) Brush(target, token);
        // 卡片底色必须不透明，才能遮住其自身阴影；浮层和提示各用本体专属色。
        Brush("ShelfTileBrush", "input", opaque: true);
        Brush("SheetBrush", "surface");
        var accent = PanelTheme.ColorOf("accent");
        resources["AccentColor"] = accent; resources["SystemAccentBrush"] = resources["AccentBrush"];
        resources["TextFont"] = UI.PanelFont;
        resources["ControlCornerRadius"] = new CornerRadius(PanelTheme.Corner(5));
        resources["OverlayCornerRadius"] = new CornerRadius(PanelTheme.Corner(8));
        for (int radius = 1; radius <= 16; radius++) resources["ShelfR" + radius] = new CornerRadius(PanelTheme.Corner(radius));
        resources["ShelfRadius10"] = PanelTheme.Corner(10);
        resources["ShelfShadowColor"] = PanelTheme.ColorOf("shadow");
        resources["ShelfShadowOpacity"] = PanelTheme.HighContrast ? 0d : palette.Effects.ShadowOpacity;
        resources["ShelfShadowBlur"] = palette.Effects.ShadowBlur;
        resources["ShelfShadowDepth"] = palette.Effects.ShadowDepth;
        resources["ShelfPopupShadowBlur"] = palette.Effects.ShadowBlur;
        resources["ShelfPopupShadowDepth"] = palette.Effects.ShadowDepth;
        resources["ShelfPopupShadowOpacity"] = PanelTheme.HighContrast ? 0d : palette.Effects.ShadowOpacity;
        resources["ShelfEmptyOutlineOpacity"] = PanelTheme.HighContrast ? 1d : .65d;
        ThemeService.ApplyHostPalette(resources, palette.IsDark, accent);
    }

    private void DressShelf(Window window, FrameworkElement root)
    {
        if (root is not Border border) return;
        if (!_shelfSurfaces.TryGetValue(border, out var surface))
        {
            var content = border.Child; border.Child = null;
            surface = new IslandBorder { Child = content, ShoulderRadius = 0, MaterialOpacity = 1 };
            border.Child = surface; _shelfSurfaces.Add(border, surface);
        }
        Soft.SetReduced(window, window is ShelfToolWindow ? Settings.Clipboard.Mode == ModuleMode.Performance : Settings.Tray.Mode == ModuleMode.Performance);
        bool reduced = Soft.GetReduced(window);
        if (reduced) Soft.StopTree(window);
        // Window-local tokens keep one module's simplified surface from changing the other's theme.
        foreach (string token in new[] { "ShelfShadowBlur", "ShelfShadowDepth", "ShelfShadowOpacity", "ShelfPopupShadowBlur", "ShelfPopupShadowDepth", "ShelfPopupShadowOpacity" })
            if (reduced) window.Resources[token] = 0d; else window.Resources.Remove(token);
        surface.Background = UI.Brush(PanelTheme.Hex("surface"));
        surface.CornerRadius = new CornerRadius(PanelTheme.Corner(12));
        PanelTheme.Dress(surface, dpiScale: VisualTreeHelper.GetDpi(window).DpiScaleX);
        PanelTheme.Text(window);
    }

    private static void AttachShelfMotion(FrameworkElement root)
    {
        if (root is ContextMenu menu)
        {
            PopupMotion.Attach(menu, () => menu.Template?.FindName("Chrome", menu) as UIElement ?? menu);
            PanelTheme.Text(menu);
        }
        root.Loaded += (_, _) => AttachChildren(root);
        static void AttachChildren(DependencyObject element)
        {
            if (element is ButtonBase button) Feedback.SetFade(button, true);
            if (element is ComboBox combo) PopupMotion.Attach(combo);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) AttachChildren(VisualTreeHelper.GetChild(element, i));
        }
    }

    private void DisposeShelfTheme()
    {
        DisposeThemeFiles();
        PanelTheme.Changed -= PublishShelfTheme;
        HostedResources.DressShelf = null; HostedResources.AttachMotion = null;
        HostedResources.SquareCorners = null; HostedResources.MotionEase = null;
    }
}
