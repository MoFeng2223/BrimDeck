namespace BrimDeck.Modules;

// Dispatcher-owned broker. A cancellation token identifies an activation generation, not just its module id.
public sealed class ModuleNoticeQueue
{
    public sealed record Ticket(string Owner, CancellationToken Lifetime, ModuleNotice Notice, DateTimeOffset ExpiresAt);
    private readonly List<Ticket> _pending = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rates = [];
    public int Count => _pending.Count;
    public static ModuleNotice Normalize(ModuleNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice); ModuleIds.Require(notice.Id);
        if (string.IsNullOrWhiteSpace(notice.Title) || notice.Title.Length > 60 || notice.Message is null || notice.Message.Length > 160
            || !Enum.IsDefined(notice.Tone) || notice.ProgressPercent is { } progress && !double.IsFinite(progress))
            throw new ModuleApiException("invalid-notice", "Notice title, message, tone or progress is invalid.");
        return notice with { DurationMilliseconds = Math.Clamp(notice.DurationMilliseconds, 900, 8000), ProgressPercent = notice.ProgressPercent is { } value ? Math.Clamp(value, 0, 100) : null };
    }
    public ModuleNoticeResult Enqueue(string owner, CancellationToken lifetime, ModuleNotice notice, DateTimeOffset now)
    {
        ModuleIds.Require(owner); notice = Normalize(notice); Prune(now);
        if (lifetime.IsCancellationRequested) return ModuleNoticeResult.Unavailable;
        int existing = _pending.FindIndex(t => t.Owner == owner && t.Lifetime == lifetime && t.Notice.Id == notice.Id);
        if (existing >= 0) { _pending[existing] = new(owner, lifetime, notice, now.AddSeconds(30)); return ModuleNoticeResult.Queued; }
        if (_pending.Count >= 16) return ModuleNoticeResult.RateLimited;
        if (!_rates.TryGetValue(owner, out var rate)) _rates[owner] = rate = new();
        if (rate.Count >= 4) return ModuleNoticeResult.RateLimited;
        rate.Enqueue(now); _pending.Add(new(owner, lifetime, notice, now.AddSeconds(30))); return ModuleNoticeResult.Queued;
    }
    public Ticket? Take(DateTimeOffset now)
    { Prune(now); if (_pending.Count == 0) return null; var ticket = _pending[0]; _pending.RemoveAt(0); return ticket; }
    public void Remove(string owner, CancellationToken lifetime) => _pending.RemoveAll(t => t.Owner == owner && t.Lifetime == lifetime);
    public void Prune(DateTimeOffset now)
    {
        _pending.RemoveAll(t => t.Lifetime.IsCancellationRequested || t.ExpiresAt <= now);
        foreach (var (id, rate) in _rates.ToArray())
        { while (rate.TryPeek(out var time) && now - time >= TimeSpan.FromMinutes(1)) rate.Dequeue(); if (rate.Count == 0) _rates.Remove(id); }
    }
}
