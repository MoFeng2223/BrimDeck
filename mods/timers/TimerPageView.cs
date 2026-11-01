using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Loc = BrimDeck.ModuleLocalization;
using UI = BrimDeck.ModuleUi;
using Soft = BrimDeck.ModuleMotion;
using PanelTheme = BrimDeck.ModuleTheme;

namespace BrimDeck.Timers;

// A bounded page, not a scrolling form. The small form keeps every value visible and opens
// one deliberate editing view; larger forms lay out the same editors directly in cards.
internal sealed class TimerPageView : Grid, IModuleView
{
    private readonly TimerWorkspace _workspace;
    private readonly Action _openSettings;
    private readonly FrameworkElement _owner;
    private readonly Action _showControls;
    private readonly Grid _surface = new(), _leaving = new() { IsHitTestVisible = false };
    private readonly PageHandover _handover;
    private readonly List<RollingStepper> _inputs = [];
    private MiniAppsLayout _layout = MiniAppsLayout.For(480, 266);
    private TimerOptions? _renderedOptions;
    private TimerMode? _viewMode;
    private RollingDigitsView? _time;
    private TextBlock? _state;
    private ScaleTransform? _progress;
    private Grid? _body;
    private Grid? _lapRecords;
    private TextBlock? _lapPageLabel;
    private Button? _lapPrevious, _lapNext, _lapButton;
    private readonly List<TextBlock> _lapLabels = [];
    private string _lapKey = "";
    private string _key = "";
    private int _lapPage;
    private bool _building, _countdownEdit;
    private FieldSpec? _fieldEdit;
    private sealed record FieldSpec(string Id, string Label, int Value, int Min, int Max, Action<int> Set);

