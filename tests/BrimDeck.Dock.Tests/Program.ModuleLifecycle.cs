using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Models;

internal static partial class Program
{
    // Native dialog responses are limited to this isolated fixture process and the expected module caption.
    private static void CheckModuleLifecycleHost(App app, MainWindow window, string scratch)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Set(app, "<Store>k__BackingField", new SettingsStore(scratch)); Set(app, "_trayStore", new FileTrayStore(scratch)); Set(app, "_moduleDirectory", scratch);
        typeof(PanelTheme).GetField("_loaded", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        // Keep dialog captions stable across UpdateSettings, which normally reapplies the saved language.
        app.Settings.Language = Loc.English; Loc.Use(Loc.English);
        app.Settings.Clipboard.Enabled = false; app.Settings.Clipboard.Hotkey = "";
        app.Settings.Clipboard.RetentionDays = 0; app.Settings.Clipboard.PersistHistory = true;
        app.Settings.Clipboard.Mode = app.Settings.Tray.Mode = ModuleMode.Off;
        app.Settings.Tray.Placement = FileTrayPlacement.Notch;
        HostedResources.ConfigureHost(() => false, Path.Combine(scratch, "attachments"));
        var change = typeof(App).GetMethod("ChangeModuleModeAsync", Private)!;
        Task<bool> Begin(bool clipboard, ModuleMode mode) => (Task<bool>)change.Invoke(app, [clipboard, mode])!;
        bool Change(bool clipboard, ModuleMode mode) { var task = Begin(clipboard, mode); CompletePerformance(task); return task.Result; }
        ClipboardService Service() => app.ClipboardHistoryService!;
        string original = Path.Combine(scratch, "original-file.txt"); File.WriteAllText(original, "Owned original file must survive.");
        try
        {
            Check("lifecycle: explicit enable creates only the requested clipboard owner", Change(true, ModuleMode.Performance) && app.Shelves is null && !Service().Listening);
            var service = Service(); var history = service.History;
            var entry = history.Add(ClipboardContent.FromText("Lifecycle fixture text"), "Fixture", "", DateTimeOffset.UtcNow, app.Settings.Clipboard)!;
            CompletePerformance(service.FlushAsync());
            Set(window, "_page", DeckPage.Clipboard); Set(window, "_expanded", true);
            Call(window, "PrepareClipboardDock"); Call(window, "RenderClipboardPage", history, (Action)(() => { }), (Action)(() => { }), (Func<(bool, bool, string)>)(() => (false, false, "")));
            var view = (ClipboardView)Get(window, "_clipboardView")!;
            view.SearchText = "fixture"; view.Refresh(); view.ShowDetails(entry); Pump();
            var inspector = (ClipboardEntryEditor)Get(view, "_inspector")!;
            var title = All<TextBox>(inspector).Single(field => AutomationProperties.GetAutomationId(field) == "clipboard-edit-title");
            title.Text = "Unsaved lifecycle draft";
            Check("lifecycle: switching presentation preserves live owner, query and draft", Change(true, ModuleMode.Full) && ReferenceEquals(service, Service()) && ReferenceEquals(view, Get(window, "_clipboardView")) && view.SearchText == "fixture" && view.HasUnsavedDraft && title.Text == "Unsaved lifecycle draft");
            Check("lifecycle: canceling draft shutdown preserves the exact view and owner", !AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 7, () => Change(true, ModuleMode.Off)) && ReferenceEquals(service, Service()) && ReferenceEquals(view, Get(window, "_clipboardView")) && view.HasUnsavedDraft);
            Check("lifecycle: confirmed draft shutdown releases its windows and image lease", AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 6, () => Change(true, ModuleMode.Off)) && app.ClipboardHistoryService is null && Get(app, "_clipboardImages") is null);

