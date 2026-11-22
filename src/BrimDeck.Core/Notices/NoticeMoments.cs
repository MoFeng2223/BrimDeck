using System.Globalization;
using System.Text.RegularExpressions;

namespace BrimDeck.Core.Notices;

// A date and time read from a notice's words. Start and End are wall-clock times in the user's zone (Kind Unspecified), as the
// plan page keeps them; nothing is turned into UTC. End is set only where the words give one ("15:00–16:00", "11月23日 20:00 –
// 11月25日 12:00"); otherwise the caller takes one hour. AllDay: only a date was written; Start is that day at 00:00 and End,
// when set, is the last day (inclusive) at 00:00 ("11月23日至25日"). InTitle says which field it was read from; Index and Length
// are where it stands in that field as it came in (UTF-16 units), Text is that part of it.
public sealed record NoticeMoment(DateTime Start, DateTime? End, bool AllDay, bool InTitle, int Index, int Length, string Text);

// Reads dates and times out of a notice's title and body, in Chinese and English, for the "加入日程" strip. A wrong date
// offered is worse than none, so every rule leans towards leaving a doubtful text alone:
// - A day word alone ("今天", "明天", "tomorrow") is not offered: such words are everywhere in chat. With a time it is.
// - A bare "N点" is a time only when a verb-like word follows ("3点开会", "3点前"), a word before says so ("改到4点") or a date
//   stands before it; "5点赞", "3点建议", "上涨5点", "差一点" are not. "一点" without a period word is never a time ("早一点到").
// - A clock time alone whose minutes are not a multiple of 5 ("(4:29)", "14:37 已签收") is a length or a timestamp; beside a
//   date it is read.
// - "11/26" without a year is a date only next to a time or a weekday ("11/26 15:00"); alone it may be a fraction or a score.
//   A time alone right beside a number not read as a date ("11.26 下午3点") is not offered: it would land on the wrong day.
// - Recurring times ("每周四", "每天9点", "every Thursday", "Mondays") and weekday ranges ("周一至周五 9:00-18:00") are not offered.
// - Times in a link, an e-mail address, a file name or a path, scores, ratios and durations are not read; a notice with a
//   verification code (VerificationCode, or the words "验证码", "verification code") gives none at all, since its times are how
//   long the code lasts; nor does a date or time that says when something expires ("有效期至11月30日", "15:30 前有效").
// - What is already over is not offered: a timed moment once its written end has passed or, without one, once it began more
//   than 10 minutes before the notice arrived ("14:00 的会议已开始", "merged at 14:00" report it); a date once its day has passed.
// - A moment followed by words saying it was moved ("从周四改到周五", "moved to") is the old one and is not offered.
// A time without a date takes the last date written before it on the same line ("明天的会推迟到4点" is tomorrow's, "周四的会改到
// 3点" Thursday's). Without one it is on the day the notice arrived; once over there: when it began more than 12 hours before the notice
// arrived it is the next day's ("早上8点集合" seen at 23:30), otherwise it is past and dropped ("10:00 的会议纪要" seen at 14:00),
// as NoticeTimeSources.Start reads a reminder.
// A date without a year is the nearest such date to the day the notice arrived (a tie goes forward): "1月5日" seen in December is
// next January's, "11月20日" seen on 11月22日 is this year's and so past and dropped.
// An hour without a period word or AM/PM ("3点", "3:00") is read as people mean it in a working day: 1 to 6 are the afternoon
// (13:00 to 18:00), 7 to 12 the morning or noon; written with a leading zero ("03:00") or as 0 or 13 to 23 it is taken as written.
// A single weekday ("周四", "Thursday", "this Thursday") is the nearest one from the day the notice arrived, that day included;
// "本周四"/"这周四" is the Thursday of the current week (weeks begin on Monday), "下周四"/"next Thursday" the following week's,
// "下下周四" the one after, "上周四"/"last Thursday" the previous week's (past, so dropped).
// The text is only read here: it is never kept or logged.
public static partial class NoticeMoments
{
    // At most this many moments are returned.
    public const int MaxCount = 4;
    // A time range longer than this on one day (after crossing midnight) is not believed: its end is dropped.
    private static readonly TimeSpan LongestRange = TimeSpan.FromHours(16);
    // An end given with its own date may be at most this far after the start.
    private static readonly TimeSpan LongestSpan = TimeSpan.FromDays(31);
    // Moments further ahead are not offered.
    private const int FurthestDays = 731;

    // The moments in a notice's title and then its body, each in the order they are written, without repeats, at most MaxCount.
    // received is when the notice arrived; zone is the user's time zone. The texts are cut to what a notice may hold first.
    public static IReadOnlyList<NoticeMoment> Find(string? title, string? body, DateTimeOffset received, TimeZoneInfo zone)
    {
        title ??= ""; body ??= "";
        if (title.Length > VerificationCode.MaxTitle) title = title[..VerificationCode.MaxTitle];
        if (body.Length > VerificationCode.MaxBody) body = body[..VerificationCode.MaxBody];
        var now = TimeZoneInfo.ConvertTime(received, zone).DateTime;
        // A notice with a verification code offers the code; its times say how long the code lasts ("15:30 前有效"). The words
        // count too, since VerificationCode leaves a code alone that has a time beside it.
        if (CodeWords().IsMatch(title) || CodeWords().IsMatch(body) || VerificationCode.Find(title, body) is not null) return [];
        var found = new List<NoticeMoment>();
        Read(title, true, now, found);
        Read(body, false, now, found);
        return found;
    }

