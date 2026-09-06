using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrimDeck.Core;

// Prices are USD per token, exactly as returned by the remote catalog.
public sealed record TokenPrices(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, decimal? CacheWriteHour);
public sealed record PriceTier(long MinimumPromptTokens, TokenPrices Prices);
public sealed record ModelPrice(string Model, TokenPrices Prices, IReadOnlyList<PriceTier> Tiers);
public sealed record CostSummary(decimal Amount, int Reported, int Estimated, int Unpriced)
{
    public bool HasValue => Reported + Estimated > 0;
    public bool IsEstimate => Estimated > 0;
}

public sealed class Pricing : IDisposable
{
    public const string Source = "https://openrouter.ai/api/v1/models";
    public const string SourcePage = "https://openrouter.ai/models";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheFile;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset? _lastAttempt;
    private Dictionary<string, ModelPrice> _lookup = new(StringComparer.OrdinalIgnoreCase);
    public int ModelCount { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? LastError { get; private set; }
    public bool IsStale => UpdatedAt is null || _now() - UpdatedAt >= RefreshInterval;

    public Pricing(string directory, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _cacheFile = Path.Combine(directory, "model-prices.json");
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null; _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (!File.Exists(_cacheFile)) return;
            using var cache = JsonDocument.Parse(File.ReadAllText(_cacheFile));
            var timestamp = cache.RootElement.Get("updatedAt").Date();
            if (timestamp is null || timestamp > _now().AddMinutes(5)) throw new InvalidDataException();
            var models = Parse(cache.RootElement.Get("catalog"));
            SetCatalog(models, timestamp.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { LastError = "价格缓存不可用"; }
    }

    // The first call in each process fetches online even when a disk cache exists.
    public async Task RefreshAsync(bool force = false, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var now = _now();
            if (!force && _lastAttempt is { } previous && now - previous < RefreshInterval) return;
            _lastAttempt = now;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Source);
                request.Headers.UserAgent.ParseAdd("BrimDeck/0.1.0");
                using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                using var catalog = JsonDocument.Parse(json);
                var models = Parse(catalog.RootElement);
                var updated = _now();
                SetCatalog(models, updated); LastError = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                    var temporary = _cacheFile + ".tmp";
                    var payload = JsonSerializer.Serialize(new { updatedAt = updated, catalog = catalog.RootElement });
                    await File.WriteAllTextAsync(temporary, payload, cancellation).ConfigureAwait(false);
                    File.Move(temporary, _cacheFile, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = "价格已更新，缓存保存失败"; }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
            { LastError = ModelCount > 0 ? "更新失败，使用缓存价格" : "价格暂不可用"; }
        }
        finally { _gate.Release(); }
    }

    public static IReadOnlyList<ModelPrice> Parse(JsonElement catalog)
    {
        var models = new List<ModelPrice>();
        foreach (var item in catalog.Get("data").Items())
        {
            var id = item.Get("id").Text();
            var pricing = item.Get("pricing");
            if (string.IsNullOrWhiteSpace(id) || !id.Contains('/') || ReadPrice(pricing, "prompt") is not { } input || ReadPrice(pricing, "completion") is not { } output) continue;
            var prices = new TokenPrices(input, output, ReadPrice(pricing, "input_cache_read"), ReadPrice(pricing, "input_cache_write"), ReadPrice(pricing, "input_cache_write_1h"));
            var tiers = new List<PriceTier>();
            foreach (var tier in pricing.Get("overrides").Items())
            {
                if (tier.Get("min_prompt_tokens").Number() is not { } threshold || threshold < 0 || threshold > 1e15) continue;
                tiers.Add(new((long)threshold, new(ReadPrice(tier, "prompt") ?? input, ReadPrice(tier, "completion") ?? output,
                    ReadPrice(tier, "input_cache_read") ?? prices.CacheRead, ReadPrice(tier, "input_cache_write") ?? prices.CacheWrite,
                    ReadPrice(tier, "input_cache_write_1h") ?? prices.CacheWriteHour)));
            }
            models.Add(new(id, prices, tiers.OrderBy(x => x.MinimumPromptTokens).ToArray()));
        }
        if (models.Count == 0) throw new InvalidDataException("No usable model prices.");
        return models;
    }
    private static decimal? ReadPrice(JsonElement pricing, string name)
    {
        var field = pricing.Get(name);
        string? value = field.ValueKind == JsonValueKind.Number ? field.GetRawText() : field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price is >= 0 and <= 1_000 ? price : null;
    }
    private static string Normalize(string model)
    {
        var value = model.Trim().ToLowerInvariant();
        return value.StartsWith("claude-", StringComparison.Ordinal) || value.StartsWith("anthropic/claude-", StringComparison.Ordinal)
            ? Regex.Replace(value, @"(?<=\d)\.(?=\d)", "-") : value;
    }
    private void SetCatalog(IReadOnlyList<ModelPrice> models, DateTimeOffset updated)
    {
        var lookup = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) lookup[Normalize(model.Model)] = model;
        foreach (var alias in models.GroupBy(x => Normalize(x.Model[(x.Model.IndexOf('/') + 1)..])))
            if (alias.Count() == 1) lookup.TryAdd(alias.Key, alias.Single());
        _lookup = lookup; ModelCount = models.Count; UpdatedAt = updated;
    }
    public ModelPrice? Find(string model)
    {
        string key = Normalize(model);
        var lookup = _lookup;
        if (lookup.TryGetValue(key, out var price)) return price;
        // Only a dated snapshot can fall back to its exact base model. Variants remain distinct.
        string undated = Regex.Replace(key, @"-\d{8}$", "");
        return undated != key && lookup.TryGetValue(undated, out price) ? price : null;
    }
    public decimal? Estimate(TokenEntry entry)
    {
        if (entry.ReportedCostUsd is { } reported) return reported;
        var model = Find(entry.Model);
        if (model is null) return null;
        long prompt = entry.Input + entry.CacheRead + entry.CacheWrite + entry.CacheWriteHour;
        var prices = model.Tiers.LastOrDefault(t => prompt >= t.MinimumPromptTokens)?.Prices ?? model.Prices;
        if ((entry.CacheRead > 0 && prices.CacheRead is null) || (entry.CacheWrite > 0 && prices.CacheWrite is null) || (entry.CacheWriteHour > 0 && prices.CacheWriteHour is null)) return null;
        return entry.Input * prices.Input + entry.Output * prices.Output + entry.CacheRead * (prices.CacheRead ?? 0) +
            entry.CacheWrite * (prices.CacheWrite ?? 0) + entry.CacheWriteHour * (prices.CacheWriteHour ?? 0);
    }
    public CostSummary Summarize(IEnumerable<TokenEntry> entries)
    {
        decimal amount = 0; int reported = 0, estimated = 0, unpriced = 0;
        foreach (var entry in entries)
        {
            if (Estimate(entry) is not { } price) { unpriced++; continue; }
            amount += price;
            if (entry.ReportedCostUsd is not null) reported++; else estimated++;
        }
        return new(amount, reported, estimated, unpriced);
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
