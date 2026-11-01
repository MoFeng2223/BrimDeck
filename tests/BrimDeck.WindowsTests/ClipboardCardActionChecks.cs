using System.IO;
using System.Reflection;
using System.Text.Json;
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
    private static void CheckCardActions(string directory, Action<string, bool> check, string? renderDirectory)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var app = (App)Application.Current; bool animations = app.Settings.Animations;
        var settings = new ClipboardSettings { RetentionDays = 0, PasteOnActivate = false };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        string file = Path.Combine(directory, "drag-source.txt"); File.WriteAllText(file, "Synthetic original file");
        var older = history.Add(ClipboardContent.FromText("Frequently copied synthetic snippet"), "Fixture", "", now.AddMinutes(-10), settings)!;
        var rich = history.Add(new ClipboardContent { Kind = ClipboardContentKind.RichText, Text = "Synthetic rich\ttext", Html = "<b>Synthetic rich\ttext</b>" }, "Fixture", "", now.AddMinutes(-1), settings)!;
        var link = history.Add(ClipboardContent.FromText("https://example.org/design?utm_source=fixture&keep=1"), "Fixture", "", now.AddMinutes(-2), settings)!;
        var files = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [file] }, "Fixture", "", now.AddMinutes(-3), settings)!;
        var image = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, "Fixture", "", now.AddMinutes(-4), settings)!;
        var color = history.Add(ClipboardContent.FromText("#0080FF"), "Fixture", "", now.AddMinutes(-5), settings)!;
        history.RecordUse(older.Id, now); history.RecordUse(older.Id, now);
        CheckCopyAccounting(history, settings, older.Id, now, check);
        var activations = new List<(ClipboardEntry Entry, bool CopyOnly)>(); var preferences = new List<ClipboardSort>();
        using var view = new ClipboardView(history, () => settings, (entry, copyOnly) => activations.Add((entry, copyOnly)),
            () => { }, () => { }, () => { }, () => (true, false, ""), grid => settings.GridView = grid, () => { }, confirm: _ => true,
            setSort: order => { settings.SortOrder = order; preferences.Add(order); });
        var window = new Window { Content = view, Left = -20000, Top = -20000, Width = 1080, Height = 210, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            app.Settings.Animations = false; window.Show(); Pump();
            var list = Descendants<ListBox>(view).Single();
            ClipboardCard Card(string id) { list.ScrollIntoView(list.Items.OfType<ClipboardEntry>().Single(entry => entry.Id == id)); Pump(); return Descendants<ClipboardCard>(view).Single(card => card.Entry?.Id == id); }
            void Gesture(UIElement element, RoutedEvent routed, MouseButton button = MouseButton.Left) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routed });
            void Click(UIElement element) { Gesture(element, Mouse.PreviewMouseDownEvent); Gesture(element, Mouse.PreviewMouseUpEvent); Pump(); }
            void RightClick(ClipboardCard card) { Gesture(card, Mouse.PreviewMouseDownEvent, MouseButton.Right); Gesture(card, Mouse.MouseUpEvent, MouseButton.Right); Pump(); }
            MenuItem Row(string id) => list.ContextMenu!.Items.OfType<MenuItem>().Single(row => AutomationProperties.GetAutomationId(row) == "clipboard-card-menu-" + id);
            void Choose(string id) { Row(id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(); }
            Click(Card(rich.Id));
            check("card click: copy-only preference applies to a single release", activations.Count == 1 && activations[0].Entry.Id == rich.Id && activations[0].CopyOnly);
            settings.PasteOnActivate = true; Click(Card(rich.Id));
            check("card click: paste preference requests exactly one recall", activations.Count == 2 && !activations[^1].CopyOnly);
            settings.PasteOnActivate = false;
            list.SelectedItem = list.Items.OfType<ClipboardEntry>().Single(entry => entry.Id == rich.Id);
            var focusedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
            check("card keyboard: Enter follows copy-only preference", view.HandleShortcut(Key.Enter, ModifierKeys.None, focusedRow) && activations[^1].CopyOnly);
            settings.PasteOnActivate = true;
            check("card keyboard: Ctrl+Enter always copies only", view.HandleShortcut(Key.Enter, ModifierKeys.Control, focusedRow) && activations[^1].CopyOnly);
            settings.PasteOnActivate = false;
            var card = Card(rich.Id); var button = Descendants<Button>(card).First(); int before = activations.Count;
            Click(button);
            check("EdgeDrop card click: action-button mouse gestures never paste the card body", activations.Count == before);
            Gesture(card, Mouse.PreviewMouseUpEvent);
            check("EdgeDrop card click: release without the same card press is inert", activations.Count == before);
            Gesture(card, Mouse.PreviewMouseDownEvent);
            Gesture(Card(link.Id), Mouse.PreviewMouseUpEvent);
            check("EdgeDrop card click: release on a different card never pastes", activations.Count == before);
            card = Card(rich.Id); Gesture(card, Mouse.PreviewMouseDownEvent);
            var drag = (ClipboardDragDrop)typeof(ClipboardView).GetField("_drag", hidden)!.GetValue(view)!;
            typeof(ClipboardDragDrop).GetField("_pressAt", hidden)!.SetValue(drag, new Point(-100000, -100000));
            Gesture(card, Mouse.PreviewMouseUpEvent);
            check("EdgeDrop card click: a moved or attempted drag never falls through to paste", activations.Count == before);
            view.ShowPreview(rich); Pump(); var floating = app.Windows.OfType<ClipboardPreviewWindow>().Single(value => value.Entry.Id == rich.Id); Click(Card(rich.Id));
            check("EdgeDrop card click: independent preview no longer consumes the original card action", !view.PreviewOpen && floating.IsVisible && activations.Count == before + 1);
            Click(Card(link.Id));
            check("EdgeDrop card click: another card pastes once without closing the detached preview", !view.PreviewOpen && floating.IsVisible && activations.Count == before + 2 && activations[^1].Entry.Id == link.Id);
            floating.CloseImmediately();
            before = activations.Count; RightClick(Card(rich.Id));
            check("EdgeDrop card menu: right click selects the actual card without copying or pasting", list.ContextMenu?.IsOpen == true && (list.SelectedItem as ClipboardEntry)?.Id == rich.Id && activations.Count == before && view.PopupOpen);
            check("EdgeDrop card menu: translated paste, plain, copy, metadata, favorite, preview and delete rows are reachable", new[] { "paste", "plain", "copy", "details", "favorite", "preview", "delete" }.All(id => Row(id).IsVisible));
            Choose("plain");
            check("EdgeDrop card menu: plain paste retains identity but strips rich formats", activations[^1] is { CopyOnly: false, Entry.Content.Kind: ClipboardContentKind.Text, Entry.Content.Html.Length: 0, Entry.Content.Rtf.Length: 0 } && activations[^1].Entry.Id == rich.Id && activations[^1].Entry.Content.Text == rich.Content.Text);
            RightClick(Card(link.Id)); Choose("clean-link");
            check("EdgeDrop card menu: clean link copies offline without replacing original history", activations[^1].CopyOnly && activations[^1].Entry.Content.Text == "https://example.org/design?keep=1" && history.Items.Single(entry => entry.Id == link.Id).Content.Text.Contains("utm_source"));
            RightClick(Card(files.Id)); Choose("paths");
            check("EdgeDrop card menu: file path copy uses original path, not an attachment cache", activations[^1].CopyOnly && activations[^1].Entry.Content.Text == file && File.Exists(file));
            RightClick(Card(image.Id));
            check("EdgeDrop card menu: image exposes explicit Save As without writing a file on browse", Row("save-image").IsVisible); view.ClosePopups();
            RightClick(Card(color.Id)); Choose("color-rgb");
            check("EdgeDrop card menu: upstream color conversion copies RGB without changing color history", activations[^1].CopyOnly && activations[^1].Entry.Content.Text == "rgb(0, 128, 255)" && history.Items.Single(entry => entry.Id == color.Id).Content.Text == "#0080FF");
            before = activations.Count; RightClick(Card(rich.Id)); Click(Card(link.Id));
            check("EdgeDrop card menu: a dismissing click is not a paste through the menu", activations.Count == before); view.ClosePopups();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.Items.OfType<ClipboardEntry>().Single(entry => entry.Id == rich.Id)); list.SelectedItem = row.DataContext;
            check("EdgeDrop card menu: Shift+F10 opens the selected card rather than toolbar actions", view.HandleShortcut(Key.F10, ModifierKeys.Shift, row) && list.ContextMenu?.IsOpen == true && Row("paste").IsVisible); view.ClosePopups();
            CheckHoverPreview(view, list, history, settings, check, () => activations.Count);
            CheckSort(view, history, settings, preferences, check);
            CheckAdaptiveGrid(view, list, history, settings, window, check);
            CheckClipboardNotice(view, list, window, check);
            if (renderDirectory is not null) Render(view, renderDirectory, "clipboard-card-count-sort.png");
            check("EdgeDrop card actions: isolated menu and click tests preserve original file and capture opt-out", File.ReadAllText(file) == "Synthetic original file" && !settings.Enabled);
        }
        finally { view.ClosePopups(); window.Close(); app.Settings.Animations = animations; }
    }
    private static void CheckCopyAccounting(ClipboardHistory history, ClipboardSettings settings, string id, DateTimeOffset now, Action<string, bool> check)
    {
        var before = history.Items.Single(entry => entry.Id == id);
        var duplicate = history.Add(before.Content, "Fixture", "", now.AddMinutes(-10), settings)!;
        check("EdgeDrop hitCount: duplicate capture preserves identity and increments exactly once", duplicate.Id == id && duplicate.CopyCount == 4 && duplicate.UseCount == 2 && history.Items.Count == 6);
        var snapshot = JsonSerializer.Deserialize<ClipboardSnapshot>(JsonSerializer.Serialize(history.Snapshot()))!;
        var restored = new ClipboardHistory(); restored.Restore(snapshot, settings, now);
        check("EdgeDrop hitCount: repeat count survives snapshot storage", restored.Items.Single(entry => entry.Id == id).CopyCount == 4 && snapshot.Version == 4);
        restored.Restore(new ClipboardSnapshot { Version = 3, Items = [before with { UseCount = 2 }] }, settings, now);
        check("EdgeDrop hitCount: old snapshots recover only first capture plus known uses", restored.Items.Single().CopyCount == 3);
        restored.Restore(new ClipboardSnapshot { Items = [before with { CopyCount = -1 }] }, settings, now);
        check("EdgeDrop hitCount: invalid stored counters normalize at the storage boundary", restored.Items.Single().CopyCount == 1);
        check("EdgeDrop hitCount: accounting does not rewrite capture time or retention policy", before.CopiedAt == now.AddMinutes(-10) && before.LastUsedAt == now && settings.RetentionDays == 0);
        var copy = settings.Copy(); copy.SortOrder = ClipboardSort.Count;
        var roundTrip = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(copy))!; roundTrip.Normalize();
        check("EdgeDrop sorting: preference is independently copied and survives normal settings storage", settings.SortOrder == ClipboardSort.Time && roundTrip.SortOrder == ClipboardSort.Count);
        roundTrip.SortOrder = (ClipboardSort)99; roundTrip.Normalize();
        check("EdgeDrop sorting: unknown persisted sort returns to newest time", roundTrip.SortOrder == ClipboardSort.Time);
        var grouped = history.Query(new ClipboardQuery { Sort = ClipboardSort.Count, Source = "Fixture", Kinds = [ClipboardContentKind.Text] }).ToList();
        check("EdgeDrop sorting: count order composes with existing filters without mutating canonical history", grouped.First().Id == id && history.Query().First().Id != id);
    }
    private static void CheckSort(ClipboardView view, ClipboardHistory history, ClipboardSettings settings, List<ClipboardSort> preferences, Action<string, bool> check)
    {
        var app = (App)Application.Current;
        var list = Descendants<ListBox>(view).Single(); var sort = Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-sort");
        var actions = (StackPanel)sort.Parent;
        check("EdgeDrop sorting: new button is immediately left of settings", actions.Children.IndexOf(sort) + 1 == actions.Children.IndexOf(Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-settings")));
        void Choose(string id)
        {
            sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            sort.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-sort-" + id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        }
        string repeated = history.Items.OrderByDescending(entry => entry.CopyCount).First().Id;
        app.Settings.Animations = true; list.SelectedItem = history.Items.First(entry => entry.Content.Kind == ClipboardContentKind.RichText); list.ScrollIntoView(list.SelectedItem); Pump();
        var state = view.CapturePresentation(); Choose("count");
        check("EdgeDrop sorting: count ranks repeats first and persists the chosen mode", ((ClipboardEntry)list.Items[0]).Id == repeated && view.QueryState.Sort == ClipboardSort.Count && settings.SortOrder == ClipboardSort.Count && preferences.SequenceEqual(new[] { ClipboardSort.Count }));
        check("EdgeDrop sorting: reorder preserves query, selection and scroll state", view.CapturePresentation() == state && view.QueryState.Text == "");
        if (Soft.Enabled)
        {
            var moving = Descendants<ListBoxItem>(view).Where(row => row.RenderTransform is TranslateTransform transform && transform.HasAnimatedProperties).ToArray();
            check("EdgeDrop sorting: stable keyed rows use the shared movement animation", moving.Length > 0 && !list.HasAnimatedProperties);
            WaitClipboardMotion(Soft.Move + 60);
            check("EdgeDrop sorting: reordered cards settle at exact slots", moving.All(row => row.RenderTransform.Value.OffsetX == 0 && row.RenderTransform.Value.OffsetY == 0));
        }
        app.Settings.Animations = false; Choose("time");
        check("EdgeDrop sorting: disabled motion returns immediately to newest copy order", list.Items.OfType<ClipboardEntry>().Select(entry => entry.Id).SequenceEqual(history.Query().Select(entry => entry.Id)) && Descendants<ListBoxItem>(view).All(row => row.RenderTransform is not TranslateTransform transform || !transform.HasAnimatedProperties));
        view.QueryState = new ClipboardQuery { Sort = ClipboardSort.Count }; Pump();
        var first = Descendants<ClipboardCard>(view).First(card => card.Entry?.Id == repeated);
        var count = Descendants<TextBlock>(first).Single(text => AutomationProperties.GetAutomationId(text) == "clipboard-card-copy-count");
        check("EdgeDrop hitCount: duplicate card shows trailing multiplication count", count.IsVisible && count.Text == "×4" && count.TranslatePoint(new Point(), first).X + count.ActualWidth > first.ActualWidth - 20);
        var marker = new ClipboardDragDrop(history).EntryData(repeated);
        var target = Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-collection-prompts");
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([marker, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, target, new Point(1, 1)]); args.RoutedEvent = DragDrop.DropEvent;
        target.RaiseEvent(args); Pump();
        check("EdgeDrop hitCount: internal organization does not masquerade as another copy", args.Effects == DragDropEffects.Copy && history.Items.Single(entry => entry.Id == repeated).CopyCount == 4);
        var snapshot = history.Snapshot();
        history.Restore(snapshot with { Items = snapshot.Items.Select((entry, index) => entry with { CopiedAt = DateTimeOffset.UtcNow.AddDays(-index) }).ToList() }, settings, DateTimeOffset.UtcNow);
        settings.GridView = true; view.ApplyLayout(); Pump();
        var gridCards = Descendants<ClipboardCard>(view).OrderBy(card => card.TranslatePoint(new Point(), view).X).ToArray();
        check("EdgeDrop sorting: count-ordered grid fills its first row across capture dates", gridCards.Count(card => Math.Abs(card.TranslatePoint(new Point(), view).Y - gridCards[0].TranslatePoint(new Point(), view).Y) < 1) == 4);
        settings.GridView = false; view.ApplyLayout(); Pump();
    }
}
