using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Protocol;

namespace BrimDeck.Native;

// The local end of brim-notify: a named pipe that only the current user can open, never over the network, named for this
// user and logon session (NoticeProtocol.PipeName). Each connection may send several frames; every frame gets one reply.
// Messages are only data: nothing in them is run or opened.
//
// A sender that waits for how its notice ends (NoticeMessage.Wait) keeps its connection after the reply: the handler's answer
// then carries a wait, the connection leaves the listeners (which go on serving the others) and gets the result as one more
// frame. A sender that closes the connection first (it ended, Ctrl+C) abandons the wait.
internal sealed class NoticePipeServer : IDisposable
{
    // What the handler makes of a message: the reply, and for a sender that waits the result to come and what to do when
    // the sender goes away first.
    internal sealed record Answer(NoticeMessage Reply, Task<NoticeMessage>? Result = null, Action? Abandoned = null);

    private const int Instances = 4;
    // Connections that may wait for a result at once; a further one is told so and not kept (dev-docs/brim-cli.md).
    internal const int Waiters = 16;
    // A result is waited for at most this long past the longest time a notice may stay.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(NoticeProtocol.MaxTimeoutMs) + TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
    // A sender that does not read its reply lets go of the instance after this (life-L11, sec-L2).
    private static readonly TimeSpan ReplyLimit = TimeSpan.FromSeconds(10);
    // How long a new server waits for the previous one to close its instances before taking the name anyway.
    private static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(2);
    // The previous server's listeners. Switching notices off and on replaces the server; until the old instances are closed,
    // FirstPipeInstance would fail and the channel would be down for a retry (life-L2). Only touched on the UI thread.
    private static Task s_released = Task.CompletedTask;
    private readonly Func<NoticeMessage, Task<Answer>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _listeners = [];
    private readonly object _gate = new();
    // The connections that wait for a result, under _gate; each holds an instance of the pipe until it ends.
    private readonly List<Task> _waits = [];
    private int _waiting;
    // Messages of every kind over all connections, counted here so that a flood is answered without reaching the UI thread
    // (sec-L2). It is above the notices' own limit (NoticeCenter.Admit), which still decides for notices.
    private readonly IntakeGate _intake = new(300, TimeSpan.FromSeconds(10));
    private int _live;
    public string Name { get; }
    // Set when the pipe could not be created, for example because another BrimDeck of this user already serves the name.
    public string? Error { get; private set; }
    // A message's handling failed; the sender got "internal-error".
    public Action<Exception>? Failed { get; set; }

    // name: another name than this user's and session's own, for a check that must not take BrimDeck's pipe.
    public NoticePipeServer(Func<NoticeMessage, Task<Answer>> handle, string? name = null)
    {
        _handle = handle;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Name = name ?? NoticeProtocol.PipeName(process.SessionId, WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
    }

    public void Start()
    {
        var previous = s_released;
        for (int i = 0; i < Instances; i++) _listeners.Add(Task.Run(() => ListenAsync(previous)));
    }

    private PipeSecurity Security()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task ListenAsync(Task previous)
    {
        try { await previous.WaitAsync(ReleaseLimit, _stop.Token); }
        catch (Exception) when (!_stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { return; }
        var retry = TimeSpan.FromMilliseconds(200);
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            bool first = false;
            try
            {
                lock (_gate)
                {
                    // While none of BrimDeck's instances exists, the name must be free: FirstPipeInstance fails if anyone holds it.
                    first = _live == 0;
                    var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                    pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, Instances + Waiters, PipeTransmissionMode.Byte, options, 0, 0, Security());
                    _live++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = first && HeldByAnotherUser()
                    ? Loc.T("通道名称被另一个用户占用。", "Another user holds the channel's name.")
                    : ex.Message;
                try { await Task.Delay(retry, _stop.Token); } catch (OperationCanceledException) { return; }
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
                continue;
            }
            Error = null;
            retry = TimeSpan.FromMilliseconds(200);
            // Kept: the connection went on to wait for a result (WaitAsync), which closes it; this listener serves the next one.
            bool kept = false;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                kept = await ServeAsync(pipe);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
            finally
            {
                if (!kept)
                {
                    await pipe.DisposeAsync();
                    lock (_gate) _live--;
                }
            }
        }
    }

    // The connection of a sender that waits: the result goes to it as one more frame, unless the sender closes the connection
    // first, which abandons the wait. Then the connection ends either way.
    private async Task WaitAsync(NamedPipeServerStream pipe, Answer answer)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(WaitLimit);
            // A sender that waits sends nothing more: its next read ends when it closes the connection.
            var gone = pipe.ReadAsync(new byte[1], limit.Token).AsTask();
            // Still reading when the result comes first: the read then ends with the pipe, which is expected.
            _ = gone.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            var first = await Task.WhenAny(answer.Result!, gone);
            if (first == answer.Result)
            {
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                reply.CancelAfter(ReplyLimit);
                await NoticeFrames.WriteAsync(pipe, await answer.Result!, reply.Token);
            }
            else answer.Abandoned?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { answer.Abandoned?.Invoke(); }
        catch (Exception ex) { Failed?.Invoke(ex); answer.Abandoned?.Invoke(); }
        finally
        {
            await pipe.DisposeAsync();
            lock (_gate) { _live--; _waiting--; }
        }
    }

