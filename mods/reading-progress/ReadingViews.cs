using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Modules;

namespace BrimDeck.ReadingProgress;

internal abstract class ReadingView(ReadingSession session, ModuleViewContext context) : Grid, IModuleView, IModuleLayoutView
{
    protected readonly ReadingSession Session = session;
    protected readonly ModuleViewContext Context = context;
    protected bool Ended;
    public FrameworkElement Element => this;
    protected string T(string chinese, string english) => Context.Translate(chinese, english);
    protected void Listen()
    {
        Session.Changed += Refresh;
        Context.OnAppearanceChanged(_ => Refresh());
    }
    public void Refresh()
    {
        if (Ended || Context.Lifetime.IsCancellationRequested) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        Render();
    }
    protected abstract void Render();
    public virtual void SetViewport(ModuleViewport viewport) { }
    public void SetViewport(double width, double height, double scale = 1) => SetViewport(new(width, height) { ContentScale = scale });
    public void Dispose() { if (Ended) return; Ended = true; Session.Changed -= Refresh; Children.Clear(); }
    protected static TextBlock Label(string text, double size, string token = "PanelPrimary")
    {
        var label = ModuleUi.Text(text, size);
        label.HorizontalAlignment = HorizontalAlignment.Stretch;
        label.TextAlignment = TextAlignment.Left;
        label.SetResourceReference(TextBlock.ForegroundProperty, token);
        return label;
    }
}

internal sealed class ReadingPart(ReadingSession session) : IModuleCompactPart
{
    public IModuleView CreateView(ModuleViewContext context) => new ProgressPartView(session, context);
}

internal sealed class ProgressPartView : ReadingView, IModuleCompactWidth
{
    private double _scale = 1, _width = 24, _height = 24;
    public double? PreferredWidth => 24;
    public double? CompactWidth => 24;
    public double? PreferredHeight => 24;

    internal ProgressPartView(ReadingSession session, ModuleViewContext context) : base(session, context)
    {
        AutomationProperties.SetAutomationId(this, "reading-compact");
        Listen();
    }
    public override void SetViewport(ModuleViewport viewport)
    {
        _scale = Math.Clamp(viewport.ContentScale, .75, 2);
        _width = viewport.Width; _height = viewport.Height;
        Refresh();
    }
    protected override void Render()
    {
        var data = Session.Data;
        double size = Math.Max(8, Math.Min(20 * _scale, Math.Min(_width, _height)));
        var ring = ModuleUi.Ring(100.0 * data.Page / data.Target, size, 1.75 * size / 20);
        ring.HorizontalAlignment = HorizontalAlignment.Center; ring.VerticalAlignment = VerticalAlignment.Center;
        bool completed = data.Page == data.Target;
        var ink = new SolidColorBrush((Color)ColorConverter.ConvertFromString(data.Accent)); ink.Freeze();
        foreach (var arc in ring.Children.OfType<System.Windows.Shapes.Shape>().Skip(1))
        {
            if (completed) arc.Stroke = ink;
            else arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PanelPrimary");
        }
        double stroke = Math.Max(.65, .9 * size / 20);
        var glyph = Geometry.Parse(ReadingModule.BookGlyph).Clone();
        double fit = Math.Max(.1, size * .48 - stroke) / Math.Max(glyph.Bounds.Width, glyph.Bounds.Height);
        var placement = new Matrix(fit, 0, 0, fit, 0, 0);
        glyph.Transform = new MatrixTransform(placement);
        // Center the actual strokes, not the Path's layout box (its origin and unused space are asymmetric).
        Rect strokes = glyph.GetRenderBounds(new Pen(Brushes.Black, stroke) { LineJoin = PenLineJoin.Round });
        placement.OffsetX = size / 2 - (strokes.Left + strokes.Width / 2);
        placement.OffsetY = size / 2 - (strokes.Top + strokes.Height / 2);
        glyph.Transform = new MatrixTransform(placement); glyph.Freeze();
        var book = new System.Windows.Shapes.Path
        {
            Data = glyph, Width = size, Height = size,
            Stretch = Stretch.None, StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        if (completed) book.Stroke = ink;
        else book.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PanelPrimary");
        AutomationProperties.SetAutomationId(book, "reading-compact-icon");
        ring.Children.Add(book); Children.Clear(); Children.Add(ring);
        ToolTip = $"{data.Book} · {data.Page}/{data.Target} · {100.0 * data.Page / data.Target:0}%";
        AutomationProperties.SetName(this, ToolTip.ToString());
    }
}

internal sealed class ReadingPage : ReadingView
{
    private readonly StackPanel _contents = new() { Margin = new Thickness(14, 10, 14, 12) };
    private readonly TextBlock _book = Label("", 18), _count = Label("", 42), _remaining = Label("", 12, "PanelSecondary");
    private readonly TextBlock _result = Label("", 11, "PanelSecondary"), _host = Label("", 11, "PanelSecondary");
    private readonly Grid _progress = new() { Height = 5, Margin = new Thickness(0, 0, 0, 1) };
    private readonly ColumnDefinition _read = new(), _unread = new();
    private readonly Expander _details = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly Dictionary<string, Button> _actions = [];
    private readonly Button _settings;

