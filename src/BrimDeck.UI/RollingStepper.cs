using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public sealed class RollingStepper : Grid
{
    public sealed record EditDraft(string Text, int Start, int Length, bool Focused);
    private readonly Border _chrome, _numberArea;
    private readonly RepeatButton _less, _more;
    private readonly RollingDigitsView _digits = new(24);
    private readonly TextBox _editor;
    private readonly string _label;
    private bool _editing, _dragging, _coasting, _keyAdjusting;
    public bool IsAdjusting => _editing || _dragging || _coasting || _keyAdjusting || _less.IsPressed || _more.IsPressed;
    private double _holdSince, _dragY, _dragAt, _dragRemainder, _velocity, _coastAt;
    public int Minimum { get; }
    public int Maximum { get; }
    public int Value { get; private set; }
    public event Action<int>? ValueChanged;
    public EditDraft? Draft => _editing ? new(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, _editor.IsKeyboardFocusWithin) : null;
    public EditDraft? TakeDraft()
    {
        var draft = Draft;
        // A leaving editor must not commit when the layout takes away its keyboard focus.
        _editing = false; return draft;
    }
    public void RestoreDraft(EditDraft draft)
    {
        _editing = true; _digits.Visibility = Visibility.Hidden; _editor.Visibility = Visibility.Visible;
        _editor.Text = draft.Text;
        _editor.Select(Math.Min(draft.Start, draft.Text.Length), Math.Min(draft.Length, Math.Max(0, draft.Text.Length - draft.Start)));
        if (!int.TryParse(draft.Text, out int number) || number < Minimum || number > Maximum)
            _chrome.BorderBrush = UI.Brush("#E78284");
        if (draft.Focused) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        { if (IsLoaded && IsVisible && _editing) _editor.Focus(); });
    }

    public RollingStepper(FrameworkElement owner, string label, int value, int minimum, int maximum)
    {
        Minimum = minimum; Maximum = maximum; Value = Math.Clamp(value, minimum, maximum); _label = label;
        Width = 128; Height = 42; Focusable = true;
        AutomationProperties.SetName(this, label);
        _chrome = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
        _chrome.SetResourceReference(Border.BackgroundProperty, "PanelFill"); Children.Add(_chrome);
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        _chrome.Child = row;
        _less = MakeButton(owner, "−", -1); _more = MakeButton(owner, "+", 1);
        row.Children.Add(_less); SetColumn(_more, 2); row.Children.Add(_more);
        var center = new Grid();
        _digits.Foreground = UI.Brush(UI.Primary); _digits.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(_digits);
        _editor = new TextBox { Visibility = Visibility.Collapsed, FontFamily = UI.PanelFont, FontSize = 22, TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent };
        _editor.SetResourceReference(Control.ForegroundProperty, "PanelPrimary"); AutomationProperties.SetName(_editor, label);
        center.Children.Add(_editor);
        _numberArea = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Child = center, Cursor = Cursors.SizeNS,
            ToolTip = Loc.T("上下拖动调整；双击输入；方向键微调", "Drag vertically, double-click to type, or use arrow keys") };
        SetColumn(_numberArea, 1); row.Children.Add(_numberArea);
        _numberArea.MouseLeftButtonDown += Down; _numberArea.MouseMove += Move; _numberArea.MouseLeftButtonUp += Up;
        _numberArea.LostMouseCapture += (_, _) => { _dragging = false; };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (CommitEdit()) Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelEdit(); Focus(); e.Handled = true; }
        };
        _editor.LostKeyboardFocus += (_, _) => { if (_editing) CommitEdit(); };
        PreviewKeyDown += HandleKey;
        KeyUp += (_, _) => _keyAdjusting = false;
        MouseWheel += (_, e) => { if (!IsKeyboardFocusWithin || _editing) return; Bump(Math.Sign(e.Delta)); e.Handled = true; };
        IsKeyboardFocusWithinChanged += (_, _) =>
        { if (!IsKeyboardFocusWithin) _keyAdjusting = false;
            if (!_editing) _chrome.BorderBrush = IsKeyboardFocusWithin ? UI.Brush(UI.Accent) : Brushes.Transparent; };
        Unloaded += (_, _) => { _coasting = _dragging = false; if (_numberArea.IsMouseCaptured) _numberArea.ReleaseMouseCapture(); };
        SetValue(Value, false);
    }
    private RepeatButton MakeButton(FrameworkElement owner, string text, int direction)
    {
        var button = new RepeatButton { Content = text, FontSize = 18, Delay = 380, Interval = 90, ClickMode = ClickMode.Press,
            Style = owner.TryFindResource("TimerStepButton") as Style };
        AutomationProperties.SetName(button, _label + (direction < 0 ? Loc.T(" 减少", " decrease") : Loc.T(" 增加", " increase")));
        button.PreviewMouseLeftButtonDown += (_, _) => { Window.GetWindow(this)?.Activate(); button.Focus(); _holdSince = Eased.Now; _coasting = false; };
        button.PreviewKeyDown += (_, e) => { if (!e.IsRepeat) _holdSince = Eased.Now; };
        button.Click += (_, e) =>
        {
            double held = button.IsPressed ? Eased.Now - _holdSince : 0;
            button.Interval = held > .9 ? 55 : 90; Bump(direction * (held > 1.5 ? 10 : 1)); e.Handled = true;
        };
        return button;
    }
    public void SetValue(int value, bool notify = true)
    {
        int next = Math.Clamp(value, Minimum, Maximum); bool changed = next != Value; Value = next;
        _digits.Foreground = UI.Brush(UI.Primary); _digits.Text = Value.ToString();
        _less.IsEnabled = Value > Minimum; _more.IsEnabled = Value < Maximum;
        if (changed && notify) ValueChanged?.Invoke(Value);
    }
    public void Fit(double width, double height)
    {
        Width = width; Height = height;
        _digits.FontSize = height < 38 ? 16 : 24;
        _editor.FontSize = height < 38 ? 16 : 22;
        _chrome.Padding = new Thickness(height < 38 ? 1 : 3);
        _chrome.CornerRadius = new CornerRadius(height < 38 ? 9 : 12);
    }
    private void Bump(int amount)
    { if (_editing && !CommitEdit()) return; SetValue(Value + amount); }
    public void BeginEdit()
    {
        Window.GetWindow(this)?.Activate();
        if (_editing) { _editor.Focus(); _editor.SelectAll(); return; }
        _coasting = _dragging = false;
        if (_numberArea.IsMouseCaptured) _numberArea.ReleaseMouseCapture();
        _editing = true; _digits.Visibility = Visibility.Hidden; _editor.Visibility = Visibility.Visible;
        _editor.Text = Value.ToString(); _editor.Focus(); _editor.SelectAll();
    }
    public bool CommitEdit()
    {
        if (!_editing) return true;
        if (!int.TryParse(_editor.Text, out int value) || value < Minimum || value > Maximum)
        {
            _chrome.BorderThickness = new Thickness(1); _chrome.BorderBrush = UI.Brush("#E78284");
            _editor.ToolTip = Loc.T($"请输入 {Minimum}–{Maximum} 的整数", $"Enter a whole number from {Minimum} to {Maximum}"); return false;
        }
        _editing = false; _editor.Visibility = Visibility.Collapsed; _digits.Visibility = Visibility.Visible;
        _chrome.BorderBrush = IsKeyboardFocusWithin ? UI.Brush(UI.Accent) : Brushes.Transparent; _editor.ToolTip = null; SetValue(value); return true;
    }
    public bool CancelAdjustment()
    {
        if (!IsAdjusting) return false;
        _dragging = _coasting = _keyAdjusting = false;
        if (_numberArea.IsMouseCaptured) _numberArea.ReleaseMouseCapture();
        if (_less.IsMouseCaptured) _less.ReleaseMouseCapture();
        if (_more.IsMouseCaptured) _more.ReleaseMouseCapture();
        CancelEdit(); Focus(); return true;
    }
    private void CancelEdit()
    { _editing = false; _editor.Visibility = Visibility.Collapsed; _digits.Visibility = Visibility.Visible; _chrome.BorderBrush = Brushes.Transparent; }
    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (_editing) return;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        if (e.Key is Key.Up or Key.Right) Bump(step);
        else if (e.Key is Key.Down or Key.Left) Bump(-step);
        else if (e.Key == Key.PageUp) Bump(10);
        else if (e.Key == Key.PageDown) Bump(-10);
        else if (e.Key == Key.Home) SetValue(Minimum);
        else if (e.Key == Key.End) SetValue(Maximum);
        else if (e.Key is Key.Enter or Key.F2) BeginEdit();
        else return;
        _keyAdjusting = !_editing; e.Handled = true;
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (_editing) return;
        if (e.ClickCount >= 2) { BeginEdit(); e.Handled = true; return; }
        Window.GetWindow(this)?.Activate(); Focus(); _coasting = false; _dragging = true; _dragY = e.GetPosition(this).Y; _dragAt = Eased.Now; _dragRemainder = _velocity = 0;
        _numberArea.CaptureMouse(); e.Handled = true;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        double y = e.GetPosition(this).Y, now = Eased.Now, dy = _dragY - y;
        _velocity = _velocity * .6 + dy / Math.Max(.001, now - _dragAt) * .4;
        _dragY = y; _dragAt = now; DragSteps(dy); e.Handled = true;
    }
    private void DragSteps(double pixels)
    {
        _dragRemainder += pixels;
        int steps = (int)(_dragRemainder / 12);
        if (steps == 0) return;
        _dragRemainder -= steps * 12; SetValue(Value + steps);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; _numberArea.ReleaseMouseCapture();
        double now = Eased.Now;
        _velocity = now - _dragAt > .09 ? 0 : _velocity / 60;
        if (!Soft.Enabled || Math.Abs(_velocity) < 2) return;
        _coasting = true; _coastAt = now;
        Motion.Start(this, at =>
        {
            if (!_coasting || !IsLoaded || !IsVisible || !Soft.Enabled) return false;
            double dt = Math.Clamp((at - _coastAt) * 60, 0, 2.5); _coastAt = at;
            int before = Value; DragSteps(_velocity * dt); _velocity *= Math.Pow(.94, dt);
            if (Math.Abs(_velocity) < .4 || before == Value && (Value == Minimum || Value == Maximum)) _coasting = false;
            return _coasting;
        });
        e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new StepperPeer(this);
    private sealed class StepperPeer(RollingStepper owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPattern(pattern);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 10;
        public double SmallChange => 1;
        public double Maximum => owner.Maximum;
        public double Minimum => owner.Minimum;
        public double Value => owner.Value;
        public void SetValue(double value)
        {
            if (IsReadOnly) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.SetValue((int)Math.Round(value));
        }
    }
}