            Check("lifecycle: clipboard can be reopened without opting into capture", Change(true, ModuleMode.Performance) && !Service().Listening);
            service = Service(); history = service.History;
            app.Settings.Clipboard.PersistHistory = false; service.ApplySettings(); CompletePerformance(service.FlushAsync());
            history.Add(ClipboardContent.FromText("Memory-only fixture"), "Fixture", "", DateTimeOffset.UtcNow, app.Settings.Clipboard);
            int count = history.Items.Count;
            Check("lifecycle: canceling memory-only shutdown preserves all session entries", !AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 7, () => Change(true, ModuleMode.Off)) && ReferenceEquals(service, Service()) && history.Items.Count == count);
            Check("lifecycle: confirmed memory-only shutdown never writes a hidden replacement history", AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 6, () => Change(true, ModuleMode.Off)) && app.ClipboardHistoryService is null && !File.Exists(Path.Combine(scratch, "clipboard-history.bin")));

            string blocker = Path.Combine(scratch, "blocked-clipboard"); File.WriteAllText(blocker, "Owned write blocker");
            app.Settings.Clipboard.PersistHistory = true; app.Settings.Clipboard.StorageDirectory = blocker;
            Check("lifecycle: a failing storage fixture can still hold a live session", Change(true, ModuleMode.Performance));
            service = Service(); history = service.History;
            history.Add(ClipboardContent.FromText("Failed close fixture"), "Fixture", "", DateTimeOffset.UtcNow, app.Settings.Clipboard);
            Check("lifecycle: failed close reports the barrier and resumes the same owner", !AnswerModuleDialog(Loc.T("模块状态", "Module status"), 1, () => Change(true, ModuleMode.Off)) && ReferenceEquals(service, Service()) && app.Settings.Clipboard.Mode == ModuleMode.Performance && history.Items.Count == 1 && !(bool)Get(service, "_stopping")!);
            File.Delete(blocker); Directory.CreateDirectory(blocker); CompletePerformance(service.FlushAsync());

            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string target = Path.Combine(scratch, "migrated");
            var migration = service.ChangeStorageDirectoryAsync(target, async preference => { committed.SetResult(); await release.Task; app.Settings.Clipboard.StorageDirectory = preference; });
            CompletePerformance(committed.Task);
            Check("lifecycle: path migration refuses concurrent close without losing its owner", !Change(true, ModuleMode.Off) && service.ChangingStorage && ReferenceEquals(service, Service()));
            release.SetResult(); CompletePerformance(migration); CompletePerformance(service.FlushAsync());
            Check("lifecycle: migration completes at the selected encrypted destination", migration.Result.Succeeded && new ClipboardHistoryStore(target).Load().Items.Count == 1);

            var drag = new ClipboardDragDrop(history); var scope = Get(drag, "_dragScope")!;
            using var operation = ((OperationTracker)Get(scope, "Operations")!).Enter();
            var stopping = Begin(true, ModuleMode.Off); Pump();
            Check("lifecycle: an active export keeps clipboard ownership until completion", !stopping.IsCompleted && ReferenceEquals(service, Service()) && (bool)Get(scope, "Suspended")!);
            operation.Dispose(); CompletePerformance(stopping);
            Check("lifecycle: completed export releases clipboard without polling or replay", stopping.Result && app.ClipboardHistoryService is null);

            string shelfData = Path.Combine(scratch, "Shelves"); Directory.CreateDirectory(shelfData);
            string shelfSettings = Path.Combine(shelfData, "settings.json");
            File.WriteAllText(shelfSettings, "{\"HostIntegrationVersion\":0,\"Hotkey\":\"\"}");
            string blockedBackup = shelfSettings + ".pre-host-integration.bak"; Directory.CreateDirectory(blockedBackup);
            Check("lifecycle: failed shelf initialization rolls back Off and releases its partial lease", !AnswerModuleDialog(Loc.T("模块状态", "Module status"), 1, () => Change(false, ModuleMode.Performance)) && app.Settings.Tray.Mode == ModuleMode.Off && app.Shelves is null && Get(app, "_shelfImages") is null);
            Directory.Delete(blockedBackup);
            Check("lifecycle: the same shelf can retry after fixing its owned storage", Change(false, ModuleMode.Performance) && app.Shelves is not null);
            var controller = app.Shelves!; var shelf = controller.Shelves.ActiveWindow;
            shelf.ViewModel.AddFiles([original]);
            Check("lifecycle: canceling shelf shutdown restores input and keeps the shared model", !AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 7, () => Change(false, ModuleMode.Off)) && ReferenceEquals(app.Shelves, controller) && controller.Accepting && shelf.ViewModel.Items.Single().Value == original);
            Check("lifecycle: confirmed shelf close preserves its durable original path", AnswerModuleDialog(Loc.T("关闭模块", "Turn off module"), 6, () => Change(false, ModuleMode.Off)) && app.Shelves is null && new FileTrayStore(scratch).Load().Items.Single().Path == original);

            Check("lifecycle: shelf save-failure fixture starts independently", Change(false, ModuleMode.Performance));
            controller = app.Shelves!;
            // The restored model is cleared explicitly before testing only the empty-session save barrier.
            controller.Shelves.ActiveWindow.ViewModel.Clear();
            string trayBlocker = Path.Combine(scratch, "blocked-tray"); File.WriteAllText(trayBlocker, "Owned tray blocker");
            Set(app, "_trayStore", new FileTrayStore(trayBlocker));
            Check("lifecycle: failed shelf barrier leaves its controller live and accepting", !AnswerModuleDialog(Loc.T("模块状态", "Module status"), 1, () => Change(false, ModuleMode.Off)) && ReferenceEquals(app.Shelves, controller) && controller.Accepting);
            Set(app, "_trayStore", new FileTrayStore(scratch)); Check("lifecycle: shelf shutdown retries without a replacement owner", Change(false, ModuleMode.Off));

            Check("lifecycle: clipboard preview works with the shelf completely Off", Change(true, ModuleMode.Performance) && app.Shelves is null);
            history = Service().History;
            var image = history.Add(new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Image(), PixelWidth = 400, PixelHeight = 300 }, "Fixture", "", DateTimeOffset.UtcNow, app.Settings.Clipboard)!;
            app.OpenClipboardPreview(image, new Rect(300, 80, 210, 146), new Rect(200, 34, 1080, 210), 1);
            var previews = (ClipboardPreviewWindows)Get(app, "_clipboardPreviews")!;
            WaitPreview(() => previews.Windows.Single().PreviewBody is Image { Source: BitmapSource });
            Check("lifecycle: preview uses shared imaging without reconstructing shelves", app.Shelves is null && previews.Windows.Single().IsVisible);
            Check("lifecycle: final clipboard close releases detached previews", Change(true, ModuleMode.Off) && previews.Windows.Count == 0 && Get(app, "_clipboardImages") is null);
            Check("lifecycle: all mode changes leave consent and ordinary original bytes intact", !app.Settings.Clipboard.Enabled && File.ReadAllText(original) == "Owned original file must survive.");
        }
        finally
        {
            ((DispatcherTimer)Get(app, "_save")!).Stop(); CompletePerformance((Task)Get(app, "_writing")!);
            typeof(App).GetMethod("DisposeClipboard", Private)!.Invoke(app, null);
            typeof(App).GetMethod("DisposeShelves", Private)!.Invoke(app, null);
            (Get(app, "_clipboardImages") as IDisposable)?.Dispose(); (Get(app, "_shelfImages") as IDisposable)?.Dispose();
            Set(window, "_preview", true); Set(window, "_previewExpanded", true);
        }
    }

    private static bool AnswerModuleDialog(string caption, int button, Func<bool> action)
    {
        int answered = 0;
        using var stopped = new ManualResetEventSlim();
        // A native modal loop can suspend WPF DispatcherTimer delivery after an awaited barrier.
        // A dedicated fixture thread observes only this process and never reads the dialog body.
        var responder = new Thread(() =>
        {
            while (!stopped.Wait(10))
            {
                LifecycleEnumWindows((handle, _) =>
                {
                    LifecycleWindowProcess(handle, out uint process);
                    if (process != Environment.ProcessId) return true;
                    var kind = new StringBuilder(64); LifecycleClass(handle, kind, kind.Capacity);
                    if (kind.ToString() != "#32770") return true;
                    var title = new StringBuilder(256); LifecycleTitle(handle, title, title.Capacity);
                    if (title.ToString() != caption) return true;
                    var control = LifecycleDialogItem(handle, button);
                    // Windows can expose the sole OK button as IDCANCEL; use its actual child control.
                    if (control == IntPtr.Zero && button == 1) control = LifecycleDialogItem(handle, 2);
                    if (control == IntPtr.Zero || !LifecycleEnabled(control)) return true;
                    if (LifecyclePost(control, 0x00F5, IntPtr.Zero, IntPtr.Zero)) Interlocked.CompareExchange(ref answered, 1, 0);
                    return false;
                }, IntPtr.Zero);
            }
        }) { IsBackground = true, Name = "Owned module dialog fixture" };
        responder.Start();
        try
        {
            bool result = action();
            if (answered != 1) throw new InvalidOperationException("Expected exactly one owned module dialog: " + caption);
            return result;
        }
        finally { stopped.Set(); responder.Join(); }
    }
    private delegate bool LifecycleWindowCallback(IntPtr handle, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")] private static extern bool LifecycleEnumWindows(LifecycleWindowCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint LifecycleWindowProcess(IntPtr handle, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int LifecycleClass(IntPtr handle, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int LifecycleTitle(IntPtr handle, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool LifecyclePost(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetDlgItem")] private static extern IntPtr LifecycleDialogItem(IntPtr handle, int id);
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")] private static extern bool LifecycleEnabled(IntPtr handle);
}
