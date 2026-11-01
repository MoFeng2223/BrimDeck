using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class Program
{
    private sealed record PasteReceiverState(long Window, string Text, string Decoy, int Pastes);

    // Explicit opt-in only: desktops in one window station share a clipboard, so a new desktop alone is not isolation.
    // Use a disposable VM / Windows Sandbox session. This probe must never run against the user's ordinary desktop.
    private static int CheckPasteIntegration(string[] args)
    {
        if (!args.Contains("--allow-input") || Environment.GetEnvironmentVariable("BRIMDECK_PASTE_E2E") != "isolated-session")
        {
            Console.WriteLine("NOT RUN: real clipboard and input require --allow-input plus BRIMDECK_PASTE_E2E=isolated-session in a disposable test session.");
            return 2;
        }
        string scratch = Path.Combine(Path.GetTempPath(), "BrimDeck-Paste-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        string nonce = Guid.NewGuid().ToString("N"), report = Path.Combine(scratch, nonce + ".json");
        using var receiver = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false } };
        receiver.StartInfo.ArgumentList.Add("--paste-receiver"); receiver.StartInfo.ArgumentList.Add(scratch); receiver.StartInfo.ArgumentList.Add(nonce);
        Window? chooser = null; ClipboardPasteSession? session = null; bool receiverStarted = false;
        var heldKeys = new HashSet<ushort>();
        void ReleaseKeys()
        {
            if (heldKeys.Count == 0) return;
            // Only release this fixture's keys; key-up events must not remain queued after a foreground-abort.
            try { PasteKeys([], heldKeys.ToArray()); } finally { heldKeys.Clear(); }
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            receiverStarted = receiver.Start();
            PasteReceiverState Read()
            {
                using var stream = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<PasteReceiverState>(stream)!;
            }
            bool Ready() { try { return File.Exists(report) && Read().Window != 0; } catch (IOException) { return false; } }
            WaitPaste(Ready, 5000);
            var state = Read(); var targetWindow = new IntPtr(state.Window);
            WaitPaste(() => ClipboardPaste.Foreground == targetWindow, 3000);
            var button = new Button { Content = "Synthetic copy and paste", IsDefault = true, MinHeight = 44, Margin = new Thickness(20) };
            chooser = new Window { Content = button, Width = 360, Height = 130, Left = 60, Top = 60, Title = "BrimDeck isolated paste chooser", ShowActivated = false, ShowInTaskbar = false };
            ClipboardPasteTarget target = default; string payload = ""; bool copyOnly = false; ClipboardPasteResult result = ClipboardPasteResult.Waiting;
            using var hotkeys = new GlobalHotkeys();
            Check("paste receiver: isolated shortcut registers without replacing a user's shortcut", hotkeys.Register(HotkeyKind.Clipboard, 0x4000 | 0x2 | 0x4, 0x87) == HotkeyStatus.Active);
            void OwnedForeground(IntPtr expected = default)
            {
                var foreground = ClipboardPaste.Foreground;
                PasteWindowProcess(foreground, out uint process);
                if ((process != receiver.Id && process != Environment.ProcessId) || (expected != IntPtr.Zero && foreground != expected))
                    throw new InvalidOperationException("ABORT: the intended fixture no longer owns foreground; no more input is allowed.");
            }
            void OpenChooser()
            {
                OwnedForeground();
                if (ClipboardPaste.Foreground != targetWindow) throw new InvalidOperationException("ABORT: receiver no longer owns foreground before capture.");
                target = ClipboardPaste.Capture(targetWindow); chooser.Show(); chooser.Activate(); button.Focus();
            }
            void Advance()
            {
                if (session is null) return;
                OwnedForeground(); result = session.Advance(ClipboardPaste.Platform);
                if (result != ClipboardPasteResult.Waiting) timer.Stop();
            }
            Exception? fixtureFailure = null;
            void Guard(Action action)
            {
                if (fixtureFailure is not null) return;
                try { action(); }
                catch (Exception error) { fixtureFailure = error; timer.Stop(); session?.Cancel(); }
            }
            void WaitOwned(Func<bool> ready, int milliseconds) => WaitPaste(() =>
            {
                if (fixtureFailure is not null) throw new InvalidOperationException("Isolated input fixture aborted.", fixtureFailure);
                OwnedForeground(); return ready();
            }, milliseconds);
            timer.Tick += (_, _) => Guard(Advance);
            button.Click += (_, _) => Guard(() =>
            {
                session?.Cancel(); timer.Stop();
                var data = ClipboardListener.ToDataObject(ClipboardContent.FromText(payload));
                Clipboard.SetDataObject(data, true); chooser.Hide();
                if (copyOnly) { session = null; return; }
                session = new(target, ClipboardPaste.Sequence, ClipboardPaste.Platform.Now); timer.Start(); Advance();
            });
            hotkeys.Pressed += (_, _) => Guard(OpenChooser);
            payload = "synthetic-click-" + nonce; target = ClipboardPaste.Capture(targetWindow);
            chooser.Show(); chooser.UpdateLayout();
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            OwnedForeground(targetWindow);
            var chooserWindow = new WindowInteropHelper(chooser).Handle;
            var hit = PasteWindowAt(new PastePoint { X = (int)point.X, Y = (int)point.Y });
            if (PasteRoot(hit, 2) != chooserWindow) throw new InvalidOperationException("ABORT: another window obscures the synthetic chooser.");
            PasteClick(point); WaitOwned(() => result != ClipboardPasteResult.Waiting, 3000);
            WaitOwned(() => Read().Pastes == 1, 3000); state = Read();
            Check("paste receiver: a real chooser click inserts once at the original caret in another process", result == ClipboardPasteResult.Submitted && state.Text == "left|" + payload + "right" && state.Decoy == "decoy" && state.Pastes == 1);
            string first = payload; payload = "synthetic-hotkey-" + nonce; result = ClipboardPasteResult.Waiting;
            OwnedForeground(targetWindow); heldKeys.UnionWith([0x11, 0x10, 0x87]);
            PasteKeys([0x11, 0x10, 0x87], []); // Ctrl+Shift+F24 stays held while the chooser receives WM_HOTKEY.
            WaitOwned(() => chooser.IsVisible && ClipboardPaste.Foreground == chooserWindow && button.IsKeyboardFocused, 3000);
            OwnedForeground(chooserWindow); ReleaseKeys();
            WaitOwned(() => ClipboardPaste.ModifiersReleased, 1000);
            OwnedForeground(chooserWindow); PasteKeys([0x0D], [0x0D]);
            WaitOwned(() => result != ClipboardPasteResult.Waiting, 3000); WaitOwned(() => Read().Pastes == 2, 3000); state = Read();
            Check("paste receiver: a real shortcut recall and Enter return input after modifiers release", result == ClipboardPasteResult.Submitted && state.Text == "left|" + first + payload + "right" && state.Decoy == "decoy" && state.Pastes == 2);
            copyOnly = true; payload = "synthetic-copy-only-" + nonce;
            OwnedForeground(targetWindow); OpenChooser();
            WaitOwned(() => ClipboardPaste.Foreground == chooserWindow && button.IsKeyboardFocused, 3000);
            OwnedForeground(chooserWindow); PasteKeys([0x0D], [0x0D]); WaitOwned(() => !chooser.IsVisible, 3000);
            string unchanged = state.Text; long until = Environment.TickCount64 + 1400;
            while (Environment.TickCount64 < until) { OwnedForeground(); Pump(); Thread.Sleep(5); }
            state = Read();
            Check("paste receiver: explicit copy-only sends no paste and leaves both receiving fields unchanged", session is null && !timer.IsEnabled && state.Pastes == 2 && state.Text == unchanged && state.Decoy == "decoy");
            Console.WriteLine("PASTE RECEIVER RESULT: real mouse/shortcut input and independent WPF receiver verified. Not a browser, elevated-window, multi-monitor or full production-host acceptance test.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            timer.Stop(); session?.Cancel(); chooser?.Close();
            try { ReleaseKeys(); }
            finally
            {
                if (receiverStarted && !receiver.HasExited) { receiver.Kill(entireProcessTree: true); receiver.WaitForExit(); }
                Directory.Delete(scratch, true);
            }
        }
    }
    private static int RunPasteReceiver(string directory, string nonce)
    {
        if (Environment.GetEnvironmentVariable("BRIMDECK_PASTE_E2E") != "isolated-session" || !Guid.TryParseExact(nonce, "N", out _) || !Path.IsPathFullyQualified(directory)) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var field = new TextBox { Text = "left|right", Margin = new Thickness(20), FontSize = 18 };
        var decoy = new TextBox { Text = "decoy", Margin = new Thickness(20), FontSize = 18 };
        var layout = new StackPanel(); layout.Children.Add(field); layout.Children.Add(decoy);
        var window = new Window { Title = "BrimDeck synthetic receiver", Content = layout, Width = 500, Height = 250, Left = 500, Top = 250 };
        int pastes = 0;
        void Report()
        {
            string path = Path.Combine(directory, nonce + ".json"), temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new PasteReceiverState(new WindowInteropHelper(window).Handle.ToInt64(), field.Text, decoy.Text, pastes))); File.Move(temp, path, true);
        }
        DataObject.AddPastingHandler(field, (_, _) => { pastes++; window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Report); });
        window.Loaded += (_, _) => { field.Focus(); field.Select(5, 0); Report(); };
        window.Activated += (_, _) => { if (pastes == 0) { field.Focus(); field.Select(5, 0); } };
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; watchdog.Tick += (_, _) => { watchdog.Stop(); window.Close(); }; watchdog.Start();
        try { app.Run(window); return 0; } finally { watchdog.Stop(); }
    }
    private static void WaitPaste(Func<bool> ready, int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!ready() && Environment.TickCount64 < deadline) { Pump(); Thread.Sleep(5); }
        if (!ready()) throw new TimeoutException("Isolated paste fixture did not reach its next owned state.");
    }
    private static void PasteKeys(ushort[] down, ushort[] up)
    {
        var inputs = down.Select(key => new PasteInput { Type = 1, Data = new PasteInputUnion { Key = new PasteKeyboard { VirtualKey = key } } })
            .Concat(up.Select(key => new PasteInput { Type = 1, Data = new PasteInputUnion { Key = new PasteKeyboard { VirtualKey = key, Flags = 2 } } })).ToArray();
        uint sent = PasteSend((uint)inputs.Length, inputs, Marshal.SizeOf<PasteInput>());
        if (sent == inputs.Length) return;
        // A partial submission can leave this fixture's key-down events unmatched.
        var release = down.Select(key => new PasteInput { Type = 1, Data = new PasteInputUnion { Key = new PasteKeyboard { VirtualKey = key, Flags = 2 } } }).ToArray();
        if (release.Length > 0) PasteSend((uint)release.Length, release, Marshal.SizeOf<PasteInput>());
        throw new InvalidOperationException("Isolated test input was not fully submitted.");
    }
    private static void PasteClick(Point point)
    {
        if (!PasteCursor((int)point.X, (int)point.Y)) throw new InvalidOperationException("Synthetic click location could not be selected.");
        PasteInput[] inputs = [new() { Data = new() { Mouse = new() { Flags = 2 } } }, new() { Data = new() { Mouse = new() { Flags = 4 } } }];
        if (PasteSend(2, inputs, Marshal.SizeOf<PasteInput>()) == 2) return;
        PasteSend(1, [inputs[1]], Marshal.SizeOf<PasteInput>());
        throw new InvalidOperationException("Synthetic mouse click was not fully submitted.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct PasteInput { public uint Type; public PasteInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct PasteInputUnion { [FieldOffset(0)] public PasteMouse Mouse; [FieldOffset(0)] public PasteKeyboard Key; }
    [StructLayout(LayoutKind.Sequential)] private struct PasteMouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct PasteKeyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct PastePoint { public int X, Y; }
    [DllImport("user32", EntryPoint = "WindowFromPoint")] private static extern IntPtr PasteWindowAt(PastePoint point);
    [DllImport("user32", EntryPoint = "GetAncestor")] private static extern IntPtr PasteRoot(IntPtr window, uint flags);
    [DllImport("user32", EntryPoint = "SendInput", SetLastError = true)] private static extern uint PasteSend(uint count, PasteInput[] inputs, int size);
    [DllImport("user32", EntryPoint = "SetCursorPos")] private static extern bool PasteCursor(int x, int y);
    [DllImport("user32", EntryPoint = "GetWindowThreadProcessId")] private static extern uint PasteWindowProcess(IntPtr window, out uint process);
}
