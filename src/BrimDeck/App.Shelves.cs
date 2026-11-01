using System.IO;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Models;
using DropShelf.Services;

namespace BrimDeck;

public partial class App
{
    public AppController? Shelves { get; private set; }
    public ShelfTrayAdapter ShelfTray { get; private set; } = null!;

    private void InitializeShelves(string folder)
    {
        if (Settings.Tray.Mode == ModuleMode.Off || Shelves is not null) return;
        _shelfImages ??= HostedResources.AcquireImaging();
        var data = Path.Combine(folder, "Shelves");
        Shelves = new AppController(Dispatcher, new EmbeddedHostOptions
        {
            DataDirectory = data,
            CacheDirectory = VirtualFileDropService.CacheDirectoryPath,
            InstanceName = ShelfMessageWindow,
            LegacyDragOutMode = Settings.Tray.ProtectOriginals ? TransferMode.Copy : TransferMode.Move,
            LegacyCloseWhenEmpty = Settings.Tray.AutoRemoveAfterDragOut,
            Language = () => Loc.Language == "zh-CN" ? AppLanguage.Chinese : AppLanguage.English,
            ShowSettings = OpenShelfSettings,
            ShowHelp = () => OpenShelfSettings("Help"),
            UninstallFailed = () => ConfirmPrompt(_settingsWindow, new(PromptId.ShelfUninstallFailed, DropShelf.Localization.Strings.UninstallFailed)),
            SettingsWindow = () => _settingsWindow,
            FloatingEnabled = () => Settings.Tray.Placement != FileTrayPlacement.Notch,
            ShowIsland = ShowIslandShelf,
            IsIslandBusy = () => Decks.Any(deck => deck is not null && deck.ShelfInteractionBusy),
            HotkeyConflicts = ShelfHotkeyConflicts,
            AnimationsEnabled = () => Settings.Animations && Settings.Tray.Mode == ModuleMode.Full,
            AutomaticEnabled = () => Settings.Tray.Mode == ModuleMode.Full,
            ThumbnailLimit = () => ModuleRuntimePolicy.For(Settings.Tray.Mode).ThumbnailPixels,
            Exit = ExitApplication,
            GetStartWithWindows = () => Settings.LaunchAtStartup,
            SetStartWithWindows = enabled =>
            {
                try { StartupRegistration.SetEnabled(enabled); var next = Settings.Copy(); next.LaunchAtStartup = enabled; UpdateSettings(next); return true; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
            },
        });
        Shelves.SettingsChanged += ShelfSettingsChanged;
        ShelfTray = new ShelfTrayAdapter(Shelves.Shelves);
        // 旧岛内列表只用于迁移原路径及元数据；运行时的唯一内容集合是原版 ViewModel。
        foreach (var item in _trayStore!.Load().Items)
        {
            var read = ShelfTray.Model.AddFiles([item.Path]);
            if (read.Added == 0) continue;
            var added = ShelfTray.Items[^1]; added.Pinned = item.Pinned; added.AddedAt = item.AddedAt;
        }
        ShelfTray.PersistedChanged += SaveIslandShelf; ShelfTray.Changed += RefreshIslandShelf;
        ApplyShelfSettings();
        Shelves.Start(startInBackground: true);
    }

    internal static string ShelfMessageWindow => "BrimDeck.Shelves." + Environment.UserName;

    private void ShelfSettingsChanged(object? sender, EventArgs e)
    {
        if (Exiting || ModulesChanging) return;
        if (!ShelfRunning || Shelves is null) { OpenShelfSettings(); return; }
        foreach (var deck in Decks.ToList()) deck.TrayChanged();
    }

    private void ApplyShelfSettings()
    {
        if (Shelves is null) return;
        LanguageService.SetMode(Loc.Normalize(Settings.Language) == "zh-CN" ? AppLanguage.Chinese : AppLanguage.English);
        Shelves.RefreshHostState();
        PublishShelfTheme();
    }

    private void SaveIslandShelf()
    {
        if (Exiting || ModulesChanging) return;
        // 只持久化普通原路径；临时附件和文本不进入旧持久化格式，也不伪装成永久文件。
        _trayStore?.Save(IslandShelfSnapshot());
    }
    private FileTray IslandShelfSnapshot()
    {
        var snapshot = new FileTray();
        foreach (var item in ShelfTray.Items.Where(item => item.IsFileSystem && !item.IsTemporaryAttachment))
        {
            if (snapshot.Add([item.Value], item.AddedAt) > 0) snapshot.Items[^1].Pinned = item.Pinned;
        }
        return snapshot;
    }
    private void RefreshIslandShelf()
    {
        if (Exiting || ModulesChanging) return;
        foreach (var deck in Decks.ToList()) deck.TrayChanged();
    }

    private void ShowIslandShelf()
    {
        if (Exiting || ModulesChanging || !ShelfRunning || Settings.Tray.Placement == FileTrayPlacement.Floating) return;
        var monitor = GlobalHotkeys.CursorMonitor();
        var deck = Decks.FirstOrDefault(panel => panel.HotkeyMonitor == monitor) ?? Deck;
        deck.SelectPage(DeckPage.FileTray);
        deck.OpenFromHotkey();
    }

    private bool ShelfHotkeyConflicts(DropShelf.Models.HotkeyGesture gesture) =>
        new[] { Settings.HideHotkey, Settings.ExpandHotkey, Settings.ResizeHotkey, Settings.Clipboard.Hotkey }
            .Any(text => HotkeyText.Overlap(HostShelfHotkey(gesture), text));

    private static string HostShelfHotkey(DropShelf.Models.HotkeyGesture gesture) =>
        HotkeyText.Format((gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? HotkeyModifiers.Ctrl : 0) |
            (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? HotkeyModifiers.Alt : 0) |
            (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? HotkeyModifiers.Shift : 0) |
            (gesture.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? HotkeyModifiers.Win : 0), gesture.Key.ToString());

    public void OpenShelf()
    {
        if (Exiting || ModulesChanging) return;
        if (!ShelfRunning || Shelves is null) { OpenShelfSettings(); return; }
        Shelves.Shelves.OpenActive(activate: true);
    }
    public void NewShelf()
    {
        if (Exiting || ModulesChanging) return;
        if (!ShelfRunning || Shelves is null) { OpenShelfSettings(); return; }
        Shelves.Shelves.OpenAtCursor(activate: true);
    }
    public void OpenShelfSettings() => OpenShelfSettings("Shelf");
    public void OpenShelfSettings(string section)
    {
        if (Exiting || ModulesChanging) return;
        OpenSettings();
        _settingsWindow?.ShowShelfSection(section);
    }
    private void DisposeShelves()
    {
        if (Shelves is null) return;
        Shelves.SettingsChanged -= ShelfSettingsChanged;
        ShelfTray.PersistedChanged -= SaveIslandShelf; ShelfTray.Changed -= RefreshIslandShelf; ShelfTray.Dispose();
        Shelves.Dispose(); Shelves = null; ShelfTray = null!;
    }
}
