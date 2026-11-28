namespace BrimDeck.Core.Notices;

// What the recent notices page lists: today's notices unless earlier ones were asked for ("查看更早的通知"), and only the
// unseen ones under the "未读" filter.
public readonly record struct NoticeListFilter(bool Earlier, bool UnreadOnly);

// Which notice the recent notices page puts in front as it comes into view, and where its list starts.
// - The panel opened for a notice (the one showing on the island, a planner reminder): that notice, always listed; a filter
//   that would hide it gives way.
// - Otherwise the notice picked last (in the list, or by opening the panel from the island) stays in front from one opening to
//   the next, while the list still shows it. One that only "查看更早的通知" or the unread filter would bring back gives way to
//   the newest.
public static class NoticePageFocus
{
    public static bool Listed(Notice notice, NoticeListFilter filter, DateTime today, IReadOnlySet<string> fresh)
        => (filter.Earlier || notice.ReceivedAt.ToLocalTime().Date == today) && (!filter.UnreadOnly || fresh.Contains(notice.Id));

    // picked: the notice picked last; openedFor: the notice the panel is opening for. Returns the picked notice to keep (null:
    // none, so the newest leads) and the filter the list takes.
    public static (string? Picked, NoticeListFilter Filter) Settle(IReadOnlyList<Notice> notices, string? picked, string? openedFor,
        NoticeListFilter filter, DateTime today, IReadOnlySet<string> fresh)
    {
        if (picked is null || notices.FirstOrDefault(n => n.Id == picked) is not { } notice) return (null, filter);
        if (openedFor == picked)
            return (picked, new NoticeListFilter(filter.Earlier || notice.ReceivedAt.ToLocalTime().Date != today, filter.UnreadOnly && fresh.Contains(picked)));
        return Listed(notice, filter, today, fresh) ? (picked, filter) : (null, filter);
    }

    // The list's scroll offset as the page comes into view, for the selected row at rowTop: the top while the whole row shows
    // above the fade at the list's foot, else the row at the top of the view.
    public static double Offset(double rowTop, double rowHeight, double viewport, double foot)
        => rowTop + rowHeight <= viewport - foot ? 0 : Math.Max(0, rowTop);
}
