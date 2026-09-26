namespace BrimDeck.Core;

public enum MediaDispatchResult { Unavailable, Sent, Uncertain }

public static class MediaRouting
{
    // Only an operation known not to have been sent may fall through to another transport.
    public static async Task<bool> DispatchAsync(Func<Task<MediaDispatchResult>> dedicated, Func<Task<bool>> system)
        => await dedicated() switch
        {
            MediaDispatchResult.Sent => true,
            MediaDispatchResult.Unavailable => await system(),
            _ => false
        };

    public static bool SameSong(MediaTrack a, MediaTrack b) =>
        string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(a.Artist) || string.IsNullOrWhiteSpace(b.Artist) ||
         string.Equals(a.Artist.Trim(), b.Artist.Trim(), StringComparison.OrdinalIgnoreCase));

    // A supplement owns only these three controls. It cannot replace metadata, time,
    // modes, or state, even while the primary is waiting for the next song to load.
    public static MediaTrack Merge(MediaTrack primary, MediaTrack? controls) => primary with
    {
        CanToggle = primary.CanToggle || controls?.CanToggle == true,
        CanPrevious = !primary.PreviousRestricted && (primary.CanPrevious || controls?.CanPrevious == true),
        CanNext = primary.CanNext || controls?.CanNext == true
    };
}
