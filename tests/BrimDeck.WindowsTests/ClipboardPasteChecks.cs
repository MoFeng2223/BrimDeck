using BrimDeck.Native;

internal static class ClipboardPasteChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var target = new ClipboardPasteTarget(new IntPtr(10), 2, 3, new IntPtr(11));
        var platform = new Platform();
        ClipboardPasteSession Session() => new(target, 7, platform.Now);
        var session = Session();
        check("paste session: already-foreground receiver submits without reactivation", session.Advance(platform) == ClipboardPasteResult.Submitted && platform.Restores == 0 && platform.Sends == 1);
        check("paste session: completed requests never send twice", session.Advance(platform) == ClipboardPasteResult.Submitted && platform.Sends == 1);
        platform = new() { Foreground = new IntPtr(1), Ready = false };
        session = Session();
        check("paste session: asynchronous activation waits instead of failing on its return value", session.Advance(platform) == ClipboardPasteResult.Waiting && platform.Restores == 1 && platform.Sends == 0);
        session.Advance(platform);
        check("paste session: waiting never repeatedly steals foreground", platform.Restores == 1);
        platform.Foreground = target.Window; session.Advance(platform);
        check("paste session: foreground alone does not prove input focus ready", platform.Sends == 0);
        platform.Ready = true;
        check("paste session: delayed input focus eventually submits once", session.Advance(platform) == ClipboardPasteResult.Submitted && platform.Sends == 1);
        platform = new() { ModifiersReleased = false }; session = Session(); session.Advance(platform); platform.Now += 1201;
        check("paste session: held shortcut times out without injection", session.Advance(platform) == ClipboardPasteResult.ModifiersTimeout && platform.Sends == 0);
        platform = new() { Foreground = new IntPtr(1), Ready = false }; session = Session(); session.Advance(platform); platform.Now += 1201;
        check("paste session: failed focus restoration has its own bounded result", session.Advance(platform) == ClipboardPasteResult.FocusTimeout && platform.Restores == 1 && platform.Sends == 0);
        platform = new() { Sequence = 8 }; session = Session();
        check("paste session: another clipboard write cancels the old payload", session.Advance(platform) == ClipboardPasteResult.ClipboardChanged && platform.Sends == 0);
        platform = new() { Foreground = new IntPtr(20) }; session = Session();
        check("paste session: switching external apps cancels instead of forcing the old target", session.Advance(platform) == ClipboardPasteResult.TargetChanged && platform.Restores == 0 && platform.Sends == 0);
        platform = new() { Valid = false }; session = Session();
        check("paste session: destroyed or reused receiver identity cannot receive input", session.Advance(platform) == ClipboardPasteResult.TargetUnavailable && platform.Sends == 0);
        platform = new(); session = new(default, 7, platform.Now);
        check("paste session: missing target is different from blocked SendInput", session.Advance(platform) == ClipboardPasteResult.NoTarget && platform.Sends == 0);
        foreach (uint sent in new uint[] { 0, 1, 3 })
        {
            platform = new() { Sent = sent }; session = Session();
            check($"paste session: {sent} submitted keys reports input failure without an unproven UIPI claim", session.Advance(platform) == ClipboardPasteResult.InputBlocked && session.Input.Sent == sent && platform.Sends == 1);
        }
        platform = new(); session = Session(); session.Cancel();
        check("paste session: copy-only or newer request cancels pending work silently", session.Advance(platform) == ClipboardPasteResult.Cancelled && platform.Sends == 0 && platform.Restores == 0);
        var second = Session();
        check("paste session: cancelling an older request never cancels the new one", second.Advance(platform) == ClipboardPasteResult.Submitted && platform.Sends == 1);
        platform = new() { ChangeDuringFocusCheck = true }; session = Session();
        check("paste session: clipboard generation is checked again immediately before sending", session.Advance(platform) == ClipboardPasteResult.ClipboardChanged && platform.Sends == 0);
    }
    private sealed class Platform : IClipboardPastePlatform
    {
        public long Now { get; set; } = 100;
        public IntPtr Foreground { get; set; } = new(10);
        public uint Sequence { get; set; } = 7;
        public bool ModifiersReleased { get; set; } = true;
        internal bool Valid = true, Ready = true, ChangeDuringFocusCheck;
        internal int Restores, Sends;
        internal uint Sent = 4;
        public bool IsExternal(IntPtr window) => window.ToInt64() >= 10;
        public bool IsValid(ClipboardPasteTarget target) => Valid;
        public bool FocusReady(ClipboardPasteTarget target) { if (ChangeDuringFocusCheck) Sequence = 8; return Ready; }
        public void RequestForeground(ClipboardPasteTarget target) => Restores++;
        public ClipboardInputResult Send(ClipboardPasteTarget target) { Sends++; return new(Sent, Sent == 4 ? 0 : 5); }
    }
}
