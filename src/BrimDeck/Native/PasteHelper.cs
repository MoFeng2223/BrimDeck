using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Pasting into a window of a program run as administrator, which drops the keys an ordinary BrimDeck sends (ShelfService.Paste).
// The first such paste starts this same program as administrator (a UAC prompt) with Switch; that copy, the helper, presses Ctrl+V
// for BrimDeck from then on. The text or files are on the clipboard already. A declined prompt is asked again at the next such paste.
//
// BrimDeck opens a pipe under a name no one can guess and accepts only the process it started; the helper accepts only the
// BrimDeck that started it. The helper does nothing but paste into the window in front, and may bring the window forward only for
// its first paste, the one its UAC prompt interrupted. It ends when the pipe does: when BrimDeck exits or ends. While it runs, a
// program that took over BrimDeck could have it paste into a window run as administrator.
internal sealed class PasteHelper : IDisposable
{
    public const string Switch = "--paste-helper";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly SemaphoreSlim _one = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    // Pastes into the window through the helper, starting the helper first when it is not running; false when the UAC prompt was
    // declined or the paste could not be made.
    public async Task<bool> PasteAsync(IntPtr window)
    {
        await _one.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (_pipe is not { IsConnected: true } && !await StartAsync()) return false;
            await _writer!.WriteLineAsync(HelperLine.Make("paste", window.ToInt64().ToString()));
            var reply = await _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return reply is not null && HelperLine.Parse(reply) is ["pasted", "1"];
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            Close();
            return false;
        }
        finally { _one.Release(); }
    }

    private async Task<bool> StartAsync()
    {
        Close();
        var name = HelperRights.PipeName("Paste");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            if (await StartElevated(name) is not { } started) { pipe.Dispose(); return false; }
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (HelperRights.ClientOf(pipe) != (uint)started) { pipe.Dispose(); return false; }
        }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException)
        {
            pipe.Dispose();
            return false;
        }
        if (_disposed) { pipe.Dispose(); return false; }
        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        return true;
    }

    // The UAC prompt holds the thread that asks for it until it is answered, so it is asked on a thread of its own.
    private static Task<int?> StartElevated(string name)
    {
        var done = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
                info.ArgumentList.Add(Switch); info.ArgumentList.Add(name); info.ArgumentList.Add(Environment.ProcessId.ToString());
                using var process = Process.Start(info);
                done.SetResult(process?.Id);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { done.SetResult(null); }
        }) { IsBackground = true, Name = "BrimDeck paste helper start" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private void Close()
    {
        _reader?.Dispose(); _writer?.Dispose(); _pipe?.Dispose();
        _reader = null; _writer = null; _pipe = null;
    }

    // Ends the helper: it goes as soon as the pipe closes.
    public void Dispose()
    {
        _disposed = true;
        Close();
    }

    // ---- The helper ----
    // Started by BrimDeck with the pipe's name and BrimDeck's process id; runs as administrator until the pipe closes.
    public static int Run(string name, string parent)
    {
        if (!uint.TryParse(parent, out uint parentId)) return 1;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        try { pipe.Connect(10000); }
        catch (Exception error) when (error is TimeoutException or IOException) { return 1; }
        if (HelperRights.ServerOf(pipe) != parentId || !HelperRights.IsThisProgram(parentId)) return 1;
        using var reader = new StreamReader(pipe, Utf8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        bool first = true;
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (HelperLine.Parse(line) is not ["paste", var value] || !long.TryParse(value, out long handle)) continue;
                bool pasted = Paste(new IntPtr(handle), bringForward: first);
                first = false;
                writer.WriteLine(HelperLine.Make("pasted", pasted ? "1" : "0"));
            }
        }
        catch (IOException) { }
        return 0;
    }

    // As BrimDeck pastes (ShelfService.Paste): once the window is in front and no modifier is still held, within a second.
    private static bool Paste(IntPtr window, bool bringForward)
    {
        if (!PasteInput.IsOther(window)) return false;
        long started = Environment.TickCount64;
        bool asked = false;
        while (true)
        {
            if (PasteInput.Foreground != window)
            {
                if (!bringForward) return false;
                if (!asked) { asked = true; PasteInput.Activate(window); }
            }
            else if (!PasteInput.ModifiersDown) return PasteInput.SendPaste();
            if (Environment.TickCount64 - started > 1000) return false;
            Thread.Sleep(20);
        }
    }
}
