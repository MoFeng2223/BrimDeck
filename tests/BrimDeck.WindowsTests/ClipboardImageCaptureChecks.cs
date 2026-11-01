using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;

internal static partial class ClipboardChecks
{
    internal static void RunImages(Action<string, bool> check)
    {
        PrepareClipboardResources();
        string directory = Path.Combine(Path.GetTempPath(), "BrimDeck-ImageCapture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string imageFile = Path.Combine(directory, "synthetic-chat-image.png"), textFile = Path.Combine(directory, "synthetic-attachment.txt");
        byte[] png = Image(); File.WriteAllBytes(imageFile, png); File.WriteAllText(textFile, "Synthetic attachment, not a chat record.");
        var settings = new ClipboardSettings { RetentionDays = 0 };
        DataObject Files(params string[] paths) { var data = new DataObject(); var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files); return data; }
        BitmapSource Bitmap() { using var stream = new MemoryStream(png, false); return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
        try
        {
            var copied = Files(imageFile);
            var content = ClipboardListener.ReadContent(copied, settings, "QQ");
            check("chat image capture: QQ single PNG file offer is an image, not an attachment card", content is { Kind: ClipboardContentKind.Image, PixelWidth: 400, PixelHeight: 300, Image.Length: > 0 });
            check("chat image capture: file-backed image retains the exact original path", content!.Files.SequenceEqual(new[] { imageFile }) && File.ReadAllBytes(imageFile).SequenceEqual(png));
            foreach (string source in new[] { "QQ", "qq", "QQNT", "TIM" })
                check("chat image capture: recognized copy source / " + source, ClipboardListener.ReadContent(copied, settings, source)?.Kind == ClipboardContentKind.Image);
            foreach (string source in new[] { "explorer", "Editor", "NotQQ", "" })
                check("chat image capture: ordinary file copy stays a path reference / " + source, ClipboardListener.ReadContent(copied, settings, source) is { Kind: ClipboardContentKind.Files } ordinary && ordinary.Files.SequenceEqual(new[] { imageFile }) && ordinary.Image.Length == 0);
            var withBitmap = Files(imageFile); withBitmap.SetImage(Bitmap());
            check("chat image capture: QQ file plus bitmap still produces one full image", ClipboardListener.ReadContent(withBitmap, settings, "QQ") is { Kind: ClipboardContentKind.Image, PixelWidth: 400, PixelHeight: 300 });
            check("chat image capture: Explorer preview bitmap cannot replace an explicit file copy", ClipboardListener.ReadContent(withBitmap, settings, "explorer") is { Kind: ClipboardContentKind.Files } file && file.Files.Single() == imageFile);
            var mixed = Files(imageFile, textFile); mixed.SetImage(Bitmap());
            check("chat image capture: mixed multi-file offers never collapse to the first thumbnail", ClipboardListener.ReadContent(mixed, settings, "QQ") is { Kind: ClipboardContentKind.Files } group && group.Files.SequenceEqual(new[] { imageFile, textFile }));
            check("chat image capture: QQ ordinary attachment is still a file", ClipboardListener.ReadContent(Files(textFile), settings, "QQ") is { Kind: ClipboardContentKind.Files });
            var noImages = settings.Copy(); noImages.CaptureImages = false;
            check("chat image privacy: single QQ image respects image opt-out without falling through to files", ClipboardListener.ReadContent(copied, noImages, "QQ") is null && ClipboardListener.ReadContent(withBitmap, noImages, "QQ") is null);
            var noFiles = settings.Copy(); noFiles.CaptureFiles = false;
            check("chat image privacy: image capture is independent of ordinary attachment capture", ClipboardListener.ReadContent(copied, noFiles, "QQ") is { Kind: ClipboardContentKind.Image } && ClipboardListener.ReadContent(Files(textFile), noFiles, "QQ") is null);
            var encoder = new BmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8193, 1, 96, 96, PixelFormats.Bgr24, null, new byte[8193 * 3 + 3], 8193 * 3 + 3)));
            string hugeDimensions = Path.Combine(directory, "too-wide.bmp"); using (var stream = File.Create(hugeDimensions)) encoder.Save(stream);
            check("chat image safety: compressed metadata cannot bypass pixel dimension limits", ClipboardListener.ReadContent(Files(hugeDimensions), settings, "QQ") is { Kind: ClipboardContentKind.Files, Image.Length: 0 });
            var encoded = new DataObject(); encoded.SetData("PNG", new MemoryStream(png, false));
            check("chat image capture: encoded PNG-only data is decoded without a bitmap format", ClipboardListener.ReadContent(encoded, settings, "QQ") is { Kind: ClipboardContentKind.Image, PixelWidth: 400, PixelHeight: 300 });
            var mime = new DataObject(); mime.SetData("image/png", png);
            check("chat image capture: image/png byte data follows the same bounded image path", ClipboardListener.ReadContent(mime, settings, "QQ")?.Kind == ClipboardContentKind.Image);
            check("chat image privacy: encoded PNG honors image opt-out", ClipboardListener.ReadContent(encoded, noImages, "QQ") is null);
            var broken = new DataObject(); broken.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3, 4 })); broken.SetImage(Bitmap());
            check("chat image capture: a broken optional encoded format can still use the actual bitmap", ClipboardListener.ReadContent(broken, settings, "QQ")?.Kind == ClipboardContentKind.Image);
            string bad = Path.Combine(directory, "not-an-image.png"); File.WriteAllText(bad, "Synthetic invalid image.");
            check("chat image safety: extension alone cannot fabricate image pixels", ClipboardListener.ReadContent(Files(bad), settings, "QQ") is { Kind: ClipboardContentKind.Files, Image.Length: 0 });
            string large = Path.Combine(directory, "oversized.png"); using (var stream = File.Create(large)) stream.SetLength(ClipboardHistory.MaxItemBytes + 1L);
            check("chat image safety: oversized file image remains a path-only file", ClipboardListener.ReadContent(Files(large), settings, "QQ") is { Kind: ClipboardContentKind.Files, Image.Length: 0 });
            check("chat image safety: missing local image remains an inert original path", ClipboardListener.ReadContent(Files(Path.Combine(directory, "missing.png")), settings, "QQ") is { Kind: ClipboardContentKind.Files, Image.Length: 0 });
            foreach (string path in new[] { @"\\never.example\share\image.png", @"\\?\C:\image.png", "https://never.example/image.png" })
                check("chat image safety: indirect or remote location is not decoded / " + path, ClipboardListener.ReadContent(Files(path), settings, "QQ") is { Kind: ClipboardContentKind.Files, Image.Length: 0 });
            var recalled = ClipboardListener.ToDataObject(content!);
            check("chat image recall: paste offers actual image and PNG rather than a file list", recalled.GetDataPresent(DataFormats.Bitmap) && recalled.GetDataPresent("PNG", false) && !recalled.GetDataPresent(DataFormats.FileDrop, false));
            check("chat image recall: history/cloud exclusion markers stay present", recalled.GetDataPresent("CanIncludeInClipboardHistory") && recalled.GetDataPresent("CanUploadToCloudClipboard"));
            var preview = ClipboardPreview.Create(content!);
            check("chat image card: capture feeds an edge-to-edge image body, not file tiles", preview is System.Windows.Controls.Image { Source: BitmapSource, Stretch: Stretch.UniformToFill });
            var detailed = ClipboardPreview.Create(content!, detailed: true);
            check("chat image preview: expanded content preserves aspect ratio and real pixels", detailed is System.Windows.Controls.Image { Source: BitmapSource, Stretch: Stretch.Uniform });
            var history = new ClipboardHistory(); var entry = history.Add(content!, "QQ", "", DateTimeOffset.UtcNow, settings)!;
            var card = new ClipboardCard { DataContext = entry, DockMode = true, Width = 210, Height = 146 };
            card.Measure(new Size(210, 146)); card.Arrange(new Rect(0, 0, 210, 146)); card.UpdateLayout();
            check("chat image card: unattached cards do not decode", Descendants<System.Windows.Controls.Image>(card).All(image => image.Source is null));
            var host = new Window { Content = card, Width = 240, Height = 190, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                host.Show();
                long deadline = Environment.TickCount64 + 5000;
                while (!Descendants<System.Windows.Controls.Image>(card).Any(image => image.Source is BitmapSource) && Environment.TickCount64 < deadline)
                { System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Thread.Sleep(1); }
                check("chat image card: real dock card labels the type as image and renders a full preview", Descendants<System.Windows.Controls.Image>(card).Any(image => image.Source is BitmapSource) && Descendants<TextBlock>(card).Any(text => text.Text == ClipboardContentTypes.Label(ClipboardContentKind.Image)) && !Descendants<TextBlock>(card).Any(text => text.Text == Path.GetFileName(imageFile)));
            }
            finally { host.Close(); }
            File.Delete(imageFile);
            check("chat image preview: stored pixels survive source disappearance without another file read", ClipboardPreview.Create(entry.Content, true) is System.Windows.Controls.Image && entry.Content.Files.Single() == imageFile);
            check("chat image capture: no implicit recording enablement or real clipboard access", !settings.Enabled && history.Items.Count == 1 && File.ReadAllText(textFile) == "Synthetic attachment, not a chat record.");
            Console.WriteLine("IMAGE CAPTURE RESULT: isolated QQ file offers, image pixels, cards and recall checks completed");
        }
        finally { Directory.Delete(directory, true); }
    }
}
