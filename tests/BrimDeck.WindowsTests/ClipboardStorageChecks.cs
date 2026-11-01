using System.IO;
using System.Text;
using System.Windows.Threading;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Native;
using DropShelf.Services;
using ClipboardService = BrimDeck.ClipboardService;

internal static partial class ClipboardChecks
{
    internal static void RunStorage(Action<string, bool> check)
    {
        ClipboardHistoryTests.Run(check);
        var task = CheckStorageAsync(check);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Isolated clipboard storage checks did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static async Task CheckStorageAsync(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "BrimDeck-Storage-" + Guid.NewGuid().ToString("N"));
        string original = Path.Combine(root, "original"), custom = Path.Combine(root, "custom"), cacheRoot = Path.Combine(root, "cache");
        string priorCache = VirtualFileDropService.CacheRoot;
        Directory.CreateDirectory(root); VirtualFileDropService.ConfigureCacheRoot(cacheRoot);
        var settings = new ClipboardSettings { MaximumItems = 0, RetentionDays = 0 };
        var now = DateTimeOffset.UtcNow;
        var originalStore = new ClipboardHistoryStore(original);
        try
        {
            using (var service = new ClipboardService(original, () => settings, Dispatcher.CurrentDispatcher))
            {
                var first = service.History.Add(ClipboardContent.FromText("Isolated persistent storage fixture"), "Fixture", "", now, settings)!;
                service.History.ToggleFavorite(first.Id); service.History.Move(first.Id, "prompts");
                var group = service.History.AddCollection("Storage fixture")!;
                service.History.SetCollectionAppearance(group.Id, "#0080FF", "folder");
                var usage = await service.MeasureStorageAsync();
                check("clipboard storage: usage measures the encrypted file and encoded content separately", usage.Count == 1 && usage.ContentBytes == first.Content.ByteCount && usage.HistoryBytes == new FileInfo(originalStore.FilePath).Length && usage.HistoryBytes > 0 && usage.PendingBytes == 0 && usage.CacheBytes == 0);
                check("clipboard storage: custom directory defaults preserve the existing host location", service.StorageDirectory == original && settings.StorageDirectory == "");
                check("clipboard storage: encrypted data contains no plaintext fixture", !Encoding.UTF8.GetString(File.ReadAllBytes(originalStore.FilePath)).Contains(first.Content.Text, StringComparison.Ordinal));
                bool oldKeptAtCommit = false;
                var result = await service.ChangeStorageDirectoryAsync(custom, preference =>
                {
                    oldKeptAtCommit = File.Exists(originalStore.FilePath) && new ClipboardHistoryStore(custom).Load().Items.Single().Id == first.Id;
                    settings.StorageDirectory = preference; return Task.CompletedTask;
                });
                check("clipboard storage: pointer commits only after the new encrypted history exists and before deleting the old", result.Succeeded && oldKeptAtCommit && !File.Exists(originalStore.FilePath) && service.StorageDirectory == custom && settings.StorageDirectory == custom);
                service.History.Add(ClipboardContent.FromText("Captured after path switch"), "Fixture", "", now.AddSeconds(1), settings);
                usage = await service.MeasureStorageAsync();
                var saved = new ClipboardHistoryStore(custom).Load();
                check("clipboard storage: future writes use the new directory and retain favorites, assignments and collections", saved.Items.Count == 2 && saved.Items.Single(item => item.Id == first.Id) is { Favorite: true, CollectionId: "prompts" } && saved.Collections.Any(item => item.Id == group.Id && item.BackgroundColor == "#0080FF" && item.Icon == "folder") && usage.Count == 2 && !File.Exists(originalStore.FilePath));
                var snapshotBytes = File.ReadAllBytes(new ClipboardHistoryStore(custom).FilePath);
                var conflict = new ClipboardHistoryStore(Path.Combine(root, "conflict"));
                conflict.Save(new ClipboardSnapshot { Items = [new ClipboardEntry { Content = ClipboardContent.FromText("Destination history must remain intact"), CopiedAt = now }] });
                byte[] conflictBytes = File.ReadAllBytes(conflict.FilePath); bool callback = false;
                result = await service.ChangeStorageDirectoryAsync(conflict.DirectoryPath, _ => { callback = true; return Task.CompletedTask; });
                check("clipboard storage: target conflicts cannot overwrite or merge histories", !result.Succeeded && !callback && service.StorageDirectory == custom && File.ReadAllBytes(conflict.FilePath).SequenceEqual(conflictBytes) && File.ReadAllBytes(new ClipboardHistoryStore(custom).FilePath).SequenceEqual(snapshotBytes));
                string failed = Path.Combine(root, "commit-failed");
                result = await service.ChangeStorageDirectoryAsync(failed, _ => throw new IOException("Isolated settings commit failure"));
                check("clipboard storage: a failed settings commit keeps the old file and removes the provisional copy", !result.Succeeded && service.StorageDirectory == custom && settings.StorageDirectory == custom && File.ReadAllBytes(new ClipboardHistoryStore(custom).FilePath).SequenceEqual(snapshotBytes) && !File.Exists(new ClipboardHistoryStore(failed).FilePath));
                string pendingTarget = Path.Combine(root, "pending-target"); Directory.CreateDirectory(pendingTarget);
                string pendingFile = new ClipboardHistoryStore(pendingTarget).FilePath + ".tmp"; File.WriteAllText(pendingFile, "Existing pending fixture");
                result = await service.ChangeStorageDirectoryAsync(pendingTarget, _ => throw new InvalidOperationException("Unexpected commit"));
                check("clipboard storage: a target pending file is not overwritten or removed", !result.Succeeded && File.ReadAllText(pendingFile) == "Existing pending fixture" && service.StorageDirectory == custom);
                bool exclusive = false;
                try { new ClipboardHistoryStore(pendingTarget).Save(service.History.Snapshot(), overwrite: false); } catch (IOException) { exclusive = true; }
                check("clipboard storage: exclusive save also preserves a concurrently created pending file", exclusive && File.ReadAllText(pendingFile) == "Existing pending fixture");
                string blocker = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocker, "Fixture");
                result = await service.ChangeStorageDirectoryAsync(blocker, _ => throw new InvalidOperationException("Unexpected commit"));
                check("clipboard storage: a file destination fails without touching current history", !result.Succeeded && service.StorageDirectory == custom && File.ReadAllText(blocker) == "Fixture");
                result = await service.ChangeStorageDirectoryAsync("relative-storage", _ => throw new InvalidOperationException("Unexpected commit"));
                check("clipboard storage: relative paths are rejected rather than resolving against build output", !result.Succeeded && service.StorageDirectory == custom);
                var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
                string during = Path.Combine(root, "during-copy");
                var migration = service.ChangeStorageDirectoryAsync(during, async preference => { entered.SetResult(); await release.Task; settings.StorageDirectory = preference; });
                await entered.Task;
                var concurrent = service.History.Add(ClipboardContent.FromText("Capture during migration"), "Fixture", "", now.AddSeconds(2), settings);
                check("clipboard storage: migration leaves the shared history live while writes are suspended", service.ChangingStorage && concurrent is not null);
                release.SetResult(); result = await migration; await service.MeasureStorageAsync();
                check("clipboard storage: capture during migration is flushed to the committed destination", result.Succeeded && new ClipboardHistoryStore(during).Load().Items.Count == 3 && !File.Exists(new ClipboardHistoryStore(custom).FilePath));
                string cache = VirtualFileDropService.CreateCacheDirectory();
                try
                {
                    File.WriteAllBytes(Path.Combine(cache, "Clipboard.png"), new byte[1024]);
                    string source = Path.Combine(root, "original-file.txt"); File.WriteAllBytes(source, new byte[2048]);
                    service.History.Add(new ClipboardContent { Kind = ClipboardContentKind.Files, Files = [source] }, "Fixture", "", now.AddSeconds(3), settings);
                    usage = await service.MeasureStorageAsync();
                    check("clipboard storage: shared cache uses its owner and excludes original file sizes", usage.CacheBytes == 1024 && VirtualFileDropService.CacheDirectoryPath == cacheRoot && usage.ContentBytes == service.History.Items.Sum(item => item.Content.ByteCount));
                }
                finally { await VirtualFileDropService.ReleaseCacheAsync(cache); }
                usage = await service.MeasureStorageAsync();
                check("clipboard storage: releasing shared attachment cache updates measured usage", usage.CacheBytes == 0);
                result = await service.ChangeStorageDirectoryAsync("", preference => { settings.StorageDirectory = preference; return Task.CompletedTask; });
                await service.MeasureStorageAsync();
                check("clipboard storage: restoring default migrates all records back and stores an empty preference", result.Succeeded && settings.StorageDirectory == "" && service.StorageDirectory == original && originalStore.Load().Items.Count == 4);
            }
            using (var service = new ClipboardService(original, () => settings, Dispatcher.CurrentDispatcher))
            {
                check("clipboard storage: a restart restores the selected location without enabling capture", service.History.Items.Count == 4 && !service.Listening && !settings.Enabled);
                settings.PersistHistory = false; service.ApplySettings(); await service.MeasureStorageAsync();
                check("clipboard storage: persistence opt-out deletes disk history while retaining the live session", !File.Exists(originalStore.FilePath) && service.History.Items.Count == 4);
                var result = await service.ChangeStorageDirectoryAsync(custom, preference => { settings.StorageDirectory = preference; return Task.CompletedTask; });
                await service.MeasureStorageAsync();
                check("clipboard storage: memory-only path changes neither create nor import history", result.Succeeded && service.History.Items.Count == 4 && !File.Exists(new ClipboardHistoryStore(custom).FilePath));
                settings.PersistHistory = true; service.ApplySettings(); await service.MeasureStorageAsync();
                check("clipboard storage: enabling persistence writes the current session at the chosen path", new ClipboardHistoryStore(custom).Load().Items.Count == 4);
            }
            using (var service = new ClipboardService(original, () => settings, Dispatcher.CurrentDispatcher))
            {
                check("clipboard storage: startup honors a custom path rather than the default directory", service.StorageDirectory == custom && service.History.Items.Count == 4);
                settings.PersistHistory = false; service.ApplySettings(); await service.MeasureStorageAsync();
            }
            // An invalid preference must not read, erase or overwrite another history at the fallback directory.
            originalStore.Save(new ClipboardSnapshot { Items = [new ClipboardEntry { Content = ClipboardContent.FromText("Untouched fallback fixture"), CopiedAt = now }] });
            byte[] fallback = File.ReadAllBytes(originalStore.FilePath);
            settings.StorageDirectory = "invalid-relative"; settings.PersistHistory = true;
            using (var service = new ClipboardService(original, () => settings, Dispatcher.CurrentDispatcher))
            {
                service.History.Add(ClipboardContent.FromText("Memory-only invalid path fixture"), "Fixture", "", now, settings);
                var usage = await service.MeasureStorageAsync();
                check("clipboard storage: invalid preference never falls back to an unrelated history", usage.Count == 1 && usage.HistoryBytes is null && service.Status.Length > 0 && File.ReadAllBytes(originalStore.FilePath).SequenceEqual(fallback));
            }
            var copy = settings.Copy(); copy.StorageDirectory = "  " + custom + "  "; copy.Normalize();
            check("clipboard storage: paths normalize and round-trip without sharing preferences", copy.StorageDirectory == custom && settings.StorageDirectory == "invalid-relative" && System.Text.Json.JsonSerializer.Deserialize<ClipboardSettings>(System.Text.Json.JsonSerializer.Serialize(copy))!.StorageDirectory == custom);
            Console.WriteLine("CLIPBOARD STORAGE RESULT: completed; isolated paths, DPAPI, migration, usage and count policy checked.");
        }
        finally
        {
            VirtualFileDropService.ConfigureCacheRoot(priorCache);
            Directory.Delete(root, recursive: true);
        }
    }
}
