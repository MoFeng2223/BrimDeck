using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    internal void ShowModelDetails(bool details) { _details = details; _modelPage = 0; RenderUsage(); }
    internal void SelectQuotaGroup(string group) { _antigravityGroup = group; RenderUsage(); }
    internal FrameworkElement ScaledContent => ContentCanvas;

    private const double ContentBottomInset = 10, ColumnTopInset = 2, EdgeInset = 6;

    private enum Tier { Roomy, Default, Dense, Compact, Tiny }
    private sealed record SegmentItem(string Text, string Name, bool Active, Action Select, string? Tip = null);
    private sealed record QuotaView(List<Quota> Quotas, List<string> Groups, int Pages, int Page);

    // Every size is a whole DIP so text stays crisp; the panel steps between tiers instead of scaling continuously.
    private sealed record Layout(Tier Tier, double Name, double Number, double Label, double Reset, double Foot, double Plan, double Segment,
        double Head, double LabelRow, double QuotaGap, double RowGap, double Track, double ColumnPad, double Toolbar, double FootTop, double FootBottom, bool Footer, bool UsedWord)
    {
        public static Layout For(Tier tier) => tier switch
        {
            Tier.Roomy => new(tier, 18, 30, 14, 12.5, 14, 12, 12, 30, 18, 20, 6, 6, 20, 40, 10, 9, true, true),
            Tier.Default => new(tier, 15, 20, 12, 11, 12, 10.5, 10.5, 24, 16, 10, 4, 4, 14, 36, 8, 7, true, true),
            Tier.Dense => new(tier, 14, 18, 11.5, 10.5, 11, 10.5, 10.5, 22, 15, 8, 3, 4, 10, 36, 6, 5, true, true),
            Tier.Compact => new(tier, 13, 16, 11, 10, 11, 9.5, 9.5, 20, 13, 4, 2, 3, 10, 32, 4, 4, true, true),
            _ => new(tier, 12, 15, 10.5, 10, 10, 9.5, 9.5, 18, 13, 4, 2, 3, 8, 28, 0, 0, false, false)
        };
        // A single column wider than 480 DIP lays its quotas side by side with larger numbers.
        public Layout Wide() => this with
        {
            Name = Math.Max(Name, 17), Number = Math.Max(Number, 30), Label = Math.Max(Label, 13), Reset = Math.Max(Reset, 12),
            Foot = Math.Max(Foot, 13), LabelRow = Math.Max(LabelRow, 18), RowGap = Math.Max(RowGap, 6)
        };
        public double NumberRow => Math.Ceiling(Number * 1.2);
        public double FootRow => Math.Ceiling(Foot * 1.5);
        public double QuotaHeight => LabelRow + RowGap + NumberRow + RowGap + Track;
        public double FooterHeight(bool inline) => Footer ? FootTop + 1 + FootBottom + (inline ? 1 : 2) * FootRow : 0;
        public double Required(int slots, bool inline) => Head + slots * QuotaHeight + Math.Max(0, slots - 1) * QuotaGap + FooterHeight(inline);
    }

    private static double ToolbarFor(double panelHeight) => panelHeight < 170 ? 28 : panelHeight >= 320 ? 40 : 36;

    private static (Layout Layout, bool Inline, bool Wide) Fit(int count, int slots, double columnWidth, double panelHeight)
    {
        bool wide = count == 1 && columnWidth >= 480;
        // Width limits the tier first; extra height only enlarges columns that are wide enough for it.
        var byWidth = columnWidth >= 300 && count <= 2 ? Tier.Default : columnWidth >= 170 ? Tier.Dense : Tier.Compact;
        var start = panelHeight < 170 ? Tier.Tiny : panelHeight >= 320 && columnWidth >= 260 ? Tier.Roomy : byWidth;
        for (var tier = start; tier <= Tier.Tiny; tier++)
        {
            var layout = Layout.For(tier); if (wide) layout = layout.Wide();
            bool inline = columnWidth >= 300 && (count <= 2 || tier == Tier.Roomy && columnWidth >= 260);
            double available = panelHeight - layout.Toolbar - ContentBottomInset - ColumnTopInset;
            if (layout.Required(wide ? 1 : slots, inline) <= available) return (layout, inline, wide);
        }
        return (Layout.For(Tier.Tiny), false, wide);
    }

    private static List<TokenEntry> Entries(ProviderSnapshot snapshot, int days)
    {
        var start = DateTime.Now.Date.AddDays(1 - days);
        return snapshot.Entries.Where(x => x.Time.LocalDateTime >= start).ToList();
    }

    public void RenderUsage()
    {
        if (UsageContent is null) return;
        ViewButtons.Children.Clear(); PeriodButtons.Children.Clear(); UsageContent.Children.Clear();
        ClockLabel.Text = DateTime.Now.ToString("HH:mm");
        double panelHeight = ContentCanvas.Height;
        ToolbarRow.Height = new GridLength(ToolbarFor(panelHeight));
        ViewButtons.Children.Add(Segmented([new("用量", "AI 用量", !_details, () => ShowModelDetails(false)), new("模型", "模型明细", _details, () => ShowModelDetails(true))], 12, 18, 10));
        if (_details)
            PeriodButtons.Children.Add(Segmented(new[] { 1, 7, 30 }.Select(days => new SegmentItem(days == 1 ? "今日" : $"{days} 天", days == 1 ? "今日" : $"{days} 天", _days == days,
                () => { _days = days; _modelPage = 0; RenderUsage(); })).ToList(), 11.5, 17, 8));
        ModeLabel.Text = _app.DemoMode ? "演示" : "";
        var updateStatus = _refreshing ? "更新中" : LastRefreshError is not null ? "更新失败" :
            _updatedAt is { } time ? $"更新于 {time.LocalDateTime:MM-dd HH:mm:ss}" : "尚未更新";
        SettingsButton.ToolTip = "设置\n" + updateStatus;

        var apps = Settings.EnabledApps;
        if (apps.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(UI.Fixed(Settings.UsagePage ? "未选择应用" : "AI 用量已关闭", 14, 20, UI.Secondary));
            var open = Segmented([new("打开设置", "打开设置", false, _app.OpenSettings)], 12, 22, 12);
            open.Margin = new Thickness(0, 12, 0, 0); open.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(open); UsageContent.Children.Add(empty);
            return;
        }
        var columns = apps.Select(entry => (Entry: entry, Snapshot: Snapshots.FirstOrDefault(x => x.Id == entry.Id) ?? new ProviderSnapshot(entry.Id))).ToList();
        if (_details) RenderModels(columns.Select(x => x.Snapshot).ToList());
        else RenderColumns(columns);
    }

    // A joined switch: one rounded strip, the selected segment filled. Buttons carry accessible names.
    private static Border Segmented(IReadOnlyList<SegmentItem> items, double fontSize, double lineHeight, double padX)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            var text = UI.Fixed(item.Text, fontSize, lineHeight, UI.Secondary); text.ClearValue(TextBlock.ForegroundProperty); text.ClearValue(TextBlock.FontWeightProperty);
            var button = new Button { Style = (Style)Application.Current.FindResource("SegmentButton"), Content = text, Height = lineHeight, FontSize = fontSize,
                Padding = new Thickness(padX, 0, padX, 0), Tag = item.Active ? "Selected" : null, ToolTip = item.Tip };
            button.Click += (_, _) => item.Select();
            AutomationProperties.SetName(button, item.Name);
            strip.Children.Add(button);
        }
        return new Border { Child = strip, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(Math.Round(lineHeight / 3) + 1), Padding = new Thickness(1) };
    }

    private QuotaView Quotas(ProviderSnapshot snapshot)
    {
        var quotas = snapshot.Quotas.OrderBy(x => x.Minutes is > 0 ? x.Minutes.Value : int.MaxValue).ToList();
        var groups = new List<string>();
        string GroupOf(Quota quota) => UI.QuotaGroup(quota) is { Length: > 0 } group ? group : "其他";
        if (snapshot.Id == ProviderId.Antigravity && quotas.Any(x => UI.QuotaGroup(x).Length > 0))
        {
            groups = quotas.Select(GroupOf).Distinct().ToList();
            if (!groups.Contains(_antigravityGroup)) _antigravityGroup = groups[0];
            quotas = quotas.Where(x => GroupOf(x) == _antigravityGroup).ToList();
        }
        int pages = Math.Max(1, (quotas.Count + 1) / 2);
        int page = Math.Clamp(_quotaPages.GetValueOrDefault(snapshot.Id), 0, pages - 1);
        if (pages > 1) quotas = quotas.Skip(page * 2).Take(2).ToList();
        return new QuotaView(quotas, groups, pages, page);
    }

    private void RenderColumns(List<(AppEntry Entry, ProviderSnapshot Snapshot)> columns)
    {
        int count = columns.Count;
        double columnWidth = (ContentCanvas.Width - EdgeInset * 2) / count;
        var views = columns.Select(x => Quotas(x.Snapshot)).ToList();
        int slots = views.Max(view => Math.Clamp(view.Quotas.Count, 1, 2));
        var (layout, inline, wide) = Fit(count, slots, columnWidth, ContentCanvas.Height);
        ToolbarRow.Height = new GridLength(layout.Toolbar);
        // Spare height is always split evenly above and below the quotas, so resizing moves them smoothly
        // instead of pinning them under the title until a threshold is crossed.
        const bool center = true;
        var grid = new UniformGrid { Columns = count, Rows = 1 };
        for (int i = 0; i < count; i++)
            grid.Children.Add(Column(columns[i].Entry, columns[i].Snapshot, views[i], layout, inline, wide, center, i > 0, columnWidth - layout.ColumnPad * 2 - (i > 0 ? 1 : 0)));
        UsageContent.Children.Add(grid);
    }

    private FrameworkElement Column(AppEntry entry, ProviderSnapshot snapshot, QuotaView view, Layout layout, bool inline, bool wide, bool center, bool separator, double innerWidth)
    {
        bool off = view.Quotas.Count == 0;
        var column = new Grid();
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.Head) });
        column.RowDefinitions.Add(new RowDefinition());
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title row: the plan sits right after the name, the group switch keeps the right edge, and the name gives way first.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = UI.Fixed(snapshot.Name, layout.Name, layout.Head, off ? UI.Secondary : UI.Primary, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var tip = new List<string> { snapshot.Status, snapshot.Source, snapshot.QuotaTime?.LocalDateTime.ToString("MM-dd HH:mm:ss") ?? "" };
        if (!layout.Footer) foreach (var (label, days) in new[] { ("今日", 1), ("7 天", 7) })
            tip.Add(snapshot.UsageAvailable ? $"{label} {UI.Number(Entries(snapshot, days).Sum(x => x.Total))} · {UI.Cost(Entries(snapshot, days), _app.Prices)}" : $"{label} —");
        name.ToolTip = string.Join("\n", tip.Where(x => !string.IsNullOrWhiteSpace(x)));
        head.Children.Add(name);
        name.Measure(new Size(double.PositiveInfinity, layout.Head));
        double nameWidth = name.DesiredSize.Width, occupied = 0;
        if (snapshot.Plan.Length > 0)
        {
            var chip = PlanChip(snapshot, layout);
            Grid.SetColumn(chip, 1); head.Children.Add(chip);
            chip.Measure(new Size(double.PositiveInfinity, layout.Head)); occupied += chip.DesiredSize.Width;
        }
        FrameworkElement? control = null;
        if (view.Groups.Count > 1) control = GroupSwitch(view.Groups, layout, innerWidth - occupied - nameWidth);
        else if (view.Pages > 1)
        {
            control = Segmented([new($"{view.Page + 1}/{view.Pages} ›", "切换配额", false, () => { _quotaPages[snapshot.Id] = (view.Page + 1) % view.Pages; RenderUsage(); }, "切换配额")],
                layout.Segment, Math.Ceiling(layout.Segment * 1.35), 6);
            control.Margin = new Thickness(6, 0, 0, 0); control.VerticalAlignment = VerticalAlignment.Center;
        }
        if (control is not null)
        {
            Grid.SetColumn(control, 3); head.Children.Add(control);
            control.Measure(new Size(double.PositiveInfinity, layout.Head)); occupied += control.DesiredSize.Width;
        }
        name.MaxWidth = Math.Max(20, innerWidth - occupied);
        column.Children.Add(head);

        FrameworkElement body;
        if (off)
        {
            var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(UI.Fixed(snapshot.StatusLabel, Math.Max(11, layout.Label + 1), Math.Ceiling(layout.Label * 1.5), UI.Secondary, FontWeights.SemiBold));
            var detail = UI.Text(snapshot.Status, Math.Max(10, layout.Reset), UI.Tertiary); detail.FontFamily = UI.PanelFont; detail.Margin = new Thickness(0, 3, 0, 0); detail.MaxHeight = 34; detail.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(detail); body = status;
        }
        else if (wide)
        {
            var row = new UniformGrid { Rows = 1, Columns = view.Quotas.Count, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < view.Quotas.Count; i++)
            {
                var block = QuotaBlock(entry, view.Quotas[i], layout);
                block.Margin = new Thickness(i > 0 ? 18 : 0, 0, i < view.Quotas.Count - 1 ? 18 : 0, 0);
                row.Children.Add(block);
            }
            body = row;
        }
        else
        {
            var stack = new StackPanel { VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top };
            for (int i = 0; i < view.Quotas.Count; i++)
            {
                var block = QuotaBlock(entry, view.Quotas[i], layout);
                if (i > 0) block.Margin = new Thickness(0, layout.QuotaGap, 0, 0);
                stack.Children.Add(block);
            }
            body = stack;
        }
        Grid.SetRow(body, 1); column.Children.Add(body);

        if (layout.Footer)
        {
            var footer = new StackPanel();
            footer.Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, layout.FootTop, 0, layout.FootBottom) });
            var rows = new UniformGrid { Rows = inline ? 1 : 2, Columns = inline ? 2 : 1 };
            var today = TotalRow(snapshot, "今日", 1, layout); var week = TotalRow(snapshot, "7 天", 7, layout);
            if (inline) { today.Margin = new Thickness(0, 0, 8, 0); week.Margin = new Thickness(8, 0, 0, 0); }
            rows.Children.Add(today); rows.Children.Add(week); footer.Children.Add(rows);
            Grid.SetRow(footer, 2); column.Children.Add(footer);
        }
        return new Border
        {
            Child = column, Padding = new Thickness(layout.ColumnPad, ColumnTopInset, layout.ColumnPad, 0),
            BorderThickness = new Thickness(separator ? 1 : 0, 0, 0, 0), BorderBrush = UI.Brush(UI.Hairline)
        };
    }

    private static Border PlanChip(ProviderSnapshot snapshot, Layout layout)
    {
        var text = UI.Fixed(UI.DisplayPlan(snapshot.Plan), layout.Plan, Math.Ceiling(layout.Plan * 1.45), UI.Tertiary);
        return new Border
        {
            Child = text, BorderBrush = UI.Brush(UI.Outline), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(layout.Tier >= Tier.Dense ? 4 : 5, 0, layout.Tier >= Tier.Dense ? 4 : 5, 0), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = string.Join("\n", new[] { snapshot.Plan, snapshot.PlanSource }.Where(x => x.Length > 0))
        };
    }

    // Full group names when the title row has room, then a three-letter form, then initials.
    private Border GroupSwitch(List<string> groups, Layout layout, double available)
    {
        string[][] variants =
        [
            groups.ToArray(),
            groups.Select(group => group.Length > 3 ? group[..3] + "…" : group).ToArray(),
            groups.Select(group => group[..1]).ToArray()
        ];
        Border chosen = null!;
        foreach (var labels in variants)
        {
            var items = groups.Select((group, i) => new SegmentItem(labels[i], "Antigravity " + group, group == _antigravityGroup, () => SelectQuotaGroup(group), labels[i] == group ? null : group)).ToList();
            chosen = Segmented(items, layout.Segment, Math.Ceiling(layout.Segment * 1.35), labels == variants[0] ? 7 : 5);
            chosen.Measure(new Size(double.PositiveInfinity, layout.Head));
            if (chosen.DesiredSize.Width + 6 <= available) break;
        }
        chosen.Margin = new Thickness(6, 0, 0, 0); chosen.VerticalAlignment = VerticalAlignment.Center;
        return chosen;
    }

    // Three rows per quota: the period name, the used share with its reset countdown, and the track.
    private static Grid QuotaBlock(AppEntry entry, Quota quota, Layout layout)
    {
        double used = Math.Clamp(quota.UsedPercent, 0, 100);
        string color = AppPresets.QuotaColor(entry, used);
        int level = AppPresets.Level(used);
        var block = new Grid();
        foreach (double height in new[] { layout.LabelRow, layout.RowGap, layout.NumberRow, layout.RowGap, layout.Track })
            block.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var label = UI.Fixed(UI.QuotaLabel(quota), layout.Label, layout.LabelRow, UI.Secondary); label.ToolTip = quota.Label; label.HorizontalAlignment = HorizontalAlignment.Left;
        block.Children.Add(label);
        // The number always shows in full; the countdown at the right is what gives way in a narrow column.
        var numbers = new Grid(); numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); numbers.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(numbers, 2);
        var number = new TextBlock { FontFamily = UI.DisplayFont, LineHeight = layout.NumberRow, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Height = layout.NumberRow,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        number.Inlines.Add(new Run($"{used:0.#}%") { FontSize = layout.Number, FontWeight = FontWeights.SemiBold, Foreground = UI.Brush(level > 0 ? color : UI.Primary) });
        if (layout.UsedWord) number.Inlines.Add(new Run(" 已用") { FontSize = 11, FontFamily = UI.PanelFont, Foreground = UI.Brush(UI.Tertiary) });
        number.ToolTip = $"已用 {used:0.#}% · 剩余 {quota.Remaining:0.#}%";
        numbers.Children.Add(number);
        var resetText = UI.ShortReset(quota.ResetAt);
        if (resetText.Length > 0)
        {
            var reset = UI.Line(resetText, layout.Reset, UI.Tertiary); reset.FontFamily = UI.PanelFont;
            reset.VerticalAlignment = VerticalAlignment.Bottom; reset.HorizontalAlignment = HorizontalAlignment.Right; reset.Margin = new Thickness(8, 0, 0, 1);
            reset.ToolTip = quota.ResetAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            Grid.SetColumn(reset, 1); numbers.Children.Add(reset);
        }
        block.Children.Add(numbers);
        var track = new Grid(); Grid.SetRow(track, 4);
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, used), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 100 - used), GridUnitType.Star) });
        var background = new Border { Background = UI.Brush(UI.Track), CornerRadius = new CornerRadius(layout.Track / 2) }; Grid.SetColumnSpan(background, 2); track.Children.Add(background);
        if (used > 0) track.Children.Add(new Border { Background = UI.Brush(color), CornerRadius = new CornerRadius(layout.Track / 2) });
        block.Children.Add(track);
        return block;
    }

    private Grid TotalRow(ProviderSnapshot snapshot, string label, int days, Layout layout)
    {
        var entries = Entries(snapshot, days);
        var row = new Grid { Height = layout.FootRow };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 30 }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = UI.Fixed(label, layout.Foot, layout.FootRow, UI.Tertiary); caption.Margin = new Thickness(0, 0, 8, 0); caption.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(caption);
        var tokens = UI.Fixed(snapshot.UsageAvailable ? UI.Number(entries.Sum(x => x.Total)) : "—", layout.Foot, layout.FootRow, UI.Secondary); tokens.HorizontalAlignment = HorizontalAlignment.Left;
        tokens.ToolTip = snapshot.UsageAvailable ? $"{label} Token · {entries.Sum(x => x.Total):N0}" : snapshot.UsageNote; Grid.SetColumn(tokens, 1); row.Children.Add(tokens);
        var price = UI.Fixed(snapshot.UsageAvailable ? UI.Cost(entries, _app.Prices) : "—", layout.Foot, layout.FootRow, snapshot.UsageAvailable ? UI.Primary : UI.Tertiary, FontWeights.SemiBold);
        price.Margin = new Thickness(8, 0, 0, 0); price.ToolTip = snapshot.UsageAvailable ? UI.CostNote(entries, _app.Prices) : snapshot.UsageNote; Grid.SetColumn(price, 2); row.Children.Add(price);
        return row;
    }

    private void RenderModels(List<ProviderSnapshot> snapshots)
    {
        var rows = snapshots.SelectMany(s => Entries(s, _days).GroupBy(x => x.Model).Select(g => new { Snapshot = s, Model = g.Key, Tokens = g.ToList() })).OrderByDescending(x => x.Tokens.Sum(t => t.Total)).ToList();
        const double rowHeight = 27, headerHeight = 22, pagerHeight = 24;
        double available = ContentCanvas.Height - ToolbarFor(ContentCanvas.Height) - ContentBottomInset - 4;
        int pageSize = Math.Clamp((int)Math.Floor((available - headerHeight - pagerHeight) / rowHeight), 1, 8);
        int pages = Math.Max(1, (rows.Count + pageSize - 1) / pageSize); _modelPage = Math.Clamp(_modelPage, 0, pages - 1);
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) }); panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(pagerHeight) });
        Grid ModelRow(string app, string model, string total, string price, bool header)
        {
            var row = new Grid { Height = header ? headerHeight : rowHeight };
            foreach (double width in new[] { 1.1, 3.4, 1.8, 1.5 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            string[] values = [app, model, total, price];
            for (int i = 0; i < values.Length; i++)
            {
                var text = header ? UI.Fixed(values[i], 11, headerHeight, UI.Tertiary)
                    : UI.Fixed(values[i], 12.5, rowHeight, i == 1 ? UI.Secondary : UI.Primary, i == 0 || i == 3 ? FontWeights.SemiBold : FontWeights.Normal);
                text.HorizontalAlignment = i >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                if (i > 0) text.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(text, i); row.Children.Add(text);
            }
            return row;
        }
        var headerRow = new Border { Child = ModelRow("应用", "模型", "Token", "费用 · USD", true), BorderBrush = UI.Brush(UI.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) };
        panel.Children.Add(headerRow);
        var list = new StackPanel(); Grid.SetRow(list, 1); panel.Children.Add(list);
        foreach (var row in rows.Skip(_modelPage * pageSize).Take(pageSize))
        {
            var item = new Border { Child = ModelRow(row.Snapshot.Name, row.Model, UI.Number(row.Tokens.Sum(x => x.Total)), UI.Cost(row.Tokens, _app.Prices), false),
                BorderBrush = UI.Brush("#0DFFFFFF"), BorderThickness = new Thickness(0, list.Children.Count > 0 ? 1 : 0, 0, 0) };
            item.ToolTip = UI.CostNote(row.Tokens, _app.Prices) + $"\n输入 {row.Tokens.Sum(x => x.Input):N0} · 缓存 {row.Tokens.Sum(x => x.CacheRead + x.CacheWrite + x.CacheWriteHour):N0} · 输出 {row.Tokens.Sum(x => x.Output):N0}";
            list.Children.Add(item);
        }
        if (rows.Count == 0) { var empty = UI.Fixed("暂无用量明细", 13, 20, UI.Tertiary); empty.Margin = new Thickness(0, 24, 0, 0); empty.HorizontalAlignment = HorizontalAlignment.Center; list.Children.Add(empty); }
        if (pages > 1)
        {
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var previous = Segmented([new("‹", "上一页", false, () => { _modelPage--; RenderUsage(); }, "上一页")], 12, 18, 7); previous.IsEnabled = _modelPage > 0;
            var count = UI.Fixed($"{_modelPage + 1} / {pages}", 11, 18, UI.Tertiary); count.Margin = new Thickness(10, 0, 10, 0);
            var next = Segmented([new("›", "下一页", false, () => { _modelPage++; RenderUsage(); }, "下一页")], 12, 18, 7); next.IsEnabled = _modelPage < pages - 1;
            navigation.Children.Add(previous); navigation.Children.Add(count); navigation.Children.Add(next);
            Grid.SetRow(navigation, 2); panel.Children.Add(navigation);
        }
        UsageContent.Children.Add(new Border { Child = panel, Padding = new Thickness(14, 0, 14, 0) });
    }
}
