using BrimDeck.Core;

internal static class PageSwitchFitTests
{
    internal static void Run(Action<string, bool> check)
    {
        // The panel's sizes: 28 DIP buttons, a 40 DIP more button, 4 DIP of padding.
        static int Direct(int pages, double room) => PageSwitchFit.Direct(pages, room, 28, 40, 4);
        static double Width(int direct, bool more) => PageSwitchFit.Width(direct, more, 28, 40, 4);

        check("Seven pages are 206 DIP wide without the more button", Width(7, false) == 206);
        check("Four pages and the more button are 160 DIP wide", Width(4, true) == 160);
        check("Only the more button is 44 DIP wide", Width(0, true) == 44);
        check("Every page stands in the switch while all fit", Direct(7, 206) == 7 && Direct(3, 500) == 3);
        check("One DIP short of all pages, the last ones move to the more button", Direct(7, 205) == 5);
        // The narrowest panel (440 DIP) with a 30 DIP clock, an 8 DIP gap and a 20 DIP inset leaves 177 DIP.
        check("The narrowest panel shows four of seven pages and the more button", Direct(7, 177) == 4 && Width(4, true) <= 177);
        check("Twenty pages on the widest panel still leave the clock its room", Width(Direct(20, 537), true) <= 537 && Direct(20, 537) == 17);
        check("A room too small for one button keeps only the more button", Direct(7, 60) == 0);
        check("No pages need no buttons", Direct(0, 300) == 0);
    }
}
