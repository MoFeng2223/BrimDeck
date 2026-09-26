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
    private readonly string _manualFile;
    private readonly object _sync = new();
    private readonly Dictionary<string, ModelPrice> _remote = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ModelPrice> _manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset? _lastAttempt;
    private Dictionary<string, ModelPrice> _lookup = new(StringComparer.OrdinalIgnoreCase);
    public int ModelCount { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? LastError { get; private set; }
    public string? ManualLoadWarning { get; private set; }
    public event Action? Changed;
    public IReadOnlyList<ModelPrice> Models
    {
        get { lock (_sync) return MergedModels().Values.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
    public bool IsManual(string model) { lock (_sync) return _manual.ContainsKey(Normalize(model)); }
    public bool IsStale => UpdatedAt is null || _now() - UpdatedAt >= RefreshInterval;

    public Pricing(string directory, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _cacheFile = Path.Combine(directory, "model-prices.json");
        _manualFile = Path.Combine(directory, "manual-model-prices.json");
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null; _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (File.Exists(_cacheFile))
            {
                using var cache = JsonDocument.Parse(File.ReadAllText(_cacheFile));
                var timestamp = cache.RootElement.Get("updatedAt").Date();
                if (timestamp is null || timestamp > _now().AddMinutes(5)) throw new InvalidDataException();
                var stored = cache.RootElement.Get("models");
                var models = stored.ValueKind == JsonValueKind.Array
                    ? stored.Deserialize<List<ModelPrice>>()?.Where(ValidModel).ToArray() ?? []
                    : Parse(cache.RootElement.Get("catalog"));
                if (models.Count == 0) throw new InvalidDataException();
                SetCatalog(models, timestamp.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { LastError = Loc.T("价格缓存不可用", "The price cache is unavailable"); }
        try
        {
            if (File.Exists(_manualFile))
            {
                var models = JsonSerializer.Deserialize<List<ModelPrice>>(File.ReadAllText(_manualFile));
                if (models is null || models.Any(m => !ValidModel(m)) || models.Select(m => Normalize(m.Model)).Distinct().Count() != models.Count)
                    throw new InvalidDataException();
                _manual = models.ToDictionary(m => Normalize(m.Model), StringComparer.OrdinalIgnoreCase);
                RebuildLookup();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { ManualLoadWarning = Loc.T("无法读取手动模型价格，原文件已保留。", "The prices added by hand could not be read; the original file is kept. "); }
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
                    string payload;
                    lock (_sync) payload = JsonSerializer.Serialize(new { updatedAt = updated, models = _remote.Values.ToArray() });
                    await File.WriteAllTextAsync(temporary, payload, cancellation).ConfigureAwait(false);
                    File.Move(temporary, _cacheFile, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = Loc.T("价格已更新，缓存保存失败", "Prices were updated, but the cache could not be saved"); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException)
            { LastError = ModelCount > 0 ? Loc.T("更新失败，使用缓存价格", "Update failed; cached prices are used") : Loc.T("价格暂不可用", "Prices are not available yet"); }
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
        lock (_sync)
        {
            // Remote updates are incremental; manually edited entries live separately and always win.
            foreach (var model in models) _remote[Normalize(model.Model)] = model;
            UpdatedAt = updated; RebuildLookup();
        }
    }
    private void RebuildLookup()
    {
        var merged = MergedModels();
        var models = merged.Values.ToArray();
        var lookup = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) lookup[Normalize(model.Model)] = model;
        foreach (var alias in models.GroupBy(x => Normalize(x.Model[(x.Model.IndexOf('/') + 1)..])))
            if (alias.Count() == 1) lookup.TryAdd(alias.Key, alias.Single());
        foreach (var remote in _remote.Values)
            if (_manual.TryGetValue(Normalize(remote.Model[(remote.Model.IndexOf('/') + 1)..]), out var manual))
                lookup.TryAdd(Normalize(remote.Model), manual);
        _lookup = lookup; ModelCount = models.Length;
        Changed?.Invoke();
    }
    private Dictionary<string, ModelPrice> MergedModels()
    {
        var merged = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in _remote)
        {
            var bare = Normalize(model.Value.Model[(model.Value.Model.IndexOf('/') + 1)..]);
            // A manually added bare model ID also protects the same model when it later arrives online.
            if (!_manual.ContainsKey(model.Key) && !_manual.ContainsKey(bare)) merged[model.Key] = model.Value;
        }
        foreach (var model in _manual) merged[model.Key] = model.Value;
        return merged;
    }
    private static bool ValidPrices(TokenPrices? prices) => prices is not null &&
        prices.Input is >= 0 and <= 1000 && prices.Output is >= 0 and <= 1000 &&
        (prices.CacheRead is null or >= 0 and <= 1000) && (prices.CacheWrite is null or >= 0 and <= 1000) &&
        (prices.CacheWriteHour is null or >= 0 and <= 1000);
    private static bool ValidModel(ModelPrice? model) => model is not null && !string.IsNullOrWhiteSpace(model.Model) &&
        ValidPrices(model.Prices) && model.Tiers is not null && model.Tiers.All(t => t is not null && t.MinimumPromptTokens >= 0 && ValidPrices(t.Prices));

    // Prices remain per-token internally; conversion from per-million happens only at the settings boundary.
    public bool TrySetManual(string model, TokenPrices prices, bool replaceExisting, out string? error)
    {
        error = null; model = model.Trim();
        if (!Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._:/+\-]{0,199}$"))
        { error = Loc.T("请输入有效的模型 ID（仅限字母、数字及 . _ : / + -）。", "Enter a valid model ID (letters, digits and . _ : / + - only)."); return false; }
        if (!ValidPrices(prices)) { error = Loc.T("单价必须为零或正数，且不能超出支持范围。", "Prices must be zero or positive and within the supported range."); return false; }
        lock (_sync)
        {
            if (ManualLoadWarning is not null) { error = ManualLoadWarning + Loc.T("请先修复该文件。", "Repair that file first."); return false; }
            var key = Normalize(model);
            var match = Find(model) ?? _manual.GetValueOrDefault(Normalize(model[(model.IndexOf('/') + 1)..]));
            if (!replaceExisting && match is not null) { error = Loc.T("该模型已存在，请编辑现有模型。", "This model already exists. Edit the existing model instead."); return false; }
            if (replaceExisting && (match is null || Normalize(match.Model) != key))
            { error = Loc.T("模型已发生变化，请重新打开编辑窗口。", "The model has changed. Open the editor again."); return false; }
            // A manual price is a flat price and is not silently superseded by remote context tiers.
            var next = new Dictionary<string, ModelPrice>(_manual, StringComparer.OrdinalIgnoreCase)
            { [key] = new(model, prices, []) };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_manualFile)!);
                var temporary = _manualFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _manualFile, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error = Loc.T("无法写入手动价格，请检查设置目录权限。", "The prices added by hand could not be written. Check the permissions of the settings folder."); return false; }
            _manual = next; RebuildLookup(); return true;
        }
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
        if (model is null && entry.Key.StartsWith("ag:", StringComparison.Ordinal))
            model = FindAntigravityBase(entry.Model);
        if (model is null) return null;
        long prompt = entry.Input + entry.CacheRead + entry.CacheWrite + entry.CacheWriteHour;
        var prices = model.Tiers.LastOrDefault(t => prompt >= t.MinimumPromptTokens)?.Prices ?? model.Prices;
        // Blank cache prices in a manual entry: a cache read costs nothing, a cache write costs the input price, and a
        // one-hour cache write costs the cache write price. Remote entries keep a missing cache price as unknown, so
        // their records stay unpriced rather than guessed.
        if (_manual.TryGetValue(Normalize(model.Model), out var manual) && ReferenceEquals(manual, model))
        {
            var write = prices.CacheWrite ?? prices.Input;
            prices = prices with { CacheRead = prices.CacheRead ?? 0, CacheWrite = write, CacheWriteHour = prices.CacheWriteHour ?? write };
        }
        if ((entry.CacheRead > 0 && prices.CacheRead is null) || (entry.CacheWrite > 0 && prices.CacheWrite is null) || (entry.CacheWriteHour > 0 && prices.CacheWriteHour is null)) return null;
        return entry.Input * prices.Input + entry.Output * prices.Output + entry.CacheRead * (prices.CacheRead ?? 0) +
            entry.CacheWrite * (prices.CacheWrite ?? 0) + entry.CacheWriteHour * (prices.CacheWriteHour ?? 0);
    }
    public CostSummary Summarize(IEnumerable<TokenEntry> entries)
    {
        decimal amount = 0; int reported = 0, estimated = 0, unpriced = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Model) && entry.Total == 0 && entry.ReportedCostUsd is null) continue;
            if (Estimate(entry) is not { } price) { unpriced++; continue; }
            amount += price;
            if (entry.ReportedCostUsd is not null) reported++; else estimated++;
        }
        return new(amount, reported, estimated, unpriced);
    }
    private ModelPrice? FindAntigravityBase(string model)
    {
        // Reasoning settings are not separate model prices. Keep this fallback local to
        // Antigravity; exact catalog/manual entries above always take precedence.
        // https://antigravity.google/docs/models
        // https://ai.google.dev/gemini-api/docs/thinking
        // https://platform.claude.com/docs/en/build-with-claude/extended-thinking
        // https://huggingface.co/openai/gpt-oss-120b
        var key = Normalize(model);
        var pattern = key switch
        {
            var s when Regex.IsMatch(s, @"^(google/)?gemini-\d") => @"-(minimal|low|medium|high)$",
            var s when Regex.IsMatch(s, @"^(anthropic/)?claude-(sonnet|opus|haiku)-\d") => @"-thinking$",
            var s when Regex.IsMatch(s, @"^(openai/)?gpt-oss-(20b|120b)-") => @"-(low|medium|high)$",
            _ => null
        };
        if (pattern is null) return null;
        var baseModel = Regex.Replace(key, pattern, "");
        return baseModel != key ? Find(baseModel) : null;
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
