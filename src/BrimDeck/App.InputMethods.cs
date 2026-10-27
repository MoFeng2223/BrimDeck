using System.Windows.Threading;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App
{
    internal InputMethodController InputMethods { get; private set; } = null!;
    private readonly DispatcherTimer _inputMethodWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MainWindow? _inputMethodDeck;

    private void InitializeInputMethods()
    {
        InputMethods = new(action => Dispatcher.BeginInvoke(DispatcherPriority.Input, action), target => InputMethodPanel(target)?.CanPresentInputMethods == true);
        InputMethods.Started += target =>
        {
            var deck = InputMethodPanel(target);
            if (_inputMethodDeck != deck) _inputMethodDeck?.ClearInputMethodPresentation();
            _inputMethodDeck = deck; deck?.DismissInputMethodResult();
        };
        InputMethods.Selecting += (target, cycle) =>
        {
            var deck = InputMethodPanel(target);
            if (deck is null) return;
            if (_inputMethodDeck != deck) _inputMethodDeck?.EndInputMethodSelection();
            _inputMethodDeck = deck; deck.ShowInputMethodSelection(cycle);
        };
        InputMethods.Released += () => _inputMethodDeck?.EndInputMethodSelection();
        InputMethods.Finished += (name, success) => { if (_inputMethodDeck?.CanPresentInputMethods == true) _inputMethodDeck.ShowInputMethodResult(name, success); };
        InputMethods.Notified += (target, profile) =>
        {
            var deck = InputMethodPanel(target);
            if (deck?.CanPresentInputMethods != true || deck.IsExpanded) return;
            if (_inputMethodDeck != deck) _inputMethodDeck?.ClearInputMethodPresentation();
            _inputMethodDeck = deck; deck.ShowInputMethodResult(profile.Name, true);
        };
        InputMethods.ResetPresentation += () =>
        { foreach (var deck in Decks.ToList()) deck.ClearInputMethodPresentation(); _inputMethodDeck = null; };
        _inputMethodWatch.Tick += (_, _) => InputMethods.Tick();
        ConfigureInputMethods();
        _inputMethodWatch.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += InputMethodSessionChanged;
    }

    private void ConfigureInputMethods() => InputMethods.Configure(Settings.InputMethods,
        BrimDeck.Core.HotkeyText.Same(Settings.InputMethods.Shortcut, Settings.HideHotkey)
        || BrimDeck.Core.HotkeyText.Same(Settings.InputMethods.Shortcut, Settings.ExpandHotkey));

    private MainWindow? InputMethodPanel(InputMethodTarget target)
    {
        if (Exiting || UserHidden || target.Process == Environment.ProcessId || target.Window == 0) return null;
        var monitor = TargetInputMethod.Monitor(target);
        return Decks.FirstOrDefault(d => d.HotkeyMonitor == monitor) ?? Deck;
    }

    private void InputMethodSessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs args)
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { InputMethods.Suspend(); _inputMethodDeck?.ClearInputMethodPresentation(); }); }

    private void DisposeInputMethods()
    {
        _inputMethodWatch.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= InputMethodSessionChanged;
        InputMethods?.Dispose();
    }
}
