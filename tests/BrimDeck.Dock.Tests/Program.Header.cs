using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void CheckSharedClipboardHeader(MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string directory)
    {
        var toolbar = (FrameworkElement)window.FindName("Toolbar"); var row = (RowDefinition)window.FindName("ToolbarRow");
        var island = (IslandBorder)window.FindName("Island"); var stage = (FrameworkElement)window.FindName("Stage");
        var list = All<ListBox>(view).Single(); var query = view.QueryState;
        var oldMode = settings.Clipboard.HeaderMode; bool grid = settings.Clipboard.GridView;
        double oldHeight = window.Height, oldWidth = window.Width, clipboardHeight = settings.ClipboardHeight;
        bool usage = settings.UsagePage, music = settings.MusicPage, monitor = settings.MonitorPage;
        try
        {
            settings.Clipboard.GridView = false; settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge; settings.ClipboardHeight = 210;
            window.Width = 1920; window.Height = 700;
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 1920, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 1920, 1032));
            Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d);
            view.QueryState = new ClipboardQuery(); view.ApplyLayout();
            window.SetExpanded(false, instant: true); window.SetExpanded(true, instant: true); Pump();
            var selected = history.Items.Single(entry => entry.Id == "file"); list.SelectedItem = selected; OpenAutomaticPreview(view,selected); Pump();
            var state = view.CapturePresentation(); view.ClosePopups();
            Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
            Point Edge() { var panel = Bounds(island); return new Point((panel.Left + panel.Right) / 2, stage.PointToScreen(new Point()).Y + 1); }
            Point Body() { var panel = Bounds(island); return new Point((panel.Left + panel.Right) / 2, panel.Top + 100); }
            Check("shared clipboard header: edge mode starts with the translated header only", toolbar.Visibility == Visibility.Collapsed && row.ActualHeight == 0 && Near(island.ActualHeight, 210));
            Call(window, "TrackClipboardHeader", Edge()); Pump();
            Check("shared clipboard header: the initial opening hover cannot reveal a second level", toolbar.Visibility == Visibility.Collapsed);
            Call(window, "TrackClipboardHeader", Body()); Call(window, "TrackClipboardHeader", Edge()); Pump();
            Check("shared clipboard header: returning to top edge reveals actual page switch, clock and settings", toolbar.IsVisible && Near(row.ActualHeight, 36)
                && ((FrameworkElement)window.FindName("PageSwitch")).IsVisible && ((FrameworkElement)window.FindName("ClockLabel")).IsVisible && ((FrameworkElement)window.FindName("SettingsButton")).IsVisible);
            Check("shared clipboard header: island expands down while keeping full card viewport", Near(island.ActualHeight, 246) && Near(view.ActualHeight, 210) && Near(view.TranslatePoint(new Point(), (UIElement)window.FindName("ContentCanvas")).Y, 36));
            Render(island, directory, "clipboard-shared-header-edge.png");
            Check("shared clipboard header: reveal preserves selection and query without recreating view", ReferenceEquals(view, Get(window, "_clipboardView")) && (list.SelectedItem as ClipboardEntry)?.Id == selected.Id && view.CapturePresentation().SelectedId == state.SelectedId && view.QueryState.Text.Length == 0);
            Call(window, "TrackClipboardHeader", Body());
            var hide = (DispatcherTimer)Get(window, "_clipboardHeaderLeave")!;
            Check("shared clipboard header: returning to content schedules a short leave grace", hide.IsEnabled);
            WaitHeader(520);
            Check("shared clipboard header: transient mode retracts after returning to content", !toolbar.IsVisible && Near(island.ActualHeight, 210) && Near(row.ActualHeight, 0));
            settings.Animations = true; Call(window, "TrackClipboardHeader", Edge()); Pump();
            if (Soft.Enabled)
            {
                WaitHeader(90);
                Check("shared clipboard header motion: native surface and header share the existing reshape", row.ActualHeight > 0 && row.ActualHeight < 36 && island.ActualHeight > 210 && island.ActualHeight < 246 && island.HasAnimatedProperties);
                Call(window, "SetClipboardHeaderRevealed", false); Call(window, "SetClipboardHeaderRevealed", true); WaitHeader(PanelMotion.Reshape + 120);
                Check("shared clipboard header motion: reversals settle to latest reveal without stale height", Near(row.ActualHeight, 36) && Near(island.ActualHeight, 246));
            }
            settings.Animations = false;
            foreach (var palette in palettes)
            foreach (int percent in new[] { 100, 150 })
            {
                settings.Clipboard.HeaderMode = ClipboardHeaderMode.Always; Palette(palette); Call(window, "ApplyTheme"); settings.PanelScale = percent; Call(window, "ApplyScale");
                Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
                Check("shared header always " + palette.Id + "-" + percent + ": retained public controls and one-time scale", toolbar.IsVisible && Near(row.ActualHeight, 36) && Near(island.ActualHeight, 246 * percent / 100d) && Near(view.ActualHeight, 210));
                Check("shared header always " + palette.Id + "-" + percent + ": cards remain fixed reference size", All<ClipboardCard>(view).All(card => Near(card.ActualWidth, 210) && Near(card.ActualHeight, 146)));
                Render(island, directory, "clipboard-shared-header-" + palette.Id + "-" + percent + ".png");
            }
            Palette(palettes[0]); Call(window, "ApplyTheme"); settings.PanelScale = 100; Call(window, "ApplyScale");
            window.Height = 240; window.SetExpanded(true, instant: true); Pump();
            Check("shared clipboard header: work-area constraint pushes into content rather than past window", island.ActualHeight <= window.Height && Near(row.ActualHeight, 36) && view.ActualHeight < 210);
            window.Height = 700; window.SetExpanded(false, instant: true); window.SetExpanded(true, instant: true); Pump();
            Check("shared clipboard header: always-visible survives collapse and reopen", toolbar.IsVisible && Near(row.ActualHeight, 36));
            settings.Clipboard.HeaderMode = ClipboardHeaderMode.Edge; window.SetExpanded(false, instant: true); window.SetExpanded(true, instant: true); Pump();
            Check("shared clipboard header: edge mode clears reveal and arming on a fresh opening", !toolbar.IsVisible && !(bool)Get(window, "_clipboardHeaderEdgeArmed")!);
            Check("shared clipboard edge: physical coordinates support negative monitor origin and scaled hit band", MainWindow.AtClipboardHeaderEdge(new Point(-900, -198), new Rect(-1200, -180, 900, 300), -200, 1.5)
                && !MainWindow.AtClipboardHeaderEdge(new Point(-200, -198), new Rect(-1200, -180, 900, 300), -200, 1.5)
                && !MainWindow.AtClipboardHeaderEdge(new Point(-900, -185), new Rect(-1200, -180, 900, 300), -200, 1.5));
            settings.Clipboard.HeaderMode = ClipboardHeaderMode.Always;
            var stored = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings.Clipboard.Copy()))!; stored.Normalize();
            Check("shared clipboard header: persisted mode round-trips without enabling capture", stored.HeaderMode == ClipboardHeaderMode.Always && !stored.Enabled);
            stored.HeaderMode = (ClipboardHeaderMode)99; stored.Normalize();
            Check("shared clipboard header: unknown and legacy preferences default to edge reveal", stored.HeaderMode == ClipboardHeaderMode.Edge && JsonSerializer.Deserialize<ClipboardSettings>("{}")!.HeaderMode == ClipboardHeaderMode.Edge);
            settings.UsagePage = settings.MusicPage = settings.MonitorPage = true;
            Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); window.RefreshStatus(); Pump();
            Check("shared clipboard header: actual public page buttons are reachable beside the active clipboard page", new[] { "AIUsageButton", "MusicPageButton", "MonitorPageButton", "ClipboardPageButton" }.All(name => ((FrameworkElement)window.FindName(name)).IsVisible));
            Render(island, directory, "clipboard-shared-header-pages.png");
            var notice = All<Border>(view).Single(element => AutomationProperties.GetAutomationId(element) == "clipboard-notice");
            Call(view, "ShowNotice", "Synthetic item moved; entry kept in history"); Pump();
            Check("real clipboard notice: capsule is bounded inside the expanded island's bottom-right", notice.IsVisible && notice.TranslatePoint(new Point(), view).X >= 0 && Near(notice.TranslatePoint(new Point(), view).X + notice.ActualWidth, view.ActualWidth - 14));
            Render(island, directory, "clipboard-shared-header-notice.png");
            WaitHeader(3150);
            Check("real clipboard notice: three-second result leaves no persistent bottom caption", notice.Visibility == Visibility.Collapsed && ((TextBlock)notice.Child).Text.Length == 0);
            Set(window, "_page", DeckPage.Usage); Call(window, "PrepareClipboardHeader", true); window.RenderUsage(); Pump();
            Check("shared clipboard header: leaving clipboard restores ordinary page header and clears reveal transforms", toolbar.IsVisible && toolbar.Opacity == 1 && toolbar.RenderTransform.Value.IsIdentity && row.Height.Value > 0 && !(bool)Get(window, "_clipboardHeaderEdgeArmed")!);
            Set(window, "_page", DeckPage.Clipboard); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
        }
        finally
        {
            view.ClosePopups(); settings.Animations = false; settings.PanelScale = 100;
            Set(window, "_page", DeckPage.Clipboard); settings.UsagePage = usage; settings.MusicPage = music; settings.MonitorPage = monitor;
            settings.Clipboard.HeaderMode = oldMode; settings.Clipboard.GridView = grid; settings.ClipboardHeight = clipboardHeight;
            window.Height = oldHeight; window.Width = oldWidth; Palette(palettes[0]); Call(window, "ApplyTheme"); Call(window, "ApplyScale");
            view.QueryState = query; view.ApplyLayout(); window.SetExpanded(false, instant: true); window.SetExpanded(true, instant: true); Pump();
        }
    }
    private static void WaitHeader(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
