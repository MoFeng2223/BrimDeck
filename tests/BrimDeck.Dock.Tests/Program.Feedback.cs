using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class Program
{
    private static void CheckFeedbackHost(App app, MainWindow host, DeckSettings settings, ClipboardHistory history, PanelPalette[] palettes, string scratch, string directory)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var prices = new Pricing(scratch); Set(app, "<Prices>k__BackingField", prices);
        Set(app, "<Store>k__BackingField", new SettingsStore(Path.Combine(scratch, "feedback-settings")));
        typeof(App).GetMethod("InitializeFeedback", Private)!.Invoke(app, null);
        var preferences = new SettingsWindow(app) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            var grant = new PromptRequest(PromptId.ClipboardDeleteEntry, "Synthetic deletion risk");
            var remembered = settings.Prompts.Copy(); remembered.Remember(grant, new(true, true));
            Check("prompt host: remembered risk commits through the ordinary writer", (bool)typeof(App).GetMethod("SavePromptPreferences", Private)!.Invoke(app, [remembered])! && settings.Prompts.IsSuppressed(grant.Id) && new SettingsStore(app.Store.DirectoryPath).Load().Prompts.IsSuppressed(grant.Id));
            app.RestorePrompt(grant.Id);
            Check("prompt host: individual restoration is durable without enabling capture", !settings.Prompts.IsSuppressed(grant.Id) && !new SettingsStore(app.Store.DirectoryPath).Load().Prompts.IsSuppressed(grant.Id) && !settings.Clipboard.Enabled);
            var store = app.Store; string blocker = Path.Combine(scratch, "blocked-prompt-settings"); File.WriteAllText(blocker, "Synthetic blocker");
            Set(app, "<Store>k__BackingField", new SettingsStore(blocker));
            remembered = settings.Prompts.Copy(); remembered.Remember(grant, new(true, true));
            Check("prompt host: failed settings writes cannot grant a remembered deletion", !(bool)typeof(App).GetMethod("SavePromptPreferences", Private)!.Invoke(app, [remembered])! && !settings.Prompts.IsSuppressed(grant.Id) && app.ModuleProblem.Length > 0);
            Set(app, "<Store>k__BackingField", store);
            typeof(App).GetMethod("SavePromptPreferences", Private)!.Invoke(app, [settings.Prompts.Copy()]);
            foreach (var definition in PromptCatalog.All) settings.Prompts.Remember(new(definition.Id, "Synthetic acknowledged risk"), new(true, true));
            preferences.ShowPage(SettingsWindow.PromptsPage); preferences.Show(); Pump();
            var restoreButtons = All<Button>(preferences).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("prompt-restore-", StringComparison.Ordinal)).ToArray();
            Check("prompt host: every risk has a reachable independent restore control", restoreButtons.Length == PromptCatalog.All.Count() && restoreButtons.All(button => button.IsEnabled));
            restoreButtons.Single(button => AutomationProperties.GetAutomationId(button) == "prompt-restore-clipboard.delete-entry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check("prompt host: restore controls change no unrelated risk or history", !settings.Prompts.IsSuppressed(grant.Id) && settings.Prompts.IsSuppressed(PromptId.ShelfDeleteAction) && history.Items.Count == 6);
            app.RestorePrompt();
            Check("prompt host: restore all is durable and does not perform a business operation", settings.Prompts.Suppressed.Count == 0 && new SettingsStore(store.DirectoryPath).Load().Prompts.Suppressed.Count == 0 && history.Items.Count == 6);
            foreach (var palette in palettes.Where(palette => palette.Id is PanelThemes.Dark or PanelThemes.Light))
            foreach (string language in new[] { Loc.Chinese, Loc.English })
            {
                Palette(palette); Loc.Use(language);
                foreach (var definition in new[] { PromptCatalog.Get(PromptId.ClipboardClearAll), PromptCatalog.Get(PromptId.ShelfSaveFailed) })
                {
                    string message = Loc.T("此操作将影响当前会话。原文件不会删除。请确认是否继续。", "This action affects the current session. Original files are not deleted. Confirm whether to continue.");
                    var prompt = new PromptWindow(new(definition.Id, message)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                    try
                    {
                        prompt.Show(); prompt.Width = 320; Pump();
                        Check("prompt host " + palette.Id + "/" + language + "/" + definition.Key + ": narrow choice stays inside theme chrome", prompt.RememberChoice.IsVisible && prompt.RememberChoice.IsChecked == false && prompt.AcceptButton.TranslatePoint(new Point(), prompt).X + prompt.AcceptButton.ActualWidth <= prompt.ActualWidth + 1 && ((Border)prompt.Content).Background is SolidColorBrush brush && brush.Color == PanelTheme.ColorOf("popup"));
                        Render((FrameworkElement)prompt.Content, directory, "prompt-" + palette.Id + "-" + language + "-" + definition.Key + ".png");
                    }
                    finally { prompt.Close(); }
                }
            }
            Loc.Use(Loc.Chinese); Palette(palettes[0]);
            Set(host, "_page", DeckPage.Clipboard); Call(host, "PrepareClipboardDock");
            Call(host, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (false, false, "")));
            host.SetExpanded(true, instant: true); Pump();
            var view = (ClipboardView)Get(host, "_clipboardView")!; var focus = Keyboard.FocusedElement;
            app.ReportFeedback("synthetic-copy", "Synthetic copied", host); Pump();
            Check("feedback host: expanded result uses the owning clipboard surface without focus", All<QuietNotice>(view).Single().IsVisible && ReferenceEquals(focus, Keyboard.FocusedElement));
            host.SetExpanded(false, instant: true); Pump(); double width = host.Width, height = host.Height;
            app.ReportFeedback("synthetic-paste", "Copied; use Ctrl+V", host); Pump();
            var compact = (QuietNotice)Get(host, "_moduleNotice")!;
            Check("feedback host: collapsed result changes no geometry or keyboard focus", compact.IsVisible && !compact.IsHitTestVisible && !compact.Focusable && host.Width == width && host.Height == height && ReferenceEquals(focus, Keyboard.FocusedElement));
            host.Hide(); app.ReportFeedback("synthetic-hidden", "Deferred synthetic result", host); Pump();
            Check("feedback host: a completely hidden island stays hidden and retains only its last result", !host.IsVisible && !compact.Timer.IsEnabled && (string)Get(host, "_pendingModuleFeedback")! == "Deferred synthetic result");
            host.Show(); host.SetExpanded(true, instant: true); Pump();
            Check("feedback host: only the next explicit opening reveals the deferred result", All<QuietNotice>(view).Single().Label.Text == "Deferred synthetic result" && (string)Get(host, "_pendingModuleFeedback")! == "");
            // A missing synthetic source fails during payload construction, before any system clipboard write.
            var copyPreferences = settings.Copy().Clipboard; copyPreferences.Enabled = false; copyPreferences.PersistHistory = false;
            using var service = new ClipboardService(Path.Combine(scratch, "copy-failure"), () => copyPreferences, app.Dispatcher);
            Set(app, "_clipboard", service);
            var missing = new ClipboardEntry { Id = "missing-original", Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [Path.Combine(scratch, "never-created.txt")] } };
            var library = new ClipboardLibraryWindow(history,
                (entry, copyOnly) => typeof(App).GetMethod("ActivateLibraryEntry", Private)!.Invoke(app, [entry, copyOnly]),
                () => (false, false, service.Status), () => { }, () => { }, preferences: () => copyPreferences)
                { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            Set(app, "_clipboardLibrary", library);
            try
            {
                library.Show(); Pump();
                foreach (bool copyOnly in new[] { true, false })
                {
                    var pending = new ClipboardPasteSession(default, 0, 0); Set(app, "_pasteSession", pending);
                    ((DispatcherTimer)Get(app, "_clipboardPasteWait")!).Start();
                    app.ActivateClipboardEntry(host, missing, copyOnly); Pump();
                    Check("copy failure island " + copyOnly + ": preserves the open surface, reports failure and cancels old input", host.IsExpanded && All<QuietNotice>(view).Single().Label.Text == service.Status
                        && service.Status.Length > 0 && pending.Result == ClipboardPasteResult.Cancelled && Get(app, "_pasteSession") is null && !((DispatcherTimer)Get(app, "_clipboardPasteWait")!).IsEnabled);
                    pending = new ClipboardPasteSession(default, 0, 0); Set(app, "_pasteSession", pending);
                    ((DispatcherTimer)Get(app, "_clipboardPasteWait")!).Start();
                    Call(library, "Activate", missing, copyOnly); Pump();
                    Check("copy failure library " + copyOnly + ": retains the actual failure instead of a requested-success message", library.IsVisible && All<TextBlock>(library).Any(label => label.Text == service.Status)
                        && pending.Result == ClipboardPasteResult.Cancelled && Get(app, "_pasteSession") is null && !((DispatcherTimer)Get(app, "_clipboardPasteWait")!).IsEnabled);
                }
            }
            finally { Set(app, "_clipboardLibrary", null); library.ForceClose(); Set(app, "_clipboard", null); }
        }
        finally
        {
            Set(app, "<Exiting>k__BackingField", true); preferences.Close();
            ((DispatcherTimer)Get(app, "_save")!).Stop(); CompletePerformance((Task)Get(app, "_writing")!);
            UserPrompts.Configure(() => new(), _ => false);
        }
    }
}
