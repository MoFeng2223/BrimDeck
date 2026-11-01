using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private InputMethodCycle? _inputMethodCycle;
    private Border? _inputMethodHighlight;
    private TranslateTransform? _inputMethodHighlightMove;
    private Viewbox? _inputMethodListView;
    internal bool CanPresentInputMethods => IsVisible && !IsSuppressed && !_preview && !_poweringOff && !Resizing;

    internal void ShowInputMethodSelection(InputMethodCycle cycle)
    {
        if (!CanPresentInputMethods) return;
        if (!ReferenceEquals(_inputMethodCycle, cycle))
        {
            _inputMethodCycle = cycle;
            const double rowHeight = 48, verticalChrome = 82;
            double width = Math.Min(320, Math.Max(1, WorkDip.Width - 24));
            double height = Math.Min(verticalChrome + cycle.Items.Count * rowHeight, Math.Max(130, WorkDip.Height - 80));
            var root = new Grid { Margin = new Thickness(14, 8, 14, 10), Focusable = false };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var title = InputMethodText(Loc.T("输入法", "Input method"), 13, "PanelSecondary"); title.Margin = new Thickness(10, 0, 0, 0);
            root.Children.Add(title);
            // Every row has the same fixed columns. The whole list fits as one unit on a small
            // display; selecting another item never scrolls or changes any row's geometry.
            var list = new Grid { Width = Math.Max(1, width - 28), Height = cycle.Items.Count * rowHeight };
            _inputMethodHighlightMove = new TranslateTransform();
            _inputMethodHighlight = new Border { Height = 48, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = _inputMethodHighlightMove, IsHitTestVisible = false };
            _inputMethodHighlight.SetResourceReference(Border.BackgroundProperty, "PanelFillSelected");
            var selectionMark = InputMethodText("›", 18, "PanelPrimary"); selectionMark.HorizontalAlignment = HorizontalAlignment.Right; selectionMark.Margin = new Thickness(0, 0, 8, 0);
            _inputMethodHighlight.Child = selectionMark;
            list.Children.Add(_inputMethodHighlight);
            var rows = new StackPanel(); list.Children.Add(rows);
            foreach (var item in cycle.Items)
            {
                var row = new Grid { Height = 48, Margin = new Thickness(10, 0, 10, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                var glyph = InputMethodText(item.Name.EnumerateRunes().FirstOrDefault().ToString(), 14, "PanelPrimary");
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                var icon = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Child = glyph, HorizontalAlignment = HorizontalAlignment.Left };
                icon.SetResourceReference(Border.BackgroundProperty, "PanelFill"); row.Children.Add(icon);
                var name = InputMethodText(item.Name, 13, "PanelPrimary"); name.Margin = new Thickness(7, 0, 8, 0);
                name.ToolTip = item.Name;
                Grid.SetColumn(name, 1); row.Children.Add(name); AutomationProperties.SetName(row, item.Name);
                if (string.Equals(cycle.CurrentId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var current = InputMethodText(Loc.T("已启用", "Active"), 10.5, "PanelTertiary"); current.Margin = new Thickness(0, 0, 20, 0); Grid.SetColumn(current, 2); row.Children.Add(current);
                }
                rows.Children.Add(row);
            }
            _inputMethodListView = new Viewbox { Child = list, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(_inputMethodListView, 1); root.Children.Add(_inputMethodListView);
            var help = InputMethodText(InputMethodGesture.Hint(((App)Application.Current).InputMethods.Shortcut), 10.5, "PanelTertiary");
            help.ToolTip = help.Text;
            help.Margin = new Thickness(10, 4, 0, 0); Grid.SetRow(help, 2); root.Children.Add(help);
            ShowTransient(root, width, height);
            MoveInputMethodHighlight(cycle, instant: true);
        }
        else MoveInputMethodHighlight(cycle, instant: false);
    }

    private void MoveInputMethodHighlight(InputMethodCycle cycle, bool instant)
    {
        if (_inputMethodHighlightMove is null) return;
        double y = cycle.SelectedIndex * 48;
        if (instant || !Soft.Enabled) SetImmediate(_inputMethodHighlightMove, TranslateTransform.YProperty, y);
        else _inputMethodHighlightMove.BeginAnimation(TranslateTransform.YProperty, Soft.Fade(_inputMethodHighlightMove.Y, y, Soft.Choose, Soft.Follow));
    }

    private static TextBlock InputMethodText(string text, double size, string resource)
    {
        var block = new TextBlock { Text = text, FontFamily = UI.PanelFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Focusable = false };
        block.SetResourceReference(TextBlock.ForegroundProperty, resource); return block;
    }

    internal void EndInputMethodSelection() { _inputMethodCycle = null; EndTransient(); }
    internal void ShowInputMethodResult(string name, bool success) => ShowTransientNotice(name, success);
    internal void ClearInputMethodPresentation()
    {
        _inputMethodCycle = null;
        if (_transientNotice?.Module is not null) return;
        if (_transient is not null || _transientNotice is not null || _pendingTransientNotice is not null || _transientCompactShape is not null) ClearTransientPresentation();
    }
    internal void DismissInputMethodResult()
    {
        _pendingTransientNotice = null;
        if (_transientNotice is null) return;
        RestoreTransientContent(animate: true);
    }
}
