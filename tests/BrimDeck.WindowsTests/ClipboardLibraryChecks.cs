using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class ClipboardChecks
{
    private static void CheckLibraryFormats(ClipboardSettings settings, Action<string, bool> check)
    {
        var rich = Text("Hello rich text"); rich.SetData(DataFormats.Html, "<b>Hello</b> rich text"); rich.SetData(DataFormats.Rtf, "{\\rtf1 Hello \\b rich\\b0 text}");
        var captured = ClipboardListener.ReadContent(rich, settings)!;
        check("library formats: rich text stores its inert plain preview and original formats", captured.Kind == ClipboardContentKind.RichText && captured.Text == "Hello rich text" && captured.Html.Length > 0 && captured.Rtf.Length > 0);
        var recalled = ClipboardListener.ToDataObject(captured);
        check("library formats: rich text recall preserves HTML, RTF and Unicode without a system write", recalled.GetText() == captured.Text && recalled.GetData(DataFormats.Html) as string == captured.Html && recalled.GetData(DataFormats.Rtf) as string == captured.Rtf);
        var code = Text("const answer = 42;"); code.SetData(DataFormats.Html, "<pre>const answer = 42;</pre>");
        check("library formats: rich code keeps its developer type and HTML payload", ClipboardListener.ReadContent(code, settings) is { Kind: ClipboardContentKind.Code, Html.Length: > 0 });
        var htmlOnly = new DataObject(); htmlOnly.SetData(DataFormats.Html, "<!--StartFragment--><p>Hello &amp; world</p><!--EndFragment-->");
        check("library formats: HTML-only sources have decoded searchable text", ClipboardListener.ReadContent(htmlOnly, settings) is { Kind: ClipboardContentKind.RichText, Text: "Hello & world" });
        var rtfOnly = new DataObject(); rtfOnly.SetData(DataFormats.Rtf, "{\\rtf1 Hello \\b world\\b0}");
        check("library formats: RTF-only sources never load a document or embedded object", ClipboardListener.ReadContent(rtfOnly, settings) is { Kind: ClipboardContentKind.RichText, Text: "Hello world" });
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" rx=\"4\" fill=\"#0080FF\"/><path d=\"M6 12L10 16L18 8\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"2\"/></svg>";
        var vector = new DataObject(); vector.SetData("image/svg+xml", svg);
        var asset = ClipboardListener.ReadContent(vector, settings)!;
        check("library formats: SVG has a native vector format and Unicode fallback on recall", asset.Kind == ClipboardContentKind.Svg && ClipboardListener.ToDataObject(asset).GetData("image/svg+xml") as string == svg && ClipboardListener.ToDataObject(asset).GetText() == svg);
        check("library preview: static SVG is rendered as vector shapes", ClipboardPreview.Create(asset) is Viewbox box && Descendants<System.Windows.Shapes.Shape>(box).Count() == 2);
        var unsafeSvg = ClipboardContent.FromText("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><image href=\"https://never.example/image.png\"/></svg>");
        check("library preview: active or linked SVG falls back to inert source text", ClipboardPreview.Create(unsafeSvg) is TextBlock);
        var color = ClipboardPreview.Create(ClipboardContent.FromText("linear-gradient(90deg, #0080FF, #00FF80)"));
        check("library preview: gradients are local sampled brushes", Descendants<Border>(color).Any(border => border.Background is LinearGradientBrush gradient && gradient.GradientStops.Count == 2));
        var highlighted = ClipboardPreview.Create(ClipboardContent.FromText("const answer = 42;")) as TextBlock;
        check("library preview: code uses inert highlighted runs and a mono font", highlighted is not null && highlighted.Inlines.OfType<Run>().Count() > 2 && highlighted.FontFamily.Source.Contains("Consolas", StringComparison.Ordinal));
        var image = ClipboardListener.ToDataObject(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 });
        check("library formats: screen capture provenance distinguishes screenshots", ClipboardListener.ReadContent(image, settings, "SnippingTool")?.Kind == ClipboardContentKind.Screenshot && ClipboardListener.ReadContent(image, settings, "Editor")?.Kind == ClipboardContentKind.Image);
        var noImage = settings.Copy(); noImage.CaptureImages = false;
        check("library privacy: screenshot capture respects the image opt-out", ClipboardListener.ReadContent(image, noImage, "SnippingTool") is null);
    }

    private static void CheckLibrary(ClipboardHistory history, ClipboardSettings settings, ClipboardView island, Action<string, bool> check, Action<string> theme, string? renderDirectory)
    {
        int recalls = 0; bool copyOnly = false, confirm = true;
        var library = new ClipboardLibraryWindow(history, (_, copy) => { recalls++; copyOnly = copy; }, () => (false, false, ""), () => { }, () => { }, _ => confirm, () => settings)
        { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            theme(PanelThemes.Dark); library.Show(); Pump();
            check("library window: independent native resizable manager leaves island intact", library.IsVisible && island.IsVisible && library.ResultCount == 5 && library.ResizeMode == ResizeMode.CanResize);
            check("library window: timeline creates actual preview rows", Descendants<ClipboardLibraryItem>(library).Any() && Descendants<GroupItem>(library).Any());
            check("library theme: rendered root paints its own native surface", library.Content is Grid root && Equals(root.Background, Application.Current.Resources["PanelSurface"]));
            CheckClipboardMotion(island, library, check);
            CheckClipboardOrganization(history, island, library, check, value => confirm = value, renderDirectory);
            CheckClipboardAppearance(history, island, library, check, theme, renderDirectory);
            CheckClipboardApplications(history, settings, island, library, check, theme, renderDirectory);
            var list = Descendants<ListBox>(library).Single();
            var first = history.Query().First(); library.FocusEntry(first.Id); Pump();
            var editor = Descendants<ClipboardEntryEditor>(library).Single();
            TextBox Field(string id) => Descendants<TextBox>(editor).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-edit-" + id);
            library.SetFilter(new ClipboardQuery { Source = "Browser fixture", Kinds = [ClipboardContentKind.Link, ClipboardContentKind.Text] }); Pump();
            check("library filters: source and multiple content types combine", library.ResultCount == 3);
            library.SetFilter(new ClipboardQuery { Text = "rocket" }); Pump(); check("library filters: realtime full text narrows timeline", library.ResultCount == 1);
            library.SetFilter(new()); library.FocusEntry(first.Id); Pump();
            var snippet = history.CreateSnippet("Release template", "Café details", ["reply"], ClipboardContent.FromText("Thanks for the detailed design feedback."), DateTimeOffset.UtcNow, settings, out _)!; Pump();
            check("library synchronization: new entries reach the window and island immediately", library.ResultCount == 6 && island.ResultCount == 6);
            library.FocusEntry(snippet.Id); Pump();
            Field("title").Text = "Launch reply"; Field("tags").Text = "team, reusable"; Field("notes").Text = "Customer release instructions";
            check("library drafts: typing never mutates saved content", library.HasUnsavedChanges && history.Items.Single(item => item.Id == snippet.Id).Title == "Release template");
            history.Add(ClipboardContent.FromText("Capture while editing"), "Editor fixture", "", DateTimeOffset.UtcNow, settings); Pump();
            check("library drafts: live captures preserve the selected edit draft", library.HasUnsavedChanges && Field("title").Text == "Launch reply");
            check("library editing: one save updates metadata and clears dirty state", library.SaveDraft() && !library.HasUnsavedChanges && history.Items.Single(item => item.Id == snippet.Id).Title == "Launch reply");
            island.SearchText = "launch reusable instructions"; Pump(); check("library synchronization: island searches edited title, tags and notes", island.ResultCount == 1);
            island.QueryState = new();
            Field("title").Text = "Unsaved title"; confirm = false; library.Close(); Pump(); check("library drafts: close asks before discarding edits", library.IsVisible && library.HasUnsavedChanges);
            confirm = true; editor.TryDiscard(); library.FocusEntry(snippet.Id); Pump();
            Field("title").Text = "Stale local draft";
            history.Update(snippet.Id, "Launch reply", "Updated in another view", ["team", "reusable"], snippet.Content, DateTimeOffset.UtcNow, out _); Pump();
            check("library drafts: concurrent edits do not silently overwrite another surface", !library.SaveDraft() && library.HasUnsavedChanges && history.Items.Single(item => item.Id == snippet.Id).Notes == "Updated in another view");
            editor.TryDiscard(); library.FocusEntry(snippet.Id); Pump();
            history.Archive(snippet.Id, true); Pump();
            check("library archive: both active surfaces omit archived entries", library.ResultCount == 6 && island.ResultCount == 6);
            library.SetFilter(new ClipboardQuery { Archive = ClipboardArchiveScope.Archived }); island.QueryState = new ClipboardQuery { Archive = ClipboardArchiveScope.Archived }; Pump();
            check("library archive: archived scope is available on both surfaces", library.ResultCount == 1 && island.ResultCount == 1);
            history.Archive(snippet.Id, false); library.SetFilter(new()); island.QueryState = new(); Pump();
            island.QueryState = new ClipboardQuery { Source = "Editor fixture", Kinds = [ClipboardContentKind.Text] }; Pump();
            check("island filters: source and type filters work without the library window", island.ResultCount == 1);
            island.QueryState = new();
            library.FocusEntry(snippet.Id); Pump();
            var row = Descendants<ClipboardLibraryItem>(library).First(item => item.Entry?.Id == snippet.Id);
            Descendants<Button>(row).Single(button => button.Content as string == Loc.T("复制", "Copy")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("library reuse: the copy button makes exactly one copy-only request", recalls == 1 && copyOnly);
            Descendants<Button>(row).Single(button => button.Content as string == Loc.T("回贴", "Paste")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("library reuse: paste is an explicit action, not an incidental selection", recalls == 2 && !copyOnly);
            var filterButton = OpenFilters(island);
            var sourceMenu = filterButton.ContextMenu!.Items.OfType<MenuItem>().First(item => item.HasItems); sourceMenu.IsSubmenuOpen = true; Pump(); sourceMenu.ApplyTemplate();
            check("island filters: native submenu template exposes its popup", sourceMenu.Template.FindName("PART_Popup", sourceMenu) is Popup { IsOpen: true });
            island.ClosePopups(); Pump();
            island.ShowDetails(history.Items.Single(item => item.Id == snippet.Id)); Pump();
            check("island details: editing opens locally instead of requiring a second window", island.PopupOpen);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var islandEditor = (ClipboardEntryEditor)typeof(ClipboardView).GetField("_inspector", flags)!.GetValue(island)!;
            Descendants<TextBox>(islandEditor).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-edit-notes").Text = "Edited from island";
            check("island details: editing saves through the same history owner", islandEditor.Save() && history.Items.Single(item => item.Id == snippet.Id).Notes == "Edited from island");
            island.ClosePopups(); Pump();
            if (renderDirectory is not null) Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-dark.png");
            theme(PanelThemes.Light); library.RefreshTheme(); Pump();
            check("library theme: controls follow the same dynamic palette as the island", Equals(library.Background, Application.Current.Resources["PanelSurface"]) && Equals(Field("title").Foreground, Application.Current.Resources["PanelPrimary"]));
            if (renderDirectory is not null) Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-light.png");
            library.Width = 860; library.Height = 540; Pump();
            check("library layout: compact window gives the timeline space instead of squeezing three columns", list.ActualWidth > 600 && !editor.IsVisible
                && Descendants<TextBox>(library).Where(field => field.IsVisible).All(field => field.ActualWidth > 20));
            if (renderDirectory is not null) Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-compact.png");
            var detailsButton = Descendants<Button>(library).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-library-details");
            detailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            double timelineWidth = list.ActualWidth;
            check("library layout: compact details overlay without shrinking the timeline", editor.IsVisible && editor.ActualWidth > 280
                && list.ActualWidth > 600 && !list.IsEnabled);
            var save = Descendants<Button>(editor).Single(button => button.Style == editor.FindResource("LibraryPrimaryButton"));
            var delete = Descendants<Button>(editor).Single(button => button.Style == editor.FindResource("LibraryDangerButton"));
            check("library actions: themed primary save and separated deletion keep shared feedback", Feedback.GetFade(save) && Feedback.GetFade(delete)
                && Equals(save.BorderBrush, Application.Current.Resources["PanelAccent"]) && Equals(save.Foreground, Application.Current.Resources["PanelPrimary"])
                && VisualTreeHelper.GetParent(save) != VisualTreeHelper.GetParent(delete));
            if (renderDirectory is not null)
            {
                Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-compact-details.png");
                var detailScroll = Descendants<ScrollViewer>(library).Single(scroll => ReferenceEquals(scroll.Content, editor));
                detailScroll.ScrollToBottom(); Pump();
                Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-editor-actions.png");
                detailScroll.ScrollToTop(); Pump();
            }
            Field("title").Text = "Resize draft";
            Descendants<Button>(library).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-library-back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("library drafts: returning to timeline keeps the hidden editor draft", !editor.IsVisible && library.HasUnsavedChanges && list.IsEnabled && Math.Abs(list.ActualWidth - timelineWidth) < 1);
            detailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("library drafts: reopening details restores the same unsaved fields", editor.IsVisible && Field("title").Text == "Resize draft");
            library.Width = 1180; Pump();
            check("library layout: widening restores the persistent inspector without replacing its draft", editor.IsVisible && list.IsEnabled
                && ReferenceEquals(editor, Descendants<ClipboardEntryEditor>(library).Single()) && Field("title").Text == "Resize draft");
            library.Width = 860; Pump();
            check("library drafts: narrowing with a draft keeps the editor open", editor.IsVisible && library.HasUnsavedChanges);
            editor.TryDiscard(); library.FocusEntry(snippet.Id); Pump();
            var preservedPreview = editor.Children.OfType<Border>().First().Child;
            history.ToggleFavorite(snippet.Id); Pump();
            check("library presentation: metadata refresh keeps the same detail preview", ReferenceEquals(preservedPreview, editor.Children.OfType<Border>().First().Child));
            library.Hide(); Pump(); int hiddenCount = library.ResultCount;
            history.Add(ClipboardContent.FromText("Hidden manager update"), "Editor fixture", "", DateTimeOffset.UtcNow, settings); Pump();
            check("library visibility: hidden manager delays history refresh", library.ResultCount == hiddenCount);
            library.Show(); Pump(); check("library visibility: showing applies the coalesced update", library.ResultCount == hiddenCount + 1);
            history.Remove(history.Items.Single(item => item.Content.Text == "Hidden manager update").Id); Pump();
            library.Close(); Pump(); check("library lifetime: closing manager does not close the island", !library.IsVisible && island.IsVisible);
            history.Add(ClipboardContent.FromText("After manager close"), "Editor fixture", "", DateTimeOffset.UtcNow, settings); Pump(); check("library lifetime: capture still updates island after manager closes", island.ResultCount == 8);
        }
        finally { library.ForceClose(); island.QueryState = new(); }
    }
}
