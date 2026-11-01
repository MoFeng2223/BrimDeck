using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DropShelf.Interop;
using DropShelf.Localization;
using DropShelf.Services;
using DropShelf.ViewModels;

namespace DropShelf.Views;

/// <summary>在宿主设置视口中复用原版功能模型、编辑器及拖动交互；不创建另一个管理窗口。</summary>
public partial class ShelfSettingsContent : UserControl, IDisposable
{
    private readonly AppController _controller;
    private readonly DependencyPropertyDescriptor _recording = DependencyPropertyDescriptor.FromProperty(Controls.HotkeyBox.IsRecordingProperty, typeof(Controls.HotkeyBox));
    private bool _disposed, _wasRecording;
    public SettingsViewModel ViewModel { get; }
    public string Section { get; private set; } = "Shelf";
    public bool IsModal => ViewModel.IsEditorOpen || _pendingDelete is not null;
    public event Action<bool>? RecordingChanged;
    public event Action<bool>? ModalChanged;
    public Action<UIElement>? EnterContent { get; set; }

    public ShelfSettingsContent(AppController controller)
    {
        HostedResources.Attach(this);
        InitializeComponent();
        _controller = controller;
        DataContext = ViewModel = new SettingsViewModel(controller);
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _recording.AddValueChanged(HotkeyEditor, RecordingStateChanged);
        ViewModel.PropertyChanged += ViewModelChanged;
        LanguageService.LanguageChanged += LanguageChanged;
        Unloaded += (_, _) => Deactivate();
        RefreshText();
    }

    public void ApplyResources(ResourceDictionary palette)
    {
        foreach (var key in palette.Keys.Cast<object>()) Resources[key] = palette[key];
    }

    public void SetFloatingEnabled(bool enabled)
    {
        FloatingOptions.IsEnabled = enabled;
        PlacementNote.Text = enabled ? Strings.HostFloatingDescription : Strings.HostIslandDescription;
    }

    public bool OpenSection(string section)
    {
        if (section is not ("Shelf" or "Actions" or "History" or "Help")) section = "Shelf";
        if (IsModal) return false;
        EndCardDrag(); EndHistoryDrag(cancel: true); HotkeyEditor.CancelRecording();
        Section = section;
        ShelfScroll.Visibility = section == "Shelf" ? Visibility.Visible : Visibility.Collapsed;
        ActionsScroll.Visibility = section == "Actions" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = section == "History" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = section == "Help" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in SectionTabs.Children.OfType<Button>())
            button.Style = (Style)FindResource(Equals(button.Tag, section) ? "AccentButtonStyle" : typeof(Button));
        EnterContent?.Invoke(section switch { "Actions" => ActionsScroll, "History" => HistoryPage, "Help" => HelpPage, _ => ShelfScroll });
        return true;
    }

    private void OnSectionClick(object sender, RoutedEventArgs e) => OpenSection((string)((Button)sender).Tag);
    private void LanguageChanged(object? sender, EventArgs e) => RefreshText();
    private void RefreshText()
    {
        CacheHelp.Text = Strings.HostCacheDescription;
        ActionHelp.Text = Strings.OnboardingActionsBody(ViewModel.Hotkey.IsEmpty ? null : ViewModel.Hotkey.ToString());
        SetFloatingEnabled(FloatingOptions.IsEnabled);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.HotkeyDescription)) RefreshText();
        if (e.PropertyName == nameof(SettingsViewModel.IsEditorOpen))
        {
            bool editing = ViewModel.IsEditorOpen;
            SectionTabs.IsEnabled = !editing;
            ModalChanged?.Invoke(editing);
            if (editing) { EnterContent?.Invoke(EditorDialog); Dispatcher.BeginInvoke(() => EditorNameBox.Focus()); }
        }
    }

    private void RecordingStateChanged(object? sender, EventArgs e)
    {
        if (_wasRecording == HotkeyEditor.IsRecording) return;
        _wasRecording = HotkeyEditor.IsRecording;
        RecordingChanged?.Invoke(_wasRecording);
    }

    private void OnDeleteActionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ActionCardViewModel card) ConfirmDeleteCustomAction(card);
        e.Handled = true;
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor is not { } editor) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.ChooseTargetFolder, Multiselect = false };
        if (Directory.Exists(editor.Folder)) dialog.InitialDirectory = editor.Folder;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) editor.Folder = dialog.FolderName;
    }

    private void OnBrowseProgramClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Editor is not { } editor) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Strings.ChooseProgram, Filter = Strings.ProgramFilter, DereferenceLinks = true };
        if (File.Exists(editor.ProgramPath)) dialog.InitialDirectory = Path.GetDirectoryName(editor.ProgramPath);
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) editor.ProgramPath = dialog.FileName;
    }

    private void OnAddBlockedAppClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddBlockedAppButton, Placement = PlacementMode.Bottom };
        HostedResources.Attach(menu);
        foreach (var app in ForegroundAppWatcher.GetWindowedApps().Except(ViewModel.BlockedApps, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem { Header = app.Replace("_", "__") };
            item.Click += (_, _) => ViewModel.AddBlockedApp(app); menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = Strings.BrowseApp };
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Strings.ChooseProgram, Filter = Strings.AppFilter, DereferenceLinks = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) ViewModel.AddBlockedApp(Path.GetFileName(dialog.FileName));
        };
        menu.Items.Add(browse); menu.IsOpen = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyEditor.IsRecording)
        {
            if (_draggingCard) { EndCardDrag(); e.Handled = true; }
            else if (_draggingHistory) { EndHistoryDrag(cancel: true); e.Handled = true; }
            else if (_pendingDelete is not null) { CloseConfirm(); e.Handled = true; }
            else if (ViewModel.Editor is { } editor) { editor.CancelCommand.Execute(null); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>临时切页只结束视图交互；最终关闭才解绑模型，避免主题重建后订阅丢失。</summary>
    public void Deactivate()
    {
        EndCardDrag(); EndHistoryDrag(cancel: true);
        HotkeyEditor.CancelRecording();
        if (_wasRecording) { _wasRecording = false; RecordingChanged?.Invoke(false); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Deactivate();
        CloseConfirm(); ViewModel.Editor?.CancelCommand.Execute(null);
        _recording.RemoveValueChanged(HotkeyEditor, RecordingStateChanged);
        ViewModel.PropertyChanged -= ViewModelChanged;
        LanguageService.LanguageChanged -= LanguageChanged;
        ViewModel.Detach(); _ghost?.Close();
    }

    private static T? FindAncestor<T>(DependencyObject? source, Func<T, bool>? match = null) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T candidate && (match is null || match(candidate))) return candidate;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private (Image Image, Point Position)? Snapshot(FrameworkElement element) => !element.IsVisible || element.ActualWidth < 1 ? null
        : (new Image { Source = VisualCapture.Of(element), Width = element.ActualWidth, Height = element.ActualHeight }, element.TranslatePoint(default, DragLayer));
    private static DoubleAnimation Motion(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(ShelfMotion.Enabled ? 240 : 0))
        { EasingFunction = HostedResources.MotionEase ?? new CubicEase { EasingMode = EasingMode.EaseOut } };
}
