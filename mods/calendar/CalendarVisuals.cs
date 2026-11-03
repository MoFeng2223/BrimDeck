using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Loc = BrimDeck.ModuleLocalization;

namespace BrimDeck.Calendar;

// One place for the little language the month grid, the agenda and the island strip share.
// Text goes through ModuleUi.Centered so it inherits the panel font, tabular figures and trimming.
internal static class CalendarVisuals
{
    internal static string Color(EventCategory category) => category switch
    {
        EventCategory.Work => "#5B8DEF",
        EventCategory.Personal => "#3FB950",
        EventCategory.Important => "#E5534B",
        EventCategory.Health => "#B07CE8",
        _ => "#8B949E"
    };

    internal static string CategoryName(EventCategory category) => category switch
    {
        EventCategory.Work => Loc.T("工作", "Work"),
        EventCategory.Personal => Loc.T("个人", "Personal"),
        EventCategory.Important => Loc.T("重要", "Important"),
        EventCategory.Health => Loc.T("健康", "Health"),
        _ => Loc.T("其他", "Other")
    };

    internal static string FestivalName(Festival festival) => festival switch
    {
        Festival.NewYear => Loc.T("元旦", "New Year"),
        Festival.Eve => Loc.T("除夕", "New Year's Eve"),
        Festival.Spring => Loc.T("春节", "Spring Festival"),
        Festival.Lantern => Loc.T("元宵", "Lantern"),
        Festival.Qingming => Loc.T("清明", "Qingming"),
        Festival.Labour => Loc.T("劳动节", "Labour Day"),
        Festival.DragonBoat => Loc.T("端午", "Dragon Boat"),
        Festival.Qixi => Loc.T("七夕", "Qixi"),
        Festival.MidAutumn => Loc.T("中秋", "Mid-Autumn"),
        Festival.DoubleNinth => Loc.T("重阳", "Double Ninth"),
        Festival.National => Loc.T("国庆", "National Day"),
        Festival.Laba => Loc.T("腊八", "Laba"),
        _ => ""
    };

    // Chinese shows the lunar day; English only shows festivals, because a lunar string would be noise.
    internal static string NoteText(DayInfo info)
    {
        if (info.Festival != Festival.None) return FestivalName(info.Festival);
        return Loc.Language == "zh-CN" ? info.Lunar : "";
    }

    internal static string Weekday(DateOnly day)
    {
        if (Loc.Language == "zh-CN") return "周" + "日一二三四五六"[(int)day.DayOfWeek];
        return new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }[(int)day.DayOfWeek];
    }

    internal static string WeekLetter(int day) =>
        Loc.Language == "zh-CN" ? "日一二三四五六"[day].ToString() : "SMTWTFS"[day].ToString();

    internal static string DayTitle(DateOnly day) =>
        Loc.Language == "zh-CN" ? $"{day.Month}月{day.Day}日 {Weekday(day)}" : $"{day:MMM d} {Weekday(day)}";

    internal static string MonthTitle(DateOnly month) =>
        Loc.Language == "zh-CN" ? $"{month.Month}月 {month.Year}" : $"{month:MMMM yyyy}";

    // "10月19日 – 25日", or with both months when the week straddles one.
    internal static string WeekTitle(DateOnly day, int firstDay)
    {
        var start = MonthGrid.WeekStart(day, firstDay);
        var end = start.AddDays(6);
        if (Loc.Language != "zh-CN")
            return start.Month == end.Month ? $"{start:MMM d} – {end:d}" : $"{start:MMM d} – {end:MMM d}";
        return start.Month == end.Month ? $"{start.Month}月{start.Day}日 – {end.Day}日" : $"{start.Month}月{start.Day}日 – {end.Month}月{end.Day}日";
    }

    // Compact and stable: "2h15", "45m", "now".
    internal static string Span(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return Loc.T("现在", "now");
        if (span.TotalMinutes < 1) return Loc.T("即将", "soon");
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        int hours = (int)span.TotalHours, minutes = span.Minutes;
        return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes:00}";
    }

    internal static TextBlock Label(string text, double size, string resource = "PanelPrimary", FontWeight? weight = null)
    {
        var block = ModuleUi.Centered(text, size, ModuleUi.Primary);
        if (weight is { } value) block.FontWeight = value;
        block.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return block;
    }

    internal static Button TextButton(FrameworkElement owner, string label, Action action, string? tip = null)
    {
        var button = new Button
        {
            Content = label, Height = 24, Padding = new Thickness(8, 2, 8, 2), FontSize = 11,
            FontFamily = ModuleUi.PanelFont, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
        };
        if (owner.TryFindResource("PanelTextButton") is Style style) button.Style = style;
        button.SetResourceReference(Control.ForegroundProperty, "PanelPrimary");
        button.Click += (_, e) => { e.Handled = true; action(); };
        AutomationProperties.SetName(button, tip ?? label);
        if (tip is not null) button.ToolTip = tip;
        return button;
    }

    // A button with the panel's own hover/press fade and keyboard focus ring. The template carries named layers
    // so the host's Feedback helper can fade them exactly as it does for the built-in icon buttons.
    internal static Button Interactive(FrameworkElement owner, double radius, string focusKey = "PanelWideKeyboardFocus")
    {
        var button = new Button
        {
            Cursor = Cursors.Hand, Focusable = true,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (owner.TryFindResource(focusKey) is Style focus) button.FocusVisualStyle = focus;
        button.Template = Template(owner, radius);
        Feedback.SetFade(button, true);
        return button;
    }

    private static ControlTemplate Template(FrameworkElement owner, double radius)
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        var hover = Layer(owner, "PanelHover", radius);
        hover.Name = "HoverLayer";
        var press = Layer(owner, "PanelPressed", radius);
        press.Name = "PressLayer";
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        chrome.AppendChild(presenter);
        grid.AppendChild(hover);
        grid.AppendChild(press);
        grid.AppendChild(chrome);
        return new ControlTemplate(typeof(Button)) { VisualTree = grid };
    }

    private static FrameworkElementFactory Layer(FrameworkElement owner, string key, double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, owner.TryFindResource(key) as Brush ?? Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(UIElement.OpacityProperty, 0.0);
        border.SetValue(UIElement.IsHitTestVisibleProperty, false);
        return border;
    }

    internal static Border Card(UIElement content, double padding)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(padding), Margin = new Thickness(2),
            Child = content
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelFill");
        return card;
    }
}
