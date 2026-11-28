using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck;

// All leases and timer callbacks run on the application dispatcher.
internal sealed class WeatherLive
{
    internal sealed record Reading(string Name, double Temperature, int? Code, bool Day, DateTimeOffset At);
    internal sealed record Selection(City? City, string Source, string Host, int Generation);
    private static readonly Dictionary<Selection, WeatherLive> Active = [];
    private static (City City, string Source, Reading Value)? _recent;
    private static (City City, DateTimeOffset At)? _lastLocation;
    private sealed record Saved(City City, string Source, Reading Value, DateTimeOffset? LocatedAt);
    private static string SavedPath => Path.Combine(WeatherFiles.Root, "weather", "compact-current.json");
    private static bool _restored;
    private bool _running;

    private static void Restore()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            if (!File.Exists(SavedPath) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath)) is not { City: not null, Value: not null } saved ||
                !double.IsFinite(saved.Value.Temperature) || saved.Value.At > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            _recent ??= (saved.City, saved.Source, saved.Value);
            if (saved.LocatedAt is { } at) _lastLocation ??= (saved.City, at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal static void Located(City city) => _lastLocation = (city, DateTimeOffset.UtcNow);

    internal static WeatherSnapshot ForDisplay(WeatherSnapshot snapshot)
    {
        if (_recent is not { } cached || WeatherData.CacheKey(cached.City, cached.Source) != snapshot.CityKey ||
            cached.Value.At <= (snapshot.Current.ObservedAt ?? snapshot.FetchedAt) ||
            DateTimeOffset.UtcNow - cached.Value.At >= TimeSpan.FromMinutes(cached.Source == "hefeng" ? 20 : 30))
            return snapshot;
        var display = snapshot.ShallowCopy();
        display.Current = snapshot.Current.Copy();
        display.Current.Temperature = cached.Value.Temperature;
        display.Current.Code = cached.Value.Code;
        display.Current.IsDay = cached.Value.Day;
        display.Current.ObservedAt = cached.Value.At;
        return display;
    }

    internal static void Wake()
    {
        foreach (var live in Active.Values)
            if (!live._running) live.Schedule(TimeSpan.Zero);
    }

    internal static void FromDetails(City city, string source, WeatherSnapshot snapshot)
    {
        if (snapshot.Current.ObservedAt is not { } at || snapshot.Temperature is not double temp || !double.IsFinite(temp)) return;
        var reading = new Reading(city.Brief, temp, snapshot.WeatherCode, snapshot.IsDay, at);
        if (_recent is { } previous && previous.City == city && previous.Source == source && previous.Value.At > at) return;
        _recent = (city, source, reading);
        foreach (var live in Active.Values.ToArray())
        {
            if (live.Key.Source != source || (live.Key.City ?? live._located) != city || live.Latest?.At >= at) continue;
            live.Latest = reading;
            live.Changed?.Invoke();
        }
    }

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Selection _selection;
    private Func<WeatherSettings> _settings;
    private event Action? Changed;
    private City? _located;
    private DateTimeOffset _locatedAt;
    private DateTimeOffset _next;
    internal Reading? Latest { get; private set; }
    internal TimeSpan Interval => TimeSpan.FromMinutes(_selection.Source == "hefeng" ? 10 : 15);
    internal bool Stale => Latest is { } r && DateTimeOffset.UtcNow - r.At >= Interval * 2;
    internal Selection Key => _selection;

    private WeatherLive(Selection selection, Func<WeatherSettings> settings)
    {
        Restore();
        _selection = selection;
        _settings = settings;
        if (selection.City is null && _lastLocation is { } location)
        {
            _located = location.City;
            _locatedAt = location.At;
        }
        if (_recent is { } cached && cached.City == (selection.City ?? _located) && cached.Source == selection.Source)
        {
            Latest = cached.Value;
            _next = cached.Value.At + Interval;
            if (selection.City is null && DateTimeOffset.UtcNow - _locatedAt >= TimeSpan.FromHours(1)) _next = default;
        }
        _timer.Tick += Tick;
        Schedule(TimeSpan.Zero);
    }

    internal static WeatherLive Acquire(Selection key, Func<WeatherSettings> settings, Action changed)
    {
        if (!Active.TryGetValue(key, out var live)) Active[key] = live = new(key, settings);
        live._settings = settings;
        live.Changed += changed;
        return live;
    }

    internal void Release(Action changed)
    {
        Changed -= changed;
        if (Changed is not null) return;
        Active.Remove(_selection);
        _timer.Stop();
        _timer.Tick -= Tick;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Schedule(TimeSpan wait)
    {
        _timer.Interval = wait > TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50);
        _timer.Start();
    }

    private async void Tick(object? sender, EventArgs args)
    {
        _timer.Stop();
        _running = true;
        var token = _lifetime.Token;
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (_next > now) return;
            var settings = _settings();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(40));
            if (_selection.City is null && (_located is null || now - _locatedAt >= TimeSpan.FromHours(1)))
            {
                var located = await WeatherData.LocateAsync(settings, request.Token);
                if (located is not null)
                {
                    if (_located != located) Latest = null;
                    _located = located;
                    _locatedAt = now;
                    _lastLocation = (located, now);
                }
            }
            var city = _selection.City ?? _located;
            var source = WeatherSources.Find(_selection.Source);
            if (city is not null && source?.CanServe(city, settings) == true)
            {
                var part = await source.FetchAsync(city, settings, [WeatherDomain.Current], request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (part?.Current is { Temperature: double temp } current && double.IsFinite(temp))
                {
                    var at = current.ObservedAt ?? now;
                    if (Latest is null || Latest.At <= at)
                    {
                        Latest = new(city.Brief, temp, current.Code, current.IsDay ?? true, at);
                        _recent = (city, _selection.Source, Latest);
                        WeatherFiles.WriteAtomic(SavedPath, JsonSerializer.Serialize(new Saved(city, _selection.Source, Latest, _selection.City is null ? _locatedAt : null)));
                    }
                    _next = now + Interval;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        finally
        {
            _running = false;
            if (!token.IsCancellationRequested)
            {
                if (_next <= now) _next = now.AddMinutes(2);
                Schedule(_next - DateTimeOffset.UtcNow);
                Changed?.Invoke();
            }
        }
    }
}