    // Whether the name is taken by a pipe another user created (the name can be worked out, so another local user could take
    // it first): the client side refuses a pipe its own user does not own, or may not open. Another BrimDeck of this user,
    // or one that is busy, does not count.
    private bool HeldByAnotherUser()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(200);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { return false; }
    }

    // True when the connection was handed on to wait for a result (WaitAsync), which then owns it.
    private async Task<bool> ServeAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            idle.CancelAfter(Idle);
            NoticeMessage? message;
            try { message = await NoticeFrames.ReadMessageAsync(pipe, idle.Token); }
            catch (InvalidDataException)
            {
                // A malformed frame cannot be skipped reliably; the sender is told once and the connection ends.
                idle.CancelAfter(ReplyLimit);
                await NoticeFrames.WriteAsync(pipe, NoticeProtocol.Reply(NoticeResults.Rejected, "bad-frame"), idle.Token);
                return false;
            }
            if (message is null) return false;
            Answer answer;
            if (!_intake.Admit(Environment.TickCount64)) answer = new(NoticeProtocol.Reply(NoticeResults.Rejected, "rate-limited", message.EventId));
            else
            {
                try { answer = await _handle(message); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Failed?.Invoke(ex); answer = new(NoticeProtocol.Reply(NoticeResults.Rejected, "internal-error")); }
            }
            // A wait beyond the limit is not kept: the sender hears that nobody waits and the notice is given up.
            bool waits = answer.Result is not null;
            if (waits)
            {
                bool room;
                lock (_gate) { room = _waiting < Waiters; if (room) _waiting++; }
                if (!room)
                {
                    answer.Abandoned?.Invoke();
                    answer = new(NoticeProtocol.Reply(NoticeResults.Rejected, "too-many-waiting", message.EventId));
                    waits = false;
                }
            }
            idle.CancelAfter(ReplyLimit);
            try { await NoticeFrames.WriteAsync(pipe, answer.Reply, idle.Token); }
            catch when (waits)
            {
                lock (_gate) _waiting--;
                answer.Abandoned?.Invoke();
                throw;
            }
            if (!waits) continue;
            var wait = WaitAsync(pipe, answer);
            lock (_gate) { _waits.RemoveAll(task => task.IsCompleted); _waits.Add(wait); }
            return true;
        }
        return false;
    }

    // Not waited for: Dispose runs on the UI thread, which a listener serving a message may be waiting for. Each listener
    // closes its pipe as it sees the cancellation; the token source goes once they all have, and the next server waits for
    // the same moment before it takes the name.
    public void Dispose()
    {
        _stop.Cancel();
        Task[] waits;
        lock (_gate) waits = [.. _waits];
        var all = Task.WhenAll([.. _listeners, .. waits]);
        s_released = all;
        _ = all.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
