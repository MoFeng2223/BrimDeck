using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Calendar;

// A bounded month grid plus the agenda of the selected day. No scrolling: the panel is sized by the
// host, so the layout arithmetic decides what to show instead of measuring first.
internal sealed class CalendarPageView : Grid, IModuleView
{
    private readonly CalendarWorkspace _workspace;
    private readonly ModuleViewContext _context;
    private readonly Grid _surface = new(), _leaving = new() { IsHitTestVisible = false };
    private readonly PageHandover _handover;
    private readonly List<CellVisual> _cells = [];
    private readonly List<RollingStepper> _steppers = [];

    private CalendarLayout _layout;
    private string _key = "", _agendaKey = "";
    private bool _building, _agendaOnly;
    private double _width = 480, _height = 266;
    private int _agendaPage;
    private DateOnly _paintedSelected, _paintedToday;
    private int _paintedRevision = -1;
    private Handover _motion = Handover.Morph;
    private DateOnly? _focusAfterRebuild;

    private Grid? _editor;
    private TextBlock? _monthTitle, _agendaTitle, _agendaNote, _status;
    private Grid? _agendaRows;
    private StackPanel? _pager;
    private Button? _todayButton;

    internal CalendarPageView(CalendarWorkspace workspace, ModuleViewContext context)
    {
        _workspace = workspace;
        _context = context;
        Children.Add(_surface);
        Children.Add(_leaving);
        _handover = new PageHandover(_leaving);
        _layout = CalendarLayout.For(_width, _height, workspace.Options.ShowLunar);
        SizeChanged += (_, _) => { if (ActualWidth > 0 && ActualHeight > 0) SetViewport(ActualWidth, ActualHeight); };
        Unloaded += (_, _) => _handover.Drop();
        workspace.Changed += Update;
        // An external mirror is read while this page is on screen; closing the page stops the polling.
        // Until the host exposes page visibility, WPF's own IsVisible (false after switching to another
        // panel or when the window is minimised) is the closest signal available.
        context.EveryVisible(TimeSpan.FromSeconds(5), Poll);
        _workspace.SyncExternal(true);
        Update();
    }

    private void Poll()
    {
        if (!IsVisible) return;
        if (Window.GetWindow(this) is { WindowState: WindowState.Minimized }) return;
        _workspace.SyncExternal();
    }

    public FrameworkElement Element => this;
    public bool IsAdjusting => _editor is not null || _steppers.Any(stepper => stepper.IsAdjusting);

    public void SetViewport(double width, double height, double scale = 1)
    {
        if (!(width > 0) || !(height > 0)) return;
        if (Math.Abs(width - _width) < 1 && Math.Abs(height - _height) < 1) return;
        _width = width;
        _height = height;
        Relayout();
        _key = "";
        Update();
    }

    public void Refresh() => Update();

    public bool CancelAdjustment()
    {
        if (_editor is not null) { CloseEditor(); return true; }
        if (_agendaOnly) { BackToMonth(); return true; }
        bool cancelled = false;
        foreach (var stepper in _steppers) cancelled |= stepper.CancelAdjustment();
        return cancelled;
    }

    public void Dispose()
    {
        _workspace.Changed -= Update;
        _handover.Drop();
        foreach (var stepper in _steppers) stepper.CancelAdjustment();
        _steppers.Clear();
        _cells.Clear();
        Children.Clear();
    }

    private void Relayout() =>
        _layout = CalendarLayout.For(_width, _height, _workspace.Options.ShowLunar, _agendaOnly);

    private void Update()
    {
        if (_building) return;
        string key = BuildKey();
        // Keep the actual editor attached, including selection, focus and IME composition, across appearance/layout updates.
        if (key != _key && _editor is null && !_steppers.Any(s => s.IsAdjusting)) { Rebuild(key); return; }
        RefreshCells();
        RefreshAgenda();
    }

    private string BuildKey() => string.Join("|", new object?[]
    {
        _workspace.Month.ToString("yyyy-MM"), _workspace.Today.ToString("yyyy-MM-dd"), _workspace.Revision,
        _layout.SideBySide, _layout.WeekView, (int)Math.Round(_layout.MonthWidth), (int)Math.Round(_layout.CellHeight),
        _layout.AgendaRows, _layout.ShowLunar, _layout.ShowDots, (int)_layout.Padding, (int)_layout.Gap,
        _workspace.Options.FirstDay, _workspace.Options.ShowLunar, _workspace.Options.ShowHoliday,
        Loc.Language, ModuleMotion.Enabled, _agendaOnly
    });

