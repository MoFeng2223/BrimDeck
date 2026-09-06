using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

internal static class UI
{
    public static readonly FontFamily TextFont = new("Microsoft YaHei");
    // The panel sets Latin text and digits in Segoe UI Variable and falls back to YaHei for Chinese.
    public static readonly FontFamily PanelFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public const string Accent = "#0A84FF";
    // Text and structure on the pure black surface: three label levels by opacity, hairlines by opacity.
    public const string Primary = "#F5F5F7", Secondary = "#9EEBEBF5", Tertiary = "#61EBEBF5";
    public const string Hairline = "#17FFFFFF", Track = "#1FFFFFFF", Fill = "#12FFFFFF", FillSelected = "#24FFFFFF", Outline = "#24FFFFFF";
    public static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    public static string ColorFor(ProviderId id) => AppPresets.ThemeColor(id);
    public static TextBlock Text(string text, double size = 13, string color = "#E6E8EF", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Line(string text, double size = 13, string color = "#E6E6E9", FontWeight? weight = null)
        => new() { Text = text, FontFamily = TextFont, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    // A single line whose height is exactly the given value, independent of the font's own line box.
    public static TextBlock Fixed(string text, double size, double lineHeight, string color, FontWeight? weight = null, FontFamily? font = null)
    {
        var block = Line(text, size, color, weight);
        block.FontFamily = font ?? PanelFont;
        block.LineHeight = lineHeight; block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.Height = lineHeight; block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }
    public static Button Button(string text, Action click, bool active = false)
    {
        var button = new Button { Content = text, Background = Brush(active ? "#293A55" : "#222224"), BorderBrush = Brush(active ? "#4D83D1" : "#333336"),
            Foreground = Brush(active ? "#DBE9FF" : "#B2B2B8"), Padding = new Thickness(10, 5, 10, 5), FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => click();
        return button;
    }
    // A progress ring: a faint track with a colored arc that starts at the top and runs clockwise.
    public static Grid Ring(double percent, string color, double size, double thickness)
    {
        var ring = new Grid { Width = size, Height = size };
        ring.Children.Add(new Ellipse { Stroke = Brush("#29FFFFFF"), StrokeThickness = thickness });
        double fraction = Math.Clamp(percent / 100, 0, 1);
        if (fraction >= .999) ring.Children.Add(new Ellipse { Stroke = Brush(color), StrokeThickness = thickness });
        else if (fraction > 0)
        {
            double radius = size / 2 - thickness / 2, center = size / 2;
            double angle = fraction * Math.PI * 2;
            var figure = new PathFigure { StartPoint = new Point(center, center - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            ring.Children.Add(new Path { Data = geometry, Stroke = Brush(color), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        return ring;
    }
    public static Border Card(UIElement child, double padding = 16) => new()
    { Child = child, Background = Brush("#1C1C1E"), CornerRadius = new CornerRadius(10), BorderBrush = Brush("#2B2B2E"), BorderThickness = new Thickness(1), Padding = new Thickness(padding) };
    public static Border Divider(double margin = 12) => new() { Height = 1, Background = Brush("#2D2F38"), Margin = new Thickness(0, margin, 0, margin) };
    public static string Number(long number) => number switch
    { >= 1_000_000_000 => (number / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B", >= 1_000_000 => (number / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M", >= 1000 => (number / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K", _ => number.ToString("N0") };
    public static string Cost(IEnumerable<TokenEntry> entries, Pricing pricing)
    {
        var cost = pricing.Summarize(entries);
        return "$" + cost.Amount.ToString("N2", CultureInfo.InvariantCulture) + (cost.Unpriced > 0 ? " +" : "");
    }
    public static string CostNote(IEnumerable<TokenEntry> entries, Pricing pricing)
    {
        var cost = pricing.Summarize(entries);
        if (!cost.HasValue && cost.Unpriced == 0) return "无用量记录";
        if (!cost.HasValue && cost.Unpriced > 0) return pricing.LastError ?? "未匹配到完整价格";
        var note = cost.IsEstimate ? "按模型单价计算 · USD" : "接口金额 · USD";
        if (cost.IsEstimate && pricing.UpdatedAt is { } time) note += $"\n价格更新 {time.LocalDateTime:MM-dd HH:mm}";
        if (cost.IsEstimate && (pricing.IsStale || pricing.LastError is not null)) note += " · 缓存";
        if (cost.Unpriced > 0) note += $"\n{cost.Unpriced} 条记录未计价";
        return note;
    }
    public static string Reset(DateTimeOffset? date)
    {
        if (date is null) return "重置时间未知";
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "已到重置时间，等待更新";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时后重置";
        if (remaining.TotalHours >= 1) return $"{remaining.Hours} 小时 {remaining.Minutes} 分后重置";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟后重置";
    }
    public static string ShortReset(DateTimeOffset? date)
    {
        if (date is null) return "";
        var remaining = date.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "待重置";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 时后";
        if (remaining.TotalHours >= 1) return $"{remaining.Hours} 时 {remaining.Minutes} 分后";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分后";
    }
    // "Gemini · 5 小时额度" → "5 小时"; the group prefix is shown by the column's switch instead.
    public static string QuotaGroup(Quota quota) => quota.Label.Contains('·') ? quota.Label.Split('·')[0].Trim() : "";
    public static string QuotaLabel(Quota quota)
    {
        var label = quota.Label.Contains('·') ? quota.Label.Split('·')[^1].Trim() : quota.Label;
        return label.Replace("额度", "", StringComparison.Ordinal).Trim();
    }
    public static string DisplayPlan(string plan) => plan.Trim().ToLowerInvariant() switch
    { "prolite" => "Pro Lite", "pro" or "google ai pro" => "Pro", "google ai ultra" => "Ultra", "plus" => "Plus", "free" => "Free", "max" => "Max", "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", _ => plan.Trim() };
    public static void OpenUrl(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
