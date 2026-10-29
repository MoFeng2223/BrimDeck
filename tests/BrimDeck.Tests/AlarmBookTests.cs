using BrimDeck.Core;

// Which page is owed which alarm. The rules look trivial and are not: a plugin shown on two displays is two pages, and
// the bug this covers — an alarm counted as delivered to "the plugin" after one page heard it — is invisible in any
// single-window test and shows up as a notification one display silently lacks.
internal static class AlarmBookTests
{
    internal static void Run(Action<string, bool> check)
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        object left = new(), right = new();

        // The plain case: an alarm due while the pages are alive reaches every page, once.
        {
            var book = new AlarmBook();
            book.Set("a", start.AddSeconds(5));
            var pages = new[] { left, right };
            book.Sweep(pages, start);
            check("AlarmBook: an alarm is not owed before it comes due", book.Deliver(pages, start).Count == 0);
            var due = book.Deliver(pages, start.AddSeconds(6));
            check("AlarmBook: one alarm due reaches every page that is there",
                due.Count == 2 && due.All(pair => pair.Alarm == "a"));
            check("AlarmBook: a page is not told the same alarm twice", book.Deliver(pages, start.AddSeconds(7)).Count == 0);
            check("AlarmBook: an alarm every page has heard is done", book.Count == 0);
        }

        // The case the whole class exists for: both pages reclaimed, then loading back one at a time. The first page's
        // hello must not be the second page's answer.
        {
            var book = new AlarmBook();
            book.Set("focus-end", start.AddSeconds(5));
            book.Sweep([], start.AddSeconds(6)); // nobody can hear: the alarm is owed, not delivered
            check("AlarmBook: an alarm due with no page listening is still owed",
                book.HasFired && !book.HasPending);
            var first = book.Hear(left, new[] { left, right }, start.AddSeconds(20));
            check("AlarmBook: the first page to load hears the held alarm",
                first.Count == 1 && first[0] == "focus-end");
            var second = book.Hear(right, new[] { left, right }, start.AddSeconds(21));
            check("AlarmBook: the second display still hears it after the first one did",
                second.Count == 1 && second[0] == "focus-end");
            check("AlarmBook: the alarm is done once every page has heard it", book.Count == 0);
        }

        // A page that went away is not one that is owed: otherwise an alarm would never be counted complete.
        {
            var book = new AlarmBook();
            book.Set("a", start.AddSeconds(5));
            book.Sweep(new[] { left, right }, start.AddSeconds(6));
            book.Deliver(new[] { left }, start.AddSeconds(6));
            // Right was closed before it heard anything; left is now the only page that exists.
            var remaining = book.Hear(left, new[] { left }, start.AddSeconds(7));
            check("AlarmBook: a page that closed is not a page still owed", remaining.Count == 0 && book.Count == 0);
        }

        // Which page heard it is per page, not per plugin: a page that was there but reclaimed does not lose its turn
        // because its sibling happened to load first.
        {
            var book = new AlarmBook();
            book.Set("a", start.AddSeconds(5));
            var pages = new[] { left, right };
            book.Sweep(pages, start.AddSeconds(6));
            // Left loads first and is told; right is still blank, so the alarm is not finished.
            var first = book.Hear(left, pages, start.AddSeconds(6));
            check("AlarmBook: the page that loads first hears it", first.Count == 1 && first[0] == "a");
            check("AlarmBook: an alarm one page heard is not done while another is still owed it", book.Count == 1);
            var second = book.Hear(right, pages, start.AddSeconds(8));
            check("AlarmBook: the page that loads second hears it too", second.Count == 1 && second[0] == "a");
            check("AlarmBook: and now it is done", book.Count == 0);
        }

        // An alarm nobody ever comes for must not occupy one of the plugin's slots for the rest of the run.
        {
            var book = new AlarmBook();
            book.Set("orphan", start.AddSeconds(1));
            book.Sweep([], start.AddSeconds(2));
            book.Sweep([], start.AddSeconds(2) + AlarmBook.Retention + TimeSpan.FromSeconds(1));
            check("AlarmBook: an alarm nobody came for is dropped eventually", book.Count == 0);
        }

        // Moving an alarm replaces it; clearing it takes it away. Both are what a page can ask for.
        {
            var book = new AlarmBook();
            book.Set("a", start.AddSeconds(5));
            book.Set("a", start.AddSeconds(50));
            check("AlarmBook: setting the same id again moves it", book.Deliver(new[] { left }, start.AddSeconds(10)).Count == 0);
            book.Clear("a");
            check("AlarmBook: a cleared alarm is gone", !book.Has("a") && book.Count == 0);
        }

        // A pending alarm is what keeps the one-second tick alive; one already fired and held is handed over by the
        // hello that comes for it, so waking for it every second would be a wake with nothing to decide.
        {
            var book = new AlarmBook();
            book.Set("a", start.AddSeconds(5));
            check("AlarmBook: a pending alarm wants the tick", book.HasPending);
            book.Sweep([], start.AddSeconds(6));
            check("AlarmBook: an alarm held for a page does not want the tick", !book.HasPending && book.HasFired);
        }
    }
}