    private void Rebuild(string key)
    {
        _building = true;
        bool hadContent = _surface.Children.Count > 0;
        _editor = null;
        _steppers.Clear();
        _handover.Hold(_surface);
        _cells.Clear();
        _monthTitle = null;
        _agendaTitle = null;
        _agendaNote = null;
        _agendaRows = null;
        _pager = null;
        _status = null;
        _todayButton = null;
        _agendaKey = "";
        Build();
        _key = key;
        _building = false;
        _handover.Release(_surface, hadContent ? _motion : Handover.None);
        _motion = Handover.Morph;
        _paintedRevision = -1;
        RefreshCells();
        RefreshAgenda();

        if (_focusAfterRebuild is { } focus)
        {
            _focusAfterRebuild = null;
            var cell = _cells.FirstOrDefault(item => item.Date == focus);
            if (cell is not null && cell.Button.IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => cell.Button.Focus());
        }
    }

    private void Build()
    {
        _surface.Children.Clear();
        var root = new Grid();
        _surface.Children.Add(root);
        if (_layout.SideBySide)
        {
            root.ColumnDefinitions.Add(new() { Width = new GridLength(_layout.MonthWidth) });
            root.ColumnDefinitions.Add(new() { Width = new GridLength(_layout.Gap) });
            root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(MonthCard());
            var agenda = AgendaCard();
            SetColumn(agenda, 2);
            root.Children.Add(agenda);
        }
        else root.Children.Add(_agendaOnly ? AgendaCard() : MonthCard());
    }

    private FrameworkElement MonthCard()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(_layout.Header) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(_layout.Weekday) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _monthTitle = CalendarVisuals.Label(CalendarVisuals.MonthTitle(_workspace.Month), _layout.Header >= 28 ? 16 : 13, "PanelPrimary", FontWeights.SemiBold);
        header.Children.Add(_monthTitle);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        string back = _layout.WeekView ? Loc.T("上一周", "Previous week") : Loc.T("上个月", "Previous month");
        string forward = _layout.WeekView ? Loc.T("下一周", "Next week") : Loc.T("下个月", "Next month");
        tools.Children.Add(Glyph("‹", back, () => Shift(-1)));
        tools.Children.Add(Glyph("›", forward, () => Shift(1)));
        _todayButton = Glyph("今", Loc.T("回到今天", "Go to today"), GoToday);
        _todayButton.Width = _layout.Header >= 28 ? 26 : 22;
        tools.Children.Add(_todayButton);
        SetColumn(tools, 1);
        header.Children.Add(tools);
        grid.Children.Add(header);

        var week = new UniformGrid { Columns = 7, Rows = 1 };
        for (int index = 0; index < 7; index++)
        {
            var letter = CalendarVisuals.Label(CalendarVisuals.WeekLetter(MonthGrid.DayIndex(_workspace.Options.FirstDay, index)), _layout.Weekday < 16 ? 9 : 10, "PanelTertiary");
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            week.Children.Add(letter);
        }
        SetRow(week, 1);
        grid.Children.Add(week);

        var cells = new UniformGrid { Columns = 7, Rows = _layout.WeekView ? 1 : 6 };
        var days = _layout.WeekView
            ? MonthGrid.Week(_workspace.Selected, _workspace.Options.FirstDay)
            : MonthGrid.Build(_workspace.Month, _workspace.Options.FirstDay);
        foreach (var date in days) cells.Children.Add(BuildCell(date));
        SetRow(cells, 2);
        grid.Children.Add(cells);

