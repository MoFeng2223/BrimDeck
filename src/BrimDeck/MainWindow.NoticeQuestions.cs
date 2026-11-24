using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
using BrimDeck.Core.Agents;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck;

// The notices page's answers to a sender's questions (a coding agent's multiple-choice question): each question with its options,
// picked by a click, and a box for words of the user's own where the question allows them; "提交" sends every answer to the
// sender that waits. The form for a notice is built once and kept while its sender waits, so what was picked and typed stays when
// the page is drawn again; while the box has the keyboard the page is not drawn again at all (QuestionFormKeepsPage).
public partial class MainWindow
{
    private readonly Dictionary<string, QuestionForm> _questionForms = new(StringComparer.Ordinal);

    // The form for the notice in front, or null when it asks nothing or nobody waits for the answers any more.
    private FrameworkElement? QuestionFormFor(Notice notice, NoticeCenter center)
    {
        foreach (var id in _questionForms.Keys.Where(id => center.Hub.Find(id) is not { Awaited: true }).ToList()) _questionForms.Remove(id);
        if (notice.Questions.Count == 0 || !notice.Awaited || !center.Awaits(notice)) return null;
        if (!_questionForms.TryGetValue(notice.Id, out var form))
            _questionForms[notice.Id] = form = new QuestionForm(notice, Look, AgendaKeyboard, answers => SubmitAnswers(notice, answers));
        if (form.Parent is Panel panel) panel.Children.Remove(form);
        else if (form.Parent is ContentControl holder) holder.Content = null;
        return new ScrollViewer { Content = form, Margin = new Thickness(0, 12, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, PanningMode = PanningMode.VerticalOnly, Background = Brushes.Transparent };
    }

    // While the user types an answer for the notice in front, the page is not drawn again for a tick or a change of data: a rebuild
    // would take the keyboard and the input method's words from the box.
    private bool QuestionFormKeepsPage(NoticeCenter center)
        => FocusedNotice(center) is { Awaited: true } notice && _questionForms.TryGetValue(notice.Id, out var form) && form.IsKeyboardFocusWithin;

    private void SubmitAnswers(Notice notice, List<NoticeAnswer> answers)
    {
        bool typing = _questionForms.TryGetValue(notice.Id, out var form) && form.IsKeyboardFocusWithin;
        _app.Notices.Answer(notice, answers);
        _questionForms.Remove(notice.Id);
        if (typing) { Keyboard.ClearFocus(); EndTyping(); }
        RenderNotices();
    }

    // The status line of a notice whose sender waits: an approval in the caution color, a question in the accent's.
    private static (string Text, string? Color)? WaitingStatus(Notice notice)
    {
        if (!notice.Awaited) return null;
        if (notice.Questions.Count > 0) return (Loc.T("需要选择", "Needs a choice"), PanelTheme.Hex("accent"));
        if (AgentKeys.Ask(notice.ReplaceKey) is not null) return (Loc.T("等待审批", "Waiting for approval"), PanelTheme.Hex("caution"));
        return null;
    }

    // A command or a path the notice is about, in a framed block of monospaced text; selectable, never run.
    private FrameworkElement NoticeCodeBlock(string code)
    {
        var text = new TextBox
        {
            Style = (Style)FindResource("PanelSelectableText"), Text = code, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), FontSize = 12,
            Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight
        };
        text.PreviewMouseLeftButtonDown += (_, _) => TakeKeyboardForWords();
        AutomationProperties.SetName(text, Loc.T("命令：", "Command: ") + code);
        return new Border
        {
            Child = text, Background = UI.Brush(UI.Fill), BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PanelTheme.Corner(8)), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 8, 0, 0), MaxHeight = 140
        };
    }
}

// The questions of one notice and what the user picked and typed for each.
internal sealed class QuestionForm : StackPanel
{
    private readonly List<Item> _items = [];
    private readonly Button _submit;
    private readonly Border _submitFace;
    private readonly Action<List<NoticeAnswer>> _submitted;

    private sealed class Item(NoticeQuestion question)
    {
        public NoticeQuestion Question { get; } = question;
        public HashSet<string> Picked { get; } = new(StringComparer.Ordinal);
        public List<(string Key, Border Row, Border Mark)> Options { get; } = [];
        public TextBox? Typed { get; set; }
        public bool Answered => Picked.Count > 0 || Typed is { Text.Length: > 0 } box && box.Text.Trim().Length > 0;
    }

