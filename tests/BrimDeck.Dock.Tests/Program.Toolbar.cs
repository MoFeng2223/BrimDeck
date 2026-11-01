using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void CheckClipboardToolbarHost(App app, MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string scratch, string directory)
    {
        var snapshot = history.Snapshot(); var preferences = settings.Clipboard.Copy(); var query = view.QueryState;
        bool mode = view.ApplicationsMode, usage = settings.UsagePage, music = settings.MusicPage, monitor = settings.MonitorPage;
        var wheelMode = settings.MouseWheel; bool rightSettings = settings.RightClickSettings, preview = (bool)Get(window, "_preview")!;
        double oldWidth = window.Width, oldHeight = window.Height, requestedWidth = 1320;
        bool pointerInside = (bool)Get(window, "_pointerInside")!;
        var oldPinBoundary = Get(view, "_setApplicationPinned")!;
        var oldSettingsWindow = Get(app, "_settingsWindow");
        int commits = 0;
        Set(view, "_setApplicationPinned", (Action<string, bool>)((source, pinned) => { settings.Clipboard.SetApplicationPinned(source, pinned); commits++; }));
        var fixtures = new[] { "Browser fixture", "Editor fixture", "Mail fixture", "Design fixture", "Terminal fixture", "Application fixture with a very long display name", "" };
        Button Button(string id) => All<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == id);
        Button Source(string source) => Button(source.Length == 0 ? "clipboard-application-unknown" : "clipboard-application-source-" + source);
        MouseWheelEventArgs Wheel(UIElement target, int delta)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            target.RaiseEvent(e); e.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(e); Pump(); return e;
        }
        MouseButtonEventArgs Right(UIElement target)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent };
            target.RaiseEvent(e); Pump(); return e;
        }
        void ReturnToClipboard()
        {
            // This offscreen host deliberately enables wheel dispatch, not pointer-driven auto-close.
            Set(window, "_pointerInside", true); ((PromptTimer)Get(window, "_leave")!).Stop();
            Set(window, "_page", DeckPage.Clipboard); Set(window, "_pageWidth", requestedWidth); Set(window, "_widestPage", requestedWidth);
            Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump(); window.UpdateLayout(); view.UpdateLayout(); Pump();
        }
        SettingsWindow? isolatedSettings = null;
        using var prices = new Pricing(scratch); Set(app, "<Prices>k__BackingField", prices);
        try
        {
            // Only the preview-mode wheel guard changes; startup and Loaded services stay disconnected in this host.
            Set(window, "_preview", false);
            settings.UsagePage = true; settings.MusicPage = settings.MonitorPage = false;
            settings.MouseWheel = MouseWheelAction.Pages; settings.RightClickSettings = false;
            settings.Clipboard.PinnedApplications = [];
            history.Restore(new ClipboardSnapshot { Items = fixtures.Select((source, index) => new ClipboardEntry { Id = "toolbar-" + index, SourceName = source, Content = ClipboardContent.FromText("Synthetic toolbar entry " + index), CopiedAt = DateTimeOffset.UtcNow.AddMinutes(-index) }).ToList() }, settings.Clipboard, DateTimeOffset.UtcNow);
            window.Width = 1920; window.Height = 700;
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 1920, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 1920, 1032));
            Set(window, "_pageWidth", 1320d); Set(window, "_widestPage", 1320d); ReturnToClipboard();
            view.ApplicationsMode = true; view.QueryState = new(); Pump();
            var pinTab = Source("Mail fixture"); Right(pinTab); var pinMenu = pinTab.ContextMenu!;
            Check("real toolbar apps: shared menu separates pin, hide and destructive clear from collection editing", pinMenu.IsOpen && pinMenu.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).SequenceEqual(new[] { "clipboard-application-pin", "clipboard-application-hide", "clipboard-application-clear" }));
            RenderMenu((IslandBorder)window.FindName("Island"), pinMenu, directory, "clipboard-toolbar-pin-menu.png");
            ((MenuItem)pinMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            var tabs = (StackPanel)Get(view, "_tabs")!;
            Check("real toolbar pins: pin updates saved preference once and displays first after total history", commits == 1 && settings.Clipboard.IsApplicationPinned("Mail fixture")
                && AutomationProperties.GetAutomationId((Button)tabs.Children[0]) == "clipboard-application-history" && ReferenceEquals(tabs.Children[1], Source("Mail fixture")));
            var reloaded = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings.Clipboard))!; reloaded.Normalize();
            Check("real toolbar pins: reload retains navigation pin without changing collection membership", reloaded.IsApplicationPinned("mail fixture") && history.Items.All(entry => entry.CollectionId.Length == 0) && !reloaded.Enabled);
            foreach (var palette in palettes)
            foreach (int percent in new[] { 100, 150 })
            foreach (double width in new[] { 480d, 660, 880, 1080, 1320 })
            {
                settings.PanelScale = percent; Call(window, "ApplyScale"); Palette(palette); Call(window, "ApplyTheme");
                requestedWidth = width; ReturnToClipboard();
                var scroll = All<ScrollViewer>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-classification-scroll");
                bool overflow = scroll.IsVisible && scroll.ViewportWidth > 0 && scroll.ScrollableWidth > .5;
                scroll.ScrollToLeftEnd(); Pump(); var wheel = Wheel(scroll, 120);
                string scenario = palette.Id + "-" + percent + "-" + width;
                Check($"real toolbar wheel {scenario}: current overflow chooses navigation or host page (overflow={overflow}; now={scroll.ScrollableWidth:0.##}; viewport={scroll.ViewportWidth:0.##}; visible={scroll.IsVisible}; page={window.CurrentPage}; handled={wheel.Handled}; width={view.ActualWidth:0.##})", overflow ? window.CurrentPage == DeckPage.Clipboard && wheel.Handled : window.CurrentPage == DeckPage.Usage && wheel.Handled);
                ReturnToClipboard(); scroll.ScrollToRightEnd(); Pump();
                if (overflow)
                {
                    Wheel(scroll, -120);
                    Check("real toolbar wheel " + scenario + ": end of overflow never leaks into page switching", window.CurrentPage == DeckPage.Clipboard && Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1);
                }
                var types = (StackPanel)Get(view, "_dockTypes")!; var actions = (StackPanel)Get(view, "_dockRight")!;
                Check("real toolbar bounds " + scenario + ": fixed controls stay inside the adaptive island", view.ActualWidth <= width + 1 && types.TranslatePoint(new Point(), view).X >= 0
                    && actions.TranslatePoint(new Point(actions.ActualWidth, 0), view).X <= view.ActualWidth + .5 && scroll.ActualWidth >= 0);
                if (width is 660 or 1320) Render((IslandBorder)window.FindName("Island"), directory, "clipboard-toolbar-" + scenario + ".png");
            }
            settings.PanelScale = 100; Call(window, "ApplyScale"); Palette(palettes[0]); Call(window, "ApplyTheme");
            requestedWidth = 1320; ReturnToClipboard();
            Wheel(Button("clipboard-type-textT"), 120);
            Check("real toolbar wheel: fixed type controls use the actual host page wheel", window.CurrentPage == DeckPage.Usage);
            ReturnToClipboard(); settings.MouseWheel = MouseWheelAction.None;
            var categoryScroll = All<ScrollViewer>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-classification-scroll");
            // The seventh fixed type can leave a small real overflow even at 1320 DIP; navigation still owns that wheel.
            bool currentOverflow = categoryScroll.IsVisible && categoryScroll.ViewportWidth > 0 && categoryScroll.ScrollableWidth > .5;
            var none = Wheel(categoryScroll, 120);
            Check("real toolbar wheel: disabling the host preserves actual classification overflow ownership", window.CurrentPage == DeckPage.Clipboard && none.Handled == currentOverflow);
            var fixedNone = Wheel(Button("clipboard-type-code"), 120);
            Check("real toolbar wheel: disabled host action stays disabled on fixed code control", window.CurrentPage == DeckPage.Clipboard && !fixedNone.Handled);
            settings.MouseWheel = MouseWheelAction.Volume; Set(window, "_wheelVolumeRest", 0);
            var volume = Wheel(Button("clipboard-settings"), 60);
            Check("real toolbar wheel: volume preference reaches host accumulator without sending a real volume key", volume.Handled && (int)Get(window, "_wheelVolumeRest")! == 60 && window.CurrentPage == DeckPage.Clipboard);
            settings.MouseWheel = MouseWheelAction.Pages; Set(window, "_wheelVolumeRest", 0);
            settings.RightClickSettings = false; var right = Right(Button("clipboard-settings"));
            Check("real toolbar right-click: disabled host setting opens no clipboard-specific menu", !right.Handled && !view.PopupOpen && Button("clipboard-settings").ContextMenu is null && Get(app, "_settingsWindow") is null);
            // An unshown settings window intercepts only the UI boundary; its loaded guard prevents update/network work.
            isolatedSettings = new SettingsWindow(app) { ShowInTaskbar = false, ShowActivated = false };
            Set(app, "_settingsWindow", isolatedSettings); settings.RightClickSettings = true;
            int before = (int)Get(isolatedSettings, "_updateOpenGeneration")!;
            right = Right(Button("clipboard-type-textT"));
            Check("real toolbar right-click: enabled preference reaches the ordinary App.OpenSettings path", right.Handled && (int)Get(isolatedSettings, "_updateOpenGeneration")! == before + 1 && !view.PopupOpen);
            var appTab = Source("Browser fixture"); before = (int)Get(isolatedSettings, "_updateOpenGeneration")!; Right(appTab);
            Check("real toolbar right-click: application pin menu does not also open settings", appTab.ContextMenu?.IsOpen == true && (int)Get(isolatedSettings, "_updateOpenGeneration")! == before); view.ClosePopups(); Pump();
        }
        finally
        {
            view.ClosePopups();
            if (isolatedSettings is not null) { bool exiting = app.Exiting; Set(app, "<Exiting>k__BackingField", true); isolatedSettings.Close(); Set(app, "<Exiting>k__BackingField", exiting); }
            Set(app, "_settingsWindow", oldSettingsWindow!); Set(view, "_setApplicationPinned", oldPinBoundary);
            settings.Clipboard = preferences; settings.UsagePage = usage; settings.MusicPage = music; settings.MonitorPage = monitor;
            settings.MouseWheel = wheelMode; settings.RightClickSettings = rightSettings; settings.PanelScale = 100; Set(window, "_preview", preview); Set(window, "_pointerInside", pointerInside);
            history.Restore(snapshot, settings.Clipboard, DateTimeOffset.UtcNow); window.Width = oldWidth; window.Height = oldHeight;
            view.ApplicationsMode = mode; view.QueryState = query; Palette(palettes[0]); Call(window, "ApplyTheme"); Call(window, "ApplyScale");
            requestedWidth = 1080; ReturnToClipboard();
        }
    }
}
