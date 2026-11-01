using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A draft is view state, not another history entry. Both hosts use this inspector and commit through History only once.
internal sealed class ClipboardEntryEditor : StackPanel
{
    private readonly ClipboardHistory _history;
    private readonly Func<ClipboardSettings> _settings;
    private readonly Action<ClipboardEntry, bool> _activate;
    private readonly Func<string, bool> _confirm;
    private TextBox? _title, _tags, _notes, _content;
    private ClipboardEntry? _entry;
    private Border? _preview;
    private ModuleMode? _previewMode;
    internal void RefreshPresentation()
    {
        if (_preview is null || _entry is null || _previewMode == _settings().Mode) return;
        _previewMode = _settings().Mode;
        _preview.Child = ClipboardPreview.Create(_entry.Content, true, _previewMode.Value, deferred: true);
    }
    private bool _loading, _saving;
    internal bool IsDirty { get; private set; }
    internal bool IsNew { get; private set; }
    internal bool IsSaving => _saving;
    internal string? EntryId => _entry?.Id;
    internal event Action<string>? Feedback, Saved;
    internal event Action? Removed;

    internal ClipboardEntryEditor(ClipboardHistory history, Func<ClipboardSettings> settings,
        Action<ClipboardEntry, bool> activate, Func<string, bool> confirm)
    {
        _history = history; _settings = settings; _activate = activate; _confirm = confirm;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ClipboardEntryEditor).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
    }
    internal bool TryDiscard()
    {
        if (IsDirty && !_confirm(Loc.T("放弃尚未保存的修改？", "Discard unsaved changes?"))) return false;
        IsDirty = false; return true;
    }
    internal void RefreshEntry()
    {
        if (!_saving && !IsDirty && !IsNew) Load(_history.Items.FirstOrDefault(item => item.Id == EntryId));
    }
    internal void FocusEditor() => _title?.Focus();
    internal void Load(ClipboardEntry? entry, bool create = false)
    {
        // Metadata refreshes keep the same immutable payload and the reader's preview/scroll ownership.
        var preview = entry is not null && ReferenceEquals(_entry?.Content, entry.Content) && _previewMode == _settings().Mode ? _preview : null;
        _loading = true; Children.Clear(); _entry = entry; IsNew = create; IsDirty = false;
        _title = _tags = _notes = _content = null; _preview = null; _previewMode = _settings().Mode;
        try
        {
            Children.Add(ClipboardPreview.Text(create ? Loc.T("新建片段 / 常用回复", "New snippet / saved reply") : Loc.T("条目详情", "Entry details"), 14));
            if (entry is null && !create)
            {
                var hint = ClipboardPreview.Text(Loc.T("选择条目查看预览、来源、分类和备注。", "Select an entry to see its preview, source, collection and notes."), 12, "PanelTertiary");
                hint.Margin = new Thickness(0, 16, 0, 0); Children.Add(hint); return;
            }
            if (entry is not null)
            {
                _preview = preview ?? new Border { Height = 140, Margin = new Thickness(0, 12, 0, 10), Child = ClipboardPreview.Create(entry.Content, true, _settings().Mode, deferred: true), ClipToBounds = true };
                _preview.SetResourceReference(Border.BackgroundProperty, "PanelFill"); _preview.SetResourceReference(Border.CornerRadiusProperty, "PanelR8"); Children.Add(_preview);
                var metadata = ClipboardPreview.Text(ClipboardContentTypes.Detail(entry.Content) + "\n" + Loc.T("来源：", "Source: ") + (entry.SourceName.Length > 0 ? entry.SourceName : Loc.T("未知", "Unknown"))
                    + "\n" + Loc.T("创建：", "Created: ") + entry.CreatedAt.ToLocalTime().ToString("g") + "\n" + Loc.T("复制：", "Copied: ") + entry.CopiedAt.ToLocalTime().ToString("g")
                    + (entry.EditedAt is { } edited ? "\n" + Loc.T("修改：", "Edited: ") + edited.ToLocalTime().ToString("g") : "")
                    + "\n" + Loc.T($"已复用 {entry.UseCount} 次", $"Reused {entry.UseCount} times"), 10.5, "PanelTertiary");
                Children.Add(metadata);
                var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                actions.Children.Add(Button(Loc.T("复制", "Copy"), () => Recall(true)));
                actions.Children.Add(Button(Loc.T("回贴", "Paste"), () => Recall(false)));
                actions.Children.Add(Button(entry.Favorite ? Loc.T("取消收藏", "Unfavorite") : Loc.T("收藏", "Favorite"), () => _history.ToggleFavorite(entry.Id)));
                actions.Children.Add(Button(entry.Archived ? Loc.T("取消归档", "Unarchive") : Loc.T("归档", "Archive"), () => { if (TryDiscard()) _history.Archive(entry.Id, !entry.Archived); }));
                Children.Add(actions);
                Label(Loc.T("移至分类", "Move to collection"));
                // A dirty draft keeps its controls while collections can be reordered or deleted in the other surface.
                // Resolve the displayed choice by its stable identity, not by an index into the changing shared list.
                var collections = _history.Collections.ToList();
                var collection = new ComboBox { Style = (Style)FindResource("LibraryCombo"), Height = 30, FontSize = 12, ItemsSource = new[] { Loc.T("自动分类 / 历史", "Automatic / History") }.Concat(collections.Select(group => group.Name)).ToList() };
                AutomationProperties.SetName(collection, Loc.T("移至分类", "Move to collection")); PopupMotion.Attach(collection);
                collection.SelectedIndex = Math.Max(0, collections.FindIndex(group => group.Id == entry.CollectionId) + 1);
                collection.SelectionChanged += (_, _) => { if (!_loading && collection.SelectedIndex >= 0) _history.Move(entry.Id, collection.SelectedIndex == 0 ? "" : collections[collection.SelectedIndex - 1].Id); };
                Children.Add(collection);
            }
            Label(Loc.T("名称", "Name")); _title = Field("title", Loc.T("条目名称", "Entry name"), 160, entry?.Title ?? ""); Children.Add(_title);
            Label(Loc.T("标签（逗号分隔）", "Tags (comma separated)")); _tags = Field("tags", Loc.T("标签", "Tags"), 1300, string.Join(", ", entry?.Tags ?? [])); Children.Add(_tags);
            Label(Loc.T("备注", "Notes")); _notes = Field("notes", Loc.T("备注", "Notes"), 4096, entry?.Notes ?? ""); _notes.Height = 68; _notes.AcceptsReturn = true; _notes.TextWrapping = TextWrapping.Wrap; Children.Add(_notes);
            if (create || entry?.Content.Kind is not (ClipboardContentKind.Image or ClipboardContentKind.Screenshot or ClipboardContentKind.Files))
            {
                Label(Loc.T("内容（改动富文本将保存为纯文本）", "Content (editing rich text saves plain text)"));
                _content = Field("content", Loc.T("编辑内容", "Edit content"), 1_000_000, entry?.Content.Text ?? "");
                _content.Height = 160; _content.AcceptsReturn = true; _content.AcceptsTab = true; _content.TextWrapping = TextWrapping.Wrap; _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                if (entry?.Content.Kind is ClipboardContentKind.Code or ClipboardContentKind.Svg) _content.FontFamily = new FontFamily("Cascadia Mono, Consolas"); Children.Add(_content);
            }
            // Keep the destructive action outside the commit row, including when translated labels wrap in a narrow inspector.
            var saveRow = new WrapPanel { Margin = new Thickness(0, 16, 0, 8), HorizontalAlignment = HorizontalAlignment.Right };
            saveRow.Children.Add(Button(Loc.T("取消修改", "Cancel changes"), () => { if (TryDiscard()) Load(_history.Items.FirstOrDefault(item => item.Id == EntryId), IsNew); }));
            saveRow.Children.Add(Button(Loc.T("保存修改", "Save changes"), () => Save(), "LibraryPrimaryButton")); Children.Add(saveRow);
            if (entry is not null)
            {
                var danger = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 4, 0, 8) };
                danger.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder");
                var delete = Button(Loc.T("删除记录…", "Delete entry…"), Delete, "LibraryDangerButton"); delete.HorizontalAlignment = HorizontalAlignment.Left; danger.Child = delete;
                Children.Add(danger);
            }
        }
        finally { _loading = false; }
    }
    internal bool Save()
    {
        if (_title is null || _notes is null || _tags is null) return false;
        var current = _history.Items.FirstOrDefault(item => item.Id == EntryId);
        if (!IsNew && current is not null && _entry is not null
            && (current.EditedAt != _entry.EditedAt || current.Content != _entry.Content || current.Title != _entry.Title || current.Notes != _entry.Notes || !current.Tags.SequenceEqual(_entry.Tags)))
        { Feedback?.Invoke(Loc.T("此记录已在另一视图中修改。草稿已保留，请取消修改后重新编辑。", "This entry changed in another view. Your draft is kept; cancel changes to reload before editing.")); return false; }
        var content = _entry?.Content ?? ClipboardContent.FromText("");
        if (_content is not null && _content.Text != content.Text) content = ClipboardContent.FromText(_content.Text);
        string[] tags = _tags.Text.Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string problem = "", id = EntryId ?? ""; bool saved;
        _saving = true;
        try
        {
            if (IsNew)
            {
                var created = _history.CreateSnippet(_title.Text, _notes.Text, tags, content, DateTimeOffset.UtcNow, _settings(), out problem);
                saved = created is not null; if (created is not null) id = created.Id;
            }
            else saved = _history.Update(id, _title.Text, _notes.Text, tags, content, DateTimeOffset.UtcNow, out problem);
        }
        finally { _saving = false; }
        if (!saved) { Feedback?.Invoke(problem.Length > 0 ? problem : Loc.T("未能保存，请检查内容或容量。", "Could not save; check content and capacity.")); return false; }
        Load(_history.Items.FirstOrDefault(item => item.Id == id)); Saved?.Invoke(id);
        Feedback?.Invoke(Loc.T("已保存，灵动岛与管理窗口已同步。", "Saved and synchronized across the island and library.")); return true;
    }
    internal void Delete()
    {
        if (EntryId is not { } id || !_confirm(Loc.T("删除此记录及其备注？原文件和系统剪贴板不会更改，此操作不可撤销。", "Delete this entry and its notes? Original files and the system clipboard are unchanged. This cannot be undone."))) return;
        IsDirty = false; IsNew = false; Load(null); _history.Remove(id); Removed?.Invoke();
    }
    private void Recall(bool copyOnly) { if (_history.Items.FirstOrDefault(item => item.Id == EntryId) is { } item) _activate(item, copyOnly); }
    private TextBox Field(string id, string name, int limit, string text)
    {
        var field = new TextBox { Style = (Style)FindResource("LibraryField"), MaxLength = limit, Text = text, MinHeight = 30, ToolTip = name };
        AutomationProperties.SetName(field, name); AutomationProperties.SetAutomationId(field, "clipboard-edit-" + id);
        field.TextChanged += (_, _) => { if (!_loading) { IsDirty = true; Feedback?.Invoke(Loc.T("有未保存的修改 · Ctrl+S 保存", "Unsaved changes · Ctrl+S to save")); } }; return field;
    }
    private Button Button(string label, Action action, string style = "LibraryButton")
    {
        var button = new Button { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 3, 3) };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private void Label(string label) { var text = ClipboardPreview.Text(label, 10.5, "PanelTertiary"); text.Margin = new Thickness(0, 12, 0, 5); Children.Add(text); }
}
