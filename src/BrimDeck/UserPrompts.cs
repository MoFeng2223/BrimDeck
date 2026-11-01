using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// A remembered confirmation authorizes only the next explicit operation of the same risk type.
// An acknowledged fault suppresses presentation only; the caller must still enforce its save barrier.
internal static class UserPrompts
{
    private static Func<PromptPreferences> _preferences = () => new();
    private static Func<PromptPreferences, bool> _commit = _ => false;
    private static Func<Window?, PromptRequest, PromptAnswer> _present = Present;
    private static Action? _unavailable;
    internal static void Configure(Func<PromptPreferences> preferences, Func<PromptPreferences, bool> commit,
        Action? unavailable = null, Func<Window?, PromptRequest, PromptAnswer>? present = null)
    { _preferences = preferences; _commit = commit; _unavailable = unavailable; _present = present ?? Present; }
    internal static bool Show(Window? owner, PromptRequest request)
    {
        var remaining = request.Parts.Where(part => !_preferences().IsSuppressed(part.Id)).ToArray();
        if (remaining.Length == 0) return PromptCatalog.Get(request.Id).Kind == PromptKind.Confirmation;
        var effective = remaining[0] with { Additional = remaining.Skip(1).ToArray() };
        PromptAnswer answer;
        try { answer = _present(owner, effective); }
        catch (Exception error) when (error is System.Windows.Markup.XamlParseException or InvalidOperationException or System.IO.IOException)
        {
            // A missing presentation layer is never consent. Saving and exit remain blocked by the caller.
            _unavailable?.Invoke();
            return false;
        }
        if (answer is { Accepted: true, DoNotShowAgain: true })
        {
            var next = _preferences().Copy(); next.Remember(effective, answer);
            // Failed settings writes must not silently grant future destructive operations.
            _commit(next);
        }
        return answer.Accepted;
    }
    private static PromptAnswer Present(Window? owner, PromptRequest request)
    {
        var window = new PromptWindow(request);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner; window.Topmost = owner.Topmost;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        try { window.ShowDialog(); return window.Answer; }
        finally { if (window.IsVisible) window.Close(); }
    }
}

internal sealed class PromptWindow : Window
{
    internal PromptAnswer Answer { get; private set; }
    internal CheckBox RememberChoice { get; }
    internal Button AcceptButton { get; }
    internal Button? CancelButton { get; }
    internal PromptWindow(PromptRequest request)
    {
        var definition = PromptCatalog.Get(request.Id);
        bool confirmation = definition.Kind == PromptKind.Confirmation;
        Title = definition.Title; Width = 460; MinWidth = 300; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 48);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(FontFamilyProperty, "FeedbackFont"); UseLayoutRounding = true; SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(PromptWindow).Assembly.GetName().Name};component/ClipboardLibraryResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(PromptWindow).Assembly.GetName().Name};component/FeedbackResources.xaml", UriKind.Relative) });
        var chrome = new Border { Padding = new Thickness(24), BorderThickness = new Thickness(1) };
        chrome.SetResourceReference(Border.BackgroundProperty, "PanelPopup"); chrome.SetResourceReference(Border.BorderBrushProperty, "PanelPopupBorder"); chrome.SetResourceReference(Border.CornerRadiusProperty, "PanelR16");
        var layout = new StackPanel(); chrome.Child = layout;
        var title = new TextBlock { Text = definition.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary"); layout.Children.Add(title);
        var message = new TextBlock { Text = string.Join("\n\n", request.Parts.Select(part => part.Message)), FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 18) };
        message.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
        layout.Children.Add(new ScrollViewer { Content = message, MaxHeight = Math.Max(100, MaxHeight - 210), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        RememberChoice = new CheckBox { Content = Loc.T("下次不再提示", "Don't show this again"), IsChecked = false, Style = (Style)FindResource("FeedbackCheckBox") };
        AutomationProperties.SetAutomationId(RememberChoice, "prompt-do-not-show-again"); layout.Children.Add(RememberChoice);
        var explanation = new TextBlock { Text = confirmation ? Loc.T("以后主动执行同类操作时，不再二次确认。", "Future explicit actions of this type will skip confirmation.")
            : Loc.T("仅关闭这类弹出提示，不会忽略错误或跳过保存。", "Hides this alert only. Errors and save checks still apply."), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(23, 5, 0, 20) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "PanelTertiary"); layout.Children.Add(explanation);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        if (confirmation)
        {
            CancelButton = new Button { Content = Loc.T("取消", "Cancel"), IsCancel = true, IsDefault = true, Style = (Style)FindResource("LibraryButton"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(CancelButton, "prompt-cancel"); actions.Children.Add(CancelButton);
            CancelButton.Click += (_, _) => { Answer = default; DialogResult = false; };
        }
        AcceptButton = new Button { Content = confirmation && request.Additional.Count > 0 ? Loc.T("继续操作", "Continue") : definition.Action,
            IsDefault = !confirmation, Style = (Style)FindResource(confirmation && request.Id is not (PromptId.ClipboardDeleteCollection or PromptId.ClipboardEndSession or PromptId.ShelfEndSession) ? "LibraryDangerButton" : "LibraryPrimaryButton"), MinWidth = 96 };
        AutomationProperties.SetAutomationId(AcceptButton, "prompt-accept"); actions.Children.Add(AcceptButton);
        AcceptButton.Click += (_, _) => { Answer = new(true, RememberChoice.IsChecked == true); DialogResult = true; };
        layout.Children.Add(actions); Content = chrome;
        AutomationProperties.SetAutomationId(this, "business-prompt-" + definition.Key);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Answer = default; DialogResult = false; e.Handled = true; } };
        SourceInitialized += (_, _) =>
        {
            var (_, _, work, scale) = Native.WindowsHost.WindowScreen(this);
            MaxWidth = Math.Max(1, work.Width / scale - 24); MinWidth = Math.Min(300, MaxWidth); Width = Math.Min(460, MaxWidth);
            MaxHeight = Math.Max(1, work.Height / scale - 24);
        };
        Loaded += (_, _) => (CancelButton ?? AcceptButton).Focus();
    }
}
