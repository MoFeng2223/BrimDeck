using System.Windows;
using BrimDeck.Core;
using System.Windows.Automation;
using System.Windows.Media.Animation;

namespace BrimDeck;

public partial class MainWindow
{
    private double _syncRotationSeconds;
    internal bool AutomaticSyncEnabled => Settings.QuotaWanted && Settings.UsageAutoSync;
    internal bool IsSynchronizing => _refreshing;

    private void ApplySyncSchedule()
    {
        if (IsLoaded && !_app.SmokeMode && AutomaticSyncEnabled) _refresh.Start();
        else _refresh.Stop();
    }

    internal Task RefreshAutomaticallyAsync(bool queueIfBusy = false)
        => AutomaticSyncEnabled ? RefreshAsync(queueIfBusy) : Task.CompletedTask;

    private void AIUsage_Click(object sender, RoutedEventArgs e) { SelectPage(BrimDeck.Core.DeckPage.Usage); ShowModelDetails(false); }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_details)
        {
            // A second click during the same read does not request another pass.
            await RefreshAsync(queueIfBusy: false);
            return;
        }
        var settings = Settings.Copy();
        settings.UsageAutoSync = !settings.UsageAutoSync;
        _app.UpdateSettings(settings);
    }

    private void RenderSyncButton()
    {
        bool paused = !_details && !Settings.UsageAutoSync;
        AIUsageButton.Visibility = Settings.UsagePage && !_details ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Visibility = Settings.UsagePage && !IsMusicPage ? Visibility.Visible : Visibility.Collapsed;
        SyncButton.Foreground = UI.Brush(paused ? "#F2777F" : UI.Secondary);
        SyncSlash.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        var tip = _details ? _refreshing ? Loc.T("正在同步用量与模型消耗…", "Syncing usage and model consumption…") : Loc.T("立即同步用量与模型消耗", "Sync usage and model consumption now")
            : Settings.UsageAutoSync ? Loc.T("每 60 秒自动同步一次，点击关闭同步", "Syncs automatically every 60 seconds. Click to turn syncing off.")
            : Loc.T("同步已关闭，点击开启", "Syncing is off. Click to turn it on.");
        SyncButton.ToolTip = tip;
        AutomationProperties.SetName(SyncButton, _details ? tip : Settings.UsageAutoSync ? Loc.T("关闭自动同步", "Turn off automatic sync") : Loc.T("开启自动同步", "Turn on automatic sync"));

        // Keep a running rotation across clock ticks and table renders; suspend it when the panel is hidden.
        double seconds = _expanded && !IsMusicPage && Settings.UsagePage && Settings.Animations && SystemParameters.ClientAreaAnimation
            && (_details ? _refreshing : Settings.UsageAutoSync) ? _details ? 1 : 4.8 : 0;
        if (_syncRotationSeconds == seconds) return;
        _syncRotationSeconds = seconds;
        SyncRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, seconds == 0 ? null : new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever
        });
    }
}