    internal TimerPageView(TimerWorkspace workspace, FrameworkElement owner, Action showControls, Action openSettings)
    {
        _workspace = workspace; _openSettings = openSettings;
        if (!workspace.Timer.Active) workspace.Options.ApplyDefaultMode(); _owner = owner; _showControls = showControls;
        Children.Add(_surface); Children.Add(_leaving); _handover = new(_leaving);
        SizeChanged += (_, _) => SetViewport(ActualWidth, ActualHeight);
        Unloaded += (_, _) => _handover.Drop();
        workspace.Changed += Update;
    }
    public bool IsAdjusting => _inputs.Any(input => input.IsAdjusting);
    public void SetViewport(double width, double height, double scale = 1)
    {
        if (width <= 0 || height <= 0) return;
        var next = MiniAppsLayout.For(width, height);
        if (_lapPage > 0 && next.LapRows != _layout.LapRows) _lapPage = _lapPage * _layout.LapRows / next.LapRows;
        _layout = next; Update();
    }
    public bool CancelAdjustment()
    {
        bool cancelled = false;
        foreach (var input in _inputs) cancelled |= input.CancelAdjustment();
        if (_fieldEdit is not null || _countdownEdit)
        { _fieldEdit = null; _countdownEdit = false; _key = ""; Update(); return true; }
        return cancelled;
    }
    public FrameworkElement Element => this;
    public void Refresh() => Update();
    public void Dispose() { _workspace.Changed -= Update; _handover.Drop(); foreach (var input in _inputs) input.CancelAdjustment(); Children.Clear(); }
    internal void Update()
    {
        if (_building) return;
        var timer = _workspace.Timer; var options = _workspace.Options;
        if (!timer.Active) _lapPage = 0;
        if (!SamePreferences(options, _renderedOptions)) _key = "";
        _renderedOptions = options.Copy();
        var mode = timer.Active ? timer.Mode : options.Mode;
        if (_viewMode != mode || timer.Active) { _fieldEdit = null; _countdownEdit = false; }
        else if (_fieldEdit is not null) _fieldEdit = PomodoroSpecs().FirstOrDefault(spec => spec.Id == _fieldEdit.Id);
        if (!timer.Active && _fieldEdit is null && !_countdownEdit)
        {
            var editing = _inputs.FirstOrDefault(input => input.Draft is not null);
            if (editing is not null && mode == TimerMode.Pomodoro && !_layout.FullFields)
                _fieldEdit = PomodoroSpecs().FirstOrDefault(spec => Equals(spec.Id, editing.Tag));
            else if (editing is not null && mode == TimerMode.Countdown && !_layout.CountdownFields) _countdownEdit = true;
        }
        string form = mode switch
        {
            TimerMode.Pomodoro when !timer.Active => $"{_layout.FullFields}/{_layout.FieldColumns}",
            TimerMode.Countdown when !timer.Active => $"{_layout.CountdownFields}/{_layout.DetailHeight >= 90}",
            TimerMode.Stopwatch when timer.Active => $"{_layout.LapRows}/{_layout.DetailHeight < 70}",
            _ => $"{_layout.DetailHeight < 50}"
        };
        string key = $"{mode}/{timer.Status}/{timer.Phase}/{Loc.Language}/{PanelTheme.Generation}/{Soft.Enabled}/" +
            $"{string.Join(',', options.Modes)}/{_layout.SideBySide}/{(_layout.SideBySide ? _layout.HeroHeight >= 120 ? 120 : 0 : _layout.HeroHeight)}/{_layout.Tabs}/{_layout.Gap}/{form}/{_fieldEdit?.Id}/{_countdownEdit}";
        if (key != _key)
        {
            _building = true;
            var drafts = _inputs.Select(input => (Label: (string)input.Tag, Draft: input.TakeDraft()))
                .Where(item => item.Draft is not null).ToDictionary(item => item.Label, item => item.Draft!);
            bool hadContent = _surface.Children.Count > 0;
            _handover.Hold(_surface); _inputs.Clear(); _time = null; _state = null; _progress = null;
            _lapRecords = null; _lapPageLabel = null; _lapPrevious = _lapNext = _lapButton = null; _lapLabels.Clear(); _lapKey = "";
            Build(mode); _viewMode = mode; _key = key;
            foreach (var input in _inputs)
                if (drafts.TryGetValue((string)input.Tag, out var draft)) input.RestoreDraft(draft);
            _building = false;
            _handover.Release(_surface, hadContent ? Handover.Morph : Handover.None);
        }
        ArrangeBody();
        if (_time is not null)
        {
            _time.Text = timer.Active ? TimerVisuals.Time(timer) : mode switch
            { TimerMode.Countdown => TimerSession.Format(options.CountdownSeconds, true), TimerMode.Pomodoro => TimerSession.Format(options.FocusMinutes * 60), _ => "0:00" };
            _time.Foreground = UI.Brush(TimerVisuals.Accent(timer));
        }
        if (_state is not null)
        {
            _state.Text = _workspace.SaveWarning.Length > 0 ? _workspace.SaveWarning : timer.Active
                ? TimerVisuals.PhaseName(timer) + (timer.Status == TimerStatus.Paused ? Loc.T(" · 已暂停", " · Paused") : "")
                : TimerVisuals.ModeName(mode);
            _state.ToolTip = _workspace.SaveWarning.Length > 0 ? _workspace.SaveWarning : _state.Text;
        }
        if (_progress is not null)
            ModuleMotion.Animate(_progress, ScaleTransform.ScaleXProperty, timer.Progress, Soft.Enabled && IsVisible, Soft.Quick);
        RefreshLaps();
    }

