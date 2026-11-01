using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    internal static void RunApplicationActions(Action<string, bool> check)
    {
        PrepareClipboardResources(); ClipboardLibraryTests.Run(check);
        var settings = new ClipboardSettings { RetentionDays = 0 };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        string directory = Path.Combine(Path.GetTempPath(), "BrimDeck-AppActions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "source.txt"); File.WriteAllText(file, "Synthetic original, not user data.");
        history.Add(ClipboardContent.FromText("Synthetic browser ordinary"), "Browser fixture", "", now, settings);
        var favorite = history.Add(ClipboardContent.FromText("const fixture = 42;"), "browser fixture", "", now, settings)!; history.ToggleFavorite(favorite.Id); history.Move(favorite.Id, "prompts");
        var archived = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] }, "BROWSER fixture", "", now, settings)!; history.Archive(archived.Id, true);
        history.Add(ClipboardContent.FromText("Synthetic mail ordinary"), "Mail fixture", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic unknown ordinary"), "", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic actual Unknown app"), "Unknown app", "", now, settings);
        bool confirm = false; string prompt = ""; int activations = 0, hides = 0;
        bool Confirm(PromptRequest request) { prompt = request.Message; return confirm; }
        ClipboardLibraryWindow? library = null; ClipboardView? view = null;
        void Hide(string source, bool hidden) { if (settings.SetApplicationHidden(source, hidden)) hides++; view!.Refresh(); library?.Refresh(); }
        using var island = view = new ClipboardView(history, () => settings, (_, _) => activations++, () => { }, () => { }, () => { }, () => (false, false, ""), _ => { }, () => { }, confirm: Confirm, setApplicationHidden: Hide);
        var window = new Window { Content = island, Width = 1320, Height = 210, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false };
        library = new ClipboardLibraryWindow(history, (_, _) => activations++, () => (false, false, ""), () => { }, () => { }, Confirm, () => settings, setApplicationHidden: Hide)
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var app = (App)Application.Current; bool motion = app.Settings.Animations;
        Button Button(DependencyObject root, string id) => Descendants<Button>(root).Single(button => AutomationProperties.GetAutomationId(button).Equals(id, StringComparison.OrdinalIgnoreCase));
        Button Source(DependencyObject root, string source) => Button(root, source.Length == 0 ? "clipboard-application-unknown" : "clipboard-application-source-" + source);
        Button All(DependencyObject root) => Button(root, "clipboard-application-history");
        bool Has(DependencyObject root, string source) => Descendants<Button>(root).Any(button => AutomationProperties.GetAutomationId(button).Equals(source.Length == 0 ? "clipboard-application-unknown" : "clipboard-application-source-" + source, StringComparison.OrdinalIgnoreCase));
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
        void Action(Button button, string id)
        {
            var menu = button.ContextMenu!;
            menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-application-" + id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        }
        void Restore(DependencyObject root, string source)
        {
            var restore = All(root).ContextMenu!.Items.OfType<MenuItem>().Single();
            restore.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item).Equals("clipboard-application-restore-" + source, StringComparison.OrdinalIgnoreCase)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        }
        ApplicationLabelMotion Motion(object owner) => (ApplicationLabelMotion)owner.GetType().GetField("_applicationMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        try
        {
            app.Settings.Animations = false; window.Show(); library.Show(); island.ApplicationsMode = true; Pump();
            var snapshot = JsonSerializer.Serialize(history.Snapshot());
            check("app actions: manager and island share the three source-only actions", Source(island, "Browser fixture").ContextMenu!.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId)
                .SequenceEqual(Source(library, "Browser fixture").ContextMenu!.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId))
                && !Source(library, "Browser fixture").AllowDrop && !Source(island, "Browser fixture").AllowDrop && All(island).ContextMenu is null);
            Click(Source(island, "Browser fixture")); Click(Source(library, "Browser fixture"));
            island.SearchText = "fixture"; library.SearchText = "fixture"; WaitClipboardMotion(130);
            Action(Source(island, "Browser fixture"), "hide");
            check("app actions: hiding selected app synchronizes both labels and returns navigation to all history", hides == 1 && settings.IsApplicationHidden("browser FIXTURE")
                && !Has(island, "Browser fixture") && !Has(library, "Browser fixture") && island.QueryState.Source == "" && All(island).Tag as string == "Selected" && All(library).Tag as string == "Selected");
            check("app actions: hiding keeps original history, search and preferences intact", JsonSerializer.Serialize(history.Snapshot()) == snapshot && island.SearchText == "fixture" && library.SearchText == "fixture"
                && history.Query(new ClipboardQuery { Source = "Browser fixture", Archive = ClipboardArchiveScope.All }).Count() == 3 && Motion(island).FragmentCount == 0 && !settings.Enabled);
            history.Add(ClipboardContent.FromText("Synthetic hidden fixture recapture"), "Browser fixture", "", now.AddMinutes(1), settings); Pump();
            check("app actions: hidden source keeps capturing without reappearing", !Has(island, "Browser fixture") && !Has(library, "Browser fixture") && history.ApplicationCount("browser fixture") == 4);
            Restore(library, "Browser fixture");
            check("app actions: All history offers an explicit restore path on either surface", !settings.IsApplicationHidden("browser fixture") && Has(island, "Browser fixture") && Has(library, "Browser fixture") && hides == 2);
            island.QueryState = new(); library.SetFilter(new());
            Action(Source(library, ""), "hide");
            check("app actions: unknown hiding cannot hide total history or a real app called Unknown app", !Has(island, "") && !Has(library, "") && All(island).IsVisible && Has(island, "Unknown app") && history.ApplicationCount("") == 1);
            Restore(island, "");
            check("app actions: unknown source can be restored independently", Has(island, "") && Has(library, "") && !settings.IsApplicationHidden(""));
            island.QueryState = new ClipboardQuery { Source = "Browser fixture", Text = "no matching fixture", Kinds = [ClipboardContentKind.Color], FavoritesOnly = true };
            library.SetFilter(new ClipboardQuery { Source = "Browser fixture", Text = "no matching fixture", Kinds = [ClipboardContentKind.Color] }); Pump();
            snapshot = JsonSerializer.Serialize(history.Snapshot()); Action(Source(island, "Browser fixture"), "clear");
            check("app actions: cancellation preserves all data and warning names hidden scope, favorites, archives and originals", JsonSerializer.Serialize(history.Snapshot()) == snapshot && prompt.Contains("4", StringComparison.Ordinal)
                && prompt.Contains(Loc.T("收藏", "favorites"), StringComparison.Ordinal) && prompt.Contains(Loc.T("归档", "archived"), StringComparison.Ordinal) && File.Exists(file));
            confirm = true; Action(Source(library, "Browser fixture"), "clear");
            check("app actions: confirmed clear ignores current search and kinds but affects only the exact source", history.ApplicationCount("Browser fixture") == 0 && history.Items.Count == 3
                && !Has(island, "Browser fixture") && !Has(library, "Browser fixture") && island.QueryState.Source == "" && File.Exists(file) && history.Collections.Any(group => group.Id == "prompts"));
            island.QueryState = new(); library.SetFilter(new()); Action(Source(island, ""), "clear");
            check("app actions: clear unknown removes only unknown provenance, not all history", history.Items.Count == 2 && history.ApplicationCount("") == 0 && Has(island, "Unknown app"));
            var code = history.Add(ClipboardContent.FromText("{\"synthetic\":true}"), "Mail fixture", "", now, settings)!; Pump();
            Click(Button(island, "clipboard-type-code"));
            var paletteButton = Button(island, "clipboard-type-palette"); var codeButton = Button(island, "clipboard-type-code");
            check("code toolbar: code sits immediately right of color and filters actual Code only", ReferenceEquals(VisualTreeHelper.GetParent(paletteButton), VisualTreeHelper.GetParent(codeButton))
                && codeButton.TranslatePoint(new Point(), island).X > paletteButton.TranslatePoint(new Point(), island).X
                && island.QueryState.Kinds.SequenceEqual(new[] { ClipboardContentKind.Code }) && island.ResultCount == 1 && codeButton.Tag as string == "Selected"
                && !Has(island, "Browser fixture") && history.Items.Any(item => item.Id == code.Id));
            foreach (double width in new[] { 440d, 480, 660, 1080, 1320 })
            {
                window.Width = width; Click(Button(island, "clipboard-search-toggle")); WaitClipboardMotion(Soft.Choose + 40);
                var types = Descendants<Button>(island).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-type-", StringComparison.Ordinal)).ToArray();
                check("code toolbar: seven fixed icons and search fit adaptive width " + width, types.Length == 7 && types.All(button => button.IsVisible && button.TranslatePoint(new Point(), island).X >= 0
                    && button.TranslatePoint(new Point(button.ActualWidth, 0), island).X <= island.ActualWidth + .5) && Descendants<TextBox>(island).Single().ActualWidth > 80);
                island.DismissFromKeyboard(); WaitClipboardMotion(Soft.Choose + 40);
            }
            window.Width = 1320; island.QueryState = new(); Pump();
            app.Settings.Animations = true;
            if (Soft.Enabled)
            {
                Action(Source(island, "Mail fixture"), "hide");
                var fragments = Descendants<Canvas>(island).Single(canvas => AutomationProperties.GetAutomationId(canvas) == "clipboard-application-fragments");
                check("app shatter: mutation happens immediately with bounded noninteractive snapshot fragments", settings.IsApplicationHidden("Mail fixture") && Motion(island).FragmentCount is > 1 and <= 21
                    && !fragments.IsHitTestVisible && fragments.Children.OfType<Rectangle>().All(shard => shard.Fill is ImageBrush { ImageSource: not null } && shard.HasAnimatedProperties));
                WaitClipboardMotion(Soft.Move + 120);
                check("app shatter: completion releases every snapshot and live label transform", Motion(island).FragmentCount == 0 && ((StackPanel)typeof(ClipboardView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(island)!).Children.OfType<Button>().All(button => button.RenderTransform.Value.IsIdentity));
                Restore(library, "Mail fixture"); Action(Source(library, "Mail fixture"), "hide");
                check("app shatter: manager uses the same fragment motion", Motion(library).FragmentCount > 1);
                library.Hide(); Pump();
                check("app shatter: hiding the owning window immediately clears fragments", Motion(library).FragmentCount == 0); library.Show(); Pump(); Restore(island, "Mail fixture");
                Action(Source(island, "Mail fixture"), "hide"); island.Visibility = Visibility.Collapsed; Pump();
                check("app shatter: leaving the island cancels pending reflow and releases snapshots", Motion(island).FragmentCount == 0); island.Visibility = Visibility.Visible; Pump(); Restore(library, "Mail fixture");
                Action(Source(island, "Mail fixture"), "hide"); island.Dispose(); Pump();
                check("app shatter: disposal cancels unfinished motion without deferring the hide preference", Motion(island).FragmentCount == 0 && settings.IsApplicationHidden("Mail fixture"));
            }
            else Console.WriteLine("SKIP: fragment animation follows Windows' disabled animation effects.");
            check("app actions: isolated verification never activates content, deletes originals or enables capture", activations == 0 && File.Exists(file) && !settings.Enabled);
            Console.WriteLine("APPLICATION ACTIONS RESULT: isolated source clearing, hiding, recovery, motion and code filter completed");
        }
        finally { app.Settings.Animations = motion; island.ClosePopups(); library.ForceClose(); window.Close(); Directory.Delete(directory, true); }
    }
}
