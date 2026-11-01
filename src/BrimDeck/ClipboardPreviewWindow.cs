using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf.Views;

namespace BrimDeck;

// Manual previews are independent tool windows. The island's automatic Popup, focus and lifetime never own this surface.
internal sealed class ClipboardPreviewWindow : ShelfToolWindow
{
    private ClipboardEntry _entry;
    private ClipboardContent? _content;
    private ModuleMode? _mode;
    private readonly Func<ClipboardEntry, bool, bool> _activate;
    private readonly Func<ClipboardSettings> _preferences;
    private readonly ClipboardDragDrop _drag;
    private readonly Grid _layout;
    private readonly ContentControl _previewBody;
    private readonly TextBlock _entryTitle, _metadata, _feedback;
    private readonly Button _copy, _paste, _export;
    private readonly DispatcherTimer _notice = new() { Interval = TimeSpan.FromSeconds(3) };
    internal ClipboardPasteTarget PasteTarget { get; set; }
    internal ClipboardEntry Entry => _entry;
    internal FrameworkElement? PreviewBody => _previewBody.Content as FrameworkElement;

    internal ClipboardPreviewWindow(ClipboardHistory history, ClipboardEntry entry, Func<ClipboardSettings> preferences, Func<ClipboardEntry, bool, bool> activate)
    {
        _entry = entry; _preferences = preferences; _activate = activate;
        AnimationPolicy = () => Soft.Enabled && _preferences().Mode == ModuleMode.Full;
        _drag = new ClipboardDragDrop(history, feedback: ShowFeedback);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ClipboardPreviewWindow).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
        _layout = new Grid { Margin = new Thickness(10, 2, 10, 8) };
        _layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); _layout.RowDefinitions.Add(new()); _layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); _layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };
        _entryTitle = ClipboardPreview.Text("", 13); _entryTitle.FontWeight = FontWeights.SemiBold; _entryTitle.TextWrapping = TextWrapping.NoWrap; heading.Children.Add(_entryTitle);
        _metadata = ClipboardPreview.Text("", 10.5, "PanelTertiary"); _metadata.Margin = new Thickness(0, 3, 0, 0); _metadata.TextWrapping = TextWrapping.NoWrap; heading.Children.Add(_metadata);
        AutomationProperties.SetAutomationId(_metadata, "preview-window-metadata"); _layout.Children.Add(heading);
        _previewBody = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var frame = new Border { Child = _previewBody, Padding = new Thickness(4), BorderThickness = new Thickness(1), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelTrack"); frame.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); frame.SetResourceReference(Border.CornerRadiusProperty, "PanelR8");
        AutomationProperties.SetAutomationId(frame, "preview-window-body"); Grid.SetRow(frame, 1); _layout.Children.Add(frame);
        _feedback = ClipboardPreview.Text("", 10.5, "PanelSecondary"); _feedback.Margin = new Thickness(4, 4, 4, 0); _feedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_feedback, "preview-window-notice"); AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite); Grid.SetRow(_feedback, 2); _layout.Children.Add(_feedback);
        var actions = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 0) };
        _copy = ActionButton("copy", "copy", () => ActivateEntry(_entry, true)); _paste = ActionButton("paste", "clipboardText", () => ActivateEntry(_entry, false));
        _export = ActionButton("drag", "arrowSquareOut", () => { });
        foreach (var button in new[] { _copy, _paste, _export }) actions.Children.Add(button);
        Point pressed = default; bool pending = false;
        _export.PreviewMouseLeftButtonDown += (_, e) => { pressed = e.GetPosition(_export); pending = true; };
        _export.PreviewMouseLeftButtonUp += (_, _) => pending = false;
        _export.PreviewMouseMove += (_, e) =>
        {
            if (!pending || e.LeftButton != MouseButtonState.Pressed) { pending = false; return; }
            var delta = e.GetPosition(_export) - pressed;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pending = false; if (_export.IsMouseCaptureWithin) Mouse.Capture(null); e.Handled = true; _drag.Export(this, _entry);
        };
        Grid.SetRow(actions, 3); _layout.Children.Add(actions); PreviewContent = _layout;
        _notice.Tick += (_, _) => { _notice.Stop(); _feedback.Text = ""; _feedback.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Handled || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
            var key = HotkeyGesture.KeyOf(e);
            if (key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.None && !OnControl(e.OriginalSource as DependencyObject)))
            { ActivateEntry(_entry, Keyboard.Modifiers == ModifierKeys.Control || !_preferences().PasteOnActivate); e.Handled = true; }
            else if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBoxBase)
            { ActivateEntry(_entry, true); e.Handled = true; }
        };
        StateChanged += (_, _) => _previewBody.Visibility = WindowState == WindowState.Minimized ? Visibility.Collapsed : Visibility.Visible;
        Closed += (_, _) => { _notice.Stop(); _previewBody.Content = null; _content = null; };
        UpdateEntry(entry); RefreshText();
    }
    private Button ActionButton(string id, string glyph, Action action)
    {
        var button = new Button { Style = (Style)FindResource("SubtleButtonStyle"), Content = EdgeDropGlyph.Create(glyph, 16), Height = 34, MinHeight = 0, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 2, 0) };
        AutomationProperties.SetAutomationId(button, "preview-window-" + id); button.Click += (_, e) => { action(); e.Handled = true; }; return button;
    }
    internal void RefreshText()
    {
        SetCaption(Loc.T("预览浮窗", "Preview window"), Loc.T("置顶／取消置顶", "Toggle keep on top"), Loc.T("关闭预览浮窗", "Close preview window"));
        _entryTitle.Text = ClipboardHistory.DisplayTitle(_entry);
        _metadata.Text = string.Join(" · ", new[] { ClipboardContentTypes.Label(_entry.Content.Kind), _entry.SourceName, _entry.CopiedAt.ToLocalTime().ToString("g"), "×" + _entry.CopyCount }.Where(value => value.Length > 0));
        foreach (var (button, label) in new[] { (_copy, Loc.T("复制整条记录 · Ctrl+Enter", "Copy whole entry · Ctrl+Enter")), (_paste, Loc.T("回贴整条记录", "Paste whole entry")), (_export, Loc.T("按住拖出整条记录", "Drag out whole entry")) })
        { button.ToolTip = label; AutomationProperties.SetName(button, label); }
    }
    internal void UpdateEntry(ClipboardEntry entry)
    {
        _entry = entry; RefreshText();
        // Immutable history metadata updates share Content; keep decoded pixels and the reader's scroll position.
        Soft.SetReduced(this, _preferences().Mode == ModuleMode.Performance); RefreshMotionPolicy();
        if (ReferenceEquals(_content, entry.Content) && _mode == _preferences().Mode) return;
        _mode = _preferences().Mode;
        double offset = _previewBody.Content is ScrollViewer previous ? previous.VerticalOffset : 0;
        _content = entry.Content;
        if (entry.Content.Kind == ClipboardContentKind.Files)
        {
            var rows = new StackPanel();
            foreach (string path in entry.Content.Files)
            {
                var one = entry with { Content = new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [path] } };
                rows.Children.Add(IslandFilePreview.Row(path, () => ActivateEntry(one, true), () => _drag.Export(this, one)));
            }
            _previewBody.Content = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        else if (ClipboardPreview.Create(entry.Content, true, _preferences().Mode, deferred: true) is { } body)
        {
            if (body is Image image) { image.Stretch = Stretch.Uniform; image.HorizontalAlignment = HorizontalAlignment.Stretch; image.VerticalAlignment = VerticalAlignment.Stretch; }
            _previewBody.Content = body;
        }
        if (_previewBody.Content is ScrollViewer scroll) scroll.ScrollToVerticalOffset(offset);
    }
    internal void ShowNear(Rect card, Rect panel, double scale, bool activate = false)
    {
        SetContentScale(scale);
        var (work, dpi) = WorkArea(new Point(card.Left + card.Width / 2, card.Top + card.Height / 2));
        double inset = Math.Min(12 * dpi, Math.Min(work.Width, work.Height) / 8); work.Inflate(-inset, -inset);
        var size = new Size(Math.Min(440 * scale * dpi, work.Width), Math.Min(380 * scale * dpi, work.Height));
        var bounds = ClipboardView.PlacePreview(card, panel, work, size, 6 * dpi);
        ShowAt(bounds, activate);
    }
    private void ActivateEntry(ClipboardEntry entry, bool copyOnly)
    {
        if (!_activate(entry, copyOnly))
        {
            ShowFeedback(Loc.T("复制未完成，请检查原内容或源文件。", "Copy could not complete. Check the original content or source file."));
            return;
        }
        if (copyOnly) ShowFeedback(Loc.T("已复制，预览浮窗保持打开", "Copied; preview window stays open"));
        else CloseAnimated();
    }
    private static bool OnControl(DependencyObject? node)
    {
        while (node is not null) { if (node is ButtonBase or TextBoxBase) return true; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        return false;
    }
    internal void ShowFeedback(string text)
    {
        if (IsClosing) return;
        _notice.Stop(); _feedback.Text = text; _feedback.Visibility = Visibility.Visible; _notice.Start();
    }
}
