using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;

internal static class PromptChecks
{
    internal static void Run(Action<string, bool> check, string? render = null)
    {
        var app = Application.Current ?? new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string key in new[] { "PanelFill", "PanelPopup", "PanelSurface", "PanelTrack", "PanelSwitchFill", "PanelHover", "PanelPressed", "PanelFillSelected" }) app.Resources[key] = System.Windows.Media.Brushes.DimGray;
        foreach (string key in new[] { "PanelPrimary", "PanelSecondary", "PanelTertiary", "PanelPopupBorder" }) app.Resources[key] = System.Windows.Media.Brushes.White;
        app.Resources["PanelAccent"] = System.Windows.Media.Brushes.Cyan; app.Resources["PanelKeyboardFocus"] = new Style();
        for (int radius = 1; radius <= 16; radius++) app.Resources["PanelR" + radius] = new CornerRadius(radius);
        var preferences = new PromptPreferences(); int presentations = 0, commits = 0, unavailable = 0;
        PromptRequest? shown = null; var answer = new PromptAnswer(true, true); bool persist = true;
        UserPrompts.Configure(() => preferences, next => { commits++; if (!persist) return false; preferences = next; return true; }, () => unavailable++,
            (_, request) => { presentations++; shown = request; return answer; });
        var delete = new PromptRequest(PromptId.ClipboardDeleteEntry, "Synthetic deletion risk; originals stay unchanged.");
        try
        {
            check("prompt policy: explicit remembered confirmation is accepted", UserPrompts.Show(null, delete) && commits == 1 && presentations == 1);
            check("prompt policy: the next explicit same-risk action skips presentation", UserPrompts.Show(null, delete) && presentations == 1);
            var combined = delete with { Additional = [new(PromptId.ClipboardClearAll, "Higher risk")] };
            check("prompt policy: new risks in a combined request are still shown", UserPrompts.Show(null, combined) && shown?.Id == PromptId.ClipboardClearAll && shown.Additional.Count == 0);
            preferences = new(); answer = new(false, true);
            check("prompt policy: cancellation never commits checked authorization", !UserPrompts.Show(null, delete) && preferences.Suppressed.Count == 0 && commits == 2);
            answer = new(true, true); persist = false;
            check("prompt policy: a failed preference commit cannot grant future deletion", UserPrompts.Show(null, delete) && !preferences.IsSuppressed(delete.Id));
            int before = presentations; UserPrompts.Show(null, delete);
            check("prompt policy: a failed commit asks again instead of granting permission", presentations == before + 1);
            persist = true;
            var fault = new PromptRequest(PromptId.ClipboardSaveFailed, "Synthetic save failure");
            UserPrompts.Show(null, fault); before = presentations;
            check("prompt policy: suppressed faults return no authorization and never present", !UserPrompts.Show(null, fault) && presentations == before && !preferences.IsSuppressed(delete.Id));
            UserPrompts.Configure(() => new(), _ => false, () => unavailable++, (_, _) => throw new InvalidOperationException("Synthetic missing presentation surface"));
            check("prompt policy: an unavailable confirmation surface cancels safely", !UserPrompts.Show(null, delete) && unavailable == 1);
            foreach (var definition in PromptCatalog.All)
            {
                var window = new PromptWindow(new(definition.Id, "Synthetic multiline consequence.\nOriginal files and the system clipboard are unchanged.")) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                try
                {
                    window.Show(); window.UpdateLayout();
                    check("prompt UI: " + definition.Key + " offers unchecked accessible remembering", window.RememberChoice.IsChecked == false && window.RememberChoice.IsVisible && AutomationProperties.GetAutomationId(window.RememberChoice) == "prompt-do-not-show-again");
                    check("prompt UI: " + definition.Key + " preserves decision semantics", definition.Kind == PromptKind.Confirmation ? window.CancelButton is { IsDefault: true, IsCancel: true } && !window.AcceptButton.IsDefault : window.CancelButton is null && window.AcceptButton.IsDefault);
                    if (render is not null && definition.Id is PromptId.ClipboardClearAll or PromptId.ClipboardSaveFailed)
                    {
                        System.IO.Directory.CreateDirectory(render);
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render((System.Windows.Media.Visual)window.Content);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var stream = System.IO.File.Create(System.IO.Path.Combine(render, definition.Key + ".png")); encoder.Save(stream);
                    }
                }
                finally { window.Close(); }
            }
            UserPrompts.Configure(() => preferences, next => { preferences = next; return true; });
            preferences = new();
            var response = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            response.Tick += (_, _) =>
            {
                if (app.Windows.OfType<PromptWindow>().FirstOrDefault(value => value.IsVisible) is not { } prompt) return;
                prompt.RememberChoice.IsChecked = true;
                prompt.CancelButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            response.Start();
            try { check("prompt UI: canceling a checked real modal never authorizes", !UserPrompts.Show(null, delete) && preferences.Suppressed.Count == 0); }
            finally { response.Stop(); }
        }
        finally { UserPrompts.Configure(() => new(), _ => false); }
    }
}