    internal ReadingPage(ReadingSession session, ModuleViewContext context) : base(session, context)
    {
        AutomationProperties.SetAutomationId(this, "reading-page");
        AutomationProperties.SetAutomationId(_book, "reading-book");
        AutomationProperties.SetAutomationId(_count, "reading-page-count");
        AutomationProperties.SetAutomationId(_result, "reading-result");
        AutomationProperties.SetAutomationId(_host, "reading-host-state");
        _book.TextTrimming = TextTrimming.CharacterEllipsis;
        _count.Margin = new Thickness(0, 6, 0, 0);
        _remaining.Margin = new Thickness(0, 0, 0, 12);
        _progress.ColumnDefinitions.Add(_read); _progress.ColumnDefinitions.Add(_unread);
        _progress.SetResourceReference(Panel.BackgroundProperty, "PanelTrack");
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, "PanelAccent"); _progress.Children.Add(fill);
        _contents.Children.Add(_book); _contents.Children.Add(_count); _contents.Children.Add(_remaining); _contents.Children.Add(_progress);
        var mainActions = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        foreach (string action in new[] { "next-page", "advance", "undo" }) mainActions.Children.Add(ActionButton(action));
        _settings = ModuleUi.Button(context, "", () => context.OpenSettings(ReadingModule.Id + ":preferences"));
        _settings.Margin = new Thickness(0, 0, 6, 4); _settings.MinHeight = 30;
        AutomationProperties.SetAutomationId(_settings, "reading-settings"); mainActions.Children.Add(_settings);
        _contents.Children.Add(mainActions);
        _result.TextWrapping = TextWrapping.Wrap; _contents.Children.Add(_result);
        var detailContents = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _host.TextWrapping = TextWrapping.Wrap; detailContents.Children.Add(_host);
        var probes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (string action in new[] { "check-background", "check-page-scope", "release-accent" }) probes.Children.Add(ActionButton(action));
        detailContents.Children.Add(probes); _details.Content = detailContents;
        _details.SetResourceReference(Control.ForegroundProperty, "PanelSecondary");
        _contents.Children.Add(_details);
        Children.Add(new ScrollViewer { Content = _contents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Listen();
    }

    private Button ActionButton(string action)
    {
        var button = ModuleUi.Button(Context, "", () => Run(action));
        button.MinHeight = 30; button.MinWidth = 64; button.Margin = new Thickness(0, 0, 6, 4);
        button.FontSize = 12;
        button.SetResourceReference(StyleProperty, "PanelOutlineButton");
        button.SetResourceReference(Control.BackgroundProperty, action == "advance" ? "PanelFillSelected" : "PanelFill");
        if (action == "advance") button.SetResourceReference(Control.BorderBrushProperty, "PanelAccent");
        AutomationProperties.SetAutomationId(button, "reading-" + action); _actions.Add(action, button);
        return button;
    }
    private async void Run(string action)
    {
        try { await Session.Invoke(action, Context.Lifetime); }
        catch (OperationCanceledException) when (Context.Lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Context.Lifetime.IsCancellationRequested || Ended) { }
        catch (Exception error) { Session.Report(error); }
    }

    public override void SetViewport(ModuleViewport viewport)
    {
        double scale = Math.Clamp(viewport.ContentScale, .75, 2);
        _book.FontSize = 18 * scale; _count.FontSize = 42 * scale;
        _remaining.FontSize = 12 * scale; _result.FontSize = _host.FontSize = 11 * scale;
        _contents.Margin = new Thickness(Math.Max(14, viewport.HorizontalInset), 10, Math.Max(14, viewport.HorizontalInset), 12);
    }
    protected override void Render()
    {
        var data = Session.Data;
        _book.Text = data.Book; _book.ToolTip = data.Book;
        _count.Text = $"{data.Page} / {data.Target}";
        _remaining.Text = data.Page == data.Target ? T("阅读目标已完成", "Reading goal complete")
            : T($"还剩 {data.Target - data.Page} 页 · 已完成 {100.0 * data.Page / data.Target:0}%", $"{data.Target - data.Page} pages left · {100.0 * data.Page / data.Target:0}% complete");
        _read.Width = new GridLength(data.Page, GridUnitType.Star);
        _unread.Width = new GridLength(data.Target - data.Page, GridUnitType.Star);
        _result.Text = Session.CanSave ? T(Session.ResultChinese, Session.ResultEnglish)
            : T("原数据无法读取，已保留；本次进度不会写入。", "The unreadable original data is preserved; this session will not be saved.");
        _details.Header = T("验收详情", "Acceptance details");
        _host.Text = Session.Host is { } state
            ? T($"面板修订 {state.Revision} · {(state.Expanded ? "已展开" : "已收起")} · 页面 {state.PageId}\n主题 {state.ThemeId} · 内容缩放 {state.ContentScale:P0} · 显示器布局 {state.DisplayRevision}",
                $"Panel revision {state.Revision} · {(state.Expanded ? "expanded" : "collapsed")} · Page {state.PageId}\nTheme {state.ThemeId} · Content scale {state.ContentScale:P0} · Display layout {state.DisplayRevision}")
            : T("等待宿主交付初始面板状态。", "Waiting for the host's initial panel state.");
        foreach (var (action, button) in _actions)
        {
            string label = action switch
            {
                "next-page" => T("＋1 页", "+1 page"), "advance" => T($"＋{data.Step} 页", $"+{data.Step} pages"),
                "undo" => T("撤销", "Undo"),
                "check-background" => T("验证后台拦截", "Check background policy"), "check-page-scope" => T("验证页面归属", "Check page ownership"),
                _ => T("释放高亮", "Release accent")
            };
            button.Content = label; AutomationProperties.SetName(button, label); button.IsEnabled = Session.CanExecute(action);
        }
        _settings.Content = T("编辑目标", "Edit goal"); AutomationProperties.SetName(_settings, _settings.Content.ToString());
    }
}
