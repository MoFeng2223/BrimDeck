using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckClipboardOrganization(ClipboardHistory history, ClipboardView island, ClipboardLibraryWindow library,
        Action<string, bool> check, Action<bool> confirmation, string? renderDirectory)
    {
        var snapshot = history.Snapshot();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var drag = (ClipboardDragDrop)typeof(ClipboardView).GetField("_drag", flags)!.GetValue(island)!;
        Button Find(DependencyObject root, string id) => Descendants<Button>(Navigation(root)).Single(button => AutomationProperties.GetAutomationId(button) == id);
        Button Category(DependencyObject root, string id) => Find(root, "clipboard-collection-" + id);
        string[] Order(DependencyObject root) => Descendants<Button>(Navigation(root)).Select(AutomationProperties.GetAutomationId)
            .Where(id => id.StartsWith("clipboard-collection-", StringComparison.Ordinal)).Select(id => id[21..]).ToArray();
        void ClickMenu(Button button, string text)
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent }); Pump();
            check("clipboard organization: right-click opens the native collection menu", button.ContextMenu?.IsOpen == true);
            var menu = button.ContextMenu!;
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, text)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false; Pump();
        }
        try
        {
            string[] initial = history.Collections.Select(group => group.Id).ToArray();
            check("clipboard organization: both surfaces expose all collection drag targets", Order(island).SequenceEqual(initial) && Order(library).SequenceEqual(initial));
            var prompts = Category(island, "prompts");
            check("clipboard organization: both surfaces remove the standalone rename entry", new[] { prompts, Category(library, "prompts") }.All(button => button.ContextMenu!.Items.OfType<MenuItem>().All(item => !Equals(item.Header, Loc.T("重命名分类…", "Rename collection…")))));
            ClickMenu(prompts, Loc.T("名称与标签外观…", "Name & label appearance…"));
            var popup = (Popup)typeof(ClipboardView).GetField("_editor", flags)!.GetValue(island)!;
            var field = Descendants<TextBox>(popup.Child).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-appearance-name");
            var save = Find(popup.Child, "clipboard-appearance-save");
            field.Text = " "; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("clipboard organization: invalid rename stays open without changing data", popup.IsOpen && history.Collections.Single(group => group.Id == "prompts").Name == "Prompts");
            field.Text = "Saved replies"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            check("clipboard organization: island rename updates the library immediately", !popup.IsOpen && history.Collections.Single(group => group.Id == "prompts").Name == "Saved replies"
                && AutomationProperties.GetName(Category(library, "prompts")).StartsWith("Saved replies", StringComparison.Ordinal));
            ClickMenu(Category(library, "prompts"), Loc.T("名称与标签外观…", "Name & label appearance…"));
            var libraryPopup = (Popup)typeof(ClipboardLibraryWindow).GetField("_appearancePopup", flags)!.GetValue(library)!;
            var libraryName = Descendants<TextBox>(libraryPopup.Child).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-appearance-name");
            libraryName.Text = "Reusable replies";
            libraryName.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(libraryName)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
            check("clipboard organization: manager rename works with Enter and synchronizes island tabs", AutomationProperties.GetName(Category(island, "prompts")) == "Reusable replies");
            var entry = history.Query().First(item => item.Content.Kind == ClipboardContentKind.Text);
            var data = drag.EntryData(entry.Id); int before = history.Items.Count;
            check("clipboard organization: internal drags contain only inert scoped ids, never copied content or file payloads", !data.GetDataPresent(DataFormats.UnicodeText) && !data.GetDataPresent(DataFormats.FileDrop)
                && data.GetData(data.GetFormats(false).Single(), false) is string encoded && encoded.EndsWith(entry.Id, StringComparison.Ordinal) && !encoded.Contains(entry.Content.Text, StringComparison.Ordinal));
            var assets = Category(island, "assets");
            var over = DragEvent(assets, data, DragDrop.DragOverEvent); assets.RaiseEvent(over); Pump();
            check("clipboard organization: entry drag previews a themed target without moving yet", over.Effects == DragDropEffects.Move && ClipboardDragDrop.GetHint(assets) == "Into"
                && Equals(assets.BorderBrush, Application.Current.Resources["PanelAccent"]) && history.Items.Single(item => item.Id == entry.Id).CollectionId == "");
            if (renderDirectory is not null) Render((FrameworkElement)Window.GetWindow(island)!.Content, renderDirectory, "clipboard-island-collection-drop.png");
            assets.RaiseEvent(DragEvent(assets, data, DragDrop.DragLeaveEvent));
            check("clipboard organization: leaving or cancelling clears the target highlight", ClipboardDragDrop.GetHint(assets) == "");
            var drop = DragEvent(assets, data, DragDrop.DropEvent); assets.RaiseEvent(drop); Pump();
            check("clipboard organization: history entries drop into an island collection without recall or duplication", drop.Effects == DragDropEffects.Move
                && history.Items.Count == before && history.Items.Single(item => item.Id == entry.Id).CollectionId == "assets");
            island.CollectionId = "assets"; library.SetFilter(new ClipboardQuery { CollectionId = "assets" }); Pump();
            check("clipboard organization: membership and counts synchronize across both views", island.ResultCount == library.ResultCount && island.ResultCount >= 1);
            var target = Category(library, "inspirations"); target.RaiseEvent(DragEvent(target, drag.EntryData(entry.Id), DragDrop.DropEvent)); Pump();
            check("clipboard organization: collection entries move to another collection across surfaces", history.Items.Count == before
                && history.Items.Single(item => item.Id == entry.Id).CollectionId == "inspirations" && !history.Query(collectionId: "assets").Any(item => item.Id == entry.Id));
            var favorites = Find(library, "clipboard-drop-favorites"); favorites.RaiseEvent(DragEvent(favorites, drag.EntryData(entry.Id), DragDrop.DropEvent)); Pump();
            check("clipboard organization: favorite drop preserves the existing collection", history.Items.Single(item => item.Id == entry.Id) is { Favorite: true, CollectionId: "inspirations" });
            var home = Find(island, "clipboard-drop-history"); home.RaiseEvent(DragEvent(home, drag.EntryData(entry.Id), DragDrop.DropEvent)); Pump();
            check("clipboard organization: history drop restores automatic membership without changing capture or favorites", history.Items.Single(item => item.Id == entry.Id) is { Favorite: true, CollectionId: "" } moved
                && moved.Content == entry.Content && moved.CopiedAt == entry.CopiedAt && moved.UseCount == entry.UseCount);
            var colors = Category(island, "colors");
            var reorder = DragEvent(colors, drag.CollectionData("inspirations"), DragDrop.DragOverEvent); colors.RaiseEvent(reorder); Pump(); colors.ApplyTemplate();
            check("clipboard organization: horizontal reordering shows its insertion edge", ClipboardDragDrop.GetHint(colors) == "Before"
                && colors.Template.FindName("DropBefore", colors) is Border { Visibility: Visibility.Visible } && Feedback.GetFade(colors));
            colors.RaiseEvent(DragEvent(colors, drag.CollectionData("inspirations"), DragDrop.DropEvent)); Pump();
            check("clipboard organization: dragging a tab changes both canonical orders", history.Collections.Select(group => group.Id).SequenceEqual(new[] { "prompts", "inspirations", "colors", "assets" })
                && Order(island).SequenceEqual(Order(library)));
            var last = Category(library, "assets");
            last.RaiseEvent(DragEvent(last, drag.CollectionData("prompts"), DragDrop.DragOverEvent, after: true)); Pump(); last.ApplyTemplate();
            check("clipboard organization: vertical reordering shows the lower insertion edge", ClipboardDragDrop.GetHint(last) == "After"
                && last.Template.FindName("DropAfter", last) is Border { Visibility: Visibility.Visible });
            if (renderDirectory is not null) Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-collection-reorder.png");
            last.RaiseEvent(DragEvent(last, drag.CollectionData("prompts"), DragDrop.DropEvent, after: true)); Pump();
            check("clipboard organization: arbitrary reverse reorder persists beyond the filtered view", history.Collections.Select(group => group.Id).SequenceEqual(new[] { "inspirations", "colors", "assets", "prompts" })
                && Order(island).SequenceEqual(Order(library)) && history.Snapshot().Collections.SequenceEqual(history.Collections));
            string[] ordered = history.Collections.Select(group => group.Id).ToArray();
            var invalid = Category(island, "colors");
            var foreign = new ClipboardDragDrop(new ClipboardHistory());
            var rejected = DragEvent(invalid, foreign.CollectionData("assets"), DragDrop.DropEvent); invalid.RaiseEvent(rejected);
            var external = new DataObject(); external.SetText("Do not import"); var externalDrop = DragEvent(invalid, external, DragDrop.DropEvent); invalid.RaiseEvent(externalDrop);
            var stale = drag.CollectionData("missing"); var staleDrop = DragEvent(invalid, stale, DragDrop.DropEvent); invalid.RaiseEvent(staleDrop);
            check("clipboard organization: foreign, stale and external drags never mutate the library", rejected.Effects == DragDropEffects.None && externalDrop.Effects == DragDropEffects.None
                && staleDrop.Effects == DragDropEffects.None && history.Items.Count == before && history.Collections.Select(group => group.Id).SequenceEqual(ordered));
            library.SetFilter(new()); library.FocusEntry(entry.Id); Pump();
            var editor = Descendants<ClipboardEntryEditor>(library).Single();
            var draftTitle = Descendants<TextBox>(editor).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-edit-title");
            draftTitle.Text = "Draft kept during organization";
            var draftCollection = Descendants<ComboBox>(editor).Single();
            var displayed = history.Collections.ToList(); int promptChoice = displayed.FindIndex(group => group.Id == "prompts") + 1;
            history.ReorderCollection("prompts", "inspirations"); Pump();
            draftCollection.SelectedIndex = promptChoice; Pump();
            check("clipboard organization: sorting elsewhere keeps dirty editor choices bound to stable category ids", library.HasUnsavedChanges
                && draftTitle.Text == "Draft kept during organization" && history.Items.Single(item => item.Id == entry.Id).CollectionId == "prompts");
            editor.TryDiscard(); history.Move(entry.Id, ""); library.SetFilter(new()); Pump();
            confirmation(false); ClickMenu(Category(library, "assets"), Loc.T("删除分类（保留记录）…", "Delete collection (keep entries)…"));
            check("clipboard organization: declining deletion leaves the category intact", history.Collections.Any(group => group.Id == "assets"));
            confirmation(true); island.CollectionId = "assets"; library.SetFilter(new ClipboardQuery { CollectionId = "assets" }); Pump();
            var photo = history.Items.First(item => item.Content.Kind == ClipboardContentKind.Image); history.Move(photo.Id, "assets"); Pump();
            ClickMenu(Category(island, "assets"), Loc.T("删除分类（保留记录）…", "Delete collection (keep entries)…"));
            check("clipboard organization: deleting a selected starter restores history in both surfaces", !history.Collections.Any(group => group.Id == "assets")
                && history.Items.Count == before && history.Items.Single(item => item.Id == photo.Id).CollectionId == "" && island.CollectionId == ""
                && island.ResultCount == before && library.ResultCount == before);
        }
        finally
        {
            confirmation(true); island.ClosePopups();
            history.Restore(snapshot, new ClipboardSettings { RetentionDays = 0 }, DateTimeOffset.UtcNow);
            island.QueryState = new(); library.SetFilter(new()); Pump();
        }
    }
    private static DragEventArgs DragEvent(Button target, IDataObject data, RoutedEvent routedEvent, bool after = false)
    {
        var point = after ? new Point(Math.Max(1, target.ActualWidth - 1), Math.Max(1, target.ActualHeight - 1)) : new Point(1, 1);
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move | DragDropEffects.Link, target, point]);
        args.RoutedEvent = routedEvent; return args;
    }
}
