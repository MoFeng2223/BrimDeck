using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using DropShelf;
using DropShelf.Interop;
using DropShelf.Localization;
using DropShelf.Models;
using DropShelf.Services;
using DropShelf.Views;

internal static class ShelfSettingsChecks
{
    // No controller.Start: these checks never install hooks, register hotkeys or touch the system clipboard.
    public static void Run(Application application, string root, Action<string, bool> check)
    {
        string data = Path.Combine(root, "settings-integration");
        Directory.CreateDirectory(data);
        string settingsPath = Path.Combine(data, "settings.json");
        var stored = SettingsService.Normalize(new AppSettings
        {
            DragOutMode = TransferMode.Move, CloseWhenEmpty = false, ShakeSensitivity = 72,
            Hotkey = "Ctrl+Alt+F8", QuickActionLayout = QuickActionLayout.Two,
        });
        new SettingsService(settingsPath).Save(stored);
        bool floating = false, busy = false, conflict = false;
        int islandOpens = 0, settingsOpens = 0, helpOpens = 0;
        EmbeddedHostOptions Options() => new()
        {
            DataDirectory = data, CacheDirectory = Path.Combine(root, "settings-attachments"),
            Exit = () => { }, GetStartWithWindows = () => false, SetStartWithWindows = _ => true,
            AnimationsEnabled = () => false, FloatingEnabled = () => floating, IsIslandBusy = () => busy,
            ShowIsland = () => islandOpens++, ShowSettings = () => settingsOpens++, ShowHelp = () => helpOpens++,
            HotkeyConflicts = _ => conflict, LegacyDragOutMode = TransferMode.Copy, LegacyCloseWhenEmpty = true,
        };
        using (var migrated = new AppController(Dispatcher.CurrentDispatcher, Options()))
        {
            check("shelf settings: migration preserves effective copy and empty-close values", migrated.Settings.DragOutMode == TransferMode.Copy && migrated.Settings.CloseWhenEmpty
                && migrated.Settings.HostIntegrationVersion == 1 && File.Exists(settingsPath + ".pre-host-integration.bak"));
            check("shelf settings: migration keeps gesture, hotkey and action preferences", migrated.Settings.ShakeSensitivity == stored.ShakeSensitivity
                && migrated.Settings.Hotkey == stored.Hotkey && migrated.Settings.QuickActionLayout == stored.QuickActionLayout);
            migrated.UpdateSettings(settings => { settings.DragOutMode = TransferMode.Move; settings.CloseWhenEmpty = false; });
        }
        using var controller = new AppController(Dispatcher.CurrentDispatcher, Options());
        check("shelf settings: migration is one-time and old host values cannot overwrite later edits", controller.Settings.DragOutMode == TransferMode.Move && !controller.Settings.CloseWhenEmpty);
        controller.RefreshHostState();
        var island = controller.Shelves.OpenActive(true);
        island.ViewModel.AddEntries([new DroppedEntry(ShelfItemKind.Text, "first independent shelf")]);
        var second = controller.Shelves.OpenAt(new POINT(-20000, -20000), false, true);
        second.ViewModel.AddEntries([new DroppedEntry(ShelfItemKind.Text, "second independent shelf")]);
        check("shelf placement: island mode opens separate models without showing floating windows", islandOpens == 2
            && !island.IsVisible && !second.IsVisible && !ReferenceEquals(island.ViewModel, second.ViewModel) && island.ViewModel.Count == 1 && second.ViewModel.Count == 1);
        using (var tray = new ShelfTrayAdapter(controller.Shelves))
        {
            check("shelf placement: island shares only the active shelf", ReferenceEquals(tray.Model, second.ViewModel) && tray.Items.Single().Value == "second independent shelf");
            floating = true; controller.RefreshHostState();
            check("shelf placement: enabling floating does not summon a window by itself", !second.IsVisible);
            controller.Shelves.OpenActive(false); Pump();
            check("shelf placement: explicit floating open displays the active model", second.IsVisible && ReferenceEquals(tray.Model, second.ViewModel));
            busy = true; floating = false; controller.RefreshHostState();
            check("shelf placement: in-flight island interaction delays hiding", second.IsVisible);
            busy = false; controller.Shelves.NotifyActivityChanged(); WaitUntil(() => !second.IsVisible);
            check("shelf placement: delayed hide retains every item and its shared model", !second.IsVisible && second.ViewModel.Count == 1 && ReferenceEquals(tray.Model, second.ViewModel));
            floating = true; controller.RefreshHostState(); Pump();
            check("shelf placement: switching back restores only previously visible shelves", second.IsVisible && !island.IsVisible);
        }
        foreach (var placement in Enum.GetValues<FileTrayPlacement>())
        {
            var hostSettings = new DeckSettings { Tray = new TraySettings { Placement = placement, Enabled = false } };
            check("shelf placement: island page depends on placement, not retired Enabled, " + placement,
                DeckPages.Shown(hostSettings, DeckPage.FileTray) == (placement != FileTrayPlacement.Floating));
        }
        controller.ShowMainWindow(); controller.ShowOnboarding();
        check("shelf identity: settings and tutorial route to host without management windows", settingsOpens == 1 && helpOpens == 1
            && !application.Windows.Cast<Window>().Any(window => window is DropShelf.Views.MainWindow or OnboardingWindow));
        using var content = new ShelfSettingsContent(controller);
        var owner = new Window { Content = content, Width = 760, Height = 650, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            owner.Show(); Pump();
            check("shelf settings: embedded control contains every original feature editor", new[] { "HotkeyEditor", "TriggerSelector", "ActionLayoutSelector", "ActionSlotsList", "MoreActionsList", "HistoryList", "EditorKindSelector", "EditorFolderBox", "EditorNameBox", "ConfirmOverlay", "DragLayer" }.All(name => content.FindName(name) is not null));
            check("shelf settings: inherited palette stays isolated from application brushes", ReferenceEquals(application.Resources["TextPrimaryBrush"], Brushes.Magenta));
            content.SetFloatingEnabled(false);
            check("shelf settings: island mode disables triggers but retains their values", !((UIElement)content.FindName("FloatingOptions")).IsEnabled && content.ViewModel.Hotkey.ToString() == "Ctrl+Alt+F8");
            content.SetFloatingEnabled(true); floating = true; conflict = true; controller.RefreshHostState();
            check("shelf settings: host shortcut conflicts have a distinct explanation", controller.IsHotkeyConflict && content.ViewModel.HotkeyDescription == Strings.HostHotkeyConflict);
            controller.SetHotkeysPaused(true);
            check("shelf settings: recording is paused without discarding the saved shortcut", controller.AreHotkeysPaused && controller.Settings.Hotkey == "Ctrl+Alt+F8");
            conflict = false; controller.SetHotkeysPaused(false);
            content.OpenSection("Actions");
            var vm = content.ViewModel;
            int originalCustomCount = controller.Settings.CustomActions!.Count;
            var before = controller.Settings.QuickActionSlots!.ToArray();
            vm.AssignSlot(0, before[1]);
            check("shelf settings: original quick action assignment swaps existing slots", controller.Settings.QuickActionSlots![0] == before[1] && controller.Settings.QuickActionSlots[1] == before[0]);
            vm.BeginNewCustomAction();
            vm.Editor!.Folder = data; vm.Editor.Name = "Owned folder action"; vm.Editor.SaveCommand.Execute(null);
            check("shelf settings: folder action editor persists through original commands", !vm.IsEditorOpen && controller.Settings.CustomActions!.Any(action => action.Name == "Owned folder action"));
            vm.BeginNewCustomAction(); vm.Editor!.Kind = CustomActionKind.CompressImage; vm.Editor.SizeText = "0"; vm.Editor.SaveCommand.Execute(null);
            check("shelf settings: original custom action validation rejects invalid image size", vm.IsEditorOpen && vm.Editor.HasError);
            vm.Editor.SizeText = "128"; vm.Editor.Name = "Owned image action"; vm.Editor.SaveCommand.Execute(null);
            check("shelf settings: image compression editor preserves the configured target size", controller.Settings.CustomActions!.Any(action => action.Name == "Owned image action" && action.ImageSizeKb == 128));
            vm.BeginNewCustomAction(); vm.Editor!.Name = "Unsaved draft";
            check("shelf settings: editor is modal and blocks section switches", content.IsModal && !content.OpenSection("Shelf") && content.Section == "Actions");
            owner.Content = null; Pump(); owner.Content = content; Pump();
            check("shelf settings: detach and reattach keep the original view model and draft", ReferenceEquals(vm, content.ViewModel) && vm.Editor?.Name == "Unsaved draft");
            vm.Editor!.CancelCommand.Execute(null);
            check("shelf settings: cancel editor releases the host modal boundary", !content.IsModal);
            var custom = vm.MoreActions.First(card => card.Action.Custom?.Name == "Owned folder action");
            typeof(ShelfSettingsContent).GetMethod("ConfirmDeleteCustomAction", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(content, [custom]);
            check("shelf settings: custom action deletion opens confirmation without immediate loss", ((UIElement)content.FindName("ConfirmOverlay")).Visibility == Visibility.Visible && controller.Settings.CustomActions!.Count == originalCustomCount + 2);
            typeof(ShelfSettingsContent).GetMethod("OnConfirmCancelClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(content, [content, new RoutedEventArgs()]);
            check("shelf settings: cancel deletion retains the custom action", controller.Settings.CustomActions!.Count == originalCustomCount + 2 && !content.IsModal);
            string historical = Path.Combine(data, "history-file.txt"); File.WriteAllText(historical, "owned history input");
            var shelf = controller.Shelves.OpenAt(new POINT(-20000, -20000), false, false); shelf.ViewModel.AddFiles([historical]); shelf.Close();
            content.OpenSection("History"); Pump();
            check("shelf settings: original fan history loads closed shelf previews", vm.HistoryEntries.Count > 0 && ((ItemsControl)content.FindName("HistoryList")).Items.Count == vm.HistoryEntries.Count);
            floating = false; controller.RefreshHostState();
            controller.RestoreAt(controller.History.Latest!, new POINT(-20000, -20000));
            WaitUntil(() => controller.Shelves.ActiveWindow.ViewModel.Items.Any(item => item.Value == historical));
            check("shelf placement: history drag restore honors island-only placement", !controller.Shelves.ActiveWindow.IsVisible
                && controller.Shelves.ActiveWindow.ViewModel.Items.Any(item => item.Value == historical));
            foreach (string section in new[] { "Shelf", "Actions", "History", "Help" })
            {
                content.OpenSection(section); Pump();
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                check("shelf settings: bounded " + section + " page renders without another application window", bitmap.PixelWidth > 400 && bitmap.PixelHeight > 200);
            }
            LanguageService.SetMode(AppLanguage.English); Pump();
            check("shelf identity: language updates hosted names and help", Strings.MenuSettings == "File transfer shelf settings" && ((TextBlock)content.FindName("CacheHelp")).Text.StartsWith("Ordinary files"));
            LanguageService.SetMode(AppLanguage.Chinese);
        }
        finally { owner.Close(); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void WaitUntil(Func<bool> condition)
    {
        long until = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < until) { Pump(); Thread.Sleep(10); }
    }
}
