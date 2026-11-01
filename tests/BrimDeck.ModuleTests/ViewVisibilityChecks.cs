using System.Windows.Controls;
using BrimDeck;

internal static partial class Program
{
    private static void ViewVisibilityChecks()
    {
        using var context = new ModuleViewContext(new Grid(), _ => { }, _ => { }, _ => { });
        Check("a new view is not presented before host confirmation", !context.IsVisible);
        int pulses = 0, errors = 0; var changes = new List<bool>();
        context.Fault = _ => errors++;
        context.OnVisibilityChanged(_ => throw new InvalidOperationException("visibility callback"));
        context.OnVisibilityChanged(changes.Add);
        using var visibleTimer = context.EveryVisible(TimeSpan.FromMilliseconds(16), () => pulses++);
        Pump(); Check("visible timer does not wake its callback before presentation", pulses == 0);
        context.SetVisible(true); Pump();
        Check("presentation starts visible timer and isolates listener failures", pulses > 0 && changes.SequenceEqual([true]) && errors == 1);
        context.SetVisible(true); Check("unchanged visibility is not re-emitted", changes.Count == 1);
        context.SetVisible(false); int hiddenPulses = pulses; Pump();
        Check("hiding a view stops its presentation timer", pulses == hiddenPulses && changes.SequenceEqual([true, false]));
        context.SetVisible(true); Pump();
        Check("showing a view restarts its timer", pulses > hiddenPulses);
        visibleTimer.Dispose(); int stoppedPulses = pulses; context.SetVisible(false); context.SetVisible(true); Pump();
        Check("disposed presentation timers cannot restart on visibility change", pulses == stoppedPulses);
        context.Dispose();
        Check("view disposal sends a final hidden signal and revokes visibility", !context.IsVisible && changes[^1] == false);
        Throws("disposed view rejects new visibility listeners", () => context.OnVisibilityChanged(_ => { }));
        Throws("disposed view rejects new visible timers", () => context.EveryVisible(TimeSpan.FromSeconds(1), () => { }));
        using var pending = new ModuleViewContext(new Grid(), _ => { }, _ => { }, _ => { });
        pending.Attach(new Grid(), "pending"); pending.SetVisible(true);
        Check("pending mounts cannot be marked visible", !pending.IsVisible);
        pending.CompleteMount(); pending.SetVisible(true);
        Check("mount completion permits host-confirmed presentation", pending.IsVisible);
        using var other = new ModuleViewContext(new Grid(), _ => { }, _ => { }, _ => { });
        other.SetVisible(true); pending.SetVisible(false);
        Check("display presentations have independent visibility", other.IsVisible && !pending.IsVisible);
        using var parent = new CancellationTokenSource(); other.BindLifetime(parent.Token); parent.Cancel();
        Check("activation cancellation immediately revokes visibility", !other.IsVisible);
        using var dispatch = new ModuleViewContext(new Grid(), _ => { }, _ => { }, _ => { });
        int removedCalls = 0; IDisposable? second = null;
        dispatch.OnVisibilityChanged(_ => second!.Dispose()); second = dispatch.OnVisibilityChanged(_ => removedCalls++);
        dispatch.SetVisible(true);
        Check("unsubscribed visibility handlers are skipped during dispatch", removedCalls == 0);
        Throws("visible timer interval is bounded", () => dispatch.EveryVisible(TimeSpan.FromMilliseconds(1), () => { }));
    }
}
