using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;
using Path = System.Windows.Shapes.Path;

internal static partial class ClipboardChecks
{
    private static void CheckClipboardAppearance(ClipboardHistory history, ClipboardView island, ClipboardLibraryWindow library, Action<string, bool> check, Action<string> theme, string? renderDirectory)
    {
        var snapshot = history.Snapshot();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Button Button(DependencyObject root, string id) => Descendants<Button>(Navigation(root)).Single(button => AutomationProperties.GetAutomationId(button) == id);
        Button Category(DependencyObject root, string id) => Button(root, "clipboard-collection-" + id);
        void Click(DependencyObject root, string id) { Button(root, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(); }
        Popup Open(bool fromIsland, string id = "prompts")
        {
            // History notifications coalesce on the dispatcher before new navigation targets are realized.
            Pump();
            var button = Category(fromIsland ? island : library, id); var menu = button.ContextMenu!;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent }); Pump();
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.T("名称与标签外观…", "Name & label appearance…"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false; Pump();
            return (Popup)(fromIsland ? typeof(ClipboardView).GetField("_editor", flags)!.GetValue(island)! : typeof(ClipboardLibraryWindow).GetField("_appearancePopup", flags)!.GetValue(library)!);
        }
        static double Contrast(Button button)
        {
            if (button.Background is not SolidColorBrush fill || button.Foreground is not SolidColorBrush ink) return 0;
            static double L(Color color)
            {
                static double C(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
                return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
            }
            return (Math.Max(L(fill.Color), L(ink.Color)) + .05) / (Math.Min(L(fill.Color), L(ink.Color)) + .05);
        }
        try
        {
            check("clipboard appearance: untouched collections keep their original native label background", !ClipboardCollectionVisuals.GetHasColor(Category(island, "prompts"))
                && Equals(Category(island, "prompts").Background, Application.Current.Resources["PanelSwitchFill"]) && !Descendants<Path>(Category(island, "prompts")).Any()
                && Category(library, "prompts").Background == Brushes.Transparent);
            var popup = Open(true); var editor = popup.Child;
            check("clipboard appearance: island picker opens locally with the common popup motion and scaled content", popup.IsOpen && island.PopupOpen
                && editor.RenderTransform is TranslateTransform && editor is FrameworkElement { LayoutTransform: ScaleTransform scale } && scale.ScaleX > 1);
            Descendants<TextBox>(editor).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-appearance-color").Text = "#DCEBFF"; Click(editor, "clipboard-appearance-icon-star");
            var preview = Button(editor, "clipboard-appearance-preview");
            check("clipboard appearance: choosing color and icon previews without committing", history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "", Icon: "" }
                && ClipboardCollectionVisuals.GetColor(preview) == "#DCEBFF" && Descendants<Path>(preview).Count() == 1 && Contrast(preview) >= 4.5);
            var popupRoot = (FrameworkElement)PresentationSource.FromVisual(editor)!.RootVisual;
            var saveBounds = Button(editor, "clipboard-appearance-save").TransformToAncestor(popupRoot).TransformBounds(new Rect(Button(editor, "clipboard-appearance-save").RenderSize));
            check("clipboard appearance: scaled picker keeps all save controls inside its real popup surface", popupRoot.ActualWidth >= editor.RenderSize.Width && saveBounds.Bottom <= popupRoot.ActualHeight + 1 && saveBounds.Right <= popupRoot.ActualWidth + 1);
            if (renderDirectory is not null) Render(popupRoot, renderDirectory, "clipboard-appearance-picker-dark.png");
            Click(editor, "clipboard-appearance-cancel"); Pump();
            check("clipboard appearance: cancelling keeps both surfaces unchanged", !popup.IsOpen && !ClipboardCollectionVisuals.GetHasColor(Category(island, "prompts")));
            popup = Open(true); editor = popup.Child;
            Descendants<TextBox>(editor).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-appearance-color").Text = "#DCEBFF"; Click(editor, "clipboard-appearance-icon-star"); Click(editor, "clipboard-appearance-save"); Pump();
            check("clipboard appearance: saving in island synchronizes fill and vector icon to manager", !popup.IsOpen && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "#DCEBFF", Icon: "star" }
                && Category(island, "prompts").Background is SolidColorBrush islandFill && Category(library, "prompts").Background is SolidColorBrush libraryFill && islandFill.Color == libraryFill.Color
                && Descendants<Path>(Category(island, "prompts")).Count() == 1
                && Descendants<Path>(Category(library, "prompts")).Count() == 1 && Contrast(Category(island, "prompts")) >= 4.5 && Contrast(Category(library, "prompts")) >= 4.5);
            popup = Open(false); editor = popup.Child;
            var hex = Descendants<TextBox>(editor).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-appearance-color"); hex.Text = "invalid"; Click(editor, "clipboard-appearance-save");
            check("clipboard appearance: invalid custom input stays open and never partially saves", popup.IsOpen && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "#DCEBFF", Icon: "star" });
            hex.Text = "#123456"; Click(editor, "clipboard-appearance-icon-code"); Click(editor, "clipboard-appearance-save");
            check("clipboard appearance: manager supports custom HEX and synchronizes dark backgrounds with readable ink", !popup.IsOpen && history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "#123456", Icon: "code" }
                && Category(island, "prompts").Foreground == Brushes.White && Contrast(Category(island, "prompts")) >= 4.5 && Contrast(Category(library, "prompts")) >= 4.5);
            history.SetCollectionAppearance("colors", "#FFF0C2", "palette"); history.SetCollectionAppearance("assets", "#D9F2DE", "image"); Pump();
            island.CollectionId = "prompts"; library.SetFilter(new ClipboardQuery { CollectionId = "prompts" }); Pump();
            var selected = Category(island, "prompts"); selected.ApplyTemplate(); var nav = Category(library, "prompts"); nav.ApplyTemplate();
            check("clipboard appearance: selected colored labels retain their tint and show a distinct native selection outline", ClipboardCollectionVisuals.GetColor(selected) == "#123456"
                && selected.Template.FindName("ColorSelection", selected) is Border { Visibility: Visibility.Visible } && nav.Template.FindName("ColorSelection", nav) is Border { Visibility: Visibility.Visible }
                && Feedback.GetFade(selected) && Feedback.GetFade(nav));
            var drag = (ClipboardDragDrop)typeof(ClipboardView).GetField("_drag", flags)!.GetValue(island)!;
            var entry = history.Items.First(item => item.CollectionId == "");
            var over = DragEvent(selected, drag.EntryData(entry.Id), DragDrop.DragOverEvent); selected.RaiseEvent(over); Pump();
            check("clipboard appearance: colored labels preserve the drag destination feedback", over.Effects == DragDropEffects.Move && ClipboardDragDrop.GetHint(selected) == "Into"
                && Equals(selected.BorderBrush, Application.Current.Resources["PanelAccent"]));
            selected.RaiseEvent(DragEvent(selected, drag.EntryData(entry.Id), DragDrop.DragLeaveEvent));
            var pale = Category(island, "colors"); pale.ApplyTemplate();
            pale.RaiseEvent(DragEvent(pale, drag.EntryData(entry.Id), DragDrop.DragOverEvent)); Pump();
            check("clipboard appearance: pale colored targets retain contrast and feedback during a drag", Contrast(pale) >= 4.5
                && pale.Background is SolidColorBrush { Color: var paleColor } && paleColor == (Color)ColorConverter.ConvertFromString("#FFF0C2")
                && pale.Template.FindName("CustomFill", pale) is Border { Visibility: Visibility.Visible } && pale.Template.FindName("Chrome", pale) is Border { Background: var chrome } && chrome == Brushes.Transparent);
            pale.RaiseEvent(DragEvent(pale, drag.EntryData(entry.Id), DragDrop.DragLeaveEvent));
            island.CollectionId = ""; library.SetFilter(new()); Pump();
            if (renderDirectory is not null)
            {
                Render((FrameworkElement)Window.GetWindow(island)!.Content, renderDirectory, "clipboard-collections-dark.png");
                Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-collections-dark.png");
            }
            theme(PanelThemes.Light); library.RefreshTheme(); Pump();
            check("clipboard appearance: color and icon survive theme changes with readable text and badges", Contrast(Category(island, "prompts")) >= 4.5 && Contrast(Category(library, "colors")) >= 4.5
                && Descendants<TextBlock>(Category(library, "colors")).All(text => Equals(text.Foreground, Category(library, "colors").Foreground))
                && !ClipboardCollectionVisuals.GetHasColor(Category(island, "inspirations")));
            if (renderDirectory is not null)
            {
                Render((FrameworkElement)Window.GetWindow(island)!.Content, renderDirectory, "clipboard-collections-light.png");
                Render((FrameworkElement)library.Content, renderDirectory, "clipboard-library-collections-light.png");
            }
            popup = Open(false); editor = popup.Child; Click(editor, "clipboard-appearance-color-default"); Click(editor, "clipboard-appearance-save");
            check("clipboard appearance: default background can keep the chosen icon", history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "", Icon: "code" }
                && !ClipboardCollectionVisuals.GetHasColor(Category(island, "prompts")) && Descendants<Path>(Category(island, "prompts")).Count() == 1);
            popup = Open(true); editor = popup.Child; Click(editor, "clipboard-appearance-reset"); Click(editor, "clipboard-appearance-save");
            check("clipboard appearance: full reset restores the original label style in both surfaces", history.Collections.Single(group => group.Id == "prompts") is { BackgroundColor: "", Icon: "" }
                && !Descendants<Path>(Category(island, "prompts")).Any() && Equals(Category(island, "prompts").Background, Application.Current.Resources["PanelSwitchFill"])
                && Category(library, "prompts").Background == Brushes.Transparent);
            var temporary = history.AddCollection("Removed while styling")!;
            popup = Open(false, temporary.Id); editor = popup.Child; history.RemoveCollection(temporary.Id); Click(editor, "clipboard-appearance-save");
            check("clipboard appearance: removed targets never recreate a collection or lose the local draft", popup.IsOpen && !history.Collections.Any(group => group.Id == temporary.Id));
            popup.IsOpen = false;
        }
        finally
        {
            island.ClosePopups();
            if (typeof(ClipboardLibraryWindow).GetField("_appearancePopup", flags)!.GetValue(library) is Popup opened) opened.IsOpen = false;
            history.Restore(snapshot, new ClipboardSettings { RetentionDays = 0 }, DateTimeOffset.UtcNow); island.QueryState = new(); library.SetFilter(new());
            theme(PanelThemes.Dark); library.RefreshTheme(); Pump();
        }
    }
}
