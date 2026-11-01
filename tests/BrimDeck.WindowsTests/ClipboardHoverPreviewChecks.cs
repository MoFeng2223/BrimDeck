using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void PrepareClipboardResources()
    {
        var app = Application.Current ?? new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string token in new[] { "PanelFill", "PanelPopup", "PanelSurface", "PanelTrack", "PanelSwitchFill", "PanelHover", "PanelPressed", "PanelFillSelected" }) app.Resources[token] = System.Windows.Media.Brushes.DimGray;
        foreach (string token in new[] { "PanelPrimary", "PanelSecondary", "PanelTertiary", "PanelPopupBorder" }) app.Resources[token] = System.Windows.Media.Brushes.White;
        app.Resources["PanelAccent"] = System.Windows.Media.Brushes.Cyan;
        app.Resources["PanelKeyboardFocus"] = new Style(); app.Resources["ClipboardDockCardRadius"] = new CornerRadius(18);
        for (int r = 1; r <= 16; r++) app.Resources["PanelR" + r] = new CornerRadius(r);
    }
    internal static void RunPreview(Action<string, bool> check)
    {
        PrepareClipboardResources();
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BrimDeck-Preview-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try { CheckCardActions(directory, check, null); CheckWakeSlider(check); }
        finally { System.IO.Directory.Delete(directory, true); }
    }
    private static void CheckHoverPreview(ClipboardView view, ListBox list, ClipboardHistory history, ClipboardSettings settings, Action<string, bool> check, Func<int> activations)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(ClipboardView).GetField(name, hidden)!.GetValue(view);
        void Call(string name, params object[] args) => typeof(ClipboardView).GetMethod(name, hidden)!.Invoke(view, args);
        var delay = (DispatcherTimer)Field("_previewDelay")!;
        var leave = (DispatcherTimer)Field("_previewLeave")!;
        var card = Descendants<ClipboardCard>(view).First(); var entry = card.Entry!;
        int before = activations(); var state = view.CapturePresentation(); var source = list.ItemsSource;
        check("hover preview: dock cards and count expose no content tooltip", Descendants<ClipboardCard>(view).All(value => value.ToolTip is null)
            && Descendants<TextBlock>(card).Single(value => AutomationProperties.GetAutomationId(value) == "clipboard-card-copy-count").ToolTip is null);
        var standalone = new ClipboardCard { DataContext = entry };
        check("hover preview: non-dock card keeps its existing management tooltip", standalone.ToolTip is string { Length: > 0 });
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
        check("hover preview: moving onto card queues a lazy dwell instead of decoding immediately", delay.IsEnabled && !view.PreviewOpen);
        check("preview sensitivity: default first-hover dwell is 300 milliseconds", settings.PreviewSensitivity == 70 && delay.Interval.TotalMilliseconds == 300);
        settings.PreviewSensitivity = 0; view.RefreshPreviewSensitivity();
        check("preview sensitivity: slower setting updates a waiting hover immediately", delay.IsEnabled && delay.Interval.TotalMilliseconds == 1000);
        settings.PreviewSensitivity = 100; view.RefreshPreviewSensitivity(); Pump();
        check("preview sensitivity: instant setting consumes pending hover without a dwell", !delay.IsEnabled && view.PreviewOpen);
        view.ClosePopups(); settings.PreviewSensitivity = 70;
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        check("hover preview: a press cancels queued expansion before click or drag", !delay.IsEnabled && !view.PreviewOpen);
        Call("OpenHoverPreview", card); Pump();
        var popup = (Popup)Field("_preview")!;
        check("hover preview: automatic flyout is inert, noncapturing and keyed to the real card", popup.IsOpen && popup.StaysOpen && (string?)Field("_previewId") == entry.Id && popup.Placement == PlacementMode.Custom);
        check("hover preview: source, time and count moved into expanded metadata", Descendants<TextBlock>(popup.Child).Any(value => AutomationProperties.GetAutomationId(value) == "clipboard-preview-metadata" && value.Text.Contains(entry.SourceName) && value.Text.Contains("×" + entry.CopyCount)));
        var nextCard = Descendants<ClipboardCard>(view).First(value => value.Entry!.Id != entry.Id);
        for (int i = 0; i < 6; i++)
        {
            var target = i % 2 == 0 ? nextCard : card;
            target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent }); Pump();
            check("hover preview rapid sweep " + i + ": open preview tracks the latest card without another dwell", (string?)Field("_previewId") == target.Entry!.Id);
            check("hover preview rapid sweep " + i + ": changing cards reuses the popup instead of replaying open and close", ReferenceEquals(popup, Field("_preview")) && popup.IsOpen && !delay.IsEnabled);
        }
        for (int i = 0; i < 20; i++)
            (i % 2 == 0 ? nextCard : card).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
        Pump();
        check("hover preview: a burst coalesces to its latest card without stale queued content", (string?)Field("_previewId") == card.Entry!.Id && Field("_previewUpdate") is null && ReferenceEquals(popup, Field("_preview")));
        nextCard.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent });
        view.ClosePopups(); Pump();
        check("hover preview: closing aborts a queued switch instead of reopening stale content", !view.PreviewOpen && Field("_previewUpdate") is null && !(bool)Field("_previewFollowing")!);
        Call("OpenHoverPreview", card); Pump(); popup = (Popup)Field("_preview")!;
        list.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        check("hover preview: crossing the gap gets a leave grace instead of instant collapse", view.PreviewOpen && leave.IsEnabled);
        popup.Child.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        check("hover preview: entering expanded content cancels leave dismissal", view.PreviewOpen && !leave.IsEnabled);
        popup.Child.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        WaitClipboardMotion(260); Pump();
        check("hover preview: leaving both card and preview dismisses automatic content", !view.PreviewOpen && !leave.IsEnabled);
        Call("OpenHoverPreview", card); Pump();
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }); Pump();
        check("hover preview: first click still pastes once rather than only dismissing automatic preview", !view.PreviewOpen && activations() == before + 1);
        Call("OpenHoverPreview", card); Pump(); popup = (Popup)Field("_preview")!; view.ShowPreview(entry); Pump();
        var detached = Application.Current.Windows.OfType<ClipboardPreviewWindow>().Single(value => value.Entry.Id == entry.Id);
        check("hover preview: manual request opens a separate window instead of pinning the hover Popup", !view.PreviewOpen && !popup.IsOpen && detached.IsVisible && !view.PopupOpen);
        view.ClosePopups(); Pump();
        check("hover preview: island popup cleanup leaves detached content independent and cancels timers", detached.IsVisible && !delay.IsEnabled && !leave.IsEnabled);
        detached.CloseImmediately();
        Call("OpenHoverPreview", card); Pump();
        var scroll = Descendants<ScrollViewer>(list).First(); double offset = scroll.HorizontalOffset;
        scroll.ScrollToHorizontalOffset(offset == 0 ? 1 : 0); Pump();
        check("hover preview: scrolling cancels stale automatic card anchor", !view.PreviewOpen);
        scroll.ScrollToHorizontalOffset(offset); Pump();
        Call("OpenHoverPreview", card); Pump(); view.ClosePopups(); Pump();
        check("hover preview: hide/dispose boundary leaves no pending timer or preview", !view.PreviewOpen && !delay.IsEnabled && !leave.IsEnabled);
        check("hover preview: browsing preserves history source, selection and retention opt-out", ReferenceEquals(source, list.ItemsSource) && state == view.CapturePresentation() && history.Items.Count == 6);
        settings.GridView = true; view.ApplyLayout(); Pump();
        var gridCards = Descendants<ClipboardCard>(view).Where(value => value.Entry is not null).ToArray();
        Call("OpenHoverPreview", gridCards[0]); Pump(); var gridPopup = Field("_preview");
        for (int i = 1; i < Math.Min(4, gridCards.Length); i++)
        {
            gridCards[i].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.PreviewMouseMoveEvent }); Pump();
            check("hover preview grid sweep " + i + ": tracks latest cell without dwell or popup replacement", (string?)Field("_previewId") == gridCards[i].Entry!.Id && ReferenceEquals(gridPopup, Field("_preview")) && !delay.IsEnabled);
        }
        view.ClosePopups(); settings.GridView = false; view.ApplyLayout(); Pump();
        CheckPreviewPlacement(check);
    }
    private static void CheckPreviewPlacement(Action<string, bool> check)
    {
        var work = new Rect(12, 12, 1896, 1008); var panel = new Rect(420, 20, 1080, 210); var size = new Size(440, 280);
        var left = ClipboardView.PlacePreview(new Rect(434, 66, 210, 146), panel, work, size, 6);
        var right = ClipboardView.PlacePreview(new Rect(1314, 66, 210, 146), panel, work, size, 6);
        check("preview placement: top dock aligns left and right flyouts to their triggering cards", left.Top == 236 && left.Left == 319 && right.Left == 1199 && right.Left > left.Left);
        var edgeLeft = ClipboardView.PlacePreview(new Rect(0, 66, 210, 146), panel, work, size, 6);
        var edgeRight = ClipboardView.PlacePreview(new Rect(1800, 66, 210, 146), panel, work, size, 6);
        check("preview placement: horizontal edges clamp inside work area including taskbar insets", edgeLeft.Left == work.Left && edgeRight.Right == work.Right && work.Contains(edgeLeft) && work.Contains(edgeRight));
        var bottom = ClipboardView.PlacePreview(new Rect(750, 850, 210, 146), new Rect(420, 800, 1080, 210), work, size, 6);
        check("preview placement: bottom dock flips above the island rather than covering its cards", bottom.Bottom == 794 && work.Contains(bottom));
        var side = ClipboardView.PlacePreview(new Rect(26, 480, 210, 146), new Rect(12, 400, 240, 400), work, new Size(440, 480), 6);
        check("preview placement: tall side dock uses free right side when above and below cannot fit", side.Left == 258 && work.Contains(side));
        var secondaryWork = new Rect(-1908, -1068, 1896, 1008);
        var secondary = ClipboardView.PlacePreview(new Rect(-1400, -1000, 315, 219), new Rect(-1800, -1050, 1620, 315), secondaryWork, new Size(660, 420), 9);
        check("preview placement: negative-origin monitor with 150-percent content stays on its own work area", secondaryWork.Contains(secondary) && secondary.Left == -1572.5 && secondary.Top == -726);
        var tiny = new Rect(40, 30, 300, 180);
        var bounded = ClipboardView.PlacePreview(new Rect(280, 60, 210, 146), new Rect(0, 0, 500, 210), tiny, new Size(760, 540), 6);
        check("preview placement: oversized content is bounded even on a tiny work area", bounded == tiny);
    }
}
