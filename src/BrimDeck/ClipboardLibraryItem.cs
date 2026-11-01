using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

internal sealed class ClipboardLibraryItem : Grid
{
    internal static readonly RoutedEvent ActionRequestedEvent = EventManager.RegisterRoutedEvent("ActionRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ClipboardLibraryItem));
    internal string RequestedAction { get; private set; } = "";
    internal ClipboardEntry? Entry => DataContext as ClipboardEntry;
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Soft.ReducedProperty && IsLoaded) Build();
    }
    public ClipboardLibraryItem()
    {
        DataContextChanged += (_, _) => Build();
        Unloaded += (_, _) => Children.Clear();
        Loaded += (_, _) => { if (Children.Count == 0) Build(); };
    }
    private void Build()
    {
        Children.Clear(); ColumnDefinitions.Clear();
        if (Entry is not { } entry) return;
        ColumnDefinitions.Add(new() { Width = new GridLength(100) }); ColumnDefinitions.Add(new());
        var preview = new Border { Width = 90, Height = 84, ClipToBounds = true, Child = ClipboardPreview.Create(entry.Content, mode: Soft.GetReduced(this) ? ModuleMode.Performance : ModuleMode.Full, deferred: true), VerticalAlignment = VerticalAlignment.Top };
        preview.SetResourceReference(Border.BackgroundProperty, "PanelSwitchFill"); preview.SetResourceReference(Border.CornerRadiusProperty, "PanelR6");
        Children.Add(preview);
        var body = new Grid(); Grid.SetColumn(body, 1); Children.Add(body);
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = ClipboardPreview.Text((entry.Favorite ? "★  " : "") + ClipboardHistory.DisplayTitle(entry), 12.5);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.FontWeight = FontWeights.SemiBold;
        body.Children.Add(title);
        var meta = ClipboardPreview.Text(ClipboardContentTypes.Detail(entry.Content) + " · " + (entry.SourceName.Length > 0 ? entry.SourceName : Loc.T("未知来源", "Unknown source")) + " · " + entry.CopiedAt.ToLocalTime().ToString("HH:mm"), 10.5, "PanelTertiary");
        meta.TextWrapping = TextWrapping.NoWrap; meta.TextTrimming = TextTrimming.CharacterEllipsis; meta.Margin = new Thickness(0, 5, 0, 0); Grid.SetRow(meta, 1); body.Children.Add(meta);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true }; Grid.SetRow(footer, 2); body.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        foreach (var (id, label) in new[] { ("copy", Loc.T("复制", "Copy")), ("paste", Loc.T("回贴", "Paste")) })
        {
            var button = new Button { Content = label, Style = (Style)FindResource("LibraryButton"), Height = 24, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetName(button, label + " · " + ClipboardHistory.DisplayTitle(entry));
            button.Click += (_, e) => { RequestedAction = id; RaiseEvent(new RoutedEventArgs(ActionRequestedEvent, this)); e.Handled = true; };
            actions.Children.Add(button);
        }
        string tags = string.Join("  ", entry.Tags.Select(tag => "#" + tag));
        var note = ClipboardPreview.Text(tags.Length > 0 ? tags : entry.Notes, 10.5, "PanelSecondary");
        note.TextWrapping = TextWrapping.NoWrap; note.TextTrimming = TextTrimming.CharacterEllipsis;
        note.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(note);
        AutomationProperties.SetName(this, ClipboardHistory.DisplayTitle(entry) + " · " + ClipboardContentTypes.Label(entry.Content.Kind));
    }
}
