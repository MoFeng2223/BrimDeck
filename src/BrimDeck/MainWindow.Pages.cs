using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private readonly Dictionary<string, Button> _pluginPageButtons = new(StringComparer.Ordinal);
    private Button? _morePages;
    private Panel? _pageButtonContainer;
    private double PageSwitchRoom { get; set; } = 90;
    private (double Width, double Height) LargestPageSize()
    {
        var directory = PageDirectory;
        var shown = directory.Visible;
        return shown.Count == 0 ? DeckPages.Size(Settings, DeckPage.Usage)
            : (shown.Max(p => directory.Size(p).Width), shown.Max(p => directory.Size(p).Height));
    }
    private void RenderPageNavigation()
    {
        _pageButtonContainer ??= AIUsageButton.Parent as Panel;
        if (_pageButtonContainer is not { } container) return;
        var directory = PageDirectory;
        var visible = directory.Visible;
        var all = new List<(DeckPageDefinition Page, Button Button)>();
        foreach (var page in visible)
        {
            Button button;
            if (page.BuiltIn is { } builtin) button = PageButton(builtin);
            else if (!_pluginPageButtons.TryGetValue(page.Id, out button!))
            {
                button = new Button { Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = _header.ButtonHeight };
                string id = page.Id;
                button.Click += (_, _) => OpenModulePage(id);
                _pluginPageButtons[id] = button;
            }
            string title = Loc.T(page.Title, page.TitleEn);
            button.ToolTip = title; AutomationProperties.SetName(button, title);
            string visualKey = page.IconPath + "|" + title;
            if (page.BuiltIn is null && !Equals(button.Tag, visualKey))
            {
                button.Tag = visualKey;
                if (page.IconPath is { Length: > 0 } path)
                {
                    var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(path), Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.5 };
                    icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
                    button.Content = icon;
                }
                else button.Content = new TextBlock { Text = title.Length > 0 ? title[..1] : "·", FontSize = 13, FontFamily = UI.PanelFont };
            }
            button.Visibility = Visibility.Visible;
            button.Foreground = UI.Brush(page.Id == CurrentPageId ? UI.Primary : UI.Secondary);
            all.Add((page, button));
        }
        foreach (var id in _pluginPageButtons.Keys.Except(visible.Select(p => p.Id), StringComparer.Ordinal).ToArray()) _pluginPageButtons.Remove(id);
        // Keep room for the clock and the page's own tools; button count, never title measurements, controls overflow.
        double tools = IsMusicPage ? 236 : IsUsagePage ? 230 : 166;
        int capacity = Math.Clamp((int)Math.Floor((Math.Max(240, _pageWidth) - tools) / 29), 2, 12);
        bool overflow = all.Count > capacity;
        var projected = all.Take(overflow ? capacity - 1 : capacity).Select(p => p.Button).ToList();
        if (overflow)
        {
            if (_morePages is null)
            {
                _morePages = new Button { Content = "⋯", Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = _header.ButtonHeight };
                _morePages.Click += (_, _) => OpenPagesMenu();
            }
            _morePages.ToolTip = Loc.T("更多页面", "More pages"); AutomationProperties.SetName(_morePages, Loc.T("更多页面", "More pages"));
            _morePages.Foreground = UI.Brush(all.Skip(capacity - 1).Any(p => p.Page.Id == CurrentPageId) ? UI.Primary : UI.Secondary);
            projected.Add(_morePages);
        }
        _navigationButtons = projected;
        string currentId = CurrentPageId;
        _navigationSelection = all.FirstOrDefault(p => p.Page.Id == currentId).Button;
        if (_navigationSelection is null || !projected.Contains(_navigationSelection)) _navigationSelection = overflow ? _morePages : projected.FirstOrDefault();
        if (!container.Children.Cast<UIElement>().SequenceEqual(projected))
        {
            container.Children.Clear(); foreach (var button in projected) container.Children.Add(button);
        }
        for (int i = 0; i < projected.Count; i++) projected[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
        FitPageButtons(_header); PageSwitchRoom = projected.Count * 29 + 3;
        PageCapsule.Visibility = projected.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }
    private void OpenPagesMenu()
    {
        if (_morePages is null) return;
        if (_modulePageMenu?.IsOpen == true) { _modulePageMenu.IsOpen = false; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu"), Width = 260, MaxHeight = 400,
            PlacementTarget = _morePages, Placement = PlacementMode.Bottom };
        var search = new TextBox { MinWidth = 208, Padding = new Thickness(8, 5, 8, 5), FontFamily = UI.PanelFont };
        AutomationProperties.SetName(search, Loc.T("搜索页面", "Search pages"));
        var searchItem = new MenuItem { Header = search, StaysOpenOnClick = true, Focusable = false };
        menu.Items.Add(searchItem);
        foreach (var page in PageDirectory.All)
        {
            string title = Loc.T(page.Title, page.TitleEn);
            var item = new MenuItem { Style = (Style)FindResource("ModulePageMenuItem"), Header = title, IsCheckable = true,
                IsChecked = page.Id == CurrentPageId, IsEnabled = PageDirectory.Shown(page), Tag = title + " " + page.Id };
            item.Click += (_, _) => { menu.IsOpen = false; if (page.BuiltIn is not null) SelectPage(page.Id); else OpenModulePage(page.Id); };
            menu.Items.Add(item);
        }
        search.TextChanged += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>().Skip(1)) item.Visibility = ((string)item.Tag).Contains(search.Text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        };
        menu.Opened += (_, _) => _leave.Stop();
        menu.Closed += (_, _) => { if (ReferenceEquals(_modulePageMenu, menu) && _expanded) PointerChanged(InsideOpenPanel(WindowsHost.Cursor())); };
        _modulePageMenu = menu; UI.Scale(menu, _scale); menu.VerticalOffset = 6 * _scale; menu.IsOpen = true;
    }
}