    // "11月26日 周四 15:00", "11月26日 周四 15:00–16:00", "11月23日 20:00 – 11月25日 12:00", "11月26日 周四（全天）",
    // "11月23日 – 11月25日（全天）"; the year is written when it is not today's year. today is the local date now.
    public static string Label(NoticeMoment moment, DateTime today)
    {
        var start = moment.Start;
        if (moment.AllDay)
        {
            if (moment.End is { } last && last.Date != start.Date)
                return Loc.T($"{Day(start, today)} – {Day(last, today)}（全天）", $"{Day(start, today)} – {Day(last, today)} (all day)");
            return Loc.T($"{Day(start, today)} {Week(start)}（全天）", $"{Week(start)}, {Day(start, today)} (all day)");
        }
        string from = Clock(start);
        string day = Loc.T($"{Day(start, today)} {Week(start)}", $"{Week(start)}, {Day(start, today)}");
        if (moment.End is not { } end) return $"{day} {from}";
        if (end.Date == start.Date) return $"{day} {from}–{Clock(end)}";
        return $"{Day(start, today)} {from} – {Day(end, today)} {Clock(end)}";
    }

    // The start in a few words for a narrow place or a list row: "今天 15:00", "明天 15:00", "周四 15:00" within the coming week,
    // otherwise "11月26日 15:00"; a date only reads "周四 全天".
    public static string Brief(NoticeMoment moment, DateTime today)
    {
        var date = moment.Start.Date;
        today = today.Date;
        string day = date == today ? Loc.T("今天", "Today") : date == today.AddDays(1) ? Loc.T("明天", "Tomorrow")
            : date > today && date < today.AddDays(7) ? Week(date) : Day(date, today);
        return day + " " + (moment.AllDay ? Loc.T("全天", "all day") : Clock(moment.Start));
    }

    // Where it was read and how long it is taken to last: "从标题认出 · 时长按 1 小时", "从正文认出".
    public static string Detail(NoticeMoment moment)
    {
        string where = moment.InTitle ? Loc.T("从标题认出", "From the title") : Loc.T("从正文认出", "From the body");
        return moment.AllDay || moment.End is not null ? where : where + Loc.T(" · 时长按 1 小时", " · 1 hour assumed");
    }

