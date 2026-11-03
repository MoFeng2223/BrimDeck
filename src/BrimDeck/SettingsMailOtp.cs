using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// QQ mailbox verification codes on the collapsed island: switch, account, authorization code (DPAPI), auto-copy, display seconds.
public partial class SettingsWindow
{
    private void MailOtpSettings()
    {
        SectionTitle(Loc.T("邮箱验证码", "Mail verification codes"), help: () => Loc.T(
            "从 QQ 邮箱只读拉取最近邮件，识别验证码后在灵动岛短暂展示并可一键复制。不会同步全文、不会写信。授权码仅保存在本机。",
            "Read-only QQ Mail: recent messages are scanned for a code, shown briefly on the island, and copied with a click. No full sync and no compose. The authorization code stays on this PC only."));

        Group(Toggle(Loc.T("在灵动岛显示验证码", "Show codes on the island"), S.MailOtpEnabled, v =>
        {
            Change(s => s.MailOtpEnabled = v);
            // Rebuild so fields enable/disable with the switch.
            ShowPage(0);
        }, help: Loc.T("关闭后立即停止轮询，岛上也不再弹出验证码。", "Off: polling stops and the island no longer shows codes.")));

        bool on = S.MailOtpEnabled;
        var address = new TextBox
        {
            Width = 260, Text = S.MailOtpAddress, IsEnabled = on,
            Style = (Style)FindResource("SettingsNumber"), FontSize = 12.5, MaxLength = 128
        };
        AutomationProperties.SetName(address, Loc.T("QQ 邮箱地址", "QQ Mail address"));
        address.LostKeyboardFocus += (_, _) => Change(s => s.MailOtpAddress = address.Text.Trim());

        var secret = new PasswordBox
        {
            Width = 200, IsEnabled = on, FontSize = 12.5, MaxLength = 128,
            Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = UI.Brush(TextPrimary)
        };
        AutomationProperties.SetName(secret, Loc.T("授权码", "Authorization code"));
        if (_app.Secrets.Read(MailOtp.SecretId).Length > 0) secret.Password = "********";
        secret.LostKeyboardFocus += (_, _) =>
        {
            var value = secret.Password;
            if (value is "********" or "") return;
            _app.Secrets.Write(MailOtp.SecretId, value.Trim());
            secret.Password = "********";
            SaveStatus(Loc.T("授权码已保存到本机。", "Authorization code saved on this PC."));
        };
        var clear = ActionButton(Loc.T("清除", "Clear"), () =>
        {
            _app.Secrets.Delete(MailOtp.SecretId);
            secret.Password = "";
            SaveStatus(Loc.T("已清除授权码。", "Authorization code cleared."));
        });
        clear.IsEnabled = on;
        var secretRow = new StackPanel { Orientation = Orientation.Horizontal };
        secretRow.Children.Add(secret);
        secretRow.Children.Add(new Border { Width = 8 });
        secretRow.Children.Add(clear);

        var seconds = new ComboBox
        {
            Width = 110, IsEnabled = on, Style = (Style)FindResource("SettingsCombo"),
            ItemsSource = MailOtp.SecondChoices.Select(s => Loc.T($"{s} 秒", $"{s} s")).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(MailOtp.SecondChoices, MailOtp.FitSeconds(S.MailOtpSeconds)))
        };
        AutomationProperties.SetName(seconds, Loc.T("展示时长", "Display duration"));
        PopupMotion.Attach(seconds);
        seconds.SelectionChanged += (_, _) =>
        {
            if (seconds.SelectedIndex >= 0) Change(s => s.MailOtpSeconds = MailOtp.SecondChoices[seconds.SelectedIndex]);
        };

        Group(
            Row(Loc.T("QQ 邮箱", "QQ Mail"), address, help: () => Loc.T(
                "例如 name@qq.com。在 QQ 邮箱设置中开启 IMAP 并生成授权码，不要填写登录密码。",
                "e.g. name@qq.com. Enable IMAP in QQ Mail settings and use an authorization code — not the login password.")),
            Row(Loc.T("授权码", "Authorization code"), secretRow, help: () => Loc.T(
                "用 Windows 本机保护存储。可随时清除以断开。",
                "Stored with this Windows user account's protection. Clear anytime to disconnect.")),
            Toggle(Loc.T("自动复制验证码", "Copy codes automatically"), S.MailOtpAutoCopy, v => Change(s => s.MailOtpAutoCopy = v), enabled: on,
                help: Loc.T("默认关闭。开启后到码时直接写入剪贴板，仍会在岛上短暂显示。",
                    "Off by default. On: the code is copied as soon as it arrives; the island still shows it briefly.")),
            Row(Loc.T("展示时长", "Display duration"), seconds));

        Note(Loc.T("锁屏或全屏隐藏岛时不会弹出明文验证码。不会保存邮件正文，也不会上传到云端。",
            "Codes are not shown while the island is hidden for the lock screen or fullscreen. Message bodies are not saved or uploaded."), 11.5, TextSecondary, new Thickness(0, -8, 0, 20));
    }
}
