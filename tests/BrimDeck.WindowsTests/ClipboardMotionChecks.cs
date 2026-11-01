using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static partial class ClipboardChecks
{
    private static void CheckClipboardMotion(ClipboardView island, ClipboardLibraryWindow library, Action<string, bool> check)
    {
        var app = (App)Application.Current;
        bool animations = app.Settings.Animations;
        var list = Descendants<ListBox>(library).Single();
        var islandList = Descendants<ListBox>(island).Single();
        var source = Descendants<ComboBox>(library).Single(combo => AutomationProperties.GetName(combo) == Loc.T("按来源应用筛选", "Filter by source application"));
        var filter = Descendants<Button>(island).Single(button => AutomationProperties.GetAutomationId(button) == "clipboard-settings");
        try
        {
            app.Settings.Animations = true;
            var navigation = Descendants<Button>(library).First(button => button.Style == library.FindResource("LibraryNav"));
            navigation.ApplyTemplate();
            check("clipboard motion: sidebar reuses shared hover and press feedback", Feedback.GetFade(navigation)
                && navigation.Template.FindName("HoverLayer", navigation) is UIElement
                && navigation.Template.FindName("PressLayer", navigation) is UIElement);
            source.ApplyTemplate();
            var toggle = Descendants<ToggleButton>(source).Single(); toggle.ApplyTemplate();
            check("clipboard motion: source selector reuses shared button feedback", Feedback.GetFade(toggle)
                && toggle.Template.FindName("HoverLayer", toggle) is UIElement && toggle.Template.FindName("PressLayer", toggle) is UIElement);
            if (Soft.Enabled)
            {
                // Let the initial detail transition settle; typing must not restart either timeline's entrance.
                WaitClipboardMotion(Soft.Short + 60);
                // FillBehavior.Stop restores opacity but retains the old clock; isolate clocks created by typing itself.
                list.BeginAnimation(UIElement.OpacityProperty, null); islandList.BeginAnimation(UIElement.OpacityProperty, null);
                library.SearchText = "rocket"; island.SearchText = "rocket"; WaitClipboardMotion(130);
                check("clipboard motion: typing filters without dimming either timeline", library.ResultCount == 1 && island.ResultCount == 1
                    && !list.HasAnimatedProperties && !islandList.HasAnimatedProperties && list.Opacity == 1 && islandList.Opacity == 1);
                library.SearchText = "rock"; island.SearchText = "rock"; WaitClipboardMotion(130);
                check("clipboard motion: continued typing never restarts list entrance", !list.HasAnimatedProperties && !islandList.HasAnimatedProperties
                    && list.Opacity == 1 && islandList.Opacity == 1);
                library.SetFilter(new ClipboardQuery { Text = "rocket" }); island.QueryState = new ClipboardQuery { Text = "rocket" }; Pump();
                check("clipboard motion: both filtered timelines use the shared content transition", list.HasAnimatedProperties && islandList.HasAnimatedProperties);
                source.IsDropDownOpen = true; Pump();
                var popup = (Popup)source.Template.FindName("PART_Popup", source);
                check("clipboard motion: source dropdown uses the common popup entrance", popup.IsOpen && popup.Child.HasAnimatedProperties && popup.Child.RenderTransform is TranslateTransform);
                source.IsDropDownOpen = false;
                OpenFilters(island); Pump();
                var menu = filter.ContextMenu!;
                check("clipboard motion: island menus enter through their chrome", menu.Template.FindName("Chrome", menu) is UIElement { HasAnimatedProperties: true });
                var submenu = menu.Items.OfType<MenuItem>().First(item => item.HasItems); submenu.IsSubmenuOpen = true; Pump();
                var nested = (Popup)submenu.Template.FindName("PART_Popup", submenu);
                check("clipboard motion: nested filters reuse popup motion too", nested.IsOpen && nested.Child.HasAnimatedProperties);
                island.ClosePopups();
            }
            else Console.WriteLine("SKIP: entrance animation checks follow Windows' disabled animation effects.");
            app.Settings.Animations = false;
            list.BeginAnimation(UIElement.OpacityProperty, null); islandList.BeginAnimation(UIElement.OpacityProperty, null);
            library.SetFilter(new()); island.QueryState = new(); Pump();
            check("clipboard motion: disabling app animations bypasses content transitions", !list.HasAnimatedProperties && !islandList.HasAnimatedProperties && list.Opacity == 1 && islandList.Opacity == 1);
            source.IsDropDownOpen = true; Pump();
            var still = (Popup)source.Template.FindName("PART_Popup", source);
            check("clipboard motion: disabled animations leave dropdown fully visible", still.IsOpen && !still.Child.HasAnimatedProperties && still.Child.Opacity == 1);
        }
        finally
        {
            source.IsDropDownOpen = false; island.ClosePopups();
            library.SetFilter(new()); island.QueryState = new();
            app.Settings.Animations = animations;
            // Let the shared popup's leaving picture close before the owning fixture continues.
            WaitClipboardMotion(Soft.Quick + 60);
        }
    }
    private static void WaitClipboardMotion(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
