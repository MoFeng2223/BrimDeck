using System.Windows;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public partial class App
{
    private ModuleStartupGuard? _moduleStartup;
    private DispatcherTimer? _moduleStartupHeartbeat;
    private bool _moduleRecoveryDecided, _moduleRecoveryPrompting;
    private int _moduleStartupOperations;
    internal bool ModulesSkippedForSession { get; private set; }
    internal string? ModuleStartupProblem { get; private set; }

    private void InitializeModuleStartup()
    {
        _moduleStartup = new(Store.DirectoryPath);
        ModulesSkippedForSession = Environment.GetCommandLineArgs().Contains("--no-modules", StringComparer.Ordinal);
        if (ModulesSkippedForSession) { _moduleRecoveryDecided = true; _moduleStartup.AcknowledgeRecovery(); }
        Deck.ContentRendered += (_, _) => _moduleStartup.FirstFramePresented();
        _moduleStartupHeartbeat = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _moduleStartupHeartbeat.Tick += (_, _) =>
        {
            _moduleStartup.Heartbeat();
            bool pending = _moduleStartupOperations > 0 || Modules.Modules.Any(m => m.Status is ModuleStatus.Starting or ModuleStatus.Waiting);
            _moduleStartup.TryFinish(pending);
            if (!_moduleStartup.Armed) _moduleStartupHeartbeat.Stop();
        };
    }
    private bool DecideModuleRecovery()
    {
        if (_moduleStartup is null || _moduleRecoveryPrompting) return false;
        if (!_moduleRecoveryDecided)
        {
            _moduleRecoveryPrompting = true;
            try
            {
                if (_moduleStartup.Recovery.Required && Settings.Modules.Enabled)
                {
                    // Name what was loading: a bare "modules" leaves the user nothing to act on.
                    var candidates = _moduleStartup.Recovery.Modules;
                    string subject = _moduleStartup.Recovery.Unreadable
                        ? Loc.T("\n\n上次的启动记录已损坏。", "\n\nThe previous startup record was unreadable.")
                        : candidates.Count == 0 ? ""
                        : Loc.T("\n\n可能相关的模组：", "\n\nPossibly related modules: ") + string.Join(Loc.T("、", ", "), candidates.Take(5))
                          + (candidates.Count > 5 ? Loc.T($" 等 {candidates.Count} 个", $" and {candidates.Count - 5} more") : "");
                    var answer = MessageBox.Show(Loc.T("上次模组启动未完成。是否本次不加载模组？\n\n选择“是”可先打开本体并管理模组；选择“否”会继续加载。此选择只影响本次运行，已保存的开关与批准记录保持不变。",
                        "The previous module startup did not finish. Start without modules this time?\n\nChoose Yes to open BrimDeck and manage modules, or No to continue loading. This only affects this run; saved switches and approvals are retained.")
                        + subject,
                        Loc.T("模组启动恢复", "Module startup recovery"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
                    ModulesSkippedForSession = answer == MessageBoxResult.Yes;
                }
                if (_moduleStartup.Recovery.Required && !_moduleStartup.AcknowledgeRecovery())
                    ModuleStartupProblem = Loc.T("无法记录本次启动恢复状态，下次启动可能再次询问。请检查应用数据目录是否可写。",
                        "Could not record this startup decision; you may be asked again. Check that the app data folder is writable.");
                _moduleRecoveryDecided = true;
            }
            finally { _moduleRecoveryPrompting = false; }
        }
        return true;
    }
    private bool AllowModuleStartup(IReadOnlyList<ModuleDefinition> definitions)
    {
        if (_moduleStartup is null || !DecideModuleRecovery()) return false;
        if (ModulesSkippedForSession) return false;
        var enabled = definitions.Where(d => d.Enabled).Select(d => d.Manifest.Id).ToArray();
        if (enabled.Length == 0) return false;
        if (_moduleStartup.Begin(enabled)) { _moduleStartupHeartbeat?.Start(); return true; }
        ModulesSkippedForSession = true;
        ModuleStartupProblem = Loc.T("无法记录模组启动状态，本次未加载模组。请检查应用数据目录是否可写。", "Could not record module startup. Modules were skipped for this run. Check that the app data folder is writable.");
        return false;
    }
    private void CompleteModuleStartupOnExit()
    {
        _moduleStartupHeartbeat?.Stop();
        // Reaching normal shutdown on the dispatcher is itself a response. A killed or hung process never reaches here.
        if (Dispatcher.CheckAccess()) _moduleStartup?.Heartbeat();
        _moduleStartup?.NormalExit();
    }
}
