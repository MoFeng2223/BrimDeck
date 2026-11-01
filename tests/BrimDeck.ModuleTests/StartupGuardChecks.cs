using System.IO;
using BrimDeck.Modules;

internal static partial class Program
{
    private sealed class StartupClock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }
    private static void StartupGuardChecks()
    {
        string folder = Path.Combine(_root, "startup-guard"); Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, "module-startup.json");
        var clock = new StartupClock(); var guard = new ModuleStartupGuard(folder, clock);
        Check("fresh startup has no recovery or side effects", !guard.Recovery.Required && !File.Exists(marker));
        Check("empty activation needs no startup journal", guard.Begin([]) && !guard.Armed && !File.Exists(marker));
        Check("journal reaches disk before module startup", guard.Begin(["notes", "timers"]) && guard.Armed && File.Exists(marker));
        var recovery = new ModuleStartupGuard(folder, clock);
        Check("abandoned startup is detected with module ids", recovery.Recovery.Required && recovery.Recovery.Modules.SequenceEqual(["notes", "timers"]));
        guard.FirstFramePresented(); guard.Heartbeat(); clock.Milliseconds += 1000;
        Check("first frame and one heartbeat do not finish startup", !guard.TryFinish(false) && File.Exists(marker));
        guard.Heartbeat();
        Check("responsive first frame does not hide pending async activation", !guard.TryFinish(true) && File.Exists(marker));
        Check("completed activation and responsive frame retire journal", guard.TryFinish(false) && !File.Exists(marker) && !guard.Armed);
        Check("healthy next launch does not prompt", !new ModuleStartupGuard(folder, clock).Recovery.Required);
        guard.Begin(["timers"]); guard.Heartbeat(); guard.Heartbeat();
        clock.Milliseconds += 4000;
        Check("stale heartbeat cannot finish or excuse a hung exit", !guard.TryFinish(false) && !guard.NormalExit() && File.Exists(marker));
        guard.Heartbeat(); Check("normal responsive exit during pending activation clears journal", guard.NormalExit() && !File.Exists(marker));
        var noFrame = new ModuleStartupGuard(folder, clock); noFrame.Begin(["notes"]); noFrame.Heartbeat(); noFrame.Heartbeat();
        Check("activation completion cannot replace first rendered frame", !noFrame.TryFinish(false));
        Check("recovery choice acknowledges only old journal", new ModuleStartupGuard(folder, clock).AcknowledgeRecovery() && !File.Exists(marker));
        noFrame.Begin(["notes"]); // Still owns the retired token; it may not erase a replacement.
        var replacement = new ModuleStartupGuard(folder, clock); replacement.Begin(["replacement"]);
        Check("old exit cannot erase replacement startup journal", !noFrame.NormalExit() && new ModuleStartupGuard(folder, clock).Recovery.Modules.SequenceEqual(["replacement"]));
        replacement.Heartbeat(); replacement.NormalExit();
        File.WriteAllText(marker, "{broken"); var malformed = new ModuleStartupGuard(folder, clock);
        Check("corrupt journal conservatively requests recovery", malformed.Recovery.Required && malformed.Recovery.Unreadable);
        Check("corrupt but identified journal can be acknowledged", malformed.AcknowledgeRecovery() && !File.Exists(marker));
        guard = new(folder, clock); guard.Begin(["timers"]);
        using (var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockedRecovery = new ModuleStartupGuard(folder, clock);
            Check("readable locked journal uses exact-content dismissal", lockedRecovery.AcknowledgeRecovery() && File.Exists(marker));
            Check("dismissed locked journal does not repeatedly prompt", !new ModuleStartupGuard(folder, clock).Recovery.Required);
        }
        var next = new ModuleStartupGuard(folder, clock); next.Begin(["new-content"]);
        Check("old dismissal never hides a new journal", new ModuleStartupGuard(folder, clock).Recovery.Required);
        next.Heartbeat(); next.NormalExit();
        string blocked = Path.Combine(folder, "blocked"); File.WriteAllText(blocked, "file");
        var unavailable = new ModuleStartupGuard(blocked, clock);
        Check("unwritable startup journal blocks starting rather than silently running", !unavailable.Begin(["timers"]) && !unavailable.Armed && unavailable.Error is not null);
        Check("startup journal never creates business storage", !Directory.Exists(Path.Combine(folder, "modules-data")));
    }
}
