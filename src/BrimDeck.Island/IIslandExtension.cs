using System.Windows;

namespace BrimDeck.Island;

// The colors a page draws with, handed to the extension when its view is created and whenever the theme or the monitor
// palette changes. Plain "#RRGGBB" strings; the extension owns every brush it makes.
public sealed record IslandColors(string Normal, string Warning, string Critical, string Primary, string Secondary, string Tertiary, string Track, string Fill);

// One extension page on the island: a niche panel that lives outside the app's own code. An extension is a DLL next to
// the app's contract assembly, with one parameterless class implementing this interface; the host loads every such DLL
// from the extensions folder, gives each occupied slot a page tab, and asks the view to draw itself.
//
// The view is the extension's alone: it is placed as the page's content and stretches with the page, and nothing else
// touches it. SetDemand tells it whether any panel is showing its page: an extension that reads something (a service,
// a sensor, a file) starts that work when demand arrives and stops when it leaves, so a page nobody opens costs
// nothing. Everything runs on the UI thread unless the extension moves it itself.
public interface IIslandExtension
{
    // The stable id the page's settings are kept under: letters, digits, dash and dot.
    string Id { get; }
    // The tab's title in the two interface languages.
    string TitleZh { get; }
    string TitleEn { get; }
    // The tab's glyph: stroke path data on a 24 by 24 grid, round caps, drawn about 1.65 thick.
    string Glyph { get; }
    // The page's content. Called once per panel that shows the page; the extension decides what to share between them.
    FrameworkElement CreateView(IslandColors colors);
    // Whether any panel is showing the page. The first call with true may come before any view exists.
    void SetDemand(bool shown);
}
