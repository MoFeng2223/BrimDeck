using BrimDeck.Modules;

namespace BrimDeck;

public sealed record ModuleAppearance(long Revision, bool Dark, bool HighContrast, string Language, double DeviceScale, double ContentScale, bool ReducedMotion, bool Preview);

public sealed partial class ModuleViewContext
{
    private readonly Dictionary<long, Action<ModuleAppearance>> _appearanceListeners = [];
    private long _appearanceNext;
    public ModuleAppearance Appearance { get; private set; } = new(0, true, false, "en", 1, 1, true, false);
    public string Translate(string chinese, string english) => Appearance.Language == "zh-CN" ? chinese : english;
    public IDisposable OnAppearanceChanged(Action<ModuleAppearance> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ObjectDisposedException.ThrowIf(_ended || Lifetime.IsCancellationRequested, this);
        long id = ++_appearanceNext; _appearanceListeners[id] = changed;
        var handle = Own(new ModuleAction(() => _appearanceListeners.Remove(id)));
        try { changed(Appearance); } catch { handle.Dispose(); throw; }
        return handle;
    }
    internal void SetAppearance(ModuleAppearance appearance)
    {
        if (_ended || Lifetime.IsCancellationRequested || appearance with { Revision = Appearance.Revision } == Appearance) return;
        Appearance = appearance with { Revision = Appearance.Revision + 1 };
        foreach (var listener in _appearanceListeners.Values.ToArray())
            try { listener(Appearance); } catch (Exception error) { Fault?.Invoke(error); }
    }
}
