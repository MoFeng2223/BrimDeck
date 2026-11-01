using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck.Core;

namespace BrimDeck;

// Created lazily by the virtualized history strip; the only payload it draws is the entry's local content.
internal sealed class ClipboardCard : IslandContentCard
{
    internal static readonly RoutedEvent FavoriteRequestedEvent = EventManager.RegisterRoutedEvent("FavoriteRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ClipboardCard));
    internal static readonly RoutedEvent RecallRequestedEvent = EventManager.RegisterRoutedEvent("RecallRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ClipboardCard));
    internal bool RequestedCopyOnly { get; private set; }
    internal ClipboardEntry? Entry => DataContext as ClipboardEntry;
    internal static readonly RoutedEvent PreviewRequestedEvent = EventManager.RegisterRoutedEvent("PreviewRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ClipboardCard));
    internal static readonly DependencyProperty DockModeProperty = DependencyProperty.Register(nameof(DockMode), typeof(bool), typeof(ClipboardCard), new PropertyMetadata(false, (d, _) => { var card = (ClipboardCard)d; card._previewContent = null; card.Build(); }));
    public bool DockMode { get => (bool)GetValue(DockModeProperty); set => SetValue(DockModeProperty, value); }
    private ClipboardContent? _previewContent;
    private ModuleMode? _previewMode;
    private TextBlock? _time;
    private string _sourcePath = "";
    private readonly Image _source = new() { Width = 14, Height = 14 };
    public ClipboardCard()
    {
        AddAction("⧉", Loc.T("复制", "Copy"), () => Recall(true));
        AddAction("↵", Loc.T("回贴", "Paste"), () => Recall(false));
        AddAction("⤢", Loc.T("预览浮窗", "Preview window"), () => RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent, this)));
        DataContextChanged += (_, _) => Build();
        Loaded += (_, _) => Build();
        Unloaded += (_, _) => { Body.Content = null; _previewContent = null; };
    }
    private void Build()
    {
        if (Entry is not { } entry) return;
        var content = entry.Content;
        var mode = Soft.GetReduced(this) ? ModuleMode.Performance : ModuleMode.Full;
        if (!ReferenceEquals(_previewContent, content) || _previewMode != mode)
        { Body.Content = DockMode ? DockPreview(content, mode) : ClipboardPreview.Create(content, mode: mode, deferred: true); _previewContent = content; _previewMode = mode; }
        if (DockMode)
        {
            SetResourceReference(CornerRadiusProperty, "ClipboardDockCardRadius");
            Layers.RowDefinitions[1].Height = new GridLength(28);
            Footer.Margin = new Thickness(14, 2, 14, 8); Footer.Height = 18;
            BuildDockFooter(entry);
            SetPinned(entry.Favorite, entry.Favorite ? Loc.T("取消收藏", "Remove favorite") : Loc.T("收藏", "Favorite"));
            AutomationProperties.SetName(this, ClipboardContentTypes.Label(content.Kind) + ": " + ClipboardHistory.DisplayTitle(entry));
            // The island reveals inert content in its anchored flyout, not a second textual tooltip.
            ToolTip = null;
            return;
        }
        Footer.Children.Clear(); Footer.ColumnDefinitions.Clear();
        Footer.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); Footer.ColumnDefinitions.Add(new());
        if (_sourcePath != entry.SourcePath) { _source.Source = SourceIcon(entry.SourcePath); _sourcePath = entry.SourcePath; }
        Footer.Children.Add(_source);
        _time = Text(Age(entry.CopiedAt), 10, "PanelTertiary"); _time.VerticalAlignment = VerticalAlignment.Center; _time.TextWrapping = TextWrapping.NoWrap;
        _time.Margin = new Thickness(4, 0, 0, 0); _time.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(_time, 1); Footer.Children.Add(_time);
        SetPinned(entry.Favorite, entry.Favorite ? Loc.T("取消收藏", "Remove favorite") : Loc.T("收藏", "Favorite"));
        AutomationProperties.SetName(this, ClipboardContentTypes.Label(content.Kind) + ": " + ClipboardHistory.DisplayTitle(entry));
        ToolTip = ClipboardHistory.DisplayTitle(entry) + "\n" + ClipboardContentTypes.Detail(content) + " · " + Bytes(content.ByteCount) + "\n"
            + entry.SourceName + " · " + entry.CopiedAt.ToLocalTime().ToString("g");
    }
    // item.css horizontal: 10/14/2/14 content inset and five 1.34-line-height text rows.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Soft.ReducedProperty) Build();
    }
    private static FrameworkElement DockPreview(ClipboardContent content, ModuleMode mode)
    {
        var preview = ClipboardPreview.Create(content, mode: mode, deferred: true);
        if (content.Kind is ClipboardContentKind.Text or ClipboardContentKind.RichText or ClipboardContentKind.Code && preview is TextBlock text)
        {
            text.Margin = new Thickness(14, 10, 14, 2); text.FontSize = 12;
            text.LineHeight = 12 * 1.34; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            text.MaxHeight = 5 * text.LineHeight; text.VerticalAlignment = VerticalAlignment.Top;
        }
        return preview;
    }
    private void BuildDockFooter(ClipboardEntry entry)
    {
        Footer.Children.Clear(); Footer.ColumnDefinitions.Clear();
        Footer.ColumnDefinitions.Add(new()); Footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); Footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var meta = new Grid(); meta.ColumnDefinitions.Add(new() { Width = new GridLength(17) }); meta.ColumnDefinitions.Add(new());
        string glyph = entry.Content.Kind switch { ClipboardContentKind.Link => "link", ClipboardContentKind.Image or ClipboardContentKind.Screenshot => "image", ClipboardContentKind.Files => "files", ClipboardContentKind.Color => "palette", _ => "textT" };
        var icon = EdgeDropGlyph.Create(glyph, 12); icon.HorizontalAlignment = HorizontalAlignment.Left; meta.Children.Add(icon);
        string title = entry.Title.Length > 0 ? entry.Title : entry.Content.Kind == ClipboardContentKind.Files ? ClipboardHistory.DisplayTitle(entry) : ClipboardContentTypes.Label(entry.Content.Kind);
        var label = Text(title, 10.5, "PanelSecondary"); label.TextTrimming = TextTrimming.CharacterEllipsis; label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1); meta.Children.Add(label); Footer.Children.Add(meta);
        _time = Text(Age(entry.CopiedAt), 10.5, "PanelTertiary"); _time.FontWeight = FontWeights.Medium; _time.Margin = new Thickness(8, 0, 0, 0); _time.VerticalAlignment = VerticalAlignment.Center;
        _time.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(_time, "clipboard-card-time");
        AutomationProperties.SetAutomationId(meta, "clipboard-card-meta"); Grid.SetColumn(_time, 1); Footer.Children.Add(_time);
        // ClipboardItem.meta-hit-count translated to the requested trailing corner, separate from the clock.
        var count = Text("×" + entry.CopyCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 10.5, "PanelSecondary");
        count.FontWeight = FontWeights.Medium; count.Margin = new Thickness(6, 0, 0, 0); count.VerticalAlignment = VerticalAlignment.Center;
        count.Visibility = entry.CopyCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(count, Loc.T($"已复制 {entry.CopyCount} 次", $"Copied {entry.CopyCount} times"));
        AutomationProperties.SetAutomationId(count, "clipboard-card-copy-count"); Grid.SetColumn(count, 2); Footer.Children.Add(count);
    }
    protected override void OnPin() => RaiseEvent(new RoutedEventArgs(FavoriteRequestedEvent, this));
    private void Recall(bool copyOnly) { RequestedCopyOnly = copyOnly; RaiseEvent(new RoutedEventArgs(RecallRequestedEvent, this)); }
    internal void UpdateTime() { if (_time is not null && Entry is { } entry) _time.Text = Age(entry.CopiedAt); }
    internal static string Age(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age.TotalMinutes < 1) return Loc.T("刚刚", "Just now");
        if (age.TotalHours < 1) return Loc.T($"{(int)age.TotalMinutes} 分钟前", $"{(int)age.TotalMinutes} min ago");
        if (age.TotalDays < 1) return Loc.T($"{(int)age.TotalHours} 小时前", $"{(int)age.TotalHours} h ago");
        return Loc.T($"{(int)age.TotalDays} 天前", $"{(int)age.TotalDays} d ago");
    }
    private static string Bytes(long bytes) => bytes >= 1024 * 1024 ? (bytes / (1024d * 1024)).ToString("0.#") + " MB" : Math.Max(1, bytes / 1024) + " KB";
    private static TextBlock Text(string text, double size, string resource)
    {
        var block = new TextBlock { Text = text, FontSize = size }; block.SetResourceReference(TextBlock.ForegroundProperty, resource); return block;
    }
    private static BitmapSource? SourceIcon(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return null;
        try
        {
            if (SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try { var icon = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24)); icon.Freeze(); return icon; }
            finally { DestroyIcon(info.Icon); }
        }
        catch (Exception error) when (error is ArgumentException or IOException or ExternalException) { return null; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SHFILEINFO
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);
    [DllImport("user32")] private static extern bool DestroyIcon(IntPtr icon);
}
