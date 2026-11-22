using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck;
using BrimDeck.Core;

namespace ReadmeMedia;

// The row of the notices' "Apps" settings page that says brim is not on PATH (SettingsWindow.PathRow), in each of its states: not
// on PATH, adding it failed, added, and on PATH (no row). The page's own ways of reading and changing PATH are replaced, so the
// registry is neither read nor written, and a brim.exe is put beside this program for as long as the pictures take, since the row
// only shows where there is one. The description behind the info mark is written out with the tips of the first picture. For
// checking, not for the README.
internal sealed partial class Renderer
{
    private async Task BrimPathScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        string brim = Path.Combine(AppContext.BaseDirectory, "brim.exe");
        bool made = !File.Exists(brim);
        if (made) File.WriteAllBytes(brim, []);
        try
        {
            async Task Shot(string name, bool onPath, string? failure, bool press, bool tips = false)
                => await SettingsPage(name, 20, height: 700, tips: tips, prepare: window =>
                {
                    Program.SetField(window, "BrimOnPath", (Func<bool>)(() => onPath));
                    Program.SetField(window, "BrimAddToPath", (Func<string?>)(() => failure));
                    if (press) window.Loaded += (_, _) => Press(window);
                    // The page is built by ShowPage, which SettingsPage calls next; the button is pressed once it is there.
                    if (press) window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Press(window));
                });
            await Shot("brim-path-missing", onPath: false, failure: null, press: false, tips: true);
            await Shot("brim-path-failed", onPath: false, failure: Loc.T("对注册表项“HKEY_CURRENT_USER\\Environment”的访问被拒绝。", "Access to the registry key 'HKEY_CURRENT_USER\\Environment' is denied."), press: true);
            await Shot("brim-path-added", onPath: false, failure: null, press: true);
            await Shot("brim-path-present", onPath: true, failure: null, press: false);
        }
        finally { if (made) File.Delete(brim); }
    }

    // Clicks the page's "Add to PATH" button, as the user would.
    private static void Press(SettingsWindow window)
    {
        string label = Loc.T("加入 PATH", "Add to PATH");
        foreach (var button in Buttons(window.RootVisual))
            if (button.Content as string == label) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return; }
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (var inner in Buttons(child)) yield return inner;
        }
    }
}
