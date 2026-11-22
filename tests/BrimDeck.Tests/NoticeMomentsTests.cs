using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Dates and times read from a notice's words (NoticeMoments): every kind of writing in Chinese and English, what must not be
// read, ranges, days that cross midnight or a year, repeats and the limit, and the words shown. Fixed clocks and a made-up zone.
internal static class NoticeMomentsTests
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.CreateCustomTimeZone("Test/China", TimeSpan.FromHours(8), "Test China", "Test China");
    // 2026-11-22 14:18 in UTC+8, a Sunday: the design's example.
    private static readonly DateTimeOffset Sunday = At(2026, 11, 22, 14, 18);

    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(8));
    private static DateTime T(int mo, int d, int h = 0, int mi = 0, int y = 2026) => new(y, mo, d, h, mi, 0);

    private static IReadOnlyList<NoticeMoment> Find(string body, DateTimeOffset? at = null) => NoticeMoments.Find("", body, at ?? Sunday, China);

    // Exactly one moment with this start (and end, all-day).
    private static bool One(string body, DateTime start, DateTime? end = null, bool allDay = false, DateTimeOffset? at = null)
        => Find(body, at) is [var m] && m.Start == start && m.End == end && m.AllDay == allDay;

    private static void All(Action<string, bool> check, string name, (string Text, DateTime Start, DateTime? End)[] cases, DateTimeOffset? at = null, bool allDay = false)
    {
        var wrong = cases.Where(c => !One(c.Text, c.Start, c.End, allDay, at)).Select(c => c.Text).ToList();
        if (wrong.Count > 0) Console.WriteLine("  not read as expected: " + string.Join(" | ", wrong));
        check(name, wrong.Count == 0);
    }

    private static void None(Action<string, bool> check, string name, string[] texts, DateTimeOffset? at = null)
    {
        var wrong = texts.Where(text => Find(text, at).Count > 0).ToList();
        if (wrong.Count > 0) Console.WriteLine("  read though it must not be: " + string.Join(" | ", wrong));
        check(name, wrong.Count == 0);
    }

    public static void Run(Action<string, bool> check)
    {
        string language = Loc.Language;
        Loc.Use(Loc.Chinese);
        try
        {
            Design(check);
            DayWords(check);
            Weekdays(check);
            Dates(check);
            Times(check);
            Ranges(check);
            Undated(check);
            Midnight(check);
            Years(check);
            Rejected(check);
            Context(check);
            Repeats(check);
            Long(check);
            Words(check);
        }
        finally { Loc.Use(language); }
    }

    private static void Design(Action<string, bool> check)
    {
        var found = NoticeMoments.Find("周会改到周四下午三点", "王敏：会议室改到 5 楼，请确认能否参加。", Sunday, China);
        check("The design's example is Thursday 15:00, from the title, without an end", found is [var m] && m.Start == T(11, 26, 15) && m.End is null && !m.AllDay
            && m.InTitle && m.Text == "周四下午三点" && m.Index == 4 && m.Length == 6);
        check("The design's example reads as drawn", found is [var d] && NoticeMoments.Label(d, Sunday.Date) == "11月26日 周四 15:00"
            && NoticeMoments.Detail(d) == "从标题认出 · 时长按 1 小时" && NoticeMoments.Brief(d, Sunday.Date) == "周四 15:00");
        // The narrow board: a time in the title and a date in the body.
        var two = NoticeMoments.Find("【重要】评审改到周四 15:00", "各位好：原定 11 月 25 日的评审改期。", Sunday, China);
        check("A time in the title and a date in the body are both offered, the title's first", two is [var a, var b] && a.InTitle && a.Start == T(11, 26, 15)
            && !b.InTitle && b.AllDay && b.Start == T(11, 25) && b.Text == "11 月 25 日");
    }

    private static void DayWords(Action<string, bool> check)
    {
        All(check, "Chinese day words with a time",
        [
            ("今天下午4点开会", T(11, 22, 16), null), ("明天上午10点", T(11, 23, 10), null), ("后天15:00见", T(11, 24, 15), null),
            ("大后天 9:30 出发", T(11, 25, 9, 30), null), ("今晚8点聚餐", T(11, 22, 20), null), ("今晚上8点聚餐", T(11, 22, 20), null),
            ("明早8点集合", T(11, 23, 8), null), ("明晚七点半", T(11, 23, 19, 30), null), ("明天中午12点", T(11, 23, 12), null)
        ]);
        All(check, "English day words with a time",
        [
            ("Call today at 4pm", T(11, 22, 16), null), ("Lunch tomorrow at 12:30 PM", T(11, 23, 12, 30), null),
            ("Dinner tonight at 7:30", T(11, 22, 19, 30), null), ("Sync 3pm tomorrow", T(11, 23, 15), null), ("noon tomorrow", T(11, 23, 12), null)
        ]);
        None(check, "A day word alone is not offered", ["今天的日报已生成", "明天见", "后天再说", "See you tomorrow", "Today's digest"]);
    }

    private static void Weekdays(Action<string, bool> check)
    {
        All(check, "Chinese weekdays from a Sunday",
        [
            ("周四下午三点", T(11, 26, 15), null), ("星期四 15:00", T(11, 26, 15), null), ("礼拜四 15:00", T(11, 26, 15), null),
            ("下周四 15:00", T(11, 26, 15), null), ("下下周四 15:00", T(12, 3, 15), null), ("这周日 15:00", T(11, 22, 15), null),
            ("周日下午3点", T(11, 22, 15), null), ("星期天晚上8点", T(11, 22, 20), null)
        ]);
        All(check, "English weekdays from a Sunday",
        [
            ("Thursday 3pm", T(11, 26, 15), null), ("Thu 3 PM", T(11, 26, 15), null), ("this Friday at 10am", T(11, 27, 10), null),
            ("next Thursday at 3:30 p.m.", T(11, 26, 15, 30), null), ("on Thursday at 3pm", T(11, 26, 15), null)
        ]);
        var wednesday = At(2026, 11, 25, 10, 0);
        All(check, "Weekdays from a Wednesday: the nearest, this week's and next week's",
        [
            ("周四 15:00", T(11, 26, 15), null), ("本周四 15:00", T(11, 26, 15), null), ("这周四 15:00", T(11, 26, 15), null),
            ("下周四 15:00", T(12, 3, 15), null), ("周三 11:00", T(11, 25, 11), null), ("next Thursday 3pm", T(12, 3, 15), null),
            ("周二 15:00", T(12, 1, 15), null)
        ], wednesday);
        check("A weekday alone is a whole day", One("周四见", T(11, 26), allDay: true) && One("See you Thursday", T(11, 26), allDay: true));
        None(check, "This week's day already gone, last week's and today's past hour are not offered", ["本周四 15:00", "上周四 15:00", "last Thursday at 3pm"]);
        None(check, "Today's weekday at an hour already over is not offered", ["周三 9:00"], wednesday);
    }

    private static void Dates(Action<string, bool> check)
    {
        All(check, "Written dates with a time",
        [
            ("11月26日 15:00", T(11, 26, 15), null), ("11月26号下午3点", T(11, 26, 15), null), ("2026年11月26日 15:00", T(11, 26, 15), null),
            ("11 月 26 日 15:00", T(11, 26, 15), null), ("十一月二十六日下午三点", T(11, 26, 15), null), ("11/26 15:00", T(11, 26, 15), null),
            ("2026-11-26 15:00", T(11, 26, 15), null), ("2026/11/26 15:00", T(11, 26, 15), null), ("2026.11.26 15:00", T(11, 26, 15), null),
            ("11月26日（周四）15:00", T(11, 26, 15), null), ("周四（11月26日）下午三点", T(11, 26, 15), null), ("11月26日的下午3点", T(11, 26, 15), null),
            ("Nov 26 at 3pm", T(11, 26, 15), null), ("November 26th, 3:00 PM", T(11, 26, 15), null), ("26 Nov 15:00", T(11, 26, 15), null),
            ("Nov 26, 2026 3pm", T(11, 26, 15), null), ("Thursday, November 26, 2026 at 3:00 PM", T(11, 26, 15), null),
            ("11/26/2026 3:00 PM", T(11, 26, 15), null), ("Thu 11/26 3pm", T(11, 26, 15), null), ("Nov. 26 3pm", T(11, 26, 15), null)
        ]);
        All(check, "A written date alone is a whole day",
        [
            ("11月26日", T(11, 26), null), ("请于11月30日前提交", T(11, 30), null), ("2026-11-26", T(11, 26), null), ("Nov 26", T(11, 26), null),
            ("on 26 November 2026", T(11, 26), null), ("11/26 周四", T(11, 26), null)
        ], allDay: true);
        None(check, "A bare 11/26, a date and a weekday that disagree, and impossible dates are not offered",
            ["11/26", "完成了 1/2", "比分 3/2", "11月26日 周五 15:00", "2月30日 15:00", "13月1日", "Nov 31 3pm"]);
    }

    private static void Times(Action<string, bool> check)
    {
        All(check, "Chinese times on the day the notice arrived (14:18)",
        [
            ("15:00开会", T(11, 22, 15), null), ("15：00开会", T(11, 22, 15), null), ("下午3点", T(11, 22, 15), null), ("下午三点", T(11, 22, 15), null),
            ("三点半开会", T(11, 22, 15, 30), null), ("3点一刻开会", T(11, 22, 15, 15), null), ("三点三刻到", T(11, 22, 15, 45), null),
            ("15点30分", T(11, 22, 15, 30), null), ("15时30分", T(11, 22, 15, 30), null), ("晚上8点", T(11, 22, 20), null),
            ("3点05分开始", T(11, 22, 15, 5), null), ("三点二十分出发", T(11, 22, 15, 20), null), ("四点钟", T(11, 22, 16), null),
            ("两点半", T(11, 22, 14, 30), null), ("下午两点半", T(11, 22, 14, 30), null), ("傍晚6点", T(11, 22, 18), null), ("时间：15:00", T(11, 22, 15), null)
        ]);
        All(check, "English times",
        [
            ("3pm", T(11, 22, 15), null), ("at 3 PM", T(11, 22, 15), null), ("3:30 p.m.", T(11, 22, 15, 30), null), ("3:30pm", T(11, 22, 15, 30), null),
            ("7:45 PM", T(11, 22, 19, 45), null), ("Are you free at 4pm?", T(11, 22, 16), null)
        ]);
        All(check, "Late-night and early-morning period words",
        [
            ("凌晨2点", T(11, 23, 2), null), ("明天凌晨2点", T(11, 23, 2), null), ("周四晚上12点", T(11, 27, 0), null), ("周四夜里1点", T(11, 27, 1), null),
            ("周四中午1点", T(11, 26, 13), null), ("周四上午12点", T(11, 26, 12), null), ("Thursday 12am", T(11, 26, 0), null), ("Thursday 12pm", T(11, 26, 12), null)
        ]);
        All(check, "An hour without a period: 1 to 6 are the afternoon, 7 to 12 the morning, a leading zero as written",
        [
            ("周四 3:00", T(11, 26, 15), null), ("周四 6点开会", T(11, 26, 18), null), ("周四 7点开会", T(11, 26, 7), null),
            ("周四 11:30", T(11, 26, 11, 30), null), ("周四 12:00", T(11, 26, 12), null), ("周四 03:00", T(11, 26, 3), null), ("周四 0:30", T(11, 26, 0, 30), null)
        ]);
        None(check, "Impossible clock readings are not offered", ["下午13点", "下午13:00", "25:00", "周四 24点", "12:60"]);
    }

    private static void Ranges(Action<string, bool> check)
    {
        All(check, "Time ranges on one day",
        [
            ("15:00–16:00", T(11, 22, 15), T(11, 22, 16)), ("15:00-16:00", T(11, 22, 15), T(11, 22, 16)), ("周四下午三点到四点", T(11, 26, 15), T(11, 26, 16)),
            ("周四下午3-4点", T(11, 26, 15), T(11, 26, 16)), ("周四下午三到四点", T(11, 26, 15), T(11, 26, 16)), ("周四 3:00 - 4:00 PM", T(11, 26, 15), T(11, 26, 16)),
            ("Thursday 3-4pm", T(11, 26, 15), T(11, 26, 16)), ("Thursday 11am-1pm", T(11, 26, 11), T(11, 26, 13)), ("Thursday 3pm to 4:30pm", T(11, 26, 15), T(11, 26, 16, 30)),
            ("周四上午11点到1点", T(11, 26, 11), T(11, 26, 13)), ("周四 9:00~10:30", T(11, 26, 9), T(11, 26, 10, 30)), ("周四 11:00-1:00", T(11, 26, 11), T(11, 26, 13)),
            ("周四 3点至5点", T(11, 26, 15), T(11, 26, 17)), ("周四晚上10点到12点", T(11, 26, 22), T(11, 27, 0))
        ]);
        All(check, "Ranges across midnight and across days",
        [
            ("周五 22:00-02:00", T(11, 27, 22), T(11, 28, 2)), ("周五 23:00-1:00", T(11, 27, 23), T(11, 28, 1)),
            ("11月23日 20:00 – 11月25日 12:00", T(11, 23, 20), T(11, 25, 12)), ("Nov 23 8pm - Nov 25 noon", T(11, 23, 20), T(11, 25, 12))
        ]);
        All(check, "Days from one date to another are whole days",
        [
            ("11月23日至25日", T(11, 23), T(11, 25)), ("11月23日-25日", T(11, 23), T(11, 25)), ("11月28日至12月2日", T(11, 28), T(12, 2)),
            ("11月28日至2日", T(11, 28), T(12, 2)), ("Nov 23-25", T(11, 23), T(11, 25)), ("Nov 23 – Nov 25", T(11, 23), T(11, 25)), ("Nov 23-25, 2026", T(11, 23), T(11, 25))
        ], allDay: true);
        check("A range longer than a working day keeps only its start", One("周四 15:00-14:00", T(11, 26, 15)) && One("周四 7:00-23:30", T(11, 26, 7)));
        check("An end before the start or too far away keeps only the start", One("11月25日 12:00 – 11月23日 20:00", T(11, 25, 12)) && One("11月23日至2027年3月1日", T(11, 23), allDay: true));
        check("A range still running is offered; a start alone more than 10 minutes past is not", One("14:00-15:00 的会议已开始", T(11, 22, 14), T(11, 22, 15))
            && Find("14:00 的会议开始了").Count == 0 && One("14:10 开始", T(11, 22, 14, 10)));
        None(check, "A range already over is not offered", ["11:00-12:00", "11月20日至21日"]);
    }

    private static void Undated(Action<string, bool> check)
    {
        check("A time not yet reached is today's", One("15:00开会", T(11, 22, 15)));
        None(check, "A time a few hours past is past", ["10:00的会议纪要已发出", "上午9点的会已结束"]);
        var lateNight = At(2026, 11, 22, 23, 30);
        check("A time that was more than 12 hours ago is tomorrow's", One("早上8点集合", T(11, 23, 8), at: lateNight) && One("9:00-10:00 例会", T(11, 23, 9), T(11, 23, 10), at: lateNight));
        check("Past but within 12 hours is dropped, not moved", Find("12:00 的会", lateNight).Count == 0);
    }

    private static void Midnight(Action<string, bool> check)
    {
        var beforeMidnight = At(2026, 11, 22, 23, 50);
        var afterMidnight = At(2026, 11, 23, 0, 5);
        check("Just before midnight, 明天 is the next day", One("明天9点", T(11, 23, 9), at: beforeMidnight) && One("tomorrow 9am", T(11, 23, 9), at: beforeMidnight));
        check("Just before midnight, 00:30 is the coming night's", One("00:30 上线", T(11, 23, 0, 30), at: beforeMidnight));
        check("Just after midnight, 明天 is the day after", One("明天 9:00", T(11, 24, 9), at: afterMidnight) && One("今晚11点", T(11, 23, 23), at: afterMidnight)
            && One("23:30", T(11, 23, 23, 30), at: afterMidnight));
        check("Just after midnight, 周一 is today and 昨天 is past", One("周一 15:00", T(11, 23, 15), at: afterMidnight) && Find("昨天下午3点", afterMidnight).Count == 0);
        check("The zone decides the day: the same moment is Monday in UTC+8", NoticeMoments.Find("", "今天下午3点", new DateTimeOffset(2026, 11, 22, 16, 30, 0, TimeSpan.Zero), China) is [var m]
            && m.Start == T(11, 23, 15));
    }

    private static void Years(Action<string, bool> check)
    {
        var december = At(2026, 12, 28, 10, 0);
        check("A January date seen in December is next year's", One("1月5日 10:00", T(1, 5, 10, y: 2027), at: december) && One("Jan 5 10am", T(1, 5, 10, y: 2027), at: december));
        check("Day words and weekdays cross the year", One("周五上午10点", T(1, 1, 10, y: 2027), at: december) && One("下周一 9:00", T(1, 4, 9, y: 2027), at: december)
            && One("大后天 9:00", T(12, 31, 9), at: december));
        check("A range across the year", One("12月30日至1月2日", T(12, 30), T(1, 2, y: 2027), allDay: true, at: december));
        check("A December date seen in January is last year's, so past", Find("12月31日 15:00", At(2027, 1, 2, 10, 0)).Count == 0);
        check("A date just past is this year's and dropped, not pushed to next year", Find("11月20日的会议纪要").Count == 0 && Find("Nov 20 recap").Count == 0
            && Find("9月1日 10:00").Count == 0);
        check("The nearest year wins", One("3月1日", T(3, 1, y: 2027), allDay: true));
        None(check, "An earlier year written out is past; years far ahead are not offered", ["2025年11月26日 15:00", "2025-11-26", "2030年1月1日"]);
        check("A written later year is kept", One("2027年1月5日 10:00", T(1, 5, 10, y: 2027)));
    }

    private static void Rejected(Action<string, bool> check)
    {
        None(check, "Scores, ratios and versions", ["比分 3:2", "比分 2:15", "最终比分 21:19", "比例 1:1", "16:9 的屏幕", "Final score 3:20", "版本 1.2.3 发布", "BrimDeck 1.15.30 已发布",
            "v2026.11.30 已发布", "升级到 2.15"]);
        None(check, "Amounts, counts, order and phone numbers", ["金额 ¥15.30", "共 3 件，合计 120 元", "订单号 2026112615001234", "订单 2026-11-26-0012 已发货",
            "电话 010-12345678", "手机 13800138000", "Room 1530", "剩余 15:30 的额度"]);
        None(check, "Verification codes, with their times", ["您的验证码是 153000，15:30 前有效", "【某某】验证码：482913，10分钟内有效", "Your verification code is 834921. It expires at 3pm."]);
        None(check, "When something expires", ["有效期至11月30日", "优惠券 11月30日 23:59 前有效", "会员将于11月30日到期", "Your trial expires on Nov 30", "Valid until Nov 30, 2026"]);
        None(check, "点 that is not a time", ["收到5点赞", "我有3点建议", "两点意见", "上证指数上涨5点", "第3点需要修改", "就差一点", "早一点到", "积分加5点到账",
            "3点多", "这两点。", "我说三点：", "3点半小时"]);
        None(check, "Lengths of time", ["会议时长30分钟", "预计2小时", "用时 12:34", "倒计时 05:00", "1点30分钟", "视频时长 1:02:03", "took 12:34"]);
        None(check, "Links, file names, paths and addresses", ["https://example.com/2026-11-26/15:00", "见 report_2026-11-26.pdf", "纪要2026-11-26.docx",
            "D:\\会议\\2026-11-26\\纪要.docx", "IMG_20261126_1530.jpg", "www.example.com/11/26", "mail 2026-11-26@example.com", "build 15:00.log"]);
        None(check, "Past dates and times", ["11月20日的会议纪要", "昨天下午3点的会", "上周四 15:00", "前天 9:00", "yesterday at 3pm", "last night at 9pm", "2025年11月26日"]);
        None(check, "Recurring times", ["每周四下午三点例会", "每天9点打卡", "周一至周五 9:00-18:00", "every Thursday at 3pm", "Mondays at 9am", "daily at 9am", "每晚8点", "一周三次"]);
        None(check, "Words that look like days", ["We sat down", "the sun is out", "you may 5 times", "wed in June", "然后天黑了", "说明天数"]);
    }

    // Taken from notices as they read: words around a moment that change what it means.
    private static void Context(Action<string, bool> check)
    {
        All(check, "A time alone takes the date written before it on its line; the time something was moved from is not offered",
        [
            ("王五：今天3点的会改到4点", T(11, 22, 16), null), ("明天的会推迟到4点", T(11, 23, 16), null), ("周四的会改到3点", T(11, 26, 15), null),
            ("周会从周四改到周五下午三点", T(11, 27, 15), null), ("The review on Nov 26 moved to Nov 27 at 3pm", T(11, 27, 15), null),
            ("11月26日的评审，15:00 开始", T(11, 26, 15), null), ("会议定在3点", T(11, 22, 15), null), ("11月20日的纪要\n3点开会", T(11, 22, 15), null)
        ]);
        None(check, "A time alone after a past date is past", ["11月20日的纪要已发，3点开会", "昨天的会 3点开始"]);
        All(check, "Meeting notices as they are written",
        [
            ("时间：周四（11/26）14:00-15:30 地点：3楼大会议室", T(11, 26, 14), T(11, 26, 15, 30)),
            ("【会议提醒】项目周会\n时间：11月26日 15:00-16:00\n地点：5楼", T(11, 26, 15), T(11, 26, 16)),
            ("邀请: 周会 @ 2026年11月26日 周四 15:00 - 16:00 (UTC+08:00) 北京", T(11, 26, 15), T(11, 26, 16)),
            ("Invitation: Weekly sync @ Thu Nov 26, 2026 3pm - 4pm (GMT+8)", T(11, 26, 15), T(11, 26, 16)),
            ("Dear all, the review is rescheduled to Thursday, Nov 26 from 3:00 PM to 4:00 PM.", T(11, 26, 15), T(11, 26, 16)),
            ("Your package arrives Monday between 2pm and 6pm", T(11, 23, 14), T(11, 23, 18)),
            ("时间：2026年11月26日（星期四）下午 3:00-4:30", T(11, 26, 15), T(11, 26, 16, 30))
        ]);
        check("A 24-hour time after 今晚 is taken as written", One("Windows 更新将在今晚 23:00 重启", T(11, 22, 23)));
        None(check, "Timestamps, lengths and times beside a number not read as a date", ["QQ音乐：正在播放 晴天 (4:29)", "电量剩余 20%，预计还能使用 3:20",
            "日程：11.26 下午 3 点 产品评审", "PR #215 merged at 2026-11-22 14:00", "发布于 2026-11-22 13:05", "14:37 已签收", "15:00 12.01 发布"]);
    }

    private static void Repeats(Action<string, bool> check)
    {
        var both = NoticeMoments.Find("周四下午三点开会", "周四 15:00 在 5 楼", Sunday, China);
        check("The same moment in the title and the body is offered once, from the title", both is [{ InTitle: true }] && both[0].Start == T(11, 26, 15));
        var differs = NoticeMoments.Find("周四下午三点开会", "周四 15:00-16:00 在 5 楼", Sunday, China);
        check("A moment with an end differs from one without", differs.Count == 2);
        var many = Find("周一 9:00，周二 10:00，周三 11:00，周四 15:00，周五 16:00，周六 17:00");
        check("At most four moments, in the order written", many.Count == NoticeMoments.MaxCount && many.Select(m => m.Start).SequenceEqual([T(11, 23, 9), T(11, 24, 10), T(11, 25, 11), T(11, 26, 15)]));
        var place = Find("王敏：评审改到 11月26日 下午3点，地点 5 楼");
        check("Where it stands in the body, as written", place is [var p] && !p.InTitle && p.Text == "11月26日 下午3点" && "王敏：评审改到 11月26日 下午3点，地点 5 楼".Substring(p.Index, p.Length) == p.Text);
        var wide = Find("会议：１１月２６日　１５：００");
        check("Full-width digits are read and the span is in the text as written", wide is [var w] && w.Start == T(11, 26, 15) && w.Text == "１１月２６日　１５：００");
        check("Nothing in nothing", NoticeMoments.Find(null, null, Sunday, China).Count == 0 && Find("").Count == 0 && Find("好的，收到").Count == 0);
    }

    private static void Long(Action<string, bool> check)
    {
        string noise = string.Concat(Enumerable.Repeat("1:1 3点 11/26 周 2026- 下午 Nov 3 ", 200));
        string body = noise[..(VerificationCode.MaxBody - 20)] + " 周五 16:00";
        NoticeMoments.Find(new string('9', 600), body, Sunday, China);
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NoticeMoment> found = [];
        for (int i = 0; i < 20; i++) found = NoticeMoments.Find(new string('9', 600), body, Sunday, China);
        clock.Stop();
        Console.WriteLine($"  20 reads of a full notice: {clock.ElapsedMilliseconds} ms");
        check("A notice of the longest text is read quickly", clock.ElapsedMilliseconds < 2000 && found.Count >= 1 && found[^1].Start == T(11, 27, 16));
        check("Text past what a notice holds is not read", Find(new string('x', VerificationCode.MaxBody) + " 周四 15:00").Count == 0);
    }

    private static void Words(Action<string, bool> check)
    {
        var today = new DateTime(2026, 11, 22);
        NoticeMoment M(DateTime start, DateTime? end = null, bool allDay = false, bool title = true) => new(start, end, allDay, title, 0, 1, "x");
        check("Chinese words for one moment", NoticeMoments.Label(M(T(11, 26, 15)), today) == "11月26日 周四 15:00"
            && NoticeMoments.Label(M(T(11, 26, 15), T(11, 26, 16)), today) == "11月26日 周四 15:00–16:00"
            && NoticeMoments.Label(M(T(11, 23, 20), T(11, 25, 12)), today) == "11月23日 20:00 – 11月25日 12:00"
            && NoticeMoments.Label(M(T(11, 26), allDay: true), today) == "11月26日 周四（全天）"
            && NoticeMoments.Label(M(T(11, 23), T(11, 25), true), today) == "11月23日 – 11月25日（全天）"
            && NoticeMoments.Label(M(T(1, 1, 10, y: 2027)), today) == "2027年1月1日 周五 10:00"
            && NoticeMoments.Label(M(T(12, 31, 22), T(1, 1, 1, y: 2027)), today) == "12月31日 22:00 – 2027年1月1日 01:00");
        check("Chinese short words and where it was read", NoticeMoments.Brief(M(T(11, 22, 15)), today) == "今天 15:00" && NoticeMoments.Brief(M(T(11, 23, 9)), today) == "明天 09:00"
            && NoticeMoments.Brief(M(T(11, 28, 9)), today) == "周六 09:00" && NoticeMoments.Brief(M(T(11, 29, 9)), today) == "11月29日 09:00"
            && NoticeMoments.Brief(M(T(11, 26), allDay: true), today) == "周四 全天" && NoticeMoments.Brief(M(T(1, 5, 9, y: 2027)), today) == "2027年1月5日 09:00"
            && NoticeMoments.Detail(M(T(11, 26, 15), title: false)) == "从正文认出 · 时长按 1 小时" && NoticeMoments.Detail(M(T(11, 26, 15), T(11, 26, 16))) == "从标题认出"
            && NoticeMoments.Detail(M(T(11, 26), allDay: true)) == "从标题认出");
        Loc.Use(Loc.English);
        try
        {
            check("English words for one moment", NoticeMoments.Label(M(T(11, 26, 15)), today) == "Thu, Nov 26 15:00"
                && NoticeMoments.Label(M(T(11, 26, 15), T(11, 26, 16)), today) == "Thu, Nov 26 15:00–16:00"
                && NoticeMoments.Label(M(T(11, 23, 20), T(11, 25, 12)), today) == "Nov 23 20:00 – Nov 25 12:00"
                && NoticeMoments.Label(M(T(11, 26), allDay: true), today) == "Thu, Nov 26 (all day)"
                && NoticeMoments.Label(M(T(11, 23), T(11, 25), true), today) == "Nov 23 – Nov 25 (all day)"
                && NoticeMoments.Label(M(T(1, 1, 10, y: 2027)), today) == "Fri, Jan 1, 2027 10:00");
            check("English short words and where it was read", NoticeMoments.Brief(M(T(11, 22, 15)), today) == "Today 15:00" && NoticeMoments.Brief(M(T(11, 23, 9)), today) == "Tomorrow 09:00"
                && NoticeMoments.Brief(M(T(11, 26, 15)), today) == "Thu 15:00" && NoticeMoments.Brief(M(T(11, 29, 9)), today) == "Nov 29 09:00"
                && NoticeMoments.Detail(M(T(11, 26, 15))) == "From the title · 1 hour assumed" && NoticeMoments.Detail(M(T(11, 26, 15), T(11, 26, 16), title: false)) == "From the body");
        }
        finally { Loc.Use(Loc.Chinese); }
    }
}
