using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DropShelf.Models;
using DropShelf.Services;

namespace DropShelf.Tests;

internal static class HostedProgram
{
    // Original test methods are invoked intact; explicit exclusions never access the real clipboard or launch external tools.
    private static readonly string[] SafeTests =
    [
        "TestSettingsRoundTrip", "TestSettingsMigration", "TestSettingsNormalization", "TestHotkeyGesture", "TestQuickActionNormalize",
        "TestQuickActionSlots", "TestShelfItemText", "TestShelfSizes", "TestQuickShelfPositioning", "TestProductVersion", "TestStringTables", "TestLanguageSwitch",
        "TestBalancedGestureTriggers", "TestSensitivityProfilesTrigger", "TestGestureDurationBounds", "TestGestureToleratesIrregularSamples",
        "TestOtherDirectionGesturesTrigger", "TestNormalMovementDoesNotTrigger", "TestScreenEdgeDoesNotTrigger", "TestHighRateShakeTriggers",
        "TestCoarseTimerShakeTriggers", "TestHighRateNormalMovement", "TestTurnaroundJitter", "TestEdgeDwellDetector",
        "TestOneHundredFilesAndDuplicates", "TestAddingLockedFileReadsNoContent", "TestClearKeepsFiles", "TestTextAndLinkEntries", "TestStackSummary",
        "TestApplyDragOut", "TestResolveDragOut", "TestApplyMoves", "TestShelfHistory", "TestDragOutEffects", "TestClassifyDropData", "TestReadDropData",
        "TestSourceShelfId", "TestShellDataObject", "TestVirtualAttachmentImport", "TestVolatileFileSnapshot", "TestChatAppImageFormats", "TestRichTextRejectsNetworkPaths",
        "TestCopyDoesNotOverwrite", "TestMissingSourceIsReported", "TestLockedFileIsReported", "TestCopyKeepsFileMetadata", "TestMoveToAction",
        "TestArchiveAddsOutputToShelf", "TestCustomFolderAction", "TestImageCompression", "TestCompressImageReplacesItems", "TestOpenWithArguments",
        "TestShellThumbnail", "TestImagingThread", "TestStartupCommand", "TestThemeAndAccent", "TestTrayIconLoader", "TestArgumentForwarding",
        "TestShelfWindowActions", "TestMainWindowSettings", "TestOnboardingSteps",
    ];

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "BrimDeck-Shelves-Upstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var type = typeof(Program);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        int passed = 0, failed = 0;
        try
        {
            type.GetField("_root", flags)!.SetValue(null, root);
            Environment.SetEnvironmentVariable("DROPSHELF_SETTINGS_PATH", Path.Combine(root, "settings.json"));
            Environment.SetEnvironmentVariable("DROPSHELF_LOG_DIRECTORY", Path.Combine(root, "logs"));
            Environment.SetEnvironmentVariable("DROPSHELF_INSTANCE", "BrimDeck-Upstream-Tests");
            VirtualFileDropService.ConfigureCacheRoot(Path.Combine(root, "attachments"));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Tokens.xaml") });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DropShelf;component/Themes/Controls.xaml") });
            ThemeService.Initialize(AppTheme.Light, null); LanguageService.SetMode(AppLanguage.Chinese);
            foreach (string name in SafeTests)
            {
                // 上游单个设置测试可能有意留下配置；不同用例必须拥有独立文件，不能改变后续用例的默认值。
                string fixture = Path.Combine(root, name);
                Directory.CreateDirectory(fixture);
                type.GetField("_root", flags)!.SetValue(null, fixture);
                Environment.SetEnvironmentVariable("DROPSHELF_SETTINGS_PATH", Path.Combine(fixture, "settings.json"));
                try
                {
                    var method = type.GetMethod(name, flags) ?? throw new MissingMethodException(name);
                    // The hosted application owns imaging between operations; the test otherwise has no App.Start lease.
                    using var images = DropShelf.HostedResources.AcquireImaging();
                    if (method.Invoke(null, null) is Task task) Complete(task);
                    passed++; Console.WriteLine("PASS upstream: " + name);
                }
                catch (Exception exception)
                {
                    failed++; Console.WriteLine("FAIL upstream: " + name + "\n" + (exception.InnerException ?? exception));
                }
                finally
                {
                    foreach (Window window in application.Windows.Cast<Window>().ToArray()) window.Close();
                    AppController.Current?.Dispose();
                }
            }
            ThemeService.Dispose();
            Console.WriteLine($"UPSTREAM_RESULT passed={passed} failed={failed}");
            Console.WriteLine("SKIP: real clipboard checks, global-input/hotkey probes, junction/7z external-command checks, and the upstream cache-race stress check are not executed by this isolated runner.");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            string stable = (string)type.GetField("_stableRoot", flags)!.GetValue(null)!;
            if (Directory.Exists(stable)) Directory.Delete(stable, true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Complete(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            long deadline = Environment.TickCount64 + 30000;
            timer.Tick += (_, _) => { if (task.IsCompleted || Environment.TickCount64 >= deadline) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Upstream test exceeded 30 seconds");
        }
        task.GetAwaiter().GetResult();
    }
}
