namespace BrimDeck.Core;

// The bridge is the only contract between a plugin page and the host. Its version and its ceilings are stated once
// here, where both sides of the seam read the same numbers and a test can hold them to it.
public static class Bridge
{
    // Raised when an event or message a page can see changes shape in a way an older page would misread. A plugin that
    // needs a newer bridge says so with "minApiVersion" and is refused at the folder, not left showing a blank page.
    public const int Version = 1;
    // What a page may hand the host to keep on its behalf.
    public const int MaximumStateBytes = 64 * 1024;
    public const int MaximumAlarms = 32;
    // Events the host buffers for a page that has not subscribed yet — enough for init and a couple of changes, not a tape.
    public const int BufferedEventsPerKind = 8;
    // A link a page hands out: a web address, not a payload. Longer than this it is something else.
    public const int MaximumUrlChars = 2048;
    // How often one plugin may open the desktop browser without a click behind it, and over how long a stretch.
    public const int MaximumLinksPerWindow = 6;
    public static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(20);
}
