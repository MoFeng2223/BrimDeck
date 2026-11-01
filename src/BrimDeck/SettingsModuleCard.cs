using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;
using BrimDeck.Modules;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private string ModuleName(ModuleInfo module) => module.Name;
    private string ModuleStateLabel(ModuleInfo module) => S.Modules.EnabledModules.Contains(module.Id)
        && _app.ExternalModules.FirstOrDefault(e => e.Manifest.Id == module.Id) is { } package && S.Modules.Approvals.GetValueOrDefault(module.Id) != package.Digest
        ? Loc.T("需重新批准", "Approval required") : _app.ModulesSkippedForSession && S.Modules.EnabledModules.Contains(module.Id)
        ? Loc.T("本次未启动", "Skipped this run") : module.Status switch
    {
        ModuleStatus.Ready => Loc.T("运行中", "Running"), ModuleStatus.Starting => module.Slow ? Loc.T("启动较慢", "Slow start") : Loc.T("启动中", "Starting"),
        ModuleStatus.Waiting => Loc.T("等待依赖", "Waiting"),
        ModuleStatus.Failed or ModuleStatus.DependencyFailed => Loc.T("启动失败", "Failed"),
        ModuleStatus.HostMismatch => Loc.T("版本不兼容", "Incompatible"),
        _ => !S.Modules.Enabled ? Loc.T("系统已关闭", "System off") : Loc.T("已关闭", "Off")
    };
    private Border ModuleEntryCard(ModuleInfo module, bool performance)
    {
        var external = _app.ExternalModules.FirstOrDefault(m => m.Manifest.Id == module.Id);
        bool approved = external is not null && S.Modules.Approvals.GetValueOrDefault(module.Id) == external.Digest;
        bool enabled = S.Modules.EnabledModules.Contains(module.Id) && approved;
        string title = ModuleName(module);
        var root = new StackPanel();
        var header = new Grid { MinHeight = 76, Margin = new Thickness(10, 2, 12, 2) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(84) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = new Grid(); label.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); label.ColumnDefinitions.Add(new());
        string stateColor = module.Status == ModuleStatus.Ready ? (_palette.Dark ? "#78CDA2" : "#168060") : module.Status is ModuleStatus.Failed or ModuleStatus.DependencyFailed ? (_palette.Dark ? "#F0A6AA" : "#B12E3A")
        : module.Status == ModuleStatus.HostMismatch ? (_palette.Dark ? "#E4B477" : "#A2631F") : TextTertiary;
        label.Children.Add(Stroke(module.Status == ModuleStatus.Ready ? "M 8,1 A 7,7 0 1 1 7.99,1 M 4.5,8 L 7,10.5 L 11.5,5.5" : module.Status is ModuleStatus.Starting or ModuleStatus.Waiting ? "M 8,1 A 7,7 0 1 1 7.99,1 M 8,4 L 8,8 L 11,10" : "M 8,1 A 7,7 0 1 1 7.99,1 M 4,4 L 12,12", 17, 17, stateColor));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(TextLine(title, 13.5, TextPrimary, FontWeights.SemiBold));
        var metadata = TextLine(module.Id + " · v" + module.Version + (external?.Manifest.Author is { Length: > 0 } author ? " · " + author : "") + " · " + (module.BuiltIn ? Loc.T("内置", "Built-in") : Loc.T("外部", "External")) + (module.Slow ? Loc.T(" · 慢启动", " · Slow startup") : ""), 10.5, TextTertiary); metadata.Margin = new Thickness(0, 5, 0, 0); names.Children.Add(metadata);
        Grid.SetColumn(names, 1); label.Children.Add(names);
        var nameButton = ActionButton("", () => { if (!performance) ShowPage(ModuleDetailPage(module.Id)); }); nameButton.Content = label; nameButton.HorizontalContentAlignment = HorizontalAlignment.Stretch; nameButton.Padding = new Thickness(7, 10, 8, 10); nameButton.Margin = new Thickness(0, 0, 6, 0); header.Children.Add(nameButton);
        var status = TextLine(ModuleStateLabel(module), 10.5, stateColor); status.TextAlignment = TextAlignment.Center; status.VerticalAlignment = VerticalAlignment.Center;
        var statusHolder = new Border { Child = status, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(6), Background = UI.Brush(Inset), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetColumn(statusHolder, 1); header.Children.Add(statusHolder);
        if (ModuleSignatureChip(module) is { } chip) header.Children.Add(chip);
        var toggle = ModuleToggle(Loc.T("启用：", "Enable: ") + title, enabled, value =>
        {
            if (value && !approved && !Confirm(Loc.T("信任并启用插件", "Trust and enable plugin"), Loc.T($"“{title}”将在 BrimDeck 进程内执行 DLL 代码。\n它能够以当前用户权限访问文件和进程；权限声明和内容摘要不是安全沙箱。\n\n仅在信任作者和当前文件来源时启用。批准仅绑定本次包内容。", $"{title} will execute DLL code inside BrimDeck.\nIt can access files and processes with your user permissions; declarations and content hashes are not a sandbox.\n\nEnable only if you trust the author and these files. Approval binds only this package content."), Loc.T("信任并启用", "Trust and enable")))
            { ShowPage(_page); return; }
            Change(s =>
            {
                if (external is not null)
                {
                    s.Modules.EnabledModules.Remove(module.Id);
                    if (value) { s.Modules.EnabledModules.Add(module.Id); s.Modules.Approvals[module.Id] = external.Digest; }
                }
            });
        });
        toggle.IsEnabled = !_app.ModuleFilesBusy && (!_app.ModulesSkippedForSession || enabled);
        if (_app.ModulesSkippedForSession) toggle.ToolTip = Loc.T("关闭后下次启动也不加载；重新启用需重启应用。", "Disable to also skip it next time. Restart the app to enable modules again.");
        AutomationProperties.SetAutomationId(toggle, "module-enable-" + module.Id); toggle.Margin = new Thickness(4, 0, 14, 0); Grid.SetColumn(toggle, 2); header.Children.Add(toggle);
        var detail = new Border { BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0), ClipToBounds = true };
        StackPanel? body = null;
        void EnsureBody()
        {
            if (body is not null) return;
            body = new StackPanel { Margin = new Thickness(18, 14, 18, 16) }; detail.Child = body;
            if (performance) ModulePerformanceDetails(module, body); else ModuleManagementDetails(module, external, body);
        }
        bool open = _expandedModules.Contains(module.Id);
        if (open) EnsureBody(); detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        int motionVersion = 0;
        Button? arrow = null;
        void SetOpen()
        {
            open = !open; if (open) _expandedModules.Add(module.Id); else _expandedModules.Remove(module.Id);
            EnsureBody(); if (!open && detail.IsKeyboardFocusWithin) nameButton.Focus();
            PointArrow(arrow!, open); AutomationProperties.SetItemStatus(nameButton, open ? Loc.T("已展开", "Expanded") : Loc.T("已收起", "Collapsed"));
            double start = detail.Visibility == Visibility.Visible ? detail.ActualHeight : 0;
            detail.BeginAnimation(HeightProperty, null); detail.Height = start; detail.Visibility = Visibility.Visible;
            double available = Math.Max(100, root.ActualWidth);
            body!.Measure(new Size(available, double.PositiveInfinity)); double target = open ? body.DesiredSize.Height + 1 : 0;
            int version = ++motionVersion;
            void Finish() { if (motionVersion != version) return; detail.BeginAnimation(HeightProperty, null); detail.Height = double.NaN; detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }
            if (!Soft.Enabled) Finish();
            else
            {
                var motion = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(Math.Min(240, S.AnimationDuration))) { EasingFunction = Soft.Out };
                motion.Completed += (_, _) => Finish(); detail.BeginAnimation(HeightProperty, motion);
                if (open) PageHandover.Appear(body);
            }
            UpdateModuleMonitoring();
        }
        arrow = ChevronButton(Loc.T("展开：", "Expand: ") + title, SetOpen); arrow.Width = 24; Grid.SetColumn(arrow, 3); header.Children.Add(arrow); PointArrow(arrow, open);
        AutomationProperties.SetAutomationId(arrow, "module-expand-" + module.Id); AutomationProperties.SetName(nameButton, (performance ? Loc.T("展开：", "Expand: ") : Loc.T("插件详情：", "Plugin details: ")) + title);
        AutomationProperties.SetItemStatus(nameButton, open ? Loc.T("已展开", "Expanded") : Loc.T("已收起", "Collapsed")); if (performance) nameButton.Click += (_, _) => SetOpen();
        root.Children.Add(header); root.Children.Add(detail);
        ModuleStatus iconState = module.Status;
        AddModuleMetricRefresh(values =>
        {
            values.TryGetValue(module.Id, out var value);
            var current = _app.Modules.Modules.FirstOrDefault(m => m.Id == module.Id) ?? module;
            stateColor = current.Status == ModuleStatus.Ready ? (_palette.Dark ? "#78CDA2" : "#168060") : current.Status is ModuleStatus.Failed or ModuleStatus.DependencyFailed ? (_palette.Dark ? "#F0A6AA" : "#B12E3A")
            : current.Status == ModuleStatus.HostMismatch ? (_palette.Dark ? "#E4B477" : "#A2631F") : TextTertiary;
            if (iconState != current.Status)
            {
                iconState = current.Status; label.Children.RemoveAt(0);
                label.Children.Insert(0, Stroke(current.Status == ModuleStatus.Ready ? "M 8,1 A 7,7 0 1 1 7.99,1 M 4.5,8 L 7,10.5 L 11.5,5.5" : current.Status is ModuleStatus.Starting or ModuleStatus.Waiting ? "M 8,1 A 7,7 0 1 1 7.99,1 M 8,4 L 8,8 L 11,10" : "M 8,1 A 7,7 0 1 1 7.99,1 M 4,4 L 12,12", 17, 17, stateColor));
            }
            SetMetricText(metadata, current.Id + " · v" + current.Version + " · " + (current.BuiltIn ? Loc.T("内置", "Built-in") : Loc.T("外部", "External")) + (current.Slow ? Loc.T(" · 慢启动", " · Slow startup") : ""));
            string text = performance ? MetricTime(value?.MillisecondsPerSecond ?? 0) + Loc.T("/秒", "/s") : value?.Errors > 0 ? Loc.T($"{value.Errors} 次错误", $"{value.Errors} errors") : ModuleStateLabel(current);
            SetMetricText(status, text);
            status.Foreground = UI.Brush(!performance && value?.Errors > 0 ? (_palette.Dark ? "#E4B477" : "#A2631F") : stateColor);
            statusHolder.ToolTip = performance ? Loc.T("最近五秒的平均回调用时", "Average callback time over the last five seconds") : value?.Errors > 0 ? Loc.T("展开查看错误详情", "Expand to see errors") : current.Slow ? Loc.T("启动超过五秒，完成后正常运行。", "Startup exceeded five seconds; it can still complete normally.") : null;
        });
        return ModuleCard(root, new Thickness(0));
    }
    private void ModuleManagementDetails(ModuleInfo module, DiscoveredModule? external, StackPanel body)
    {
        string description = external is null ? "" : Loc.T(external.Manifest.Description, string.IsNullOrWhiteSpace(external.Manifest.DescriptionEn) ? external.Manifest.Description : external.Manifest.DescriptionEn);
        if (!string.IsNullOrWhiteSpace(description)) { var text = TextLine(description, 12, TextSecondary); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(text); }
        if (!string.IsNullOrWhiteSpace(external?.Manifest.Author)) { var author = TextLine(Loc.T("作者：", "Author: ") + external.Manifest.Author, 11, TextSecondary); author.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(author); }
        if (external?.Manifest is { } manifest)
        {
            string PermissionLabel(string permission) => ModuleApi.Catalog.FirstOrDefault(c => c.Permission == permission) is { } descriptor ? Loc.T(descriptor.Name, descriptor.NameEn) : permission;
            var access = TextLine(Loc.T("接口权限：", "API permissions: ") + (manifest.Permissions.Length == 0 ? Loc.T("无", "None") : string.Join(" · ", manifest.Permissions.Select(PermissionLabel))), 11, TextSecondary);
            access.TextWrapping = TextWrapping.Wrap; access.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(access);
        }
        if (external is not null && S.Modules.Approvals.TryGetValue(module.Id, out string? digest) && digest != external.Digest)
            body.Children.Add(ModuleNotice(Loc.T("文件已更新，重新开启此模组后生效。", "Files changed. Enable the module again to load them.")));
        var links = new WrapPanel { Margin = new Thickness(-8, 0, 0, 10) };
        foreach (var page in _app.Modules.Registry.List<ModulePage>().Where(p => p.ModuleId == module.Id))
            links.Children.Add(ModuleLink(Loc.T("打开：", "Open: ") + Loc.T(page.Value.Title, page.Value.TitleEn), () => _app.Deck.OpenModulePageFromSettings(page.Id)));
        foreach (var settings in _app.Modules.Registry.List<IModuleSettings>().Where(s => s.ModuleId == module.Id))
            if (_app.Modules.Invoke(settings.ModuleId, "settings visibility", () => settings.Value.Visible))
                links.Children.Add(ModuleLink(Loc.T(settings.Value.Name, settings.Value.NameEn), () => ShowModuleSettings(settings.Id)));
        if (links.Children.Count > 0) body.Children.Add(links);
        var widgets = _app.Modules.Registry.List<ModuleWidget>().Where(w => w.ModuleId == module.Id).OrderBy(w => w.Value.Order).ToArray();
        if (widgets.Length > 0)
        {
            var heading = TextLine(Loc.T("组件", "Components"), 11, TextTertiary); heading.Margin = new Thickness(0, 0, 0, 4); body.Children.Add(heading);
            foreach (var widget in widgets)
            {
                bool enabled = _app.Modules.Invoke(widget.ModuleId, "widget state", widget.Value.Enabled);
                var controls = new StackPanel { Orientation = Orientation.Horizontal };
                if (enabled) controls.Children.Add(ModuleLink(Loc.T("设置", "Settings"), () => ShowModuleSettings(widget.Value.SettingsId)));
                controls.Children.Add(ModuleToggle(Loc.T(widget.Value.Name, widget.Value.NameEn), enabled, value =>
                {
                    _app.Modules.Invoke(widget.ModuleId, "toggle widget", () => { widget.Value.SetEnabled(value); return true; }); ShowPage(ModulesPage);
                }));
                body.Children.Add(Row(Loc.T(widget.Value.Name, widget.Value.NameEn), controls));
            }
        }
        AddModuleCompatibilityNote(module, body);
        body.Children.Add(ModuleStorageDetails(module, external));
        var performance = ModuleMetrics(module.Id, false); performance.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(performance);
        AddModuleErrors(module, body);
        var diagnostics = new TextBlock { FontSize = 11.5, Foreground = UI.Brush(TextSecondary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(diagnostics);
        AddModuleMetricRefresh(_ =>
        {
            var current = _app.Modules.Modules.FirstOrDefault(m => m.Id == module.Id);
            string text = string.Join("\n", (current?.Diagnostics ?? []).Take(8).Select(d => d.Source + ": " + d.Message));
            SetMetricText(diagnostics, text); diagnostics.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }
    private FrameworkElement? ModuleSignatureChip(ModuleInfo module)
    {
        var signature = module.Signature;
        // Unsigned is the normal state for third-party packages today; only say something when there is something to say.
        if (signature is null || signature.Status == ModuleSignatureStatus.Unsigned) return null;
        string text = signature.Status switch
        {
            ModuleSignatureStatus.Verified => Loc.T("官方认证", "Verified"),
            ModuleSignatureStatus.Beta => Loc.T("先行测试", "Beta"),
            _ => Loc.T("签名不匹配", "Invalid signature")
        };
        string color = signature.Status switch
        {
            ModuleSignatureStatus.Verified => _palette.Dark ? "#78CDA2" : "#168060",
            ModuleSignatureStatus.Beta => _palette.Dark ? "#C4A6F0" : "#7A3FB8",
            _ => _palette.Dark ? "#E4B477" : "#A2631F"
        };
        var label = TextLine(text, 10.5, color); label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
        var chip = new Border
        {
            Child = label, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(6),
            Background = UI.Brush(Inset), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        chip.ToolTip = signature.Reason is { } reason ? reason : Loc.T($"密钥 {signature.KeyId}", $"Key {signature.KeyId}")
            + (signature.SignedAt is { } at ? Loc.T($" · 签名于 {at}", $" · signed {at}") : "");
        AutomationProperties.SetName(chip, Loc.T($"签名状态：{text}", $"Signature: {text}"));
        return chip;
    }
    private void AddModuleCompatibilityNote(ModuleInfo module, StackPanel body)
    {
        if (!module.UsesInternals && module.HostRange is null) return;
        string text = module.UsesInternals
            ? Loc.T($"使用内部接口，要求 BrimDeck {module.HostRange}。宿主升级后可能失效。",
                    $"Uses internal APIs and requires BrimDeck {module.HostRange}. It may stop working after an update.")
            : Loc.T($"要求 BrimDeck {module.HostRange}。", $"Requires BrimDeck {module.HostRange}.");
        body.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Foreground = UI.Brush(_palette.Dark ? "#E4B477" : "#A2631F")
        });
    }
    private Border ModuleNotice(string text)
    {
        var label = TextLine(text, 11.5, TextSecondary); label.TextWrapping = TextWrapping.Wrap;
        return new Border { Child = label, Background = UI.Brush(Inset), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12) };
    }
    private void AddModuleErrors(ModuleInfo module, StackPanel body)
    {
        var errors = new TextBlock { FontSize = 11.5, Foreground = UI.Brush(_palette.Dark ? "#F0A6AA" : "#B12E3A"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(errors);
        AddModuleMetricRefresh(_ =>
        {
            var faults = _app.Modules.Faults.Where(f => f.ModuleId == module.Id).TakeLast(3).ToArray();
            string text = string.Join("\n", faults.Select(f => f.At.ToLocalTime().ToString("HH:mm:ss") + " · " + f.Message));
            if (text.Length == 0) text = _app.Modules.Modules.FirstOrDefault(m => m.Id == module.Id)?.Error ?? "";
            SetMetricText(errors, text); errors.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
    }
}
