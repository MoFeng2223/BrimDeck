using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class Program
{
    private static void CheckApplicationActionsHost(App app, MainWindow window, ClipboardView view, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string scratch, string directory)
    {
        var snapshot = history.Snapshot(); var preferences = settings.Clipboard.Copy(); var query = view.QueryState; bool mode = view.ApplicationsMode, animations = settings.Animations;
        var pin = Get(view, "_setApplicationPinned"); var hide = Get(view, "_setApplicationHidden"); var confirmation = Get(view, "_confirmation");
        var monitor = Get(window, "<MonitorBoundsDip>k__BackingField"); var work = Get(window, "<WorkDip>k__BackingField");
        double oldWidth = window.Width, oldHeight = window.Height; int hides = 0, pins = 0; bool confirm = true;
        string file = System.IO.Path.Combine(scratch, "application-source.txt"); File.WriteAllText(file, "Synthetic application clearing fixture.");
        Set(view, "_setApplicationPinned", (Action<string, bool>)((source, value) => { settings.Clipboard.SetApplicationPinned(source, value); pins++; view.Refresh(); }));
        Set(view, "_setApplicationHidden", (Action<string, bool>)((source, value) => { settings.Clipboard.SetApplicationHidden(source, value); hides++; view.Refresh(); }));
        Set(view, "_confirmation", (Func<PromptRequest, bool>)(_ => confirm));
        Button Button(string id) => All<Button>(view).Single(button => AutomationProperties.GetAutomationId(button).Equals(id, StringComparison.OrdinalIgnoreCase));
        Button Source(string name) => Button(name.Length == 0 ? "clipboard-application-unknown" : "clipboard-application-source-" + name);
        MenuItem Item(Button button, string action) => button.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-application-" + action);
        void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        void Right(UIElement target) { target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent }); Pump(); }
        void Restore(string name)
        {
            var menu = Button("clipboard-application-history").ContextMenu!;
            var item = menu.Items.OfType<MenuItem>().Single().Items.OfType<MenuItem>().Single(child => AutomationProperties.GetAutomationId(child) == "clipboard-application-restore-" + name);
            Click(item); Pump();
        }
        void Reset()
        {
            settings.Clipboard.HiddenApplications = []; settings.Clipboard.PinnedApplications = [];
            history.Restore(new ClipboardSnapshot { Items = [
                new() { Id = "app-browser", SourceName = "Browser fixture", Content = ClipboardContent.FromText("Synthetic browser layout fixture"), CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-code", SourceName = "browser fixture", Content = ClipboardContent.FromText("const synthetic = 42;"), Favorite = true, CollectionId = "prompts", CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-file", SourceName = "Browser fixture", Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] }, Archived = true, CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-editor", SourceName = "Editor fixture", Content = ClipboardContent.FromText("Synthetic editor layout fixture"), CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-mail", SourceName = "Mail fixture", Content = ClipboardContent.FromText("Synthetic mail layout fixture"), CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-color", SourceName = "Design fixture", Content = ClipboardContent.FromText("#79A8E8"), CopiedAt = DateTimeOffset.UtcNow },
                new() { Id = "app-unknown", Content = ClipboardContent.FromText("Synthetic unknown layout fixture"), CopiedAt = DateTimeOffset.UtcNow }
            ] }, settings.Clipboard, DateTimeOffset.UtcNow);
            view.QueryState = new(); view.ApplicationsMode = true; Pump();
        }
        var fragments = (ApplicationLabelMotion)Get(view, "_applicationMotion")!;
        try
        {
            window.Width = 1920; window.Height = 700;
            Set(window, "<MonitorBoundsDip>k__BackingField", new DipRect(0, 0, 1920, 1080)); Set(window, "<WorkDip>k__BackingField", new DipRect(0, 0, 1920, 1032));
            foreach (var palette in palettes)
            foreach (int scale in new[] { 100, 150 })
            {
                settings.Animations = false; settings.PanelScale = scale; Palette(palette); Call(window, "ApplyScale"); Call(window, "ApplyTheme");
                Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Reset();
                string scenario = palette.Id + "-" + scale;
                var target = Source("Browser fixture"); Right(target); var menu = target.ContextMenu!;
                Check("application host " + scenario + ": actual menu shares host chrome, scale and destructive boundary", menu.IsOpen && menu.Items.OfType<MenuItem>().Count() == 3
                    && menu.LayoutTransform is ScaleTransform transform && Near(transform.ScaleX, scale / 100d) && !target.AllowDrop);
                RenderMenu((IslandBorder)window.FindName("Island"), menu, directory, "application-menu-" + scenario + ".png");
                view.ClosePopups(); Pump(); DrainApplicationMotion(Soft.Quick + 40);
                settings.Animations = true;
                int before = hides; Click(Item(Source("Browser fixture"), "hide")); Pump();
                var layer = All<Canvas>(view).Single(canvas => AutomationProperties.GetAutomationId(canvas) == "clipboard-application-fragments");
                Check("application host " + scenario + ": hide commits immediately without removing any data", hides == before + 1 && settings.Clipboard.IsApplicationHidden("browser fixture") && history.Items.Count == 7
                    && !All<Button>(view).Any(button => AutomationProperties.GetAutomationId(button).Equals("clipboard-application-source-Browser fixture", StringComparison.OrdinalIgnoreCase)));
                if (Soft.Enabled)
                {
                    Check("application host " + scenario + ": actual island renders bounded noninteractive label fragments", fragments.FragmentCount is > 1 and <= 21 && !layer.IsHitTestVisible
                        && layer.Children.OfType<Rectangle>().All(shard => shard.HasAnimatedProperties && shard.Fill is ImageBrush { ImageSource: not null }));
                    DrainApplicationMotion(95);
                    Render((IslandBorder)window.FindName("Island"), directory, "application-shatter-" + scenario + ".png");
                    DrainApplicationMotion(Soft.Move + 100);
                    Check("application host " + scenario + ": fragment snapshots and reflow clocks are released", fragments.FragmentCount == 0
                        && ((StackPanel)Get(view, "_tabs")!).Children.OfType<Button>().All(button => button.RenderTransform.Value.IsIdentity));
                }
                else Check("application host " + scenario + ": Windows reduced motion skips snapshots", fragments.FragmentCount == 0);
                settings.Animations = false; Restore("Browser fixture");
                Check("application host " + scenario + ": restoration reuses provenance with its collection and favorite", history.Items.Single(item => item.Id == "app-code") is { Favorite: true, CollectionId: "prompts" }
                    && Source("Browser fixture").IsVisible && fragments.FragmentCount == 0);
                if (scale == 100)
                {
                    confirm = false; Click(Item(Source("Browser fixture"), "clear")); Pump();
                    Check("application host " + scenario + ": cancelled clear preserves all app entries and original", history.ApplicationCount("browser fixture") == 3 && File.Exists(file));
                    confirm = true; settings.Animations = true; Click(Item(Source("Browser fixture"), "clear")); Pump();
                    Check("application host " + scenario + ": confirmed clear removes exact source including archived original reference", history.ApplicationCount("Browser fixture") == 0 && history.Items.Count == 4 && File.Exists(file));
                    DrainApplicationMotion(Soft.Move + 100); settings.Animations = false; Reset();
                }
                Button("clipboard-type-code").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump();
                var code = Button("clipboard-type-code"); var color = Button("clipboard-type-palette");
                Check("application host " + scenario + ": code filter is immediately after color and returns real Code only", Near(code.TranslatePoint(new Point(), view).X, color.TranslatePoint(new Point(color.ActualWidth, 0), view).X)
                    && view.ResultCount == 1 && view.QueryState.Kinds.SequenceEqual(new[] { ClipboardContentKind.Code }));
                Render((IslandBorder)window.FindName("Island"), directory, "application-code-" + scenario + ".png");
            }
            Check("application host: no source action uses pin mutation as a hidden history owner", pins == 0 && !settings.Clipboard.Enabled && File.Exists(file));
        }
        finally
        {
            view.ClosePopups(); fragments.Clear(); Set(view, "_setApplicationPinned", pin); Set(view, "_setApplicationHidden", hide); Set(view, "_confirmation", confirmation);
            settings.Clipboard = preferences; settings.Animations = animations; settings.PanelScale = 100;
            Set(window, "<MonitorBoundsDip>k__BackingField", monitor); Set(window, "<WorkDip>k__BackingField", work);
            history.Restore(snapshot, settings.Clipboard, DateTimeOffset.UtcNow); view.ApplicationsMode = mode; view.QueryState = query;
            window.Width = oldWidth; window.Height = oldHeight; Palette(palettes[0]); Call(window, "ApplyScale"); Call(window, "ApplyTheme");
            Set(window, "_pageWidth", 1080d); Set(window, "_widestPage", 1080d); Call(window, "PrepareClipboardDock"); window.SetExpanded(true, instant: true); Pump();
        }
    }
    private static void DrainApplicationMotion(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
