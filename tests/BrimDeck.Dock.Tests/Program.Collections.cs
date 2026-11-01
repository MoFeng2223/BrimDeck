using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class Program
{
    private static void CheckCollectionEditorHost(MainWindow main, DeckSettings settings, PanelPalette[] palettes, string directory)
    {
        var history = new ClipboardHistory(); var preferences = new ClipboardSettings { Enabled = false, RetentionDays = 0 };
        history.Add(ClipboardContent.FromText("Synthetic collection appearance fixture"), "Fixture", "", DateTimeOffset.UtcNow, preferences);
        int activations = 0, changes = 0; history.Changed += () => changes++;
        using var view = new ClipboardView(history, () => preferences, (_, _) => activations++, () => { }, () => { }, () => { }, () => (false, false, ""), _ => { }, () => { });
        var host = new Border { Child = view }; host.SetResourceReference(Border.BackgroundProperty, "PanelSurface");
        var window = new Window { Content = host, Width = 1080, Height = 210, Left = 100, Top = 160, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        Popup? popup = null;
        Button Button(DependencyObject root, string id) => All<Button>(root).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-appearance-" + id);
        Popup Open()
        {
            var anchor = All<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-collection-prompts");
            var menu = anchor.ContextMenu!;
            Check("collection host: a single editor replaces standalone rename and color entries", menu.Items.OfType<MenuItem>().Count(item => Equals(item.Header, Loc.T("名称与标签外观…", "Name & label appearance…"))) == 1
                && menu.Items.OfType<MenuItem>().All(item => !Equals(item.Header, Loc.T("重命名分类…", "Rename collection…"))));
            menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            return (Popup)Get(view, "_editor")!;
        }
        try
        {
            window.Show(); Pump();
            foreach (var palette in palettes)
            foreach (double scale in new[] { 1d, 1.5d })
            {
                Palette(palette); host.LayoutTransform = new ScaleTransform(scale, scale); window.Width = 1080 * scale; window.Height = 210 * scale; Pump();
                history.UpdateCollection("prompts", "Design ideas", scale == 1 ? "#FF8000" : "#8000FF", "", out _); Pump();
                popup = Open(); var chrome = (FrameworkElement)popup.Child;
                var editor = All<ClipboardCollectionAppearance>(chrome).Single(); var ribbon = All<ClipboardCollectionColorRibbon>(chrome).Single();
                var surface = (FrameworkElement)PresentationSource.FromVisual(chrome)!.RootVisual;
                var field = All<TextBox>(chrome).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-appearance-name");
                var save = Button(chrome, "save"); var saveBounds = save.TransformToAncestor(surface).TransformBounds(new Rect(save.RenderSize));
                Check("collection host " + palette.Id + "-" + scale + ": compact name and palette layout keeps actions in bounds", editor.ActualHeight <= 160 && field.Text == "Design ideas"
                    && ribbon.IsVisible && saveBounds.Bottom <= surface.ActualHeight + 1 && saveBounds.Right <= surface.ActualWidth + 1 && chrome.LayoutTransform is ScaleTransform transform && Math.Abs(transform.ScaleX - scale) < .01);
                Check("collection host " + palette.Id + "-" + scale + ": host palette, pixel corners and exact chosen color are retained", chrome is Border border && border.Background is SolidColorBrush fill && fill.Color == PanelTheme.ColorOf("popup")
                    && border.CornerRadius.TopLeft == (palette.Effects.Pixel > 0 ? 0 : 16) && ribbon.SelectedColor == (scale == 1 ? "#FF8000" : "#8000FF"));
                Render(surface, directory, "collection-editor-" + palette.Id + "-" + scale + ".png");
                if (palette.Id == palettes[1].Id && scale == 1)
                {
                    Button(chrome, "more").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
                    Check("collection host: optional exact HEX and original icons remain reachable", All<TextBox>(chrome).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-appearance-color").IsVisible
                        && Button(chrome, "icon-star").IsVisible && Button(chrome, "reset").IsVisible);
                    Render(surface, directory, "collection-editor-light-options.png");
                }
                popup.IsOpen = false; Pump();
            }
            Palette(palettes[1]); host.LayoutTransform = Transform.Identity; window.Width = 1080; window.Height = 210; Pump();
            history.UpdateCollection("prompts", "Design ideas", "#FF8000", "", out _); Pump();
            popup = Open();
            var root = popup.Child; var nativeRibbon = All<ClipboardCollectionColorRibbon>(root).Single();
            var scroll = All<ScrollViewer>(nativeRibbon).Single();
            var hex = All<TextBox>(root).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-appearance-color");
            var expectedHandle = ((HwndSource)PresentationSource.FromVisual(nativeRibbon)!).Handle;
            CollectionMouse.GetCursorPos(out var cursor); IntPtr foreground = ClipboardPaste.Foreground;
            bool down = false;
            try
            {
                if ((CollectionMouse.GetAsyncKeyState(1) & 0x8000) != 0) throw new InvalidOperationException("Release the physical left mouse button before isolated ribbon verification.");
                Point start = nativeRibbon.PointToScreen(new Point(nativeRibbon.ActualWidth - 38, 26));
                Point end = nativeRibbon.PointToScreen(new Point(42, 26));
                if (CollectionMouse.WindowFromPoint(new((int)start.X, (int)start.Y)) != expectedHandle || CollectionMouse.WindowFromPoint(new((int)end.X, (int)end.Y)) != expectedHandle)
                    throw new InvalidOperationException("Another window covers the isolated ribbon; no mouse input was sent.");
                double before = scroll.HorizontalOffset; string color = hex.Text; int notifications = changes;
                CollectionMouse.SetCursorPos((int)start.X, (int)start.Y); DrainCollectionInput();
                CollectionMouse.mouse_event(2, 0, 0, 0, UIntPtr.Zero); down = true; DrainCollectionInput();
                for (int step = 1; step <= 8; step++)
                {
                    CollectionMouse.SetCursorPos((int)Math.Round(start.X + (end.X - start.X) * step / 8), (int)start.Y); DrainCollectionInput();
                }
                CollectionMouse.mouse_event(4, 0, 0, 0, UIntPtr.Zero); down = false; DrainCollectionInput();
                Check("collection host native drag: physical press/move/release reveals another color interval without choosing or saving", popup.IsOpen && scroll.HorizontalOffset > before + 80 && hex.Text == color && changes == notifications);
                Point click = nativeRibbon.PointToScreen(new Point(nativeRibbon.ActualWidth / 2, 26));
                if (CollectionMouse.WindowFromPoint(new((int)click.X, (int)click.Y)) != expectedHandle) throw new InvalidOperationException("The isolated swatch is no longer exposed; no click was sent.");
                CollectionMouse.SetCursorPos((int)click.X, (int)click.Y); DrainCollectionInput();
                CollectionMouse.mouse_event(2, 0, 0, 0, UIntPtr.Zero); down = true; DrainCollectionInput();
                CollectionMouse.mouse_event(4, 0, 0, 0, UIntPtr.Zero); down = false; DrainCollectionInput();
                Check("collection host native click: release chooses the newly exposed color while keeping history untouched", popup.IsOpen && hex.Text != color && hex.Text == nativeRibbon.SelectedColor && changes == notifications);
                int beforeWheel = (int)scroll.HorizontalOffset;
                CollectionMouse.mouse_event(0x0800, 0, 0, unchecked((uint)-120), UIntPtr.Zero); DrainCollectionInput();
                Check("collection host native wheel: swatch under cursor routes to horizontal browsing only", scroll.HorizontalOffset > beforeWheel && changes == notifications);
            }
            finally
            {
                if (down) CollectionMouse.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                CollectionMouse.SetCursorPos(cursor.X, cursor.Y); ClipboardPaste.RestoreFocus(foreground);
            }
            popup.IsOpen = false; Pump();
            Check("collection host: all isolated menus leave clipboard activation and recording untouched", activations == 0 && !preferences.Enabled && history.Items.Count == 1);
        }
        finally { if (popup is not null) popup.IsOpen = false; window.Close(); Loc.Use(Loc.Chinese); }
    }
    private static void DrainCollectionInput()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static class CollectionMouse
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [DllImport("user32")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    }
}
