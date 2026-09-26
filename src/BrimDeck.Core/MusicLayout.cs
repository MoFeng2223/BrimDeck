namespace BrimDeck.Core;

public sealed record MusicTier(double Toolbar, double Top, double Bottom, double Side, double Gap, double Radius,
    double MinColumn, double Nudge, double Title, double TitleLine, double Artist, double ArtistLine,
    double Lyric, double LyricLine, double LyricSide, double Time, double ProgressRow, double Bar, double BarHover,
    double Play, double Skip, double Mode, double ControlGap, double MiddleGap, double ProgressGap, double Lift, double Equalizer)
{
    public static MusicTier Compact { get; } = new(28, 6, 14, 16, 12, 8, 220, .06, 16, 22, 12, 16, 11.5, 18, 10.5, 10.5, 16, 3, 6, 24, 18, 14, 20, 6, 4, 2, 12);
    public static MusicTier Standard { get; } = new(36, 8, 20, 20, 14, 10, 240, .03, 20, 26, 13, 16, 12, 18, 11, 11.5, 18, 4, 8, 32, 24, 18, 28, 8, 8, 4, 16);
    public static MusicTier Spacious { get; } = new(40, 10, 24, 24, 18, 12, 260, 0, 26, 32, 16, 22, 14, 20, 12.5, 12, 20, 4, 8, 40, 30, 22, 36, 12, 10, 6, 20);
    // Above the spacious preset (300 tall) the text grows with the panel height, capped at 4/3 for 400.
    // Lyric line spacing grows faster than the glyphs so the previous and next lines drift away from the current one.
    public MusicTier Scaled(double factor)
    {
        static double Half(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;
        double spread = 1 + 1.6 * (factor - 1);
        return this with
        {
            Title = Half(Title * factor), TitleLine = Math.Round(TitleLine * factor), Artist = Half(Artist * factor), ArtistLine = Math.Round(ArtistLine * factor),
            Lyric = Half(Lyric * factor), LyricLine = Math.Round(LyricLine * spread), LyricSide = Half(LyricSide * factor), Equalizer = Math.Round(Equalizer * factor)
        };
    }
}

public sealed record MusicLayout(MusicTier Tier, double BodyY, double BodyHeight, double Cover, double CoverY,
    double ColumnX, double ColumnWidth, bool MergeTitle, double HeaderHeight, double MiddleHeight, int LyricLines,
    bool ShowMessage, double SlotY, double SlotHeight, double ControlsY, double TripletX, double TripletWidth,
    double ModeX, double Nudge, double ProgressY, double ProgressHeight, bool ShowFullControl, double FullControlRoom = 0)
{
    // The entry label shortens step by step. The last step is 21.8 DIP in Microsoft YaHei at the largest
    // (12) type; with 6 padding and a 1 border on each side it needs 36, which the narrowest row still has.
    // In English the second step keeps the meaning in two words; "En…" in Segoe UI is narrower than "启…".
    public static string[] FullControlLabels => Loc.IsEnglish ? ["Enable full control", "Full control", "Enable…", "En…"] : ["启用完整控制", "启用完整…", "启用…", "启…"];
    public const double FullControlGap = 8, FullControlChrome = 14, FullControlMinimum = 36;
    public double ColumnCenter => ColumnX + ColumnWidth / 2;
    public double TripletCenter => TripletX + TripletWidth / 2;
    public static MusicLayout Calculate(double width, double height, bool lyrics, bool timeline, bool mode, bool message = false, bool logMode = false, double messageHeight = 0, bool transport = true)
    {
        var tier = height < 180 ? MusicTier.Compact : height < 290 ? MusicTier.Standard : MusicTier.Spacious;
        bool small = tier == MusicTier.Compact, large = tier == MusicTier.Spacious;
        var c = large && height > 300 ? tier.Scaled(Math.Min(height / 300, 4.0 / 3)) : tier;
        double right = width - c.Side, bodyY = c.Toolbar + c.Top, bodyH = height - bodyY - c.Bottom;
        double cover = Math.Max(48, Math.Min(bodyH, width - 2 * c.Side - c.Gap - c.MinColumn));
        double coverY = bodyY + Math.Floor((bodyH - cover) / 2), colX = c.Side + cover + c.Gap, colW = right - colX;
        double progressH = timeline ? c.ProgressRow : 0, footerH = c.Lift + c.Play + (timeline ? c.ProgressGap + progressH : 0);
        // The transport glyphs occupy 3..21 of a 24-unit square. Leave a small
        // optical inset above the body bottom, keeping timeline/lyric space fixed.
        double controlsY = bodyY + bodyH - c.Play * 7 / 8 - 2;
        double progressY = bodyY + bodyH - footerH;
        double roomSeparate = bodyH - footerH - c.MiddleGap - c.TitleLine - c.ArtistLine;
        double hintHeight = messageHeight > 0 ? messageHeight : c.LyricLine;
        bool merge = lyrics && (small || roomSeparate < c.LyricLine) || message && !small && roomSeparate < hintHeight;
        double headH = c.TitleLine + (merge ? 0 : c.ArtistLine), middleH = bodyH - footerH - c.MiddleGap - headH;
        int lines = !lyrics ? 0 : !small && middleH >= 3 * c.LyricLine + 2 ? 3 : middleH >= c.LyricLine ? 1 : 0;
        bool showMessage = message && !small && middleH >= hintHeight;
        double slotH = showMessage ? hintHeight : lines * c.LyricLine, slotY = bodyY + headH + Math.Floor((middleH - slotH) / 2);
        double tripletW = c.Skip * 2 + c.Play + 2 * c.ControlGap, modeExt = c.Mode + c.ControlGap, center = colX + colW / 2;
        double nudge = Math.Max(0, Math.Floor(Math.Min(colW * c.Nudge, (center - width / 2) * .7) + .5));
        double extension = mode && !large ? modeExt : 0;
        double tripletX = Math.Floor(center - nudge - (tripletW + extension) / 2 + .5) + extension, modeX = tripletX - modeExt;
        if (mode && modeX < colX) { modeX = colX; tripletX = Math.Max(tripletX, modeX + c.Mode + 20); }
        // Netease without full control offers the entry at the right end of the transport row. The entry
        // never moves the transport; the window shortens its label to the room right of the buttons.
        double fullControlRoom = !logMode ? 0 : transport ? right - (tripletX + tripletW) - FullControlGap : colW;
        bool enable = fullControlRoom >= FullControlMinimum;
        if (!enable) fullControlRoom = 0;
        return new(c, bodyY, bodyH, cover, coverY, colX, colW, merge, headH, middleH, lines, showMessage, slotY, slotH,
            controlsY, tripletX, tripletW, modeX, nudge, progressY, progressH, enable, fullControlRoom);
    }
}

public enum MusicPlaybackMode { List, Track, Shuffle, Order }

public static class MusicPlaybackModes
{
    public static MusicPlaybackMode Current(MediaTrack track) => track.Shuffle ? MusicPlaybackMode.Shuffle : FromRepeat(track.Repeat);
    private static MusicPlaybackMode FromRepeat(MediaRepeat repeat) => repeat switch
    { MediaRepeat.List => MusicPlaybackMode.List, MediaRepeat.Track => MusicPlaybackMode.Track, _ => MusicPlaybackMode.Order };
    public static IReadOnlyList<MusicPlaybackMode> Supported(MediaTrack track)
    {
        if (!track.CanRepeat && !track.CanShuffle) return [];
        return Enum.GetValues<MusicPlaybackMode>().Where(mode => mode == MusicPlaybackMode.Shuffle ? track.CanShuffle
            : track.CanRepeat && (!track.Shuffle || track.CanShuffle) || track.CanShuffle && mode == FromRepeat(track.Repeat)).ToArray();
    }
    public static MusicPlaybackMode Next(MediaTrack track)
    {
        var modes = Supported(track).ToList();
        return modes.Count == 0 ? Current(track) : modes[(modes.IndexOf(Current(track)) + 1) % modes.Count];
    }
    public static string Label(MusicPlaybackMode mode) => mode switch
    { MusicPlaybackMode.List => Loc.T("列表循环", "Repeat all"), MusicPlaybackMode.Track => Loc.T("单曲循环", "Repeat one"), MusicPlaybackMode.Shuffle => Loc.T("随机播放", "Shuffle"), _ => Loc.T("顺序播放", "In order") };
}
