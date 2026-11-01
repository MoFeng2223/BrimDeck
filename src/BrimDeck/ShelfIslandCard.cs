// WPF translation of Edge-Drop typed ClipboardItem and item.css (767b6d63), Apache-2.0.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using DropShelf.Models;

namespace BrimDeck;

internal sealed class ShelfIslandCard : IslandContentCard
{
    private ShelfItem? _item;
    private string _contentKey = "", _language = "";
    private ImageSource? _thumbnail;
    private readonly TextBlock _detail = ClipboardPreview.Text("", 10, "PanelTertiary");
    private readonly Action<ShelfItem> _pin;
    private Grid? _dockFooter;
    private TextBlock? _dockLabel, _dockSize;
    private bool _dockMode;
    internal bool DockMode { get => _dockMode; set { if (_dockMode == value) return; _dockMode = value; _contentKey = ""; } }
    internal ShelfItem? Item => _item;
    internal ShelfIslandCard(Action<ShelfItem> pin, Action<ShelfItem> copy, Action<ShelfItem> remove)
    {
        _pin = pin;
        AddAction("⧉", Loc.T("复制", "Copy"), () => { if (_item is { } item) copy(item); });
        AddAction("×", Loc.T("移除记录", "Remove entry"), () => { if (_item is { } item) remove(item); });
        _detail.TextWrapping = TextWrapping.NoWrap; _detail.VerticalAlignment = VerticalAlignment.Center; Footer.Children.Add(_detail);
    }
    protected override void OnPin() { if (_item is { } item) _pin(item); }
    internal void Update(ShelfItem item, double width, double height)
    {
        _item = item; Tag = item; Width = width; Height = height;
        if (DockMode)
        {
            SetResourceReference(CornerRadiusProperty, "ClipboardDockCardRadius");
            Layers.RowDefinitions[1].Height = new GridLength(28); Footer.Margin = new Thickness(14, 2, 14, 8); Footer.Height = 18;
        }
        var mode = Soft.GetReduced(this) ? ModuleMode.Performance : ModuleMode.Full;
        string key = $"{item.Kind}|{item.Value}|{item.Title}|{item.Exists}|{mode}";
        if (_contentKey != key || !ReferenceEquals(_thumbnail, item.Thumbnail) || _language != Loc.Language)
        {
            _contentKey = key; _thumbnail = item.Thumbnail; _language = Loc.Language;
            if (item.Kind is ShelfItemKind.Text or ShelfItemKind.Link)
            {
                Body.Content = ClipboardPreview.Create(ClipboardContent.FromText(item.Value), mode: mode);
                if (DockMode && Body.Content is TextBlock text) { text.Margin = new Thickness(14, 10, 14, 2); text.FontSize = 12; text.LineHeight = 12 * 1.34; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; text.MaxHeight = 5 * text.LineHeight; text.VerticalAlignment = VerticalAlignment.Top; }
            }
            else if (DockMode && item.Exists && item.Kind == ShelfItemKind.File && IslandFilePreview.IsImage(item.Value) && item.Thumbnail is { } thumbnail)
                Body.Content = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            else
            {
                var body = new Grid { Margin = new Thickness(10, 8, 10, 2) };
                body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var art = IslandFilePreview.Art(item.Value, Math.Clamp(height - 60, 28, 72), item.Thumbnail, item.Exists, item.Kind == ShelfItemKind.Folder, loadThumbnail: false);
                art.HorizontalAlignment = HorizontalAlignment.Center; art.VerticalAlignment = VerticalAlignment.Center; body.Children.Add(art);
                var name = ClipboardPreview.Text(item.DisplayName, 11.5, item.Exists ? "PanelPrimary" : "PanelTertiary");
                name.TextWrapping = TextWrapping.NoWrap; name.TextAlignment = TextAlignment.Center; name.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(name, 1); body.Children.Add(name); Body.Content = body;
            }
        }
        // Geometry changes do not reread files; the same art is resized in place.
        if (Body.Content is Grid picture && item.IsFileSystem && picture.Children.Count > 0 && picture.Children[0] is FrameworkElement artElement)
            artElement.Width = artElement.Height = Math.Clamp(height - 60, 28, 72);
        _detail.Text = item.Exists ? item.Detail : Loc.T("源文件已失效", "Original unavailable");
        if (DockMode)
        {
            if (_dockFooter is null)
            {
                Footer.Children.Clear(); Footer.ColumnDefinitions.Clear(); Footer.ColumnDefinitions.Add(new()); Footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                _dockFooter = new Grid(); _dockFooter.ColumnDefinitions.Add(new() { Width = new GridLength(17) }); _dockFooter.ColumnDefinitions.Add(new());
                string glyph = item.Kind switch { ShelfItemKind.Folder => "folderOpen", ShelfItemKind.Text => "textT", ShelfItemKind.Link => "link", _ => IslandFilePreview.IsImage(item.Value) ? "image" : "files" };
                var icon = EdgeDropGlyph.Create(glyph, 12); icon.HorizontalAlignment = HorizontalAlignment.Left; _dockFooter.Children.Add(icon);
                _dockLabel = ClipboardPreview.Text("", 10.5, "PanelSecondary"); _dockLabel.TextTrimming = TextTrimming.CharacterEllipsis; _dockLabel.TextWrapping = TextWrapping.NoWrap; _dockLabel.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(_dockLabel, 1); _dockFooter.Children.Add(_dockLabel); Footer.Children.Add(_dockFooter);
                _dockSize = ClipboardPreview.Text("", 10.5, "PanelTertiary"); _dockSize.VerticalAlignment = VerticalAlignment.Center; _dockSize.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(_dockSize, 1); Footer.Children.Add(_dockSize);
            }
            _dockLabel!.Text = item.IsFileSystem ? item.DisplayName : item.Kind == ShelfItemKind.Text ? Loc.T("文本", "Text") : Loc.T("链接", "Link");
            _dockSize!.Text = !item.Exists ? Loc.T("失效", "Missing") : item.SizeBytes is { } bytes ? ShelfItem.FormatSize(bytes) : item.IsTemporaryAttachment ? Loc.T("附件", "Attachment") : "";
        }
        SetPinned(item.Pinned, item.Pinned ? Loc.T("取消固定", "Unpin") : Loc.T("固定", "Pin"));
        ToolTip = DockMode ? null : item.Value + "\n" + _detail.Text;
        System.Windows.Automation.AutomationProperties.SetName(this, item.DisplayName + " · " + _detail.Text);
    }
}
