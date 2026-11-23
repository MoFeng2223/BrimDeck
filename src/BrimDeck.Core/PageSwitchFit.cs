namespace BrimDeck.Core;

// How many page buttons the open panel's page switch shows itself. The switch keeps to the room left of the clock, which always
// stands in the middle of the panel; the pages that do not fit are listed by a more button at the switch's end.
// Buttons stand 1 DIP apart inside the switch's padding (both sides together).
public static class PageSwitchFit
{
    // The switch's width with direct page buttons, and the more button after them when more is true.
    public static double Width(int direct, bool more, double button, double moreWidth, double padding)
        => padding + direct * button + Math.Max(0, direct - 1) + (more ? (direct > 0 ? 1 : 0) + moreWidth : 0);

    // The number of pages shown as buttons: all of them while they fit in room, otherwise as many as fit beside the more button.
    public static int Direct(int pages, double room, double button, double moreWidth, double padding)
    {
        if (pages <= 0) return 0;
        if (Width(pages, false, button, moreWidth, padding) <= room) return pages;
        for (int direct = pages - 1; direct > 0; direct--)
            if (Width(direct, true, button, moreWidth, padding) <= room) return direct;
        return 0;
    }
}
