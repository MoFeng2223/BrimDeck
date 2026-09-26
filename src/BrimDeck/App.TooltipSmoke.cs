using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrimDeck;

public partial class App
{
    // Explicit opt-in because this scenario moves the real pointer, restoring it on exit.
    private async Task VerifyTooltipResponseAsync(string output, List<string> checks)
    {
        void Check(string name, bool result) => checks.Add((result ? "PASS " : "FAIL ") + name);
        OpenSettings();
        var window = _settingsWindow!;
        window.Topmost = true;
        window.Activate();
        TooltipPointer.GetCursorPos(out var original);
        async Task MoveTo(Point point)
        {
            if (!TooltipPointer.SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                throw new InvalidOperationException("Unable to move the pointer for tooltip verification.");
            await Task.Delay(30);
        }
        async Task<bool> WaitFor(Func<bool> condition, int timeout = 1000)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.ElapsedMilliseconds < timeout) await Task.Delay(5);
            return condition();
        }
        try
        {
            foreach (var page in new[] { 0, 3 })
            {
                window.ShowPage(page);
                await Task.Delay(100);
                window.UpdateLayout();
                Keyboard.ClearFocus();
                var away = window.PointToScreen(new Point(25, 25));
                await MoveTo(away);
                var info = SettingsElements(window.RootVisual).OfType<FrameworkElement>().First(e => e.ToolTip is ToolTip && e.IsVisible);
                var tip = (ToolTip)info.ToolTip;
                await MoveTo(info.PointToScreen(new Point(info.ActualWidth / 2, info.ActualHeight / 2)));
                Check($"page {page} explanation opens on real mouse hover", await WaitFor(() => tip.IsOpen && tip.ActualWidth > 0));
                Capture(tip, Path.Combine(output, $"tooltip-{page}.png"));
                await MoveTo(away);
                Check($"page {page} explanation closes after moving away", await WaitFor(() => !tip.IsOpen));
            }
        }
        finally
        {
            TooltipPointer.SetCursorPos(original.X, original.Y);
            window.Close();
        }
    }

    private static class TooltipPointer
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    }
}
