using System.Net;
using System.Text.Json;
using BrimDeck.Core;

static class PiUsageTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "pi-sessions");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "session.jsonl");
        var now = DateTimeOffset.Now;
        var time = now.AddMinutes(-1);
        string Entry(string id, string type = "message", string role = "assistant", string? date = null) => JsonSerializer.Serialize(new
        {
            type, id, timestamp = date ?? time.ToString("O"),
            message = new { role, provider = "openai-codex", model = "test-model", timestamp = time.ToUnixTimeMilliseconds(),
                usage = new { input = 10, output = 20, cacheRead = 30, cacheWrite = 40, cacheWrite1h = 15, reasoning = 7, cost = new { total = 0.12m } } }
        });
        File.WriteAllText(file, Entry("one") + "\n" + Entry("user", role: "user") + "\n");
        // Copied history must not double count; new branch requests still count.
        File.WriteAllText(Path.Combine(folder, "fork.jsonl"), Entry("one") + "\n" + Entry("two") + "\n");
        var reader = new LogReader();
        var data = reader.Read(ProviderId.Pi, folder, now);
        check("Pi counts assistant usage once across forks", data.Entries.Count == 2);
        var first = data.Entries.First();
        check("Pi token categories are disjoint; reasoning is not added twice", first.Total == 100 && first.CacheWrite == 25 && first.CacheWriteHour == 15 && first.Output == 20);
        check("Pi preserves provider/model and recorded cost", first.Model == "openai-codex/test-model" && first.ReportedCostUsd == .12m);
        check("Pi unchanged refresh does not double count", reader.Read(ProviderId.Pi, folder, now).Entries.Count == 2);
        File.AppendAllText(file, Entry("three"));
        check("Pi defers incomplete final line", reader.Read(ProviderId.Pi, folder, now).Entries.Count == 2);
        File.AppendAllText(file, "\n");
        check("Pi reads completed appended line", reader.Read(ProviderId.Pi, folder, now).Entries.Count == 3);
        var standalone = JsonSerializer.Serialize(new { type = "usage", id = "warm", timestamp = time.ToString("O"), kind = "future_kind", provider = "anthropic", model = "test", usage = new { input = 0, output = 0, cacheRead = 50, cost = new { total = .01m } } });
        File.AppendAllText(file, standalone + "\n{\"usage\":broken}\n");
        data = reader.Read(ProviderId.Pi, folder, now);
        check("Pi includes standalone usage and flags malformed records", data.Entries.Count == 4 && !data.UsageComplete);
        File.WriteAllText(file, Entry("rewritten") + "\n");
        check("Pi rereads truncated files", reader.Read(ProviderId.Pi, folder, now).Entries.Count == 3);
        check("Pi respects date cutoff", reader.Read(ProviderId.Pi, folder, now, now.LocalDateTime.Date.AddDays(1)).Entries.Count == 0);
        check("Pi missing folder is unavailable", !reader.Read(ProviderId.Pi, Path.Combine(root, "no-pi"), now).UsageAvailable);
        check("Pi is selectable as quota and usage source", ProviderCatalog.IsBuiltIn(ProviderId.Pi) && ProviderCatalog.IsUsageSource(ProviderId.Pi));
        var settings = new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.Pi }] };
        var serialized = JsonSerializer.Serialize(settings);
        check("Pi settings round trip keeps stable source", JsonSerializer.Deserialize<DeckSettings>(serialized)!.Apps[0].QuotaSource == ProviderId.Pi);
        using var handler = new NoNetwork();
        using var http = new HttpClient(handler);
        using var service = new UsageService(new Desktop(), new DataLocations("", "", "", "", "") { PiSessions = folder }, http, () => now);
        var snapshot = (await service.RefreshAsync(settings)).Single();
        check("Pi service reads usage without credential access or HTTP", handler.Calls == 0 && snapshot.UsageAvailable && snapshot.Entries.Count == 3 && snapshot.Quotas.Count == 0 && !snapshot.LiveQuota);

    }

    sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }
    }
    sealed class Desktop : IDesktopSources
    {
        public string? ReadCursorToken(string database) => throw new InvalidOperationException("Unexpected credential read");
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => throw new InvalidOperationException("Unexpected service discovery");
    }
}