    private void Build(TimerMode mode)
    {
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = new GridLength(_layout.Tabs) });
        root.RowDefinitions.Add(new() { Height = new GridLength(_layout.Gap) }); root.RowDefinitions.Add(new());
        _surface.Children.Add(root);
        var navigation = new Grid(); navigation.ColumnDefinitions.Add(new()); navigation.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        bool editing = _fieldEdit is not null || _countdownEdit;
        if (editing)
        {
            var back = Action(Loc.T("返回", "Back"), FinishEdit, "‹"); back.Height = _layout.Tabs; tabs.Children.Add(back);
            var title = Label(_fieldEdit?.Label ?? Loc.T("调整倒计时", "Set countdown"), 12);
            title.Margin = new Thickness(8, 0, 0, 0); tabs.Children.Add(title);
        }
        else
            foreach (var available in _workspace.Options.Modes)
            {
                var button = Action(TimerVisuals.ModeName(available), () =>
                {
                    if (_workspace.Timer.Active) return;
                    _workspace.Options.Mode = available; _lapPage = 0; SavePreferences();
                });
                button.Background = available == mode ? UI.Brush(UI.FillSelected) : Brushes.Transparent;
                button.IsEnabled = !_workspace.Timer.Active || available == mode;
                button.Height = _layout.Tabs; button.Margin = new Thickness(0, 0, 4, 0); tabs.Children.Add(button);
            }
        navigation.Children.Add(tabs);
        root.Children.Add(navigation);
        _body = new Grid(); SetRow(_body, 2); root.Children.Add(_body);
        if (!_workspace.Options.Modes.Any())
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(Label(Loc.T("选择你需要的小组件", "Choose your widgets"), 14));
            empty.Children.Add(Action(Loc.T("打开小程序设置", "Open mini-app settings"), _openSettings));
            _body.Children.Add(empty); return;
        }
        if (editing) { BuildEditor(); return; }
        _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new());
        _body.RowDefinitions.Add(new()); _body.RowDefinitions.Add(new()); _body.RowDefinitions.Add(new());
        var hero = Hero(mode); _body.Children.Add(hero);
        var details = new Grid();
        if (_workspace.Timer.Active) ActiveDetails(details, mode);
        else if (mode == TimerMode.Pomodoro) PomodoroFields(details);
        else if (mode == TimerMode.Countdown) Countdown(details);
        else StopwatchInfo(details);
        details.Tag = "timer-details"; _body.Children.Add(details);
        ArrangeBody();
    }

    private void ArrangeBody()
    {
        if (_body is null || _body.ColumnDefinitions.Count == 0) return;
        var hero = (FrameworkElement)_body.Children[0]; var details = (FrameworkElement)_body.Children[1];
        if (_layout.SideBySide)
        {
            _body.ColumnDefinitions[0].Width = new GridLength(_layout.HeroWidth);
            _body.ColumnDefinitions[1].Width = new GridLength(_layout.Gap); _body.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _body.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            _body.RowDefinitions[1].Height = _body.RowDefinitions[2].Height = new GridLength(0);
            SetColumn(hero, 0); SetRow(hero, 0); SetColumn(details, 2); SetRow(details, 0);
        }
        else
        {
            _body.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _body.ColumnDefinitions[1].Width = _body.ColumnDefinitions[2].Width = new GridLength(0);
            _body.RowDefinitions[0].Height = new GridLength(_layout.HeroHeight);
            _body.RowDefinitions[1].Height = new GridLength(_layout.Gap); _body.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            SetColumn(hero, 0); SetRow(hero, 0); SetColumn(details, 0); SetRow(details, 2);
        }
    }

    private Border Hero(TimerMode mode)
    {
        var timer = _workspace.Timer;
        var row = new Grid();
        bool vertical = _layout.SideBySide && _layout.HeroHeight >= 120;
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = vertical ? new GridLength(0) : GridLength.Auto });
        if (vertical) { row.RowDefinitions.Add(new()); row.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        var timeGroup = new StackPanel { VerticalAlignment = VerticalAlignment.Center,
            Orientation = _layout.Slim ? Orientation.Horizontal : Orientation.Vertical };
        _state = Label("", _layout.Slim ? 11 : 12); _state.TextTrimming = TextTrimming.CharacterEllipsis;
        _time = new RollingDigitsView(_layout.Slim ? 22 : vertical ? 38 : _layout.SideBySide ? 24 : 32) { HorizontalAlignment = HorizontalAlignment.Left };
        if (_layout.Slim)
        { timeGroup.Children.Add(_time); _state.Margin = new Thickness(8, 0, 0, 0); timeGroup.Children.Add(_state); }
        else { timeGroup.Children.Add(_state); timeGroup.Children.Add(_time); }
        row.Children.Add(timeGroup);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string label, Action action, string glyph)
        {
            var button = Action(label, action, _layout.Slim || _layout.SideBySide && !vertical ? glyph : null);
            button.Margin = new Thickness(3, 0, 0, 0); actions.Children.Add(button);
        }
        if (timer.Active)
        {
            if (timer.Status != TimerStatus.Completed)
                Add(timer.Running ? Loc.T("暂停", "Pause") : timer.Status == TimerStatus.Waiting ? Loc.T("下一轮", "Next focus") : Loc.T("继续", "Resume"),
                    () => { if (timer.Running) timer.Pause(); else timer.Resume(); }, timer.Running ? "Ⅱ" : "▷");
            Add(Loc.T("控制条", "Controls"), _showControls, "↗");
            Add(Loc.T("结束", "End"), timer.Stop, "■");
        }
        else Add(Loc.T("开始", "Start"), Start, "▷");
        if (vertical) { SetRow(actions, 1); actions.Margin = new Thickness(0, 8, 0, 0); }
        else SetColumn(actions, 1);
        row.Children.Add(actions);
        // Keep a full-width progress rail in its own fixed row; digit/phase changes cannot move it.
        var content = new Grid(); content.Children.Add(row);
        if (timer.Active && mode != TimerMode.Stopwatch)
        {
            content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = new GridLength(2) });
            var track = new Border { CornerRadius = new CornerRadius(1) }; track.SetResourceReference(Border.BackgroundProperty, "PanelFillSelected");
            _progress = new ScaleTransform(timer.Progress, 1);
            track.Child = new Border { Background = UI.Brush(TimerVisuals.Accent(timer)), CornerRadius = new CornerRadius(1), RenderTransform = _progress };
            SetRow(track, 1); content.Children.Add(track);
        }
        return Card(content, _layout.Slim ? 2 : 6);
    }

    private FieldSpec[] PomodoroSpecs()
    {
        var o = _workspace.Options;
        return [new("focus", Loc.T("专注 · 分钟", "Focus · min"), o.FocusMinutes, 1, 180, value => _workspace.Options.FocusMinutes = value),
            new("short", Loc.T("短休息 · 分钟", "Short break · min"), o.ShortBreakMinutes, 1, 60, value => _workspace.Options.ShortBreakMinutes = value),
            new("long", Loc.T("长休息 · 分钟", "Long break · min"), o.LongBreakMinutes, 1, 120, value => _workspace.Options.LongBreakMinutes = value),
            new("rounds", Loc.T("长休息间隔 · 轮", "Sessions per long break"), o.Rounds, 1, 12, value => _workspace.Options.Rounds = value)];
    }
    private void PomodoroFields(Grid host)
    {
        var fields = new UniformGrid { Columns = _layout.FullFields ? _layout.FieldColumns : 4 };
        double cellHeight = _layout.DetailHeight / (_layout.FullFields && _layout.FieldColumns == 2 ? 2 : 1);
        foreach (var spec in PomodoroSpecs())
        {
            FrameworkElement content;
            if (_layout.FullFields)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(Label(spec.Label, 11));
                var input = Input(spec); input.Fit(128, cellHeight < 62 ? 30 : 38);
                input.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(input); content = stack;
            }
            else
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                string shortLabel = spec.Max == 12 ? Loc.T("间隔 · 轮", "Every · rounds") : spec.Label;
                // Compact labels retain the unit; the complete label remains the accessible name and tooltip.
                shortLabel = shortLabel.Replace("分钟", "分").Replace("Short break", "Short").Replace("Long break", "Long");
                stack.Children.Add(Label(shortLabel, 10)); stack.Children.Add(Label(spec.Value.ToString(), 16, "PanelPrimary"));
                content = Action(spec.Label, () => { _fieldEdit = spec; Update(); });
                ((Button)content).Content = stack; ((Button)content).Height = double.NaN;
                ((Button)content).HorizontalAlignment = HorizontalAlignment.Stretch;
                ((Button)content).Padding = new Thickness(2, 0, 2, 0);
            }
            var card = Card(content, _layout.FullFields ? 3 : 0); card.Margin = new Thickness(2);
            fields.Children.Add(card);
        }
        host.Children.Add(fields);
    }
    private void Countdown(Grid host)
    {
        if (!_layout.CountdownFields)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Presets()); var edit = Action(Loc.T("调整时长", "Set duration"), () => { _countdownEdit = true; Update(); });
            SetColumn(edit, 1); row.Children.Add(edit); host.Children.Add(row); return;
        }
        host.RowDefinitions.Add(new());
        bool presets = _layout.DetailHeight >= 90;
        host.RowDefinitions.Add(new() { Height = new GridLength(presets ? 30 : 0) });
        host.Children.Add(CountdownInputs());
        if (presets) { var row = Presets(); SetRow(row, 1); host.Children.Add(row); }
    }
    private FrameworkElement CountdownInputs()
    {
        var options = _workspace.Options;
        var fields = new UniformGrid { Columns = 3 };
        foreach (var (label, value, max) in new[] { (Loc.T("小时", "Hours"), options.CountdownSeconds / 3600, 99),
            (Loc.T("分钟", "Minutes"), options.CountdownSeconds / 60 % 60, 59), (Loc.T("秒", "Seconds"), options.CountdownSeconds % 60, 59) })
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Label(label, 11));
            var input = new RollingStepper(_owner, label, value, 0, max) { Tag = "duration-" + _inputs.Count }; input.Fit(112, 32);
            input.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(input); _inputs.Add(input);
            input.ValueChanged += _ =>
            {
                if (_inputs.Count != 3) return;
                int total = _inputs[0].Value * 3600 + _inputs[1].Value * 60 + _inputs[2].Value;
                if (total == 0) { _inputs[2].SetValue(1, false); total = 1; }
                _workspace.Options.CountdownSeconds = total; SavePreferences();
            };
            var card = Card(stack, 3); card.Margin = new Thickness(2); fields.Children.Add(card);
        }
        return fields;
    }
    private FrameworkElement Presets()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (int minutes in new[] { 1, 5, 10, 25 })
        {
            var button = Action($"{minutes} " + Loc.T("分", "min"), () =>
            {
                _workspace.Options.CountdownSeconds = minutes * 60;
                if (_inputs.Count == 3) { _inputs[0].SetValue(0, false); _inputs[1].SetValue(minutes, false); _inputs[2].SetValue(0, false); }
                SavePreferences();
            });
            button.Height = 26; button.Padding = new Thickness(7, 0, 7, 0); row.Children.Add(button);
        }
        return row;
    }
    private void StopwatchInfo(Grid host)
    {
        var options = _workspace.Options;
        var stats = new UniformGrid { Columns = 2 };
        stats.Children.Add(Stat(Loc.T("分段记录", "Lap recording"), options.StopwatchLaps ? Loc.T("已开启", "On") : Loc.T("已关闭", "Off")));
        stats.Children.Add(Stat(Loc.T("记录上限", "Lap limit"), options.StopwatchLapLimit.ToString()));
        host.Children.Add(stats);
    }

    private void ActiveDetails(Grid host, TimerMode mode)
    {
        var timer = _workspace.Timer; var o = timer.Options;
        if (mode == TimerMode.Stopwatch) { LapRecords(host, o); return; }
        var stats = new UniformGrid { Columns = mode == TimerMode.Pomodoro ? 4 : 2 };
        if (mode == TimerMode.Pomodoro)
        {
            stats.Children.Add(Stat(Loc.T("已完成 · 轮", "Completed"), timer.CompletedFocus.ToString(), 4));
            stats.Children.Add(Stat(Loc.T("专注 · 分钟", "Focus · min"), o.FocusMinutes.ToString(), 4));
            stats.Children.Add(Stat(Loc.T("休息 · 分钟", "Breaks · min"), $"{o.ShortBreakMinutes} / {o.LongBreakMinutes}", 4));
            stats.Children.Add(Stat(Loc.T("长休息间隔", "Long break every"), o.Rounds + Loc.T(" 轮", " sessions"), 4));
        }
        else
        {
            stats.Children.Add(Stat(Loc.T("总时长", "Duration"), TimerSession.Format(o.CountdownSeconds)));
            stats.Children.Add(Stat(Loc.T("计时状态", "Status"), timer.Status == TimerStatus.Completed ? Loc.T("已完成", "Completed") : timer.Running ? Loc.T("进行中", "Running") : Loc.T("已暂停", "Paused")));
        }
        host.Children.Add(stats);
    }
    private void LapRecords(Grid host, TimerOptions options)
    {
        var timer = _workspace.Timer; int rows = _layout.LapRows;
        int pages = MiniAppsLayout.PageCount(timer.Laps.Count, rows); _lapPage = Math.Clamp(_lapPage, 0, pages - 1);
        bool slim = _layout.DetailHeight < 70;
        var card = new Grid(); card.ColumnDefinitions.Add(new()); card.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (!slim) { card.RowDefinitions.Add(new() { Height = new GridLength(26) }); card.RowDefinitions.Add(new()); }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (options.StopwatchLaps)
        {
            var lap = _lapButton = Action(Loc.T("计次", "Lap"), () => { _lapPage = 0; timer.Lap(); }, slim ? "+" : null);
            lap.IsEnabled = timer.CanLap; lap.Height = 24; tools.Children.Add(lap);
        }
        var reset = Action(Loc.T("重置", "Reset"), () => { _lapPage = 0; timer.ResetStopwatch(); }, slim ? "↺" : null);
        reset.Height = 24; tools.Children.Add(reset); card.Children.Add(tools);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var previous = _lapPrevious = Action(Loc.T("较新记录", "Newer laps"), () => { _lapPage--; Update(); }, "‹"); previous.IsEnabled = _lapPage > 0;
        var next = _lapNext = Action(Loc.T("较早记录", "Older laps"), () => { _lapPage++; Update(); }, "›"); next.IsEnabled = _lapPage < pages - 1;
        foreach (var button in new[] { previous, next }) { button.Height = 24; button.Width = 24; button.Padding = new Thickness(0); }
        _lapPageLabel = Label($"{_lapPage + 1}/{pages}", 10);
        pager.Children.Add(previous); pager.Children.Add(_lapPageLabel); pager.Children.Add(next);
        SetColumn(pager, 1); card.Children.Add(pager);
        var records = _lapRecords = new Grid();
        for (int at = 0; at < rows; at++)
        {
            records.RowDefinitions.Add(new() { Height = new GridLength(slim ? 26 : 22) });
            var record = Label("", slim ? 11 : 12);
            SetRow(record, at); records.Children.Add(record); _lapLabels.Add(record);
        }
        if (slim)
        {
            // One record and its navigation share a row, so even the minimum panel keeps every action accessible.
            tools.HorizontalAlignment = HorizontalAlignment.Left;
            records.Margin = new Thickness(68, 0, 0, 0); records.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else { SetRow(records, 1); SetColumnSpan(records, 2); }
        card.Children.Add(records); host.Children.Add(Card(card, 3));
    }

    private void RefreshLaps()
    {
        if (_lapRecords is null || _lapPageLabel is null || _lapPrevious is null || _lapNext is null) return;
        var timer = _workspace.Timer; var options = timer.Options;
        int pages = MiniAppsLayout.PageCount(timer.Laps.Count, _lapLabels.Count);
        _lapPage = Math.Clamp(_lapPage, 0, pages - 1);
        if (_lapButton is not null) _lapButton.IsEnabled = timer.CanLap;
        string key = $"{timer.Laps.Count}/{_lapPage}/{options.StopwatchHundredths}";
        if (_lapKey == key) return;
        bool animate = _lapKey.Length > 0; _lapKey = key;
        _lapPageLabel.Text = $"{_lapPage + 1}/{pages}";
        _lapPageLabel.ToolTip = Loc.T($"共 {timer.Laps.Count} 条，最多 {options.StopwatchLapLimit} 条", $"{timer.Laps.Count} laps, limit {options.StopwatchLapLimit}");
        _lapPrevious.IsEnabled = _lapPage > 0; _lapNext.IsEnabled = _lapPage < pages - 1;
        var records = timer.Laps.Reverse().Skip(_lapPage * _lapLabels.Count).Take(_lapLabels.Count).ToArray();
        string Format(double seconds) => options.StopwatchHundredths ? TimerVisuals.LapTime(seconds) : TimerSession.Format(seconds);
        for (int at = 0; at < _lapLabels.Count; at++)
        {
            var label = _lapLabels[at]; label.Visibility = Visibility.Visible;
            if (at < records.Length)
            {
                var item = records[at]; label.Text = $"{item.Number:00}     {Format(item.SplitSeconds)}     {Format(item.ElapsedSeconds)}";
                string description = Loc.T($"第 {item.Number} 段，分段 {Format(item.SplitSeconds)}，累计 {Format(item.ElapsedSeconds)}", $"Lap {item.Number}, split {Format(item.SplitSeconds)}, total {Format(item.ElapsedSeconds)}");
                label.ToolTip = description; AutomationProperties.SetName(label, description);
            }
            else
            {
                label.Text = at == 0 ? options.StopwatchLaps ? Loc.T("尚无分段", "No laps yet") : Loc.T("分段记录已关闭", "Lap recording off") : "";
                AutomationProperties.SetName(label, ""); label.ToolTip = label.Text;
            }
        }
        // Keep buttons and the running clock in place; only the changed record area brightens.
        if (animate && IsVisible) PageHandover.Brighten(_lapRecords);
    }

    private void BuildEditor()
    {
        if (_body is null) return;
        if (_countdownEdit) { _body.Children.Add(CountdownInputs()); return; }
        if (_fieldEdit is not { } spec) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var input = Input(spec); input.Fit(160, 42); row.Children.Add(input);
        var done = Action(Loc.T("完成", "Done"), FinishEdit); done.Margin = new Thickness(12, 0, 0, 0); row.Children.Add(done);
        _body.Children.Add(Card(row, 4));
    }
    private void FinishEdit()
    {
        foreach (var input in _inputs) if (!input.CommitEdit()) { input.BeginEdit(); return; }
        _fieldEdit = null; _countdownEdit = false; Update();
    }
    private RollingStepper Input(FieldSpec spec)
    {
        var input = new RollingStepper(_owner, spec.Label, spec.Value, spec.Min, spec.Max) { Tag = spec.Id };
        input.ValueChanged += value =>
        {
            spec.Set(value);
            if (_fieldEdit?.Id == spec.Id) _fieldEdit = _fieldEdit with { Value = value };
            SavePreferences();
        };
        _inputs.Add(input); return input;
    }
    private void SavePreferences()
    {
        _renderedOptions = _workspace.Options.Copy();
        _workspace.SaveOptions(); Update();
    }
    private static bool SamePreferences(TimerOptions options, TimerOptions? rendered) => rendered is not null &&
        options.Enabled == rendered.Enabled && options.PomodoroEnabled == rendered.PomodoroEnabled && options.CountdownEnabled == rendered.CountdownEnabled &&
        options.StopwatchEnabled == rendered.StopwatchEnabled && options.Mode == rendered.Mode && options.DefaultMode == rendered.DefaultMode &&
        options.Widgets.SequenceEqual(rendered.Widgets) &&
        options.FocusMinutes == rendered.FocusMinutes && options.ShortBreakMinutes == rendered.ShortBreakMinutes && options.LongBreakMinutes == rendered.LongBreakMinutes &&
        options.Rounds == rendered.Rounds && options.CountdownSeconds == rendered.CountdownSeconds && options.StopwatchLaps == rendered.StopwatchLaps &&
        options.StopwatchLapLimit == rendered.StopwatchLapLimit && options.StopwatchHundredths == rendered.StopwatchHundredths;
    private void Start()
    {
        foreach (var input in _inputs) if (!input.CommitEdit()) { input.BeginEdit(); return; }
        var options = _workspace.Options; options.Normalize();
        if (_workspace.Timer.Start(options.Mode, options)) _showControls();
    }
    private Button Action(string label, Action action, string? glyph = null)
    {
        var button = TimerVisuals.Button(_owner, glyph ?? label, action, label);
        button.Height = 30; button.Padding = new Thickness(glyph is null ? 9 : 7, 0, glyph is null ? 9 : 7, 0);
        return button;
    }
    private static TextBlock Label(string text, double size, string resource = "PanelSecondary")
    {
        var label = TimerVisuals.Text(text, size, resource); label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.ToolTip = text; return label;
    }
    private Border Stat(string label, string value, int columns = 2)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Label(label, _layout.DetailHeight < 50 ? 10 : 11));
        double cellWidth = _layout.DetailWidth / columns;
        double size = Math.Clamp((cellWidth - 16) / Math.Max(1, value.Length * .65), 12, _layout.DetailHeight < 50 ? 14 : 20);
        stack.Children.Add(Label(value, size, "PanelPrimary"));
        var card = Card(stack, _layout.DetailHeight < 50 ? 0 : 4); card.Margin = new Thickness(2); return card;
    }
    private static Border Card(UIElement content, double padding)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "PanelFill"); return card;
    }
}
