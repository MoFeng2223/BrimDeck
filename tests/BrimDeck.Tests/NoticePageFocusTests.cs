using BrimDeck.Core.Notices;

// Which notice the recent notices page puts in front as it comes into view, and where its list starts (NoticePageFocus).
internal static class NoticePageFocusTests
{
    public static void Run(Action<string, bool> check)
    {
        var today = DateTime.Today;
        Notice At(string id, DateTime local) => new() { Id = id, ReceivedAt = new DateTimeOffset(local) };
        var newest = At("newest", today.AddHours(12));
        var picked = At("picked", today.AddHours(9));
        var old = At("old", today.AddDays(-1).AddHours(12));
        Notice[] notices = [newest, picked, old];
        var all = new NoticeListFilter(false, false);
        var none = new HashSet<string>();

        var kept = NoticePageFocus.Settle(notices, "picked", null, all, today, none);
        check("page focus: the notice picked last stays in front while the list shows it", kept.Picked == "picked" && kept.Filter == all);

        var earlier = NoticePageFocus.Settle(notices, "old", null, all, today, none);
        check("page focus: a picked notice hidden behind the earlier notices gives way to the newest", earlier.Picked is null && earlier.Filter == all);

        var unread = new NoticeListFilter(false, true);
        var read = NoticePageFocus.Settle(notices, "picked", null, unread, today, new HashSet<string> { "newest" });
        check("page focus: a picked notice the unread filter hides gives way to the newest", read.Picked is null && read.Filter == unread);

        var gone = NoticePageFocus.Settle(notices, "deleted", null, all, today, none);
        check("page focus: a picked notice that is gone leaves nothing picked", gone.Picked is null);
        check("page focus: nothing picked stays so", NoticePageFocus.Settle(notices, null, null, all, today, none).Picked is null);

        var opened = NoticePageFocus.Settle(notices, "newest", "newest", unread, today, new HashSet<string> { "newest" });
        check("page focus: the notice opened from the island is in front and the filter keeps it", opened.Picked == "newest" && opened.Filter == unread);
        var openedRead = NoticePageFocus.Settle(notices, "picked", "picked", unread, today, none);
        check("page focus: the unread filter gives way to the notice the panel opened for", openedRead.Picked == "picked" && openedRead.Filter == all);
        var openedOld = NoticePageFocus.Settle(notices, "old", "old", all, today, none);
        check("page focus: an earlier notice the panel opened for brings the earlier notices into the list", openedOld.Picked == "old" && openedOld.Filter.Earlier);

        check("page focus: the list lists today's notices only until earlier ones are asked for",
            NoticePageFocus.Listed(newest, all, today, none) && !NoticePageFocus.Listed(old, all, today, none) && NoticePageFocus.Listed(old, new(true, false), today, none));

        check("page focus: a row whole above the fade keeps the list at its top", NoticePageFocus.Offset(100, 50, 200, 24) == 0);
        check("page focus: a row reaching into the fade moves to the top of the view", NoticePageFocus.Offset(140, 50, 200, 24) == 140);
        check("page focus: a row below the view moves to the top of the view", NoticePageFocus.Offset(600, 50, 200, 24) == 600);
    }
}
