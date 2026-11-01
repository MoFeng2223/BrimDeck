using System.Windows.Threading;

namespace DropShelf.Services;

/// <summary>
/// 后台读取缩略图、解码和编码图片的常驻线程。
/// WPF 的图片对象会给创建它的线程配一个 Dispatcher 和隐藏窗口；放在线程池线程上做时，
/// 线程池换线程后旧的 Dispatcher 不会被关闭，长时间运行后越积越多。所有后台图片处理都集中到固定的两个线程：
/// 缩略图等短小的工作一个，整批压缩图片这类耗时的工作另一个，后者不会挡住缩略图。
/// </summary>
internal static class ImagingThread
{
    private static readonly object Sync = new();
    private static Dispatcher? _worker, _longWorker;
    private static int _leases;
    internal static event Action? Idle;
    internal static int LeaseCount { get { lock (Sync) return _leases; } }
    internal static IDisposable Acquire()
    { lock (Sync) _leases++; return new Lease(); }
    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Sync)
            {
                if (--_leases != 0) return;
                Idle?.Invoke();
                _worker?.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
                _longWorker?.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
                _worker = _longWorker = null;
            }
        }
    }

    /// <summary>轮到这项工作时如果已经取消（例如置物架已关闭），直接跳过。</summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default) =>
        Queue(work, cancellationToken, false);

    public static Task RunAsync(Action work) => RunAsync(() =>
    {
        work();
        return true;
    });

    public static Task<T> RunLongAsync<T>(Func<T> work) => Queue(work, default, true);

    private static async Task<T> Queue<T>(Func<T> work, CancellationToken cancellation, bool longWork)
    {
        using var lease = Acquire();
        Dispatcher worker;
        lock (Sync)
            worker = longWork ? _longWorker ??= Start("DropShelf 图片压缩") : _worker ??= Start("DropShelf 图片处理");
        return await Run(worker, work, cancellation).ConfigureAwait(false);
    }

    private static Task<T> Run<T>(Dispatcher worker, Func<T> work, CancellationToken cancellationToken)
    {
        // 后续代码不在这个线程上继续执行，以免占住它。
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.BeginInvoke(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.SetCanceled(cancellationToken);
                return;
            }
            try { completion.SetResult(work()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private static Dispatcher Start(string name)
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = name };
        // 部分缩略图提供程序只能在单线程套间中使用。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    }
}
