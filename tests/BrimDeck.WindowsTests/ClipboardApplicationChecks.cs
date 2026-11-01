using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckClipboardApplications(ClipboardHistory history, ClipboardSettings settings, ClipboardView island, ClipboardLibraryWindow library,
        Action<string, bool> check, Action<string> theme, string? renderDirectory)
    {
        var snapshot = history.Snapshot();
        var host = Window.GetWindow(island)!; double width = host.Width, height = host.Height;
        Button Find(DependencyObject root, string id) => Descendants<Button>(Navigation(root)).Single(button => AutomationProperties.GetAutomationId(button) == id);
        Button App(DependencyObject root, string name) => Find(root, "clipboard-application-source-" + name);
        Button All(DependencyObject root) => Find(root, "clipboard-application-history");
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        bool Count(Button button, int count) => AutomationProperties.GetName(button).EndsWith(" · " + count, StringComparison.Ordinal);
        try
        {
            island.ApplicationsMode = false; island.QueryState = new(); library.SetFilter(new()); Pump();
            island.CollectionId = "colors"; Pump();
            Click(Find(island, "clipboard-mode-applications"));
            check("clipboard applications: island switches from folders to automatic apps without a hidden collection filter", island.ApplicationsMode
                && island.QueryState.CollectionId == "" && island.QueryState.Source == "" && island.ResultCount == 5
                && Find(island, "clipboard-mode-applications").Tag as string == "Selected");
            check("clipboard applications: automatic mode has its own complete history and source counts", Count(All(island), 5)
                && Count(App(island, "Browser fixture"), 3) && Count(App(island, "Image fixture"), 1) && Count(App(island, "Mail fixture"), 1));
            check("clipboard applications: automatic groups cannot rename provenance or accept collection drops", !App(island, "Browser fixture").AllowDrop && App(island, "Browser fixture").ContextMenu!.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).SequenceEqual(new[] { "clipboard-application-pin", "clipboard-application-hide", "clipboard-application-clear" })
                && !Descendants<Button>(island).Any(button => AutomationProperties.GetAutomationId(button).StartsWith("clipboard-collection-", StringComparison.Ordinal)));
            Click(App(island, "Browser fixture"));
            check("clipboard applications: choosing a source filters only that app", island.ResultCount == 3 && island.QueryState.Source == "Browser fixture"
                && App(island, "Browser fixture").Tag as string == "Selected");
            Click(All(island));
            check("clipboard applications: app-mode history restores entries from every source", island.ResultCount == 5 && island.QueryState.Source == "" && !island.QueryState.UnknownSourceOnly);
            Click(Find(island, "clipboard-mode-collections"));
            Click(Find(island, "clipboard-collection-colors"));
            check("clipboard applications: returning to folders restores existing collection management", !island.ApplicationsMode && island.ResultCount == 1
                && island.QueryState.Source == "" && Find(island, "clipboard-collection-colors").ContextMenu is not null);
            island.QueryState = new ClipboardQuery { Source = "Mail fixture", Text = "#0080FF", Kinds = [ClipboardContentKind.Color] };
            Click(Find(island, "clipboard-mode-collections")); Click(Find(island, "clipboard-mode-applications"));
            check("clipboard applications: switching classification retains search and type choices, not the previous source", island.SearchText == "#0080FF"
                && island.QueryState.Kinds.SequenceEqual(new[] { ClipboardContentKind.Color }) && island.QueryState.Source == "" && island.ResultCount == 1 && Count(All(island), 1));
            island.QueryState = new();
            Click(App(library, "Browser fixture"));
            check("clipboard applications: manager exposes automatic sources separately from folders", library.ResultCount == 3
                && App(library, "Browser fixture").Tag as string == "Selected" && !App(library, "Browser fixture").AllowDrop
                && Find(library, "clipboard-collection-colors").ContextMenu is not null);
            Click(All(library));
            check("clipboard applications: manager's application history also includes every source", library.ResultCount == 5 && Count(All(library), 5));
            Click(Find(library, "clipboard-collection-colors"));
            check("clipboard applications: choosing a manager folder clears an old application selection", library.ResultCount == 1
                && Find(library, "clipboard-collection-colors").Tag as string == "Selected" && App(library, "Browser fixture").Tag is null);
            Click(All(library));
            var selector = Descendants<ComboBox>(library).Single(combo => AutomationProperties.GetName(combo) == Loc.T("按来源应用筛选", "Filter by source application"));
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => item.Tag is ClipboardApplicationGroup { Source: "Browser fixture" }); Pump();
            check("clipboard applications: existing source selector and automatic navigation stay synchronized", library.ResultCount == 3
                && App(library, "Browser fixture").Tag as string == "Selected");
            Click(All(library));
            const string longName = "New application fixture with a long display name";
            var added = history.Add(ClipboardContent.FromText("Automatic source discovery fixture"), longName, "", DateTimeOffset.UtcNow, settings)!;
            var unknown = history.Add(ClipboardContent.FromText("Unknown provenance fixture"), "", "", DateTimeOffset.UtcNow, settings)!; Pump();
            check("clipboard applications: new sources and unknown provenance appear live in both surfaces", Count(App(island, longName), 1)
                && Count(App(library, longName), 1) && Count(All(island), 7) && Count(All(library), 7)
                && Count(Find(island, "clipboard-application-unknown"), 1) && Count(Find(library, "clipboard-application-unknown"), 1));
            Click(Find(island, "clipboard-application-unknown")); Click(Find(library, "clipboard-application-unknown"));
            check("clipboard applications: unknown is a real filter rather than an alias for all apps", island.QueryState.UnknownSourceOnly
                && island.QueryState.Source == "" && island.ResultCount == 1 && library.ResultCount == 1
                && selector.SelectedItem is ComboBoxItem { Tag: ClipboardApplicationGroup { Unknown: true } });
            Click(All(island)); Click(All(library));
            foreach (string palette in new[] { PanelThemes.Dark, PanelThemes.Light })
            {
                theme(palette); library.RefreshTheme(); Pump();
                check("clipboard applications: mode controls reuse shared feedback and native tab height / " + palette, Feedback.GetFade(Find(island, "clipboard-mode-applications"))
                    && Feedback.GetFade(Find(island, "clipboard-mode-collections")) && All(island).ActualHeight == 24 && App(library, longName).ActualHeight == 34);
                if (renderDirectory is not null)
                {
                    string suffix = palette == PanelThemes.Dark ? "dark" : "light";
                    Render((FrameworkElement)host.Content, renderDirectory, "clipboard-applications-" + suffix + ".png");
                    Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-applications-" + suffix + ".png");
                }
            }
            host.Width = 660; Pump();
            // Narrow headers lend the classification slot to an explicitly expanded search; return to browsing first.
            if (Descendants<TextBox>(island).Single().IsVisible) { island.DismissFromKeyboard(); WaitClipboardMotion(Soft.Choose + 60); Pump(); }
            var tabScroll = Descendants<ScrollViewer>(Navigation(island)).Single(scroll => scroll.Content is StackPanel panel
                && panel.Children.OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "clipboard-application-history"));
            tabScroll.ScrollToRightEnd(); Pump();
            check("clipboard applications: many apps scroll while mode controls remain reachable at narrow width", tabScroll.ScrollableWidth > 0
                && tabScroll.HorizontalOffset > 0 && Find(island, "clipboard-mode-applications").IsVisible
                && Find(island, "clipboard-mode-applications").ActualWidth < island.ActualWidth);
            host.Height = 210; Pump();
            island.ClosePopups(); var filter = OpenFilters(island);
            var grouping = filter.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.T("分类视图", "Browse by")));
            grouping.Items.OfType<MenuItem>().Single(item => item.Header.ToString()!.EndsWith(Loc.T("收藏夹", "Collections"), StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); island.ClosePopups(); Pump();
            check("clipboard applications: compact islands can switch classification from the native menu", !island.ApplicationsMode
                && !island.PopupOpen);
            OpenFilters(island); grouping = filter.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.T("分类视图", "Browse by")));
            grouping.Items.OfType<MenuItem>().Single(item => item.Header.ToString()!.EndsWith(Loc.T("应用自动分类", "Applications (automatic)"), StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); island.ClosePopups(); Pump();
            check("clipboard applications: compact mode can switch back to apps with complete history", island.ApplicationsMode && island.ResultCount == 7);
            host.Width = width; host.Height = height; Pump();
            Click(Find(island, "clipboard-application-unknown")); Click(Find(library, "clipboard-application-unknown"));
            history.Remove(unknown.Id); Pump();
            check("clipboard applications: removing the last unknown entry falls back to total history", island.ResultCount == 6 && library.ResultCount == 6
                && !island.QueryState.UnknownSourceOnly && All(island).Tag as string == "Selected" && All(library).Tag as string == "Selected");
            Click(App(island, longName)); Click(App(library, longName)); history.Remove(added.Id); Pump();
            check("clipboard applications: removing an app's final entry cannot strand either surface on a stale filter", island.ResultCount == 5 && library.ResultCount == 5
                && island.QueryState.Source == "" && All(island).Tag as string == "Selected" && All(library).Tag as string == "Selected"
                && !Descendants<Button>(island).Any(button => AutomationProperties.GetAutomationId(button) == "clipboard-application-source-" + longName));
            check("clipboard applications: browsing does not change saved collection order or entry membership", history.Collections.SequenceEqual(snapshot.Collections)
                && history.Items.All(item => snapshot.Items.Single(saved => saved.Id == item.Id).CollectionId == item.CollectionId));
        }
        finally
        {
            island.ClosePopups(); host.Width = width; host.Height = height;
            island.ApplicationsMode = false; island.QueryState = new(); library.SetFilter(new());
            history.Restore(snapshot, settings, DateTimeOffset.UtcNow);
            theme(PanelThemes.Dark); library.RefreshTheme(); Pump();
        }
    }
}