        PageHandover.Arrive(_monthTitle, _motion);
        return CalendarVisuals.Card(grid, _layout.Padding);
    }

    private Button BuildCell(DateOnly date)
    {
        var button = CalendarVisuals.Interactive(_context.Owner, 8);
        button.Margin = new Thickness(1);
        button.Padding = new Thickness(1);
        // A week cell has a whole row to itself: keep the block content-sized instead of a full-height slab.
        if (_layout.WeekView)
        {
            button.Height = Math.Clamp(_layout.CellHeight - 10, 44, 76);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(1, 2, 1, 2);
            button.Padding = new Thickness(1, 7, 1, 1);
        }
        button.Click += (_, _) => SelectDay(date);
        button.PreviewKeyDown += (_, e) => OnCellKey(date, e);

        var stack = new StackPanel
        {
            // Week cells are top-aligned so every day number sits on the same line, whatever follows it.
            VerticalAlignment = _layout.WeekView ? VerticalAlignment.Top : VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // The number and its event dots share a line, so a cell needs room for two lines at most.
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var number = ModuleUi.Centered(date.Day.ToString(), _layout.CellHeight >= 34 ? 13 : _layout.CellHeight >= 26 ? 12 : 11, ModuleUi.Primary);
        line.Children.Add(number);

        StackPanel? dots = null;
        if (_layout.ShowDots)
        {
            dots = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };
            line.Children.Add(dots);
        }
        stack.Children.Add(line);

        StackPanel? titles = null;
        if (_layout.ShowTitles)
        {
            titles = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            stack.Children.Add(titles);
        }

        TextBlock? note = null;
        if (_layout.ShowLunar)
        {
            note = new TextBlock
            {
                FontFamily = ModuleUi.PanelFont, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0), MaxWidth = Math.Max(24, _layout.MonthArea / 7 - 10),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
            };
            stack.Children.Add(note);
        }

        button.Content = stack;
        _cells.Add(new CellVisual(date, button, number, dots, note, titles));
        return button;
    }

    private void OnCellKey(DateOnly date, KeyEventArgs e)
    {
        int shift = e.Key switch
        {
            Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0
        };
        if (shift != 0)
        {
            var target = date.AddDays(shift);
            _focusAfterRebuild = target;
            SelectDay(target);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Home)
        {
            _focusAfterRebuild = _workspace.Today;
            GoToday();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter)
        {
            SelectDay(date);
            OpenEditor(null);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Escape && _agendaOnly) { BackToMonth(); e.Handled = true; }
    }

    private void SelectDay(DateOnly date)
    {
        bool side = _layout.SideBySide;
        _agendaPage = 0;
        _workspace.Select(date);
        if (!side && !_agendaOnly)
        {
            _agendaOnly = true;
            _motion = Handover.Forward;
            Relayout();
            _key = "";
            Update();
        }
    }

    // In week view the arrows walk the selected day by a week; in month view they turn the page.
    private void Shift(int direction)
    {
        _motion = direction < 0 ? Handover.Back : Handover.Forward;
        if (_layout.WeekView) _workspace.Select(_workspace.Selected.AddDays(direction * 7));
        else _workspace.Move(direction);
        _motion = Handover.Morph;
    }

    private void GoToday()
    {
        _motion = Handover.Forward;
        _workspace.GoToday();
        _motion = Handover.Morph;
    }

    private void BackToMonth()
    {
        _agendaOnly = false;
        _motion = Handover.Back;
        Relayout();
        _key = "";
        Update();
    }

    private void RefreshCells()
    {
        if (_monthTitle is not null)
            _monthTitle.Text = _layout.WeekView
                ? CalendarVisuals.WeekTitle(_workspace.Selected, _workspace.Options.FirstDay)
                : CalendarVisuals.MonthTitle(_workspace.Month);
        if (_todayButton is not null)
        {
            bool onToday = _layout.WeekView
                ? MonthGrid.WeekStart(_workspace.Selected, _workspace.Options.FirstDay) == MonthGrid.WeekStart(_workspace.Today, _workspace.Options.FirstDay)
                : _workspace.Month == MonthGrid.First(_workspace.Today);
            _todayButton.Visibility = onToday ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_cells.Count == 0) return;
        if (_paintedRevision == _workspace.Revision && _paintedSelected == _workspace.Selected && _paintedToday == _workspace.Today) return;
        _paintedRevision = _workspace.Revision;
        _paintedSelected = _workspace.Selected;
        _paintedToday = _workspace.Today;

        bool chinese = Loc.Language == "zh-CN";
        var today = _workspace.Today;
        var selected = _workspace.Selected;
        foreach (var cell in _cells)
        {
            bool inMonth = _layout.WeekView || (cell.Date.Year == _workspace.Month.Year && cell.Date.Month == _workspace.Month.Month);
            bool isToday = cell.Date == today, isSelected = cell.Date == selected;
            // Three levels from the panel theme: today is a solid block in the text colour, the chosen day takes the
            // selected fill and the accent, everything else is plain text. No hard-coded greys.
            if (isToday) cell.Button.SetResourceReference(Control.BackgroundProperty, "PanelPrimary");
            else if (isSelected) cell.Button.SetResourceReference(Control.BackgroundProperty, "PanelFillSelected");
            else cell.Button.Background = Brushes.Transparent;

            if (isToday) cell.Number.SetResourceReference(TextBlock.ForegroundProperty, "PanelSurface");
            else if (isSelected) cell.Number.SetResourceReference(TextBlock.ForegroundProperty, "PanelAccent");
            else cell.Number.SetResourceReference(TextBlock.ForegroundProperty, inMonth ? "PanelPrimary" : "PanelTertiary");
            cell.Number.Opacity = inMonth || isToday ? 1 : .55;

            var items = _workspace.ForDay(cell.Date);
            if (cell.Dots is { } dots)
            {
                dots.Children.Clear();
                for (int index = 0; index < Math.Min(2, items.Count); index++)
                    dots.Children.Add(new Border
                    {
                        Width = 3.5, Height = 3.5, CornerRadius = new CornerRadius(1.75),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(index == 0 ? 0 : 1, 0, 0, 0),
                        Background = ModuleUi.Brush(CalendarVisuals.Color(items[index].Category))
                    });
            }

            if (cell.Titles is { } titles)
            {
                titles.Children.Clear();
                for (int index = 0; index < Math.Min(2, items.Count); index++)
                {
                    var line = CalendarVisuals.Label(items[index].Title, 9, isToday ? "PanelSurface" : "PanelSecondary");
                    line.MaxWidth = Math.Max(30, _layout.MonthArea / 7 - 12);
                    line.HorizontalAlignment = HorizontalAlignment.Center;
                    if (isToday) line.Opacity = .82;
                    titles.Children.Add(line);
                }
            }

            var info = ChineseCalendar.Describe(cell.Date);
            string text = "";
            Brush? brush = null;
            if (info.Festival != Festival.None && _workspace.Options.ShowHoliday)
            {
                text = CalendarVisuals.FestivalName(info.Festival);
                brush = ModuleUi.Brush(ModuleUi.Accent);
            }
            else if (chinese) text = info.Lunar;

            if (info.RestDay) { text = Loc.T("休 ", "Rest ") + text; brush ??= ModuleUi.Brush(ModuleUi.Accent); }
            else if (info.Workday) { text = Loc.T("班 ", "Work ") + text; brush = ModuleUi.Brush("#E5534B"); }

            if (cell.Note is { } note)
            {
                note.Text = text;
                note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                if (isToday) { note.SetResourceReference(TextBlock.ForegroundProperty, "PanelSurface"); note.Opacity = .62; }
                else if (brush is not null) { note.Foreground = brush; note.Opacity = 1; }
                else { note.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary"); note.Opacity = 1; }
            }

            string spoken = $"{cell.Date:yyyy-MM-dd} {CalendarVisuals.Weekday(cell.Date)}"
                + (string.IsNullOrEmpty(text) ? "" : " " + text)
                + (items.Count == 0 ? "" : " " + Loc.T($"{items.Count} 项日程", $"{items.Count} events"))
                + (isToday ? " " + Loc.T("今天", "today") : "");
            AutomationProperties.SetName(cell.Button, spoken);
            cell.Button.ToolTip = spoken;
            cell.Button.IsTabStop = isSelected;
        }
    }

    private FrameworkElement AgendaCard()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(_layout.Header) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(18) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (_layout.AgendaOnly)
        {
            var back = Glyph("‹", Loc.T("返回月历", "Back to month"), BackToMonth);
            header.Children.Add(back);
        }
        var caption = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(_layout.AgendaOnly ? 6 : 0, 0, 6, 0)
        };
        _agendaTitle = CalendarVisuals.Label("", _layout.Header >= 28 ? 13 : 12, "PanelPrimary", FontWeights.SemiBold);
        _agendaNote = CalendarVisuals.Label("", 9, "PanelTertiary");
        caption.Children.Add(_agendaTitle);
        caption.Children.Add(_agendaNote);
        SetColumn(caption, 1);
        header.Children.Add(caption);
        var add = Glyph("＋", Loc.T("新建日程", "New event"), () => OpenEditor(null));
        SetColumn(add, 2);
        header.Children.Add(add);
        grid.Children.Add(header);

        _agendaRows = new Grid();
        SetRow(_agendaRows, 1);
        grid.Children.Add(_agendaRows);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _status = CalendarVisuals.Label("", 9, "PanelTertiary");
        footer.Children.Add(_status);
        _pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        SetColumn(_pager, 1);
        footer.Children.Add(_pager);
        SetRow(footer, 2);
        grid.Children.Add(footer);

        return CalendarVisuals.Card(grid, _layout.Padding);
    }

    private void RefreshAgenda()
    {
        if (_agendaRows is null || _status is null || _pager is null) return;
        var day = _workspace.Selected;
        var info = ChineseCalendar.Describe(day);
        if (_agendaTitle is not null) _agendaTitle.Text = CalendarVisuals.DayTitle(day);
        if (_agendaNote is not null)
        {
            string note = info.Festival != Festival.None && _workspace.Options.ShowHoliday
                ? CalendarVisuals.FestivalName(info.Festival)
                : Loc.Language == "zh-CN" ? info.Lunar : "";
            int count = _workspace.CountOn(day);
            string tally = count == 0 ? Loc.T("暂无日程", "No events") : Loc.T($"{count} 项", $"{count} events");
            _agendaNote.Text = string.IsNullOrEmpty(note) ? tally : note + " · " + tally;
        }

        if (_status is not null)
        {
            var parts = new List<string>();
            if (!_workspace.Writable) parts.Add(Loc.T("仅可查看", "View only"));
            if (_workspace.VivoStatus.Length > 0) parts.Add(_workspace.VivoStatus);
            _status.Text = string.Join(" · ", parts);
        }

        int rows = Math.Max(1, _layout.AgendaRows);
        var items = _workspace.ForDay(day);
        int pages = Math.Max(1, (items.Count + rows - 1) / rows);
        _agendaPage = Math.Clamp(_agendaPage, 0, pages - 1);
        string key = $"{day:yyyy-MM-dd}/{_workspace.Revision}/{rows}/{_agendaPage}/{items.Count}";
        if (key == _agendaKey) return;
        bool arrivals = _agendaKey.Length > 0;
        _agendaKey = key;

        _agendaRows.Children.Clear();
        _agendaRows.RowDefinitions.Clear();
        _pager.Children.Clear();

        if (items.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var hint = CalendarVisuals.Label(Loc.T("这一天还没有日程", "Nothing scheduled"), 11, "PanelSecondary");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            empty.Children.Add(hint);
            var add = CalendarVisuals.TextButton(_context.Owner, Loc.T("＋ 新建日程", "＋ New event"), () => OpenEditor(null));
            add.SetResourceReference(Control.ForegroundProperty, "PanelAccent");
            add.HorizontalAlignment = HorizontalAlignment.Center;
            add.Margin = new Thickness(0, 6, 0, 0);
            empty.Children.Add(add);
            _agendaRows.Children.Add(empty);
            return;
        }

        for (int index = 0; index < rows; index++)
        {
            _agendaRows.RowDefinitions.Add(new() { Height = new GridLength(22) });
            int at = _agendaPage * rows + index;
            if (at >= items.Count) continue;
            var row = AgendaRow(items[at]);
            SetRow(row, index);
            _agendaRows.Children.Add(row);
        }

        if (pages > 1)
        {
            var previous = Glyph("‹", Loc.T("较新", "Newer"), () => { _agendaPage--; Update(); });
            var next = Glyph("›", Loc.T("较早", "Older"), () => { _agendaPage++; Update(); });
            previous.IsEnabled = _agendaPage > 0;
            next.IsEnabled = _agendaPage < pages - 1;
            previous.Width = next.Width = 18;
            previous.Height = next.Height = 18;
            var label = CalendarVisuals.Label($"{_agendaPage + 1}/{pages}", 9, "PanelTertiary");
            label.Margin = new Thickness(2, 0, 2, 0);
            _pager.Children.Add(previous);
            _pager.Children.Add(label);
            _pager.Children.Add(next);
        }

        if (arrivals) PageHandover.Brighten(_agendaRows);
    }

    private FrameworkElement AgendaRow(CalendarEvent item)
    {
        var row = CalendarVisuals.Interactive(_context.Owner, 6, "PanelKeyboardFocus");
        row.Height = 22;
        row.Padding = new Thickness(2, 0, 1, 0);
        bool external = item.Source.Length > 0;
        if (external) row.Cursor = Cursors.Arrow;
        else row.Click += (_, _) => OpenEditor(item);

        var content = new Grid();
        content.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(3) });
        content.ColumnDefinitions.Add(new());
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        var time = CalendarVisuals.Label(item.AllDay ? Loc.T("全天", "All day") : item.Clock, 10, "PanelSecondary");
        time.TextAlignment = TextAlignment.Right;
        time.Margin = new Thickness(0, 0, 6, 0);
        content.Children.Add(time);

        var bar = new Border
        {
            Width = 3, Height = 11, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center,
            Background = ModuleUi.Brush(CalendarVisuals.Color(item.Category))
        };
        SetColumn(bar, 1);
        content.Children.Add(bar);

        var title = CalendarVisuals.Label(item.Title, 11, "PanelPrimary");
        title.Margin = new Thickness(6, 0, 4, 0);
        SetColumn(title, 2);
        content.Children.Add(title);

        if (external)
        {
            var tag = CalendarVisuals.Label("vivo", 8, "PanelTertiary");
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.Margin = new Thickness(0, 0, 2, 0);
            tag.ToolTip = Loc.T("来自 vivo 日历，只读", "From vivo calendar, read-only");
            SetColumn(tag, 3);
            content.Children.Add(tag);
        }
        else
        {
        var remove = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 10, FontFamily = ModuleUi.PanelFont,
            Padding = new Thickness(0), Cursor = Cursors.Hand, Focusable = true
        };
        if (_context.Owner.TryFindResource("PanelGlyphButton") is Style style) remove.Style = style;
        else if (_context.Owner.TryFindResource("PanelTextButton") is Style fallback) remove.Style = fallback;
        remove.SetResourceReference(Control.ForegroundProperty, "PanelTertiary");
        remove.ToolTip = Loc.T("删除", "Delete");
        AutomationProperties.SetName(remove, Loc.T("删除 ", "Delete ") + item.Title);
        SetColumn(remove, 3);
        content.Children.Add(remove);

        bool armed = false;
        void Disarm()
        {
            armed = false;
            remove.Content = "✕";
            remove.SetResourceReference(Control.ForegroundProperty, "PanelTertiary");
            remove.ToolTip = Loc.T("删除", "Delete");
        }
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            if (!armed)
            {
                armed = true;
                remove.Content = Loc.T("删", "!");
                remove.Foreground = ModuleUi.Brush("#E5534B");
                remove.ToolTip = Loc.T("再点一次删除", "Click again to delete");
                return;
            }
            _workspace.Remove(item.Id);
        };
        remove.LostMouseCapture += (_, _) => { if (armed) Disarm(); };
        remove.MouseLeave += (_, _) => { if (armed) Disarm(); };
        }

        row.Content = content;
        AutomationProperties.SetName(row, $"{(item.AllDay ? Loc.T("全天", "All day") : item.Clock)} {item.Title} {CalendarVisuals.CategoryName(item.Category)}"
            + (external ? " " + Loc.T("只读", "read-only") : ""));
        return row;
    }

    private Button Glyph(string glyph, string tip, Action action)
    {
        double size = _layout.Header >= 28 ? 24 : 20;
        var button = new Button
        {
            Content = glyph, Width = size, Height = size, Padding = new Thickness(0), Margin = new Thickness(1, 0, 0, 0),
            FontSize = _layout.Header >= 28 ? 14 : 12, FontFamily = ModuleUi.PanelFont, Cursor = Cursors.Hand,
            Focusable = true
        };
        if (_context.Owner.TryFindResource("PanelGlyphButton") is Style style) button.Style = style;
        else if (_context.Owner.TryFindResource("PanelTextButton") is Style fallback) button.Style = fallback;
        button.SetResourceReference(Control.ForegroundProperty, "PanelSecondary");
        button.Click += (_, e) => { e.Handled = true; action(); };
        AutomationProperties.SetName(button, tip);
        button.ToolTip = tip;
        return button;
    }

    private void OpenEditor(CalendarEvent? existing)
    {
        if (_editor is not null || _building) return;
        var draft = existing is null
            ? new Draft("", _workspace.Selected, "", false, 9, 0, 60, EventCategory.Work)
            : new Draft(existing.Id, existing.Day, existing.Title, existing.AllDay,
                Math.Clamp(existing.Start, 0, 1439) / 60, Math.Clamp(existing.Start, 0, 1439) % 60,
                existing.Duration, existing.Category);

        var overlay = new Grid { Background = ModuleUi.Brush("#66000000"), Focusable = true };
        overlay.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, overlay)) CloseEditor(); };
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseEditor(); e.Handled = true; } };

        var card = new Border
        {
            Width = Math.Clamp(_width - 36, 226, 400), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelPopup");
        card.SetResourceReference(Border.BorderBrushProperty, "PanelBubbleBorder");

        var stack = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new());
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(CalendarVisuals.Label(existing is null ? Loc.T("新建日程", "New event") : Loc.T("编辑日程", "Edit event"), 12, "PanelPrimary", FontWeights.SemiBold));
        var dayLabel = CalendarVisuals.Label(CalendarVisuals.DayTitle(draft.Day), 9, "PanelTertiary");
        SetColumn(dayLabel, 1);
        heading.Children.Add(dayLabel);
        stack.Children.Add(heading);

        var box = new TextBox
        {
            Text = draft.Title, FontSize = 13, MaxLength = 120, Padding = new Thickness(9, 7, 9, 7),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = ModuleUi.PanelFont,
            AcceptsReturn = false
        };
        box.SetResourceReference(Control.ForegroundProperty, "PanelPrimary");
        box.CaretBrush = ModuleUi.Brush(ModuleUi.Primary);
        AutomationProperties.SetName(box, Loc.T("日程标题", "Event title"));

        var placeholder = CalendarVisuals.Label(Loc.T("日程标题", "Event title"), 13, "PanelTertiary");
        placeholder.Margin = new Thickness(11, 0, 0, 0);
        placeholder.IsHitTestVisible = false;
        placeholder.Visibility = string.IsNullOrEmpty(draft.Title) ? Visibility.Visible : Visibility.Collapsed;

        var fieldContent = new Grid();
        fieldContent.Children.Add(box);
        fieldContent.Children.Add(placeholder);
        var field = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(1), Margin = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Child = fieldContent
        };
        field.SetResourceReference(Border.BackgroundProperty, "PanelFill");
        stack.Children.Add(field);
        box.GotKeyboardFocus += (_, _) => field.BorderBrush = ModuleUi.Brush(ModuleUi.Accent);
        box.LostKeyboardFocus += (_, _) => field.BorderBrush = Brushes.Transparent;

        var timeRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var hour = Stepper(Loc.T("小时", "Hour"), draft.Hour, 0, 23, value => draft.Hour = value);
        var minute = Stepper(Loc.T("分钟", "Minute"), draft.Minute, 0, 59, value => draft.Minute = value);
        var duration = Stepper(Loc.T("时长（分）", "Duration (min)"), Math.Clamp(draft.Duration, 5, 720), 5, 720, value => draft.Duration = value);
        timeRow.Children.Add(TimeField(Loc.T("小时", "Hour"), hour, 72));
        timeRow.Children.Add(TimeField(Loc.T("分钟", "Min"), minute, 72));
        timeRow.Children.Add(TimeField(Loc.T("时长（分）", "Duration (min)"), duration, 88));
        stack.Children.Add(timeRow);

        Button? allDay = null;
        void SyncAllDay()
        {
            if (allDay is null) return;
            allDay.Opacity = draft.AllDay ? 1 : .55;
            timeRow.IsEnabled = !draft.AllDay;
            timeRow.Opacity = draft.AllDay ? .35 : 1;
        }
        allDay = CalendarVisuals.TextButton(_context.Owner, Loc.T("全天", "All day"), () => { draft.AllDay = !draft.AllDay; SyncAllDay(); },
            Loc.T("全天事件不需要开始时间", "An all-day event ignores the start time"));
        allDay.Height = 22;
        allDay.HorizontalAlignment = HorizontalAlignment.Left;
        var allDayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        allDayRow.Children.Add(allDay);
        stack.Children.Add(allDayRow);
        SyncAllDay();

        stack.Children.Add(CalendarVisuals.Label(Loc.T("类别", "Category"), 9, "PanelTertiary"));
        var chips = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        var choices = new List<(Button Button, EventCategory Category)>();
        void SyncChips()
        {
            foreach (var (chip, category) in choices)
                chip.BorderBrush = category == draft.Category ? ModuleUi.Brush(ModuleUi.Accent) : Brushes.Transparent;
        }
        foreach (var category in Enum.GetValues<EventCategory>())
        {
            var dot = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5),
                Background = ModuleUi.Brush(CalendarVisuals.Color(category))
            };
            var chip = CalendarVisuals.Interactive(_context.Owner, 10);
            chip.Width = 23; chip.Height = 23; chip.Margin = new Thickness(0, 0, 6, 0);
            chip.BorderThickness = new Thickness(1.5);
            chip.BorderBrush = Brushes.Transparent;
            chip.Padding = new Thickness(2);
            chip.Content = dot;
            chip.ToolTip = CalendarVisuals.CategoryName(category);
            AutomationProperties.SetName(chip, CalendarVisuals.CategoryName(category));
            chip.Click += (_, _) => { draft.Category = category; SyncChips(); };
            choices.Add((chip, category));
            chips.Children.Add(chip);
        }
        SyncChips();
        stack.Children.Add(chips);

        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new());
        buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (draft.Id.Length > 0)
        {
            var remove = CalendarVisuals.TextButton(_context.Owner, Loc.T("删除", "Delete"), () =>
            {
                string id = draft.Id;
                CloseEditor();
                _workspace.Remove(id);
            });
            remove.Foreground = ModuleUi.Brush("#E5534B");
            buttons.Children.Add(remove);
        }
        var cancel = CalendarVisuals.TextButton(_context.Owner, Loc.T("取消", "Cancel"), CloseEditor);
        cancel.Margin = new Thickness(6, 0, 0, 0);
        SetColumn(cancel, 2);
        buttons.Children.Add(cancel);
        var save = CalendarVisuals.TextButton(_context.Owner, existing is null ? Loc.T("添加", "Add") : Loc.T("保存", "Save"), Commit);
        save.Margin = new Thickness(6, 0, 0, 0);
        save.SetResourceReference(Control.BackgroundProperty, "PanelFillSelected");
        save.SetResourceReference(Control.ForegroundProperty, "PanelAccent");
        SetColumn(save, 3);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        void Commit()
        {
            string title = (box.Text ?? "").Trim();
            if (title.Length == 0) return;
            var item = new CalendarEvent
            {
                Id = draft.Id,
                Date = CalendarEvent.Key(draft.Day),
                Title = title,
                AllDay = draft.AllDay,
                Start = Math.Clamp(draft.Hour, 0, 23) * 60 + Math.Clamp(draft.Minute, 0, 59),
                Duration = Math.Clamp(draft.Duration, 5, 720),
                Category = draft.Category
            };
            CloseEditor();
            _workspace.Upsert(item);
        }

        void SyncSave() => save.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) =>
        {
            draft.Title = box.Text ?? "";
            placeholder.Visibility = draft.Title.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncSave();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        SyncSave();

        card.Child = stack;
        overlay.Children.Add(card);
        _surface.Children.Add(overlay);
        _editor = overlay;
        PageHandover.Appear(overlay);
        PageHandover.Appear(card);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        });
    }

    private void CloseEditor()
    {
        if (_editor is null) return;
        _surface.Children.Remove(_editor);
        _editor = null;
        _steppers.Clear();
        _focusAfterRebuild = _workspace.Selected;
        var cell = _cells.FirstOrDefault(item => item.Date == _workspace.Selected);
        if (cell is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => cell.Button.Focus());
    }

    private RollingStepper Stepper(string label, int value, int minimum, int maximum, Action<int> changed)
    {
        var stepper = new RollingStepper(_context.Owner, label, value, minimum, maximum) { Tag = label };
        stepper.Fit(72, 34);
        stepper.ValueChanged += number => { if (!_building) changed(number); };
        _steppers.Add(stepper);
        return stepper;
    }

    private static FrameworkElement TimeField(string label, FrameworkElement input, double width)
    {
        var panel = new StackPanel { Width = width, Margin = new Thickness(0, 0, 8, 8) };
        panel.Children.Add(CalendarVisuals.Label(label, 9, "PanelTertiary"));
        input.Width = width - 2;
        panel.Children.Add(input);
        return panel;
    }

    private sealed record CellVisual(DateOnly Date, Button Button, TextBlock Number, StackPanel? Dots, TextBlock? Note, StackPanel? Titles);

    private sealed class Draft(string id, DateOnly day, string title, bool allDay, int hour, int minute, int duration, EventCategory category)
    {
        public string Id { get; } = id;
        public DateOnly Day { get; } = day;
        public string Title { get; set; } = title;
        public bool AllDay { get; set; } = allDay;
        public int Hour { get; set; } = hour;
        public int Minute { get; set; } = minute;
        public int Duration { get; set; } = duration;
        public EventCategory Category { get; set; } = category;
    }
}