    private static string Day(DateTime date, DateTime today) => date.Year == today.Year
        ? Loc.T($"{date.Month}月{date.Day}日", date.ToString("MMM d", CultureInfo.InvariantCulture))
        : Loc.T($"{date.Year}年{date.Month}月{date.Day}日", date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));

    private static string Week(DateTime date) => Loc.T("周" + "日一二三四五六"[(int)date.DayOfWeek], date.ToString("ddd", CultureInfo.InvariantCulture));

    private static string Clock(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ---- Reading one field ----

    private enum DateKind { Absolute, Slash, DayOnly, Relative, Weekday }
    // Period words: 凌晨 (Dawn); 早上, 上午 (Morning); 中午 (Noon); 下午 (Afternoon); 傍晚 (Dusk); 晚上, 今晚, tonight (Evening); AM; PM.
    private enum Period { None, Dawn, Morning, Noon, Afternoon, Dusk, Evening, Am, Pm }
    private const int Nearest = int.MinValue;

    private sealed class Token
    {
        public int Start, End;
        public bool IsTime;
        public DateKind Date;
        public int Year, Month, Day, Offset, Week = Nearest;
        public DayOfWeek Dow;
        // A day word's period ("今晚" is the evening), taken by a time after it without its own.
        public Period Default;
        public bool Recurring;
        public int Hour, Minute;
        public Period Period;
        // Written as a 24-hour time ("09:00", "15点"): no afternoon guess.
        public bool Literal;
        // Usable only beside a date or as the end of a range ("5点。", "12:00:00").
        public bool Weak;
    }

    private sealed class Endpoint
    {
        public readonly List<Token> Dates = [];
        public Token? Time;
        public int Start, End;
    }

    private static void Read(string text, bool inTitle, DateTime now, List<NoticeMoment> found)
    {
        if (found.Count >= MaxCount || text.Length == 0) return;
        VerificationCode.Normalize(text, out var s, out var map);
        var blocked = new List<(int Start, int End)>();
        foreach (Match m in Address().Matches(s)) blocked.Add((m.Index, m.Index + m.Length));
        var tokens = Tokens(s, blocked);
        int i = 0;
        // The last date written on this line: a time alone after it is on that day ("明天的会推迟到4点", "周四的会改到3点").
        DateTime? lastDate = null;
        int lastDateEnd = 0;
        while (i < tokens.Count && found.Count < MaxCount)
        {
            var chain = new List<Endpoint> { Take(tokens, ref i, s) };
            // "between 2pm and 6pm".
            int look = Math.Max(0, chain[0].Start - 10);
            bool between = s.AsSpan(look, chain[0].End - look).Contains("between", StringComparison.OrdinalIgnoreCase);
            while (i < tokens.Count && IsSeparator(s, chain[^1].End, tokens[i].Start, between)) chain.Add(Take(tokens, ref i, s));
            bool undated = chain.All(e => e.Dates.Count == 0);
            DateTime? inherited = undated && lastDate is not null && s.AsSpan(lastDateEnd, chain[0].Start - lastDateEnd).IndexOf('\n') < 0 ? lastDate : null;
            if (!undated && !chain[0].Dates.Any(d => d.Recurring) && DateOf(chain[0], null, now, out var written, out _) && written is not null)
                (lastDate, lastDateEnd) = (written, chain[0].End);
            if (Resolve(chain, now, inherited) is not { } moment || Expiry(s, chain[0].Start, chain[^1].End) || Superseded(s, chain[^1].End)) continue;
            // A time beside a number it did not read as a date ("11.26 下午3点", "15:00 12.01") would land on the wrong day.
            if (undated && UnreadDateBeside(s, chain[0].Start, chain[^1].End)) continue;
            int start = map[chain[0].Start], end = map[chain[^1].End - 1] + 1;
            var result = new NoticeMoment(moment.Start, moment.End, moment.AllDay, inTitle, start, end - start, text[start..end]);
            // "11月26日的评审，15:00 开始": the time takes the place of the whole day it is on.
            if (inherited is { } day) found.RemoveAll(f => f.InTitle == inTitle && f.AllDay && f.End is null && f.Start == day);
            if (!found.Any(f => f.Start == result.Start && f.End == result.End && f.AllDay == result.AllDay)) found.Add(result);
        }
    }

    // One side of a range: dates and a time standing together ("周四下午三点", "Thursday, Nov 26 at 3pm", "3pm tomorrow").
    private static Endpoint Take(List<Token> tokens, ref int i, string s)
    {
        var e = new Endpoint { Start = tokens[i].Start };
        if (!tokens[i].IsTime)
        {
            e.Dates.Add(tokens[i++]);
            while (i < tokens.Count && !tokens[i].IsTime && e.Dates.Count < 3 && IsGlue(s, tokens[i - 1].End, tokens[i].Start)) e.Dates.Add(tokens[i++]);
            if (i < tokens.Count && tokens[i].IsTime && IsGlue(s, tokens[i - 1].End, tokens[i].Start)) e.Time = tokens[i++];
        }
        else
        {
            e.Time = tokens[i++];
            while (i < tokens.Count && !tokens[i].IsTime && e.Dates.Count < 3 && IsGlue(s, tokens[i - 1].End, tokens[i].Start)) e.Dates.Add(tokens[i++]);
        }
        e.End = tokens[i - 1].End;
        return e;
    }

    // The time something was moved from: "从周四改到周五" offers Friday only.
    private static bool Superseded(string s, int end)
        => SupersededAfter().IsMatch(s.AsSpan(end, Math.Min(30, s.Length - end)));

    private static bool UnreadDateBeside(string s, int start, int end)
    {
        int before = start;
        while (before > 0 && char.IsWhiteSpace(s[before - 1])) before--;
        return before > 0 && char.IsAsciiDigit(s[before - 1]) || NumberAfter().IsMatch(s.AsSpan(end, Math.Min(12, s.Length - end)));
    }

    // What may stand between a date and a time of one moment: spaces, a comma, brackets, "的", "at", "on", "between", "@".
    private static bool IsGlue(string s, int from, int to)
    {
        if (to - from > 10) return false;
        var gap = new string(s.AsSpan(from, to - from).ToArray().Where(c => !(c is ' ' or '\t' or ' ' or ',' or '，' or '、' or '(' or ')' or '（' or '）' or '[' or ']' or '【' or '】')).ToArray());
        return gap.Length == 0 || gap is "的" or "@" || gap.Equals("at", StringComparison.OrdinalIgnoreCase) || gap.Equals("on", StringComparison.OrdinalIgnoreCase)
            || gap.Equals("between", StringComparison.OrdinalIgnoreCase) || gap.Equals("from", StringComparison.OrdinalIgnoreCase);
    }

    // What joins the two ends of a range.
    private static bool IsSeparator(string s, int from, int to, bool between)
    {
        if (to < from || to - from > 10) return false;
        var gap = s.AsSpan(from, to - from).Trim(" \t ").ToString().ToLowerInvariant();
        return gap is "-" or "–" or "—" or "~" or "至" or "到" or "to" or "until" or "till" or "through" or "thru" || between && gap == "and";
    }

    private readonly record struct Found(DateTime Start, DateTime? End, bool AllDay);

    // inherited: the date a time alone takes from earlier on its line.
    private static Found? Resolve(List<Endpoint> chain, DateTime now, DateTime? inherited)
    {
        if (chain.Count > 2 || chain.Any(e => e.Dates.Any(d => d.Recurring))) return null;
        var a = chain[0];
        var b = chain.Count == 2 ? chain[1] : null;
        if (!DateOf(a, null, now, out var da, out var defaultA)) return null;
        da ??= inherited;
        Found? result;
        if (b is null)
        {
            if (a.Time is { } time)
            {
                if (time.Weak && da is null) return null;
                if (Minutes(time, PeriodFor(time, defaultA)) is not { } minutes) return null;
                result = da is { } day ? new Found(day.AddMinutes(minutes), null, false) : Undated(minutes, null, now);
            }
            else
            {
                if (da is not { } day || !Explicit(a)) return null;
                result = new Found(day, null, true);
            }
        }
        else
        {
            // A range of weekdays alone is a recurring week ("周一至周五"), not a stretch of days.
            if (a.Dates.Count > 0 && b.Dates.Count > 0 && a.Dates.All(d => d.Date == DateKind.Weekday) && b.Dates.All(d => d.Date == DateKind.Weekday)) return null;
            if (!DateOf(b, da, now, out var db, out var defaultB)) return null;
            if (a.Time is { } t1 && b.Time is { } t2)
            {
                if (db is { } endDay)
                {
                    // "11月23日 20:00 – 11月25日 12:00".
                    if (da is not { } startDay) return null;
                    if (Minutes(t1, PeriodFor(t1, defaultA)) is not { } m1
                        || Minutes(t2, PeriodFor(t2, defaultB)) is not { } m2) return null;
                    var start = startDay.AddMinutes(m1);
                    DateTime? end = endDay.AddMinutes(m2);
                    if (end <= start || end - start > LongestSpan) end = null;
                    result = new Found(start, end, false);
                }
                else
                {
                    if (Range(t1, t2, defaultA) is not { } range) return null;
                    result = da is { } day ? new Found(day.AddMinutes(range.Start), range.End is { } e ? day.AddMinutes(e) : null, false) : Undated(range.Start, range.End, now);
                }
            }
            else if (a.Time is null && b.Time is null)
            {
                if (da is not { } first || db is not { } last || !Explicit(a) || !Explicit(b)) return null;
                result = new Found(first, last > first && last - first <= LongestSpan ? last : null, true);
            }
            else return null;
        }
        if (result is not { } r) return null;
        // What is over is not offered, nor what lies years ahead.
        if (r.AllDay ? (r.End ?? r.Start).Date < now.Date : Over(r.Start, r.End, now)) return null;
        if (r.Start.Date > now.Date.AddDays(FurthestDays)) return null;
        return r;
    }

    // When something stops being valid, not an event: "有效期至11月30日", "15:30 前有效", "expires Nov 30".
    private static bool Expiry(string s, int start, int end)
        => ExpiryBefore().IsMatch(s.AsSpan(Math.Max(0, start - 12), Math.Min(12, start))) || ExpiryAfter().IsMatch(s.AsSpan(end, Math.Min(6, s.Length - end)));

    // Over: its end has passed, or without an end it began more than a few minutes before the notice arrived. A notice about
    // something that began earlier mostly reports it ("14:00 的会议已开始", "merged at 14:00"); a range still running is kept.
    private static bool Over(DateTime start, DateTime? end, DateTime now) => end is { } e ? e <= now : start < now - Grace;
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // A time without a date: the day the notice arrived; once over, the next day if it began more than 12 hours before, else none.
    private static Found? Undated(int start, int? end, DateTime now)
    {
        var day = now.Date;
        var from = day.AddMinutes(start);
        DateTime? to = end is { } e ? day.AddMinutes(e) : null;
        if (!Over(from, to, now)) return new Found(from, to, false);
        if (from < now - TimeSpan.FromHours(12)) return new Found(from.AddDays(1), to?.AddDays(1), false);
        return null;
    }

    // A date that stands on its own as a whole day: a written date or a weekday, not a day word ("明天") or a bare "11/26".
    private static bool Explicit(Endpoint e) => e.Dates.Any(d => d.Date is DateKind.Absolute or DateKind.Weekday or DateKind.DayOnly);

    // The date of one side, or null when it gives none (a time alone). False when its words cannot be a date or disagree
    // ("11月26日 周五"). anchor is the start's date, for the end of a range ("至25日" is in the start's month).
    private static bool DateOf(Endpoint e, DateTime? anchor, DateTime now, out DateTime? date, out Period period)
    {
        date = null; period = Period.None;
        if (e.Dates.Count == 0) return true;
        DateTime? primary = null;
        foreach (var token in e.Dates.Where(d => d.Date != DateKind.Weekday))
        {
            DateTime? day = token.Date switch
            {
                DateKind.Relative => now.Date.AddDays(token.Offset),
                DateKind.DayOnly => anchor is { } a ? DayAfter(a, token.Day) : null,
                _ when token.Year > 0 => Make(token.Year, token.Month, token.Day),
                _ when anchor is { } a => Make(a.Year, token.Month, token.Day) is { } same && same < a ? Make(a.Year + 1, token.Month, token.Day) : Make(a.Year, token.Month, token.Day),
                _ => NearestYear(token.Month, token.Day, now.Date)
            };
            if (day is null || primary is { } p && p != day) return false;
            primary = day;
            if (token.Default != Period.None) period = token.Default;
        }
        foreach (var token in e.Dates.Where(d => d.Date == DateKind.Weekday))
        {
            if (primary is { } p) { if (p.DayOfWeek != token.Dow) return false; continue; }
            primary = WeekdayDate(now.Date, token.Dow, token.Week);
        }
        // "11/26" alone may be a fraction or a score; next to a time or a weekday it is a date.
        if (e.Dates.All(d => d.Date == DateKind.Slash && d.Year == 0) && e.Time is null) return false;
        date = primary;
        return true;
    }

    private static DateTime? Make(int year, int month, int day)
        => year is >= 1 and <= 9998 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day) : null;

    // A day of the anchor's month, or of the next month when it is before the anchor ("11月28日至2日").
    private static DateTime? DayAfter(DateTime anchor, int day)
    {
        if (Make(anchor.Year, anchor.Month, day) is { } same && same >= anchor) return same;
        var next = anchor.AddMonths(1);
        return Make(next.Year, next.Month, day);
    }

    private static DateTime? NearestYear(int month, int day, DateTime today)
    {
        DateTime? best = null;
        foreach (int year in new[] { today.Year, today.Year + 1, today.Year - 1 })
        {
            if (Make(year, month, day) is not { } candidate) continue;
            if (best is not { } b || Math.Abs((candidate - today).TotalDays) < Math.Abs((b - today).TotalDays)
                || Math.Abs((candidate - today).TotalDays) == Math.Abs((b - today).TotalDays) && candidate > b) best = candidate;
        }
        return best;
    }

    private static DateTime WeekdayDate(DateTime today, DayOfWeek dow, int week)
    {
        if (week == Nearest) return today.AddDays(((int)dow - (int)today.DayOfWeek + 7) % 7);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return monday.AddDays(7 * week + ((int)dow + 6) % 7);
    }

    // The time's own period word, else the one it takes from a day word ("今晚8点") or the range's start; a 24-hour time
    // ("今晚 23:00") takes none.
    private static Period PeriodFor(Token t, Period fallback) => t.Period != Period.None ? t.Period : t.Literal ? Period.None : fallback;

    // Minutes after the day's midnight; past 24:00 for a late night ("晚上12点", "夜里1点" are the next day's).
    private static int? Minutes(Token t, Period period)
    {
        int h = t.Hour;
        int? hour = period switch
        {
            Period.None => t.Literal || h == 0 || h >= 13 ? (h <= 23 ? h : null) : h <= 6 ? h + 12 : h,
            Period.Dawn => h <= 5 ? h : h == 12 ? 0 : null,
            Period.Morning => h <= 12 ? h : null,
            Period.Am => h is >= 1 and <= 11 ? h : h == 12 ? 0 : null,
            Period.Noon => h is 11 or 12 ? h : h is 1 or 2 ? h + 12 : null,
            Period.Afternoon or Period.Pm => h is >= 1 and <= 11 ? h + 12 : h == 12 ? 12 : null,
            Period.Dusk => h is >= 4 and <= 7 ? h + 12 : null,
            Period.Evening => h is >= 6 and <= 11 ? h + 12 : h == 12 ? 24 : h is >= 1 and <= 4 ? 24 + h : null,
            _ => null
        };
        return hour is { } x && t.Minute is >= 0 and <= 59 ? x * 60 + t.Minute : null;
    }

    // A time range on one day: the end takes the start's period ("下午3点到4点"), the start the end's AM/PM ("3-4pm"); an end
    // not after the start is the afternoon's when it was written without a period ("11点到1点"), else after midnight.
    private static (int Start, int? End)? Range(Token t1, Token t2, Period fallback)
    {
        var p1 = PeriodFor(t1, fallback);
        var p2 = PeriodFor(t2, p1);
        if (Minutes(t2, p2) is not { } end) return Minutes(t1, p1) is { } alone ? (alone, null) : null;
        int? start = Minutes(t1, p1);
        if (p1 == Period.None && t2.Period is Period.Am or Period.Pm && Minutes(t1, t2.Period) is { } shared && shared <= end) start = shared;
        if (start is not { } s) return null;
        if (p2 == Period.None)
        {
            // No period on either side: the end is the nearest reading after the start, as written or in the afternoon
            // ("23:00-1:00" ends at 01:00, "3:00-4:00" at 16:00).
            int written = t2.Hour * 60 + t2.Minute;
            int[] readings = t2.Hour < 12 ? [written, written + 12 * 60] : [written];
            end = readings.Select(r => r <= s ? r + 24 * 60 : r).Min();
        }
        else if (end <= s && t2.Period == Period.None && end < 12 * 60 && end + 12 * 60 > s) end += 12 * 60;
        if (end <= s) end += 24 * 60;
        return end - s > LongestRange.TotalMinutes ? (s, null) : (s, end);
    }

    // ---- Tokens ----

    private static List<Token> Tokens(string s, List<(int Start, int End)> blocked)
    {
        var all = new List<Token>();
        foreach (Match m in CnDate().Matches(s))
            if (Number(m.Groups["mo"].Value) is var mo and > 0 && Number(m.Groups["d"].Value) is var d and > 0)
                all.Add(new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.Absolute, Year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : 0, Month = mo, Day = d });
        foreach (Match m in IsoDate().Matches(s))
            all.Add(new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.Absolute, Year = Int(m, "y"), Month = Int(m, "mo"), Day = Int(m, "d") });
        foreach (Match m in SlashDate().Matches(s))
            all.Add(new Token { Start = m.Index, End = m.Index + m.Length, Date = m.Groups["y"].Success ? DateKind.Absolute : DateKind.Slash, Year = m.Groups["y"].Success ? Int(m, "y") : 0, Month = Int(m, "mo"), Day = Int(m, "d") });
        foreach (Match m in CnDayOnly().Matches(s))
            if (Number(m.Groups["d"].Value) is var d and > 0)
                all.Add(new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.DayOnly, Day = d });
        foreach (Match m in EnMonthDay().Matches(s))
        {
            if (!char.IsUpper(m.Groups["mon"].Value[0])) continue;
            // "Nov 23-25, 2026": the start, then the last day as a range's end (the year is the start's).
            int year = m.Groups["y"].Success ? Int(m, "y") : 0;
            var first = m.Groups["d1"];
            var last = m.Groups["e2"];
            all.Add(new Token { Start = m.Index, End = last.Success ? first.Index + first.Length : m.Index + m.Length, Date = DateKind.Absolute, Year = year, Month = MonthOf(m.Groups["mon"].Value), Day = Int(m, "d") });
            if (last.Success)
                all.Add(new Token { Start = last.Index, End = m.Index + m.Length, Date = DateKind.DayOnly, Day = Int(m, "d2") });
        }
        foreach (Match m in EnDayMonth().Matches(s))
            if (char.IsUpper(m.Groups["mon"].Value[0]))
                all.Add(new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.Absolute, Year = m.Groups["y"].Success ? Int(m, "y") : 0, Month = MonthOf(m.Groups["mon"].Value), Day = Int(m, "d") });
        foreach (Match m in CnRelative().Matches(s)) all.Add(CnDayWord(m));
        foreach (Match m in EnRelative().Matches(s)) all.Add(EnDayWord(m));
        foreach (Match m in CnWeekday().Matches(s))
        {
            string pre = m.Groups["pre"].Value.TrimEnd('个');
            all.Add(new Token
            {
                Start = m.Index, End = m.Index + m.Length, Date = DateKind.Weekday, Dow = (DayOfWeek)("日一二三四五六".IndexOf(m.Groups["d"].Value[0]) is var k and >= 0 ? k : 0),
                Week = pre switch { "下下" => 2, "下" => 1, "本" or "这" => 0, "上" => -1, _ => Nearest }, Recurring = pre == "每"
            });
        }
        foreach (Match m in EnWeekday().Matches(s))
        {
            string name = m.Groups["d"].Value;
            bool full = name.EndsWith("day", StringComparison.OrdinalIgnoreCase);
            // "Sun", "Sat", "Wed", "Mon" are words too: an abbreviation counts only with its capital, and never as a plural.
            if (!full && (!char.IsUpper(name[0]) || m.Groups["pl"].Success)) continue;
            string pre = m.Groups["pre"].Value.ToLowerInvariant();
            all.Add(new Token
            {
                Start = m.Index, End = m.Index + m.Length, Date = DateKind.Weekday, Dow = DayOf(name),
                Week = pre switch { "next" => 1, "last" => -1, _ => Nearest }, Recurring = pre == "every" || m.Groups["pl"].Success
            });
        }
        foreach (Match m in CnClock().Matches(s)) if (CnTime(s, m) is { } t) all.Add(t);
        foreach (Match m in CnHourRange().Matches(s))
        {
            if (Number(m.Groups["h"].Value) is not (var h and >= 0) || ScoreBefore(s, m.Groups["h"].Index)) continue;
            all.Add(new Token { Start = m.Index, End = m.Index + m.Length, IsTime = true, Hour = h, Period = PeriodOf(m.Groups["p"].Value), Literal = h >= 13, Weak = true });
        }
        foreach (Match m in ColonClock().Matches(s))
        {
            // Scores, ratios and durations ("比分 3:20", "用时 12:34").
            if (NotClock().IsMatch(s.AsSpan(Math.Max(0, m.Index - 8), Math.Min(8, m.Index)))) continue;
            int h = Int(m, "h");
            var period = m.Groups["ap"].Success ? AmPm(m.Groups["ap"].Value) : PeriodOf(m.Groups["p"].Value);
            all.Add(new Token
            {
                Start = m.Index, End = m.Index + m.Length, IsTime = true, Hour = h, Minute = Int(m, "m"), Period = period,
                Literal = m.Groups["h"].Value.Length == 2 && m.Groups["h"].Value[0] == '0' || h >= 13, Weak = m.Groups["sec"].Success || Int(m, "m") % 5 != 0
            });
        }
        foreach (Match m in AmPmClock().Matches(s))
        {
            if (m.Groups["sh"].Success)
            {
                var sh = m.Groups["sh"];
                int end = m.Groups["sap"].Success ? m.Groups["sap"].Index + m.Groups["sap"].Length : m.Groups["sm"].Success ? m.Groups["sm"].Index + m.Groups["sm"].Length : sh.Index + sh.Length;
                all.Add(new Token
                {
                    Start = sh.Index, End = end, IsTime = true, Hour = Int(m, "sh"), Minute = m.Groups["sm"].Success ? Int(m, "sm") : 0,
                    Period = m.Groups["sap"].Success ? AmPm(m.Groups["sap"].Value) : Period.None, Weak = true
                });
            }
            var hg = m.Groups["h"];
            all.Add(new Token { Start = hg.Index, End = m.Index + m.Length, IsTime = true, Hour = Int(m, "h"), Minute = m.Groups["m"].Success ? Int(m, "m") : 0, Period = AmPm(m.Groups["ap"].Value) });
        }
        foreach (Match m in Noon().Matches(s)) all.Add(new Token { Start = m.Index, End = m.Index + m.Length, IsTime = true, Hour = 12, Literal = true });

        // The earliest, then the longest, wins where tokens overlap.
        all.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : (y.End - y.Start).CompareTo(x.End - x.Start));
        var kept = new List<Token>();
        foreach (var token in all)
        {
            if (kept.Count > 0 && token.Start < kept[^1].End)
            {
                // "今晚上8点", "明早上9点": the period word is read twice; the time keeps what is left of it.
                if (!token.IsTime || kept[^1].IsTime || kept[^1].Date != DateKind.Relative || token.End <= kept[^1].End || token.Start < kept[^1].Start) continue;
                token.Start = kept[^1].End;
            }
            if (blocked.Any(b => token.Start < b.End && b.Start < token.End) || InWord(s, token.Start, token.End)) continue;
            kept.Add(token);
        }
        return kept;
    }

    // Glued to a file name, a path, an id or a version: the ASCII characters right beside the token say so.
    private static bool InWord(string s, int start, int end)
    {
        int left = start;
        while (left > 0 && InRun(s[left - 1])) left--;
        int right = end;
        while (right < s.Length && InRun(s[right])) right++;
        var before = s.AsSpan(left, start - left);
        var after = s.AsSpan(end, right - end);
        // "+" also marks a time zone's offset ("UTC+08:00").
        if (before.IndexOfAny("/\\_@#=&%+") >= 0 || after.IndexOfAny("/\\_@#=&%") >= 0) return true;
        if (before.Length > 0 && char.IsAsciiLetterOrDigit(before[^1])) return true;
        if (after.Length > 1 && after[0] == '.' && char.IsAsciiLetterOrDigit(after[1])) return true;
        if (before.Length > 1 && before[^1] == '.' && char.IsAsciiLetterOrDigit(before[^2])) return true;
        return false;
    }

    // What a file name, a path, an address or an id is made of; spaces, brackets, quotes and commas end it.
    private static bool InRun(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '\\' or '_' or '-' or ':' or '@' or '#' or '=' or '&' or '%' or '+' or '~';

    private static Token CnDayWord(Match m)
    {
        string w = m.Groups["w"].Value;
        var token = new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.Relative };
        token.Offset = w[0] switch
        {
            '大' => w[1] == '后' ? 3 : -3,
            '后' => 2,
            '明' => 1,
            '昨' => -1,
            '前' => -2,
            _ => 0
        };
        token.Default = w.Length > 1 && w[1] is '晚' or '夜' ? Period.Evening : w.Length > 1 && w[1] is '早' or '晨' ? Period.Morning : Period.None;
        token.Recurring = w[0] is '每' or '天';
        return token;
    }

    private static Token EnDayWord(Match m)
    {
        string w = m.Groups["w"].Value.ToLowerInvariant();
        var token = new Token { Start = m.Index, End = m.Index + m.Length, Date = DateKind.Relative };
        token.Offset = w switch { "tomorrow" or "tmrw" => 1, "yesterday" or "last" => -1, _ => 0 };
        token.Recurring = w.StartsWith("every", StringComparison.Ordinal) || w == "daily";
        string p = m.Groups["p"].Value.ToLowerInvariant();
        token.Default = w == "tonight" || p is "evening" or "night" ? Period.Evening : p == "morning" ? Period.Morning : p == "afternoon" ? Period.Afternoon : Period.None;
        return token;
    }

    private static Token? CnTime(string s, Match m)
    {
        var hg = m.Groups["h"];
        int hour = Number(hg.Value);
        if (hour < 0) return null;
        var period = PeriodOf(m.Groups["p"].Value);
        int minute = m.Groups["half"].Success ? 30 : m.Groups["q"].Success ? (m.Groups["q"].Value == "一刻" ? 15 : 45) : m.Groups["m"].Success ? Number(m.Groups["m"].Value) : 0;
        if (minute < 0) return null;
        int end = m.Index + m.Length;
        char next = end < s.Length ? s[end] : '\n';
        // "1点30分钟" is a length of time.
        if (m.Groups["mu"].Success && next == '钟') return null;
        bool bare = !m.Groups["half"].Success && !m.Groups["q"].Success && !m.Groups["m"].Success && !m.Groups["z"].Success;
        bool weak = false;
        if (period == Period.None)
        {
            if (ScoreBefore(s, hg.Index)) return null;
            // "一点" is also "a little" ("早一点到", "差一点").
            if (bare && hg.Value == "一") return null;
            bool letter = char.IsLetterOrDigit(next);
            if (letter && !FollowsClock.Contains(next)) return null;
            // "改到4点", "定在3点": the words before say it is a time.
            weak = bare && !letter && !ClockBefore().IsMatch(s.AsSpan(Math.Max(0, m.Index - 4), Math.Min(4, m.Index)));
        }
        return new Token { Start = m.Index, End = end, IsTime = true, Hour = hour, Minute = minute, Period = period, Literal = hour >= 13 || hour == 0, Weak = weak };
    }

    // Words that may follow a clock time written with "点" without a period word: "3点开会", "3点前", "3点的会", "3点到5点".
    private const string FollowsClock = "前后开召举见到至去来出集截准在的和与或起始碰聚面吃回结散完上下";

    // A number of points, not a time: "上涨5点", "第3点", "积分 5 点", "共3点".
    private static bool ScoreBefore(string s, int at)
    {
        int i = at;
        while (i > 0 && s[i - 1] == ' ') i--;
        if (i == 0) return false;
        char c = s[i - 1];
        if ("涨跌升降扣第共赞增减".Contains(c)) return true;
        if (c == '加') return !(i >= 2 && s[i - 2] == '参');
        if (i >= 2 && s.AsSpan(i - 2, 2) is "积分" or "得分" or "评分" or "分数" or "比分") return true;
        return false;
    }

    private static Period PeriodOf(string word) => word switch
    {
        "" => Period.None,
        "凌晨" => Period.Dawn,
        "清晨" or "早上" or "早晨" or "上午" => Period.Morning,
        "中午" => Period.Noon,
        "午后" or "下午" => Period.Afternoon,
        "傍晚" => Period.Dusk,
        _ => Period.Evening
    };

    private static Period AmPm(string word) => word.TrimStart()[0] is 'a' or 'A' ? Period.Am : Period.Pm;

    private static int MonthOf(string name) => name.ToLowerInvariant()[..3] switch
    {
        "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6, "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, _ => 12
    };

    private static DayOfWeek DayOf(string name) => name.ToLowerInvariant()[..2] switch
    {
        "mo" => DayOfWeek.Monday, "tu" => DayOfWeek.Tuesday, "we" => DayOfWeek.Wednesday, "th" => DayOfWeek.Thursday,
        "fr" => DayOfWeek.Friday, "sa" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday
    };

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    // Arabic digits, or a Chinese number up to 99 ("十", "十二", "二十六", "两", "零五"); -1 for anything else.
    private static int Number(string text)
    {
        if (text.Length == 0) return -1;
        if (text.All(char.IsAsciiDigit)) return int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        static int Digit(char c) => c switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1 };
        if (text[0] is '零' or '〇' && text.Length == 2) return Digit(text[1]) is var z and > 0 ? z : -1;
        int ten = text.IndexOf('十');
        if (ten < 0) return text.Length == 1 ? Digit(text[0]) : -1;
        if (text.IndexOf('十', ten + 1) >= 0 || text.Length > 3) return -1;
        int tens = ten == 0 ? 1 : ten == 1 && Digit(text[0]) is var t and > 1 && text[0] != '两' ? t : -1;
        int ones = ten == text.Length - 1 ? 0 : ten == text.Length - 2 && Digit(text[^1]) is var o and > 0 ? o : -1;
        return tens < 0 || ones < 0 ? -1 : tens * 10 + ones;
    }

    // ---- Patterns (run on the normalised text: full-width digits and colons are ordinary ones) ----

    private const string Num = @"(?:\d{1,2}|[零〇一二两三四五六七八九十]{1,3})";
    private const string CnPeriod = @"凌晨|清晨|早上|早晨|上午|中午|午后|下午|傍晚|晚上|晚间|夜里|夜间|深夜|半夜";
    private const string Month = @"(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t|tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Links and e-mail addresses: nothing in them is read.
    [GeneratedRegex(@"(?:https?://|www\.)[^\s，。；！？、“”（）<>]+|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Options)]
    private static partial Regex Address();

    // "11月26日", "11 月 26 号", "2026年11月26日", "十一月二十六日".
    [GeneratedRegex(@"(?<![\d.])(?:(?<y>\d{4})\s*年\s*)?(?<mo>" + Num + @")\s*月\s*(?<d>" + Num + @")\s*[日号](?![楼线室房门口位床桌厅馆柜台箱])", Options)]
    private static partial Regex CnDate();

    // "2026-11-26", "2026/11/26", "2026.11.26".
    [GeneratedRegex(@"(?<![0-9A-Za-z_./\\-])(?<y>20\d{2})(?<sep>[-/.])(?<mo>\d{1,2})\k<sep>(?<d>\d{1,2})(?![0-9A-Za-z_/\\-]|\.\d)", Options)]
    private static partial Regex IsoDate();

    // "11/26", "11/26/2026".
    [GeneratedRegex(@"(?<![0-9A-Za-z_./\\:-])(?<mo>1[0-2]|0?[1-9])/(?<d>3[01]|[12]\d|0?[1-9])(?:/(?<y>20\d{2}))?(?![0-9A-Za-z_/\\-]|\.\d)", Options)]
    private static partial Regex SlashDate();

    // "25日", "25号": only the end of a range ("11月23日至25日").
    [GeneratedRegex(@"(?<![\d.月])(?<d>" + Num + @")\s*[日号](?![楼线室房门口位床桌厅馆柜台箱])", Options)]
    private static partial Regex CnDayOnly();

    // "Nov 26", "November 26th", "Nov 26, 2026", "Nov 23-25".
    [GeneratedRegex(@"(?<![A-Za-z])" + Month + @"\.?\s+(?<d1>(?<d>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?)(?![0-9A-Za-z]|:\d)(?:\s*[-–~]\s*(?<e2>(?<d2>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?)(?![0-9A-Za-z]|:\d))?(?:,?\s+(?<y>20\d{2})(?![0-9:]))?", Options)]
    private static partial Regex EnMonthDay();

    // "26 Nov", "26th November 2026".
    [GeneratedRegex(@"(?<![0-9A-Za-z.:/])(?<d>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?\s+(?:of\s+)?" + Month + @"\.?(?![A-Za-z])(?:,?\s+(?<y>20\d{2})(?![0-9:]))?", Options)]
    private static partial Regex EnDayMonth();

    // Day words, and every day ("每天"), which makes what follows recurring.
    [GeneratedRegex(@"(?<![说如聪证然最])(?<w>大后天|后天|明天|明日|今天|今日|大前天|前天|昨天|昨日|今晚上?|明晚上?|昨晚上?|今早上?|明早上?|今晨|明晨|今夜|明夜|每天|每日|每晚|每早|天天)", Options)]
    private static partial Regex CnRelative();

    [GeneratedRegex(@"(?<![A-Za-z])(?:(?<w>today|tonight|tomorrow|tmrw|yesterday|every\s+day|daily)(?:\s+(?<p>morning|afternoon|evening|night))?|(?<w>this|last)\s+(?<p>morning|afternoon|evening|night))(?![A-Za-z])", Options)]
    private static partial Regex EnRelative();

    // "周四", "星期四", "礼拜天", "本周四", "下周四", "下下周四", "上周四", "每周四"; not "一周三次".
    [GeneratedRegex(@"(?<![零〇一二两三四五六七八九十几上下本这个每\d])(?<pre>下下个?|下个?|本|这个?|上个?|每个?)?(?:周|星期|礼拜)(?<d>[一二三四五六日天])(?![次遍回])", Options)]
    private static partial Regex CnWeekday();

    [GeneratedRegex(@"(?<![A-Za-z])(?:(?<pre>this|next|last|every|on)\s+)?(?<d>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues|tue|wed|thurs|thur|thu|fri|sat|sun)(?<pl>s)?\.?(?![A-Za-z])", Options)]
    private static partial Regex EnWeekday();

    // "下午3点", "三点半", "3点一刻", "15点30分", "15时30分", "凌晨2点", "3点钟".
    [GeneratedRegex(@"(?:(?<p>" + CnPeriod + @")\s*)?(?<![\d.A-Za-z])(?<!\d:)(?<h>" + Num + @")\s*[点时](?:\s*(?<half>半)|\s*(?<q>一刻|三刻)|\s*(?<m>\d{1,2})(?!\d)\s*(?<mu>分)?|\s*(?<m>[零〇一二三四五六七八九十]{1,3})\s*(?<mu>分)|\s*(?<z>钟|整))?", Options)]
    private static partial Regex CnClock();

    // The start of "3-4点", "下午三到四点".
    [GeneratedRegex(@"(?:(?<p>" + CnPeriod + @")\s*)?(?<![\d.A-Za-z])(?<!\d:)(?<h>" + Num + @")(?=\s*[-~到至]\s*" + Num + @"\s*[点时])", Options)]
    private static partial Regex CnHourRange();

    // "15:00", "下午3:00", "3:30 PM", "15:00:00".
    [GeneratedRegex(@"(?:(?<p>" + CnPeriod + @")\s*)?(?<![0-9A-Za-z.])(?<!\d:)(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)(?<sec>:[0-5]\d)?(?!\d|:\d)(?:\s*(?<ap>[ap]\.?\s?m\.?))?(?![A-Za-z])", Options)]
    private static partial Regex ColonClock();

    // "3pm", "3 PM", "3-4pm", "3:00 – 4:30 p.m." is read by ColonClock.
    [GeneratedRegex(@"(?:(?<![0-9A-Za-z.:])(?<sh>1[0-2]|0?[1-9])(?::(?<sm>[0-5]\d))?\s*(?<sap>[ap]\.?m\.?)?\s*[-–~]\s*)?(?<![0-9A-Za-z.:])(?<h>1[0-2]|0?[1-9])(?::(?<m>[0-5]\d))?\s*(?<ap>[ap]\.?\s?m\.?)(?![A-Za-z])", Options)]
    private static partial Regex AmPmClock();

    [GeneratedRegex(@"(?<![A-Za-z])(?:12\s*)?noon(?![A-Za-z])", Options)]
    private static partial Regex Noon();

    [GeneratedRegex(@"[验驗][证證][码碼]|校[验驗][码碼]|动态[码密]|verification\s+code|security\s+code|one[-\s]?time\s+(?:code|password|passcode)|(?<![A-Za-z])otp(?![A-Za-z])", Options)]
    private static partial Regex CodeWords();

    [GeneratedRegex(@"有效期|有效至|过期|失效|到期|valid\s+(?:until|through|till)|expir(?:es|y|ing)|expiration", Options)]
    private static partial Regex ExpiryBefore();

    [GeneratedRegex(@"^\s*(?:前|之前|以前|内)?\s*(?:有效|过期|失效|到期)", Options)]
    private static partial Regex ExpiryAfter();

    [GeneratedRegex(@"(?:改到|改为|改成|改在|推迟到|推到|延到|延后到|延期到|提前到|挪到|定在|定于|约在|安排在)\s*$", Options)]
    private static partial Regex ClockBefore();

    [GeneratedRegex(@"^\s*(?:的[^\s，。,.]{0,3}?)?(?:改到|改为|改成|改在|推迟到|推迟至|延后到|延期到|延期至|提前到|调整到|调整为|挪到|换到|变更为)|^\W{0,3}(?:[A-Za-z]+\s+){0,2}?(?:has\s+been\s+|is\s+|was\s+)?(?:moved|rescheduled|changed|postponed|pushed)\s+(?:to|until)(?![A-Za-z])", Options)]
    private static partial Regex SupersededAfter();

    // Just after a time: a date it did not read ("12.01", "12-01").
    [GeneratedRegex(@"^\s*\d{1,4}[./-]\d", Options)]
    private static partial Regex NumberAfter();

    // Just before "3:20": a score, a ratio or a length of time.
    [GeneratedRegex(@"比分|比例|用时|耗时|时长|历时|共计|剩余|还剩|倒计时|进度|使用|可用|续航|播放|score|ratio|duration|elapsed|took|length|vs\.?", Options)]
    private static partial Regex NotClock();
}
