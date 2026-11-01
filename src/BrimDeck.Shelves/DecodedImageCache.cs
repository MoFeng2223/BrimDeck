using System.Windows.Media.Imaging;

namespace DropShelf;

/// <summary>Regenerable presentation pixels only. Never owns history entries or attachment files.</summary>
public static class DecodedImageCache
{
    private sealed record Value(string Key, BitmapSource Image, long Bytes);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LinkedListNode<Value>> Index = new(StringComparer.Ordinal);
    private static readonly LinkedList<Value> Order = new();
    private static long _bytes;
    public static long Budget { get; set; } = 16 * 1024 * 1024;
    public static long Bytes { get { lock (Sync) return _bytes; } }
    static DecodedImageCache() => Services.ImagingThread.Idle += Clear;
    public static BitmapSource? Get(string key)
    {
        lock (Sync)
        {
            if (!Index.TryGetValue(key, out var node)) return null;
            Order.Remove(node); Order.AddFirst(node); return node.Value.Image;
        }
    }
    public static BitmapSource Add(string key, BitmapSource image)
    {
        if (!image.IsFrozen) image.Freeze();
        long bytes = ((long)image.PixelWidth * image.Format.BitsPerPixel + 7) / 8 * image.PixelHeight;
        lock (Sync)
        {
            if (Index.Remove(key, out var old)) { Order.Remove(old); _bytes -= old.Value.Bytes; }
            if (bytes <= Budget) { Index[key] = Order.AddFirst(new Value(key, image, bytes)); _bytes += bytes; }
            while ((_bytes > Budget || Index.Count > 96) && Order.Last is { } last)
            { Index.Remove(last.Value.Key); _bytes -= last.Value.Bytes; Order.RemoveLast(); }
        }
        return image;
    }
    public static void RemovePrefix(string prefix)
    {
        lock (Sync)
            foreach (var key in Index.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            { var node = Index[key]; Order.Remove(node); _bytes -= node.Value.Bytes; Index.Remove(key); }
    }
    public static void Clear() { lock (Sync) { Index.Clear(); Order.Clear(); _bytes = 0; } }
}
