// EmojiPicker / catalog.ts translation from Edge-Drop 767b6d63, Apache-2.0.
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed partial class ClipboardView
{
    private sealed record EmojiEntry(string Unified, string Name, string Category, int Sort, string[] Skins)
    {
        internal string Native => NativeOf(Unified);
        internal static string NativeOf(string unified) => string.Concat(unified.Split('-').Select(hex => char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
    }
    private static readonly Lazy<EmojiEntry[]> EmojiCatalog = new(() =>
    {
        using var stream = typeof(ClipboardView).Assembly.GetManifestResourceStream("BrimDeck.EdgeDrop.Emoji")!;
        using var json = JsonDocument.Parse(stream);
        bool Available(JsonElement entry) => !entry.TryGetProperty("has_img_twitter", out var flag) || flag.ValueKind != JsonValueKind.False;
        string Value(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return json.RootElement.EnumerateArray().Where(entry => Available(entry) && Value(entry, "category") != "Component" && Value(entry, "obsoleted_by").Length == 0)
            .Select(entry => new EmojiEntry(Value(entry, "unified"), Value(entry, "name") is { Length: > 0 } name ? name : Value(entry, "short_name"), Value(entry, "category"),
                entry.TryGetProperty("sort_order", out var sort) ? sort.GetInt32() : 9999,
                entry.TryGetProperty("skin_variations", out var skins) ? skins.EnumerateObject().Where(skin => Available(skin.Value)).OrderBy(skin => skin.Name, StringComparer.Ordinal).Select(skin => Value(skin.Value, "unified")).ToArray() : []))
            .Where(entry => entry.Unified.Length > 0 && entry.Category.Length > 0).OrderBy(entry => entry.Sort).ThenBy(entry => entry.Unified, StringComparer.Ordinal).ToArray();
    });
    private static readonly List<string> EmojiRecents = [];
    private Border? _emojiCategories;
    private ListBox? _emojiList;
    private readonly System.Collections.ObjectModel.ObservableCollection<EmojiRow> _emojiRows = [];
    private readonly List<(Button Button, int Category)> _emojiCategoryButtons = [];
    private sealed record EmojiRow(EmojiEntry[] Entries);
    private int _emojiCategory = 1, _emojiColumns;
    private static readonly string[][] EmojiSources = [[], ["Smileys & Emotion", "People & Body"], ["Animals & Nature"], ["Food & Drink"], ["Travel & Places"], ["Activities"], ["Objects"], ["Symbols", "Flags"]];
    private void SetEmojiOpen(bool open)
    {
        _emojiOpen = open;
        if (open && _emojiList is null) BuildEmojiPicker();
        if (_emojiList is not null) _emojiList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (_emojiCategories is not null) _emojiCategories.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FitDockHeader();
        if (_list is not null) _list.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (_emptyHost is not null) _emptyHost.Visibility = !open && ResultCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (open) { _emojiColumns = 0; FitEmojiGrid(); }
        RefreshDockHeader(ResultCount);
    }
    private void BuildEmojiPicker()
    {
        var categories = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        string[] glyphs = ["clockCounterClockwise", "smiley", "pawPrint", "forkKnife", "globe", "trophy", "lightbulb", "shapes"];
        string[] names = [Loc.T("最近使用", "Recents"), Loc.T("表情与人物", "Smileys and people"), Loc.T("动物与自然", "Animals and nature"), Loc.T("食物与饮品", "Food and drink"), Loc.T("旅行与地点", "Travel and places"), Loc.T("活动", "Activities"), Loc.T("物品", "Objects"), Loc.T("符号与旗帜", "Symbols and flags")];
        for (int i = 0; i < glyphs.Length; i++)
        {
            int category = i; var button = Round(EdgeDropGlyph.Create(glyphs[i], 14), names[i], () => { _emojiCategory = category; RebuildEmojiRows(); });
            button.Width = 28; categories.Children.Add(button); _emojiCategoryButtons.Add((button, category));
        }
        var categoryScroll = new ScrollViewer { Content = categories, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        categoryScroll.PreviewMouseWheel += (_, e) => { if (e.Delta < 0) categoryScroll.LineRight(); else categoryScroll.LineLeft(); e.Handled = true; };
        _emojiCategories = new Border { Child = categoryScroll, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        _emojiCategories.SetResourceReference(Border.BackgroundProperty, "PanelSurface"); Grid.SetColumnSpan(_emojiCategories, 2); _dockCenter.Children.Add(_emojiCategories);
        _emojiList = new ListBox { ItemsSource = _emojiRows, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 0, 8, 0), Focusable = false };
        _emojiList.SetValue(VirtualizingPanel.IsVirtualizingProperty, true); _emojiList.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _emojiList.SetValue(ScrollViewer.CanContentScrollProperty, true); _emojiList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var row = new FrameworkElementFactory(typeof(EmojiRowView)); _emojiList.ItemTemplate = new DataTemplate { VisualTree = row };
        Grid.SetRow(_emojiList, 3); Children.Add(_emojiList); AutomationProperties.SetAutomationId(_emojiList, "clipboard-emoji-grid");
        _emojiList.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: EmojiEntry entry } button) return;
            if (entry.Skins.Length == 0) CopyEmoji(entry.Unified);
            else
            {
                var menu = Menu();
                foreach (string unified in new[] { entry.Unified }.Concat(entry.Skins)) AddMenu(menu, EmojiEntry.NativeOf(unified), () => CopyEmoji(unified));
                menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
            }
            e.Handled = true;
        }));
        FitDockHeader();
    }
    // Native WPF glyphs replace Twemoji artwork; selection still copies the original Unicode through the host's protected path.
    private void CopyEmoji(string unified)
    {
        EmojiRecents.Remove(unified); EmojiRecents.Insert(0, unified); if (EmojiRecents.Count > 77) EmojiRecents.RemoveRange(77, EmojiRecents.Count - 77);
        _activate(new ClipboardEntry { Content = ClipboardContent.FromText(EmojiEntry.NativeOf(unified)) }, true);
        if (_emojiCategory == 0) RebuildEmojiRows();
    }
    private void FitEmojiGrid()
    {
        if (!_emojiOpen || _emojiList is null) return;
        int columns = Math.Max(8, (int)Math.Floor((ActualWidth - 16) / 36));
        if (columns == _emojiColumns) return;
        _emojiColumns = columns; RebuildEmojiRows();
    }
    private void RebuildEmojiRows()
    {
        if (_emojiList is null) return;
        foreach (var (button, category) in _emojiCategoryButtons) { button.Tag = category == _emojiCategory ? "Selected" : null; button.Visibility = category == 0 && EmojiRecents.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        IEnumerable<EmojiEntry> entries = _emojiCategory == 0 ? EmojiRecents.Select(unified => EmojiCatalog.Value.FirstOrDefault(entry => entry.Unified == unified || entry.Skins.Contains(unified)) is { } parent ? parent with { Unified = unified, Skins = [] } : null).OfType<EmojiEntry>()
            : EmojiCatalog.Value.Where(entry => EmojiSources[_emojiCategory].Contains(entry.Category));
        _emojiRows.Clear(); foreach (var row in entries.Chunk(Math.Max(8, _emojiColumns))) _emojiRows.Add(new(row));
        _emojiList.ScrollIntoView(_emojiRows.FirstOrDefault());
    }
    public sealed class EmojiRowView : StackPanel
    {
        public EmojiRowView() { Orientation = Orientation.Horizontal; Height = 32; DataContextChanged += (_, _) => Build(); }
        private void Build()
        {
            Children.Clear(); if (DataContext is not EmojiRow row) return;
            foreach (var entry in row.Entries)
            {
                var button = new Button { Width = 36, Height = 32, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 22, Content = entry.Native, ToolTip = entry.Name, Tag = entry, Focusable = false };
                button.SetResourceReference(StyleProperty, "PanelIconButton"); AutomationProperties.SetName(button, entry.Name); Children.Add(button);
            }
        }
    }
}
