using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    internal static void RunToolbar(Action<string, bool> check)
    {
        PrepareClipboardResources(); ClipboardHistoryTests.Run(check);
        var settings = new ClipboardSettings { RetentionDays = 0 };
        var history = new ClipboardHistory(); var now = DateTimeOffset.UtcNow;
        history.Add(ClipboardContent.FromText("Synthetic browser entry"), "Browser fixture", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic editor entry"), "Editor fixture", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic mail entry"), "Mail fixture", "", now, settings);
        history.Add(ClipboardContent.FromText("Synthetic unknown entry"), "", "", now, settings);
        int activations = 0, settingsOpened = 0, collapsed = 0, commits = 0;
        using var view = new ClipboardView(history, () => settings, (_, _) => activations++, () => settingsOpened++, () => { }, () => { },
            () => (true, false, ""), grid => settings.GridView = grid, () => collapsed++,
            setApplicationPinned: (source, pinned) => { settings.SetApplicationPinned(source, pinned); commits++; });
        var window = new Window { Content = view, Width = 1320, Height = 210, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        int hostWheels = 0, hostRights = 0;
        window.MouseWheel += (_, e) => { hostWheels++; e.Handled = true; };
        window.MouseRightButtonUp += (_, e) => { hostRights++; e.Handled = true; };
        Button Button(string id) => Descendants<Button>(view).Single(button => AutomationProperties.GetAutomationId(button) == id);
        Button App(string source) => Button(source.Length == 0 ? "clipboard-application-unknown" : "clipboard-application-source-" + source);
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(); }
        void Right(UIElement target) { target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent }); Pump(); }
        MouseWheelEventArgs Wheel(UIElement target, int delta)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            target.RaiseEvent(e); e.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(e); Pump(); return e;
        }
        string[] Order() => ((StackPanel)typeof(ClipboardView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Children.OfType<Button>().Select(AutomationProperties.GetAutomationId).ToArray();
        void Pin(string source)
        {
            var tab = App(source); int before = hostRights; Right(tab);
            check("toolbar pins: app right-click isolates its pin action from host settings / " + source, tab.ContextMenu?.IsOpen == true && hostRights == before
                && tab.ContextMenu.Items.OfType<MenuItem>().Select(AutomationProperties.GetAutomationId).SequenceEqual(new[] { "clipboard-application-pin", "clipboard-application-hide", "clipboard-application-clear" }));
            tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-application-pin").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        }
        try
        {
            window.Show(); view.ApplicationsMode = true; Pump();
            check("toolbar pins: all history stays first and automatic sources start unpinned", Order()[0] == "clipboard-application-history" && settings.PinnedApplications.Length == 0);
            Click(App("Browser fixture")); var query = view.QueryState; var state = view.CapturePresentation();
            Pin("Mail fixture"); Pin("Browser fixture");
            check("toolbar pins: latest pin leads applications behind all history", Order().Take(3).SequenceEqual(new[] { "clipboard-application-history", "clipboard-application-source-Browser fixture", "clipboard-application-source-Mail fixture" })
                && AutomationProperties.GetItemStatus(App("Browser fixture")) == Loc.T("已固定", "Pinned") && commits == 2);
            check("toolbar pins: pinning preserves source query, selected entry and history provenance", view.QueryState == query && view.CapturePresentation() == state && history.Items.Single(item => item.SourceName == "Browser fixture").CollectionId == "" && view.ResultCount == 1);
            var restored = JsonSerializer.Deserialize<ClipboardSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
            settings = restored; view.Refresh(); Pump();
            check("toolbar pins: normal preference reload restores pin order and permits unpin", Order()[1] == "clipboard-application-source-Browser fixture" && Equals(App("Browser fixture").ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-application-pin").Header, Loc.T("取消固定", "Unpin")));
            Pin("Browser fixture");
            check("toolbar pins: unpin restores the source to automatic order", Order().Skip(1).Take(3).SequenceEqual(new[] { "clipboard-application-source-Mail fixture", "clipboard-application-source-Browser fixture", "clipboard-application-source-Editor fixture" }) && !settings.IsApplicationPinned("Browser fixture"));
            Pin("");
            check("toolbar pins: unknown provenance can pin without confusing the history tab", Order()[0] == "clipboard-application-history" && Order()[1] == "clipboard-application-unknown" && Button("clipboard-application-history").ContextMenu is null);
            var entry = history.Items.Single(item => item.SourceName == "Mail fixture"); history.Remove(entry.Id); Pump();
            check("toolbar pins: a missing pinned source stays a preference, not a fabricated group", settings.IsApplicationPinned("Mail fixture") && !Order().Contains("clipboard-application-source-Mail fixture"));
            history.Add(entry.Content, entry.SourceName, "", now, settings); Pump();
            check("toolbar pins: a returning source recovers its saved pin", Order()[2] == "clipboard-application-source-Mail fixture");
            view.ApplicationsMode = false; Pump();
            var collection = Button("clipboard-collection-colors"); int rights = hostRights; Right(collection);
            check("toolbar right-click: collections retain rename and organizing instead of opening host settings", collection.ContextMenu?.IsOpen == true && hostRights == rights && collection.AllowDrop); view.ClosePopups(); Pump();
            view.ApplicationsMode = true; view.QueryState = new(); Pump();
            var scroll = Descendants<ScrollViewer>(view).Single(item => AutomationProperties.GetAutomationId(item) == "clipboard-classification-scroll");
            check("toolbar wheel: roomy application navigation has no horizontal overflow", scroll.ScrollableWidth <= .5);
            int wheels = hostWheels; Wheel(App("Browser fixture"), -120);
            check("toolbar wheel: nonoverflowing ScrollViewer forwards exactly one original wheel to host", hostWheels == wheels + 1 && scroll.HorizontalOffset == 0);
            foreach (double width in new[] { 480d, 660, 880, 1080, 1320, 660, 1320 })
            {
                window.Width = width; Pump(); bool overflow = scroll.IsVisible && scroll.ViewportWidth > 0 && scroll.ScrollableWidth > .5;
                scroll.ScrollToLeftEnd(); Pump(); wheels = hostWheels; Wheel(scroll, -120);
                check("toolbar wheel: current layout alone owns the wheel at width " + width, overflow ? hostWheels == wheels && scroll.HorizontalOffset > 0 : hostWheels == wheels + 1);
                if (overflow)
                {
                    scroll.ScrollToRightEnd(); Pump(); wheels = hostWheels; Wheel(scroll, -120);
                    check("toolbar wheel: overflow at end never leaks a page switch at width " + width, hostWheels == wheels);
                }
            }
            view.ApplicationsMode = false; Pump();
            foreach (double width in new[] { 660d, 1080, 1320 })
            {
                window.Width = width; Pump(); bool overflow = scroll.IsVisible && scroll.ViewportWidth > 0 && scroll.ScrollableWidth > .5;
                wheels = hostWheels; Wheel(Button("clipboard-collection-colors"), -120);
                check("toolbar wheel: collection tags use their current content width / " + width, hostWheels == wheels + (overflow ? 0 : 1));
            }
            window.Width = 660; Click(Button("clipboard-search-toggle"));
            wheels = hostWheels; Wheel(Descendants<TextBox>(view).Single(), 120);
            check("toolbar wheel: narrow expanded search keeps host wheel instead of a hidden classification axis", !scroll.IsVisible && hostWheels == wheels + 1);
            view.DismissFromKeyboard(); WaitClipboardMotion(Soft.Choose + 40); Pump();
            check("toolbar wheel: search collapse restores measured classification without stale hidden state", scroll.IsVisible && scroll.ViewportWidth > 0);
            window.Width = 1320; view.ApplicationsMode = true; Pump();
            foreach (string id in new[] { "clipboard-type-textT", "clipboard-search-toggle", "clipboard-mode-applications", "clipboard-drop-favorites", "clipboard-sort", "clipboard-settings" })
            {
                wheels = hostWheels; Wheel(Button(id), 120);
                check("toolbar wheel: fixed control retains the host wheel / " + id, hostWheels == wheels + 1);
                rights = hostRights; Right(Button(id));
                check("toolbar right-click: fixed control delegates to host settings without the retired menu / " + id, hostRights == rights + 1 && !view.PopupOpen);
            }
            Click(Button("clipboard-sort"));
            bool sortOpened = Button("clipboard-sort").ContextMenu?.IsOpen == true, sortTracked = view.PopupOpen;
            view.ClosePopups(); Pump();
            rights = hostRights; Right(Button("clipboard-sort"));
            check("toolbar right-click: immediate right-click during native menu close cannot reopen the sort picker", sortOpened && sortTracked && !ContextMenuService.GetIsEnabled(Button("clipboard-sort")) && Button("clipboard-sort").ContextMenu?.IsOpen != true && hostRights == rights + 1 && !view.PopupOpen);
            typeof(ClipboardView).GetMethod("ShowPages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null); Pump(); view.ClosePopups(); Pump();
            rights = hostRights; Right(Button("clipboard-settings"));
            check("toolbar right-click: keyboard page menu does not resurrect on a settings right-click", !ContextMenuService.GetIsEnabled(Button("clipboard-settings")) && Button("clipboard-settings").ContextMenu?.IsOpen != true && hostRights == rights + 1 && !view.PopupOpen);
            WaitClipboardMotion(250); Pump();
            check("toolbar right-click: native close completion releases temporary picker bindings", Button("clipboard-sort").ContextMenu is null && Button("clipboard-settings").ContextMenu is null);
            check("toolbar right-click: Shift+F10 on settings has no retired more menu", !view.HandleShortcut(Key.F10, ModifierKeys.Shift, Button("clipboard-settings")) && !view.PopupOpen);
            Click(Button("clipboard-settings"));
            check("toolbar settings: left-click still opens clipboard preferences once", settingsOpened == 1 && collapsed == 1 && activations == 0 && !settings.Enabled);
            Console.WriteLine("TOOLBAR RESULT: isolated pinning and routed-input checks completed");
        }
        finally { view.ClosePopups(); window.Close(); }
    }
}