    public QuestionForm(Notice notice, PanelLook look, Action<TextBox> keyboard, Action<List<NoticeAnswer>> submitted)
    {
        _submitted = submitted;
        foreach (var question in notice.Questions)
        {
            var item = new Item(question);
            _items.Add(item);
            if (Children.Count > 0) Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, 10, 0, 10) });
            if (question.Header is { Length: > 0 } header && notice.Questions.Count > 1)
                Children.Add(UI.Fixed(header, 11, 16, UI.Tertiary, FontWeights.SemiBold));
            Children.Add(new TextBlock { Text = question.Question, FontFamily = UI.PanelFont, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
            if (question.MultiSelect == true) Children.Add(UI.Fixed(Loc.T("可多选", "Pick any"), 11, 16, UI.Tertiary));
            foreach (var option in question.Options ?? []) Children.Add(OptionRow(item, option, look));
            if (question.AllowOther == true) Children.Add(OtherBox(item, look, keyboard, question.Options is { Count: > 0 }));
        }
        _submitFace = new Border { CornerRadius = new CornerRadius(PanelTheme.Corner(16)), Background = UI.Brush(UI.Primary), Padding = new Thickness(18, 0, 18, 0), Height = 32 };
        var label = UI.InkCentered(Loc.T("提交", "Submit"), 12.5, 32, UI.Primary, FontWeights.SemiBold);
        label.Foreground = new SolidColorBrush(PanelTheme.ColorOf("surface"));
        _submitFace.Child = label;
        _submit = new Button
        {
            Content = _submitFace, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 4),
            FocusVisualStyle = (Style)Application.Current.FindResource("PanelPillKeyboardFocus"), Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        AutomationProperties.SetName(_submit, Loc.T("提交回答", "Submit the answers"));
        _submit.Click += (_, _) => Submit();
        Children.Add(_submit);
        Update();
    }

    // An option: a round mark (a square one for several picks), its label and its description under it.
    private Border OptionRow(Item item, NoticeOption option, PanelLook look)
    {
        bool multi = item.Question.MultiSelect == true;
        var mark = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(multi ? 3 : 7), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = option.Label, FontFamily = UI.PanelFont, FontSize = 13, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.Wrap });
        if (option.Description is { Length: > 0 } description)
            words.Children.Add(new TextBlock { Text = description, FontFamily = UI.PanelFont, FontSize = 11.5, Foreground = UI.Brush(UI.Secondary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        var line = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left); line.Children.Add(mark); line.Children.Add(words);
        var row = new Border { Child = line, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(PanelTheme.Corner(10)), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2) };
        item.Options.Add((option.Key, row, mark));
        row.MouseEnter += (_, _) => Paint(item);
        row.MouseLeave += (_, _) => Paint(item);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (multi) { if (!item.Picked.Remove(option.Key)) item.Picked.Add(option.Key); }
            else
            {
                bool again = item.Picked.Contains(option.Key);
                item.Picked.Clear();
                if (!again) item.Picked.Add(option.Key);
                // One answer to a single-choice question: an option picked takes the place of words typed.
                if (item.Typed is { } typed) typed.Text = "";
            }
            Update();
        };
        AutomationProperties.SetName(row, option.Label + (option.Description is { Length: > 0 } d ? "，" + d : ""));
        return row;
    }

    // The box for words of the user's own, with a hint while it is empty. Enter submits once every question is answered.
    private FrameworkElement OtherBox(Item item, PanelLook look, Action<TextBox> keyboard, bool besideOptions)
    {
        var box = AgendaFields.Box(look, keyboard, 12.5);
        box.AcceptsReturn = false; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 32; box.Padding = new Thickness(9, 6, 9, 6);
        box.Background = UI.Brush(UI.Fill);
        item.Typed = box;
        string hint = besideOptions ? Loc.T("其他：输入你的回答", "Other: type your answer") : Loc.T("输入你的回答", "Type your answer");
        AutomationProperties.SetName(box, hint);
        var shown = AgendaFields.Hint(box, hint, UI.Tertiary);
        box.TextChanged += (_, _) =>
        {
            shown.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            // Words typed for a single-choice question are its answer in place of an option.
            if (box.Text.Trim().Length > 0 && item.Question.MultiSelect != true) item.Picked.Clear();
            Update();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; if (_items.All(i => i.Answered)) Submit(); } };
        var holder = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        holder.Children.Add(box); holder.Children.Add(shown);
        return holder;
    }

    private void Paint(Item item)
    {
        foreach (var (key, row, mark) in item.Options)
        {
            bool on = item.Picked.Contains(key);
            mark.BorderBrush = UI.Brush(on ? PanelTheme.Hex("accent") : UI.Tertiary);
            mark.Background = on ? UI.Brush(PanelTheme.Hex("accent")) : Brushes.Transparent;
            row.Background = UI.Brush(on ? UI.FillSelected : row.IsMouseOver ? UI.Fill : "#00000000");
        }
    }

    private void Update()
    {
        foreach (var item in _items) Paint(item);
        bool ready = _items.All(i => i.Answered);
        _submit.IsEnabled = ready;
        _submitFace.Opacity = ready ? 1 : .4;
    }

    private void Submit()
    {
        if (!_items.All(i => i.Answered)) return;
        _submitted([.. _items.Select(item => new NoticeAnswer
        {
            Id = item.Question.Id, Keys = [.. item.Picked], Text = item.Typed is { } box && box.Text.Trim().Length > 0 ? box.Text.Trim() : null
        })]);
    }
}
