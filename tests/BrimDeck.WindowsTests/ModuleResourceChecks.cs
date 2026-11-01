using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf;
using DropShelf.Services;
using DropShelf.ViewModels;

internal static class ModuleResourceChecks
{
    // No live startup, clipboard payload, user files, external receivers or forced collection.
    internal static void Run(Action<string, bool> check)
    {
        var app = Application.Current ?? new BrimDeck.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-ModuleResources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckImaging(check);
            using (HostedResources.AcquireImaging())
            {
                CheckCache(check);
                CheckPreviews(check);
                CheckViewports(root, check);
            }
            CheckEvents(check);
            CheckWindowHost(check);
            // A canceled thumbnail still owns its short STA lease until its started operation returns.
            Wait(() => ImagingThread.LeaseCount == 0);
            check("resource teardown: no imaging lease or presentation cache remains", ImagingThread.LeaseCount == 0 && DecodedImageCache.Bytes == 0);
            Console.WriteLine("MODULE RESOURCES RESULT: isolated STA leases, cache budget, visible previews, independent viewports and event coalescing completed.");
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            Directory.Delete(root, true);
        }
    }

    private static void CheckImaging(Action<string, bool> check)
    {
        using var first = HostedResources.AcquireImaging();
        using var second = HostedResources.AcquireImaging();
        var original = Complete(ImagingThread.RunAsync(() => (Dispatcher: Dispatcher.CurrentDispatcher, Apartment: Thread.CurrentThread.GetApartmentState())));
        first.Dispose();
        var shared = Complete(ImagingThread.RunAsync(() => Dispatcher.CurrentDispatcher));
        check("imaging leases: releasing one module preserves the other module's STA", original.Apartment == ApartmentState.STA && ReferenceEquals(shared, original.Dispatcher) && ImagingThread.LeaseCount == 1);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ImagingThread.RunAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned imaging gate timed out.");
            return Dispatcher.CurrentDispatcher;
        });
        Complete(entered.Task); second.Dispose();
        try
        {
            check("imaging leases: a started operation owns the worker after both modules stop", ImagingThread.LeaseCount == 1 && !original.Dispatcher.HasShutdownStarted && !pending.IsCompleted);
        }
        finally { release.Set(); }
        check("imaging leases: in-flight work completes on the same STA", ReferenceEquals(Complete(pending), original.Dispatcher));
        Wait(() => original.Dispatcher.HasShutdownFinished);
        check("imaging leases: the last completed lease shuts down the worker", ImagingThread.LeaseCount == 0 && original.Dispatcher.HasShutdownFinished);
        using var restarted = HostedResources.AcquireImaging();
        var next = Complete(ImagingThread.RunAsync(() => Dispatcher.CurrentDispatcher));
        check("imaging leases: a later module obtains a new usable STA", !ReferenceEquals(next, original.Dispatcher) && !next.HasShutdownStarted);
    }

    private static void CheckCache(Action<string, bool> check)
    {
        long budget = DecodedImageCache.Budget;
        DecodedImageCache.Clear();
        try
        {
            DecodedImageCache.Budget = ModuleRuntimePolicy.ReducedCacheBytes;
            BitmapSource? held = null;
            for (int i = 0; i < 24; i++)
            {
                var image = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, new byte[512 * 512 * 4], 512 * 4);
                DecodedImageCache.Add("resource-fixture:" + i, image); held ??= image;
                check("decoded cache: byte budget remains bounded " + i, DecodedImageCache.Bytes <= ModuleRuntimePolicy.ReducedCacheBytes);
            }
            check("decoded cache: LRU evicts old data while retaining the latest pixels", DecodedImageCache.Get("resource-fixture:0") is null && DecodedImageCache.Get("resource-fixture:23") is { IsFrozen: true });
            check("decoded cache: eviction never invalidates pixels held by another view", held is { IsFrozen: true, PixelWidth: 512, PixelHeight: 512 });
        }
        finally { DecodedImageCache.RemovePrefix("resource-fixture:"); DecodedImageCache.Budget = budget; }
    }

    private static void CheckPreviews(Action<string, bool> check)
    {
        byte[] png = Png(800, 1600);
        var content = new ClipboardContent { Kind = ClipboardContentKind.Image, Image = png, PixelWidth = 800, PixelHeight = 1600 };
        byte[] fingerprint = SHA256.HashData(content.Image);
        var card = (Image)ClipboardPreview.Create(content, mode: ModuleMode.Performance);
        var detail = (Image)ClipboardPreview.Create(content, detailed: true, mode: ModuleMode.Performance);
        check("preview sampling: portrait card and explicit detail constrain their longest edge", card.Source is BitmapSource { PixelWidth: <= 256, PixelHeight: <= 256 } && detail.Source is BitmapSource { PixelWidth: <= 768, PixelHeight: <= 768 });
        var tiny = new ClipboardContent { Kind = ClipboardContentKind.Image, Image = Png(20, 10), PixelWidth = 20, PixelHeight = 10 };
        check("preview sampling: a small original is never upscaled", ((Image)ClipboardPreview.Create(tiny, true, ModuleMode.Performance)).Source is BitmapSource { PixelWidth: 20, PixelHeight: 10 });
        int decodes = ClipboardPreview.DecodeCount;
        var first = (Image)ClipboardPreview.Create(content, mode: ModuleMode.Performance, deferred: true);
        first.Width = 200; first.Height = 130;
        check("preview visibility: an unattached image does not decode", first.Source is null && ClipboardPreview.DecodeCount == decodes);
        var root = new StackPanel(); root.Children.Add(first);
        var host = new Window { Content = root, Width = 240, Height = 340, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); Wait(() => first.Source is BitmapSource);
            var second = (Image)ClipboardPreview.Create(content, mode: ModuleMode.Performance, deferred: true);
            second.Width = 200; second.Height = 130; root.Children.Add(second);
            Wait(() => second.Source is BitmapSource);
            check("preview visibility: equal live requests reuse the same decoded pixels", ReferenceEquals(first.Source, second.Source));
            first.Visibility = Visibility.Collapsed; Pump();
            check("preview visibility: releasing one view preserves the other view", first.Source is null && second.Source is BitmapSource);
            host.Hide(); Pump();
            check("preview visibility: hiding the last view releases its decoded reference", second.Source is null);
            check("preview sampling: presentation leaves encoded original bytes unchanged", fingerprint.SequenceEqual(SHA256.HashData(content.Image)));
        }
        finally { host.Close(); }
    }

    private static void CheckViewports(string root, Action<string, bool> check)
    {
        string file = Path.Combine(root, "ordinary-source.txt"); File.WriteAllText(file, "Owned viewport fixture.");
        var model = new ShelfViewModel();
        try
        {
            model.AddFiles([file]); var item = model.Items.Single();
            int Requests() => ((System.Collections.IDictionary)typeof(ShelfViewModel).GetField("_thumbnails", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model)!).Count;
            check("shelf visibility: a hidden model does not request its top-three thumbnails", Requests() == 0 && item.Thumbnail is null);
            object first = new(), second = new();
            model.SetViewportItems(first, [item]); model.SetViewportItems(second, [item]);
            check("shelf viewport: two consumers share one request", Requests() == 1);
            model.SetViewportItems(first, []);
            check("shelf viewport: releasing one consumer keeps the other's request", Requests() == 1);
            model.SetViewportItems(second, []);
            check("shelf viewport: releasing the last consumer cancels and clears its pixels", Requests() == 0 && item.Thumbnail is null);
        }
        finally { model.ReleaseResources(); }
        check("shelf viewport teardown: the ordinary TEMP file remains unchanged", File.ReadAllText(file) == "Owned viewport fixture.");
    }

    private static void CheckEvents(Action<string, bool> check)
    {
        int initial = WindowContextEvents.SubscriberCount, firstCalls = 0, secondCalls = 0;
        var schedule = typeof(WindowContextEvents).GetMethod("Schedule", BindingFlags.Static | BindingFlags.NonPublic)!;
        var first = WindowContextEvents.Subscribe(Dispatcher.CurrentDispatcher, () => firstCalls++);
        using var second = WindowContextEvents.Subscribe(Dispatcher.CurrentDispatcher, () => secondCalls++);
        try
        {
            int hooks = ((System.Collections.ICollection)typeof(WindowContextEvents).GetField("Hooks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Count;
            check("context events: multiple islands share a single three-range hook set", WindowContextEvents.SubscriberCount == initial + 2 && hooks == 3);
            Task.Run(() => { for (int i = 0; i < 200; i++) schedule.Invoke(null, null); }).GetAwaiter().GetResult();
            check("context events: off-thread bursts queue once without touching consumers", firstCalls == 0 && secondCalls == 0 && (int)typeof(WindowContextEvents).GetField("_queued", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == 1);
            Pump(); check("context events: queued refresh reaches both UI consumers", firstCalls > 0 && firstCalls == secondCalls);
            first.Dispose(); int beforeFirst = firstCalls, beforeSecond = secondCalls;
            schedule.Invoke(null, null); Pump();
            check("context events: a released island no longer receives queued refreshes", firstCalls == beforeFirst && secondCalls > beforeSecond && WindowContextEvents.SubscriberCount == initial + 1);
        }
        finally { first.Dispose(); second.Dispose(); Pump(); }
        check("context events: final release removes all module subscriptions", WindowContextEvents.SubscriberCount == initial);
    }

    private static void CheckWindowHost(Action<string, bool> check)
    {
        int initial = WindowContextEvents.SubscriberCount, windowReads = 0;
        using var host = new WindowsHost(() => IntPtr.Zero, () => { windowReads++; return IntPtr.Zero; }, () => null);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var timer = (DispatcherTimer)typeof(WindowsHost).GetField("_timer", fields)!.GetValue(host)!;
        var hooks = (IntPtr[])typeof(WindowsHost).GetField("_hooks", fields)!.GetValue(host)!;
        var registered = hooks.ToArray();
        var orderEvent = typeof(WindowsHost).GetMethod("OnOrderEvent", fields)!;
        var schedule = typeof(WindowContextEvents).GetMethod("Schedule", BindingFlags.Static | BindingFlags.NonPublic)!;
        check("window host: full mode retains the upstream order hooks and fallback timer", registered.Length == 3 && registered.All(hook => hook != IntPtr.Zero) && timer.IsEnabled);
        host.EventDriven = true;
        check("window host: event-driven mode stops polling without losing its subscription", !timer.IsEnabled && host.EventDriven && WindowContextEvents.SubscriberCount == initial + 1);
        orderEvent.Invoke(host, [IntPtr.Zero, 0x0003u, IntPtr.Zero, 0, 0, 0u, 0u]);
        schedule.Invoke(null, null);
        check("window host: an order check is pending before disposal", (bool)typeof(WindowsHost).GetField("_orderCheckPending", fields)!.GetValue(host)!);
        int beforeDispose = windowReads;
        host.Dispose();
        check("window host: disposal stops polling and releases the shared context subscription", !timer.IsEnabled && !host.EventDriven && WindowContextEvents.SubscriberCount == initial);
        check("window host: disposal releases every upstream native order hook", registered.All(hook => !UnhookWinEvent(hook)) && hooks.All(hook => hook == IntPtr.Zero));
        Pump();
        check("window host: queued order and context callbacks do not read the disposed window", windowReads == beforeDispose);
        host.Dispose();
        host.EventDriven = true;
        orderEvent.Invoke(host, [IntPtr.Zero, 0x0003u, IntPtr.Zero, 0, 0, 0u, 0u]);
        Pump();
        check("window host: repeated disposal and late events cannot restart or enqueue the host", windowReads == beforeDispose && !timer.IsEnabled && !host.EventDriven
            && WindowContextEvents.SubscriberCount == initial && !(bool)typeof(WindowsHost).GetField("_orderCheckPending", fields)!.GetValue(host)!);
    }

    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);

    private static byte[] Png(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static T Complete<T>(Task<T> task) { Complete((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(1); }
        if (!done()) throw new TimeoutException("Module resource fixture timed out.");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
