using BrimDeck.Core;
using BrimDeck.Native;

// The processor's readings before and after the performance counters join (CpuProvider.Attach): the kernel's times give the first
// figures alone, and the handover to the counters keeps giving them.
internal static class CpuProviderChecks
{
    private const string BusyPath = @"\Processor Information(*)\% Processor Time";

    public static void Run(Action<string, bool> check)
    {
        using var pdh = new PdhQuery();
        var cpu = new CpuProvider(pdh);
        check("cpu: nothing is waited for at construction, the counters are not in the query yet", !cpu.Attached);
        var first = cpu.Sample(false);
        Thread.Sleep(300);
        var second = cpu.Sample(false);
        check("cpu: the kernel's times give no figure at first and a figure after a window, without the counters",
            !first.HasValue && second.TryGet(out var kernel) && kernel.Cores.Count > 0 && kernel.Cores.All(core => core.Usage is >= 0 and <= 1) && !cpu.Attached);
        check("cpu: before the counters join the frequency is waiting, not missing", second.TryGet(out kernel!) && kernel.Cores.All(core => !core.FrequencyMhz.HasValue)
            && kernel.Cores.All(core => core.FrequencyMhz.Reason == Why.Waiting));

        cpu.Attach();
        Thread.Sleep(300);
        bool collected = pdh.Collect();
        int instances = pdh.Read(BusyPath).Count(value => value.Instance.Contains(',') && !value.Instance.EndsWith("_Total"));
        var counters = cpu.Sample(collected);
        if (!collected || instances == 0)
        {
            // Not a handover: the counters gave nothing here, so the readings stay on the kernel's times.
            check("cpu: the counters gave no instances on this machine, the kernel's times go on (the handover is not exercised)", cpu.Attached && counters.HasValue);
            return;
        }
        check("cpu: the counters returned valid instances, and the readings come from them", cpu.Attached && counters.TryGet(out var read) && read.Cores.Count == instances
            && read.Cores.All(core => core.Usage is >= 0 and <= 1));
        Thread.Sleep(300);
        var again = cpu.Sample(pdh.Collect());
        check("cpu: and stay there on the next reading, for as many processors", again.TryGet(out read!) && read.Cores.Count == instances);

        // The counters stop giving readings (a failed collection): the last reading is kept, and the times that stand in start a window
        // of their own, not one that reaches back to before the handover.
        Thread.Sleep(300);
        var kept = cpu.Sample(false);
        var keptAgain = cpu.Sample(false);
        check("cpu: when the counters stop the last reading is kept, and still kept while the kernel's times only take their baseline",
            kept.TryGet(out var a) && keptAgain.TryGet(out var b) && ReferenceEquals(a, b) && a.Cores.Count == instances);
        Thread.Sleep(300);
        var resumed = cpu.Sample(false);
        check("cpu: after a window of its own the kernel's times give a valid figure again", resumed.TryGet(out var c) && c.Cores.Count > 0 && c.Cores.All(core => core.Usage is >= 0 and <= 1));
    }
}
