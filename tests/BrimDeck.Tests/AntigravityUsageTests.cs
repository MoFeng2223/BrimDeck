using BrimDeck.Core;

internal static class AntigravityUsageTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        var now = DateTimeOffset.Now;
        var desktopFolder = Path.Combine(root, "antigravity", "conversations");
        var cliFolder = Path.Combine(root, "antigravity-cli", "conversations");
        Directory.CreateDirectory(desktopFolder); Directory.CreateDirectory(cliFolder);
        var desktopDb = Path.Combine(desktopFolder, "desktop.db"); var cliDb = Path.Combine(cliFolder, "cli.db");
        File.WriteAllText(desktopDb, ""); File.WriteAllText(cliDb, "");
        var fake = new Databases();
        // A request whose cache read exceeds its input, repeated in a second step, plus one CLI request.
        fake.Steps[desktopDb] = [Step(now.AddHours(-1), "m1", input: 5000, output: 60, cacheRead: 12000), Step(now.AddHours(-1), "m1", 5000, 60, 12000), Step(now.AddDays(-40), "old", 5, 5, 0)];
        fake.Models[desktopDb] = [Generation("m1", "gemini-3.8-flash")];
        fake.Steps[cliDb] = [Step(now.AddMinutes(-5), "c1", 12000, 150, 0)];
        fake.Models[cliDb] = [Generation("c1", "gemini-3.8-flash")];

        var snapshot = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(snapshot, fake, [desktopFolder, cliFolder], now.AddDays(-29), now);
        check("Antigravity desktop and CLI conversations are both counted, each message once",
            snapshot.UsageAvailable && snapshot.Entries.Select(e => e.Key).OrderBy(k => k).SequenceEqual(["ag:c1", "ag:m1"]));
        var desktop = snapshot.Entries.Single(e => e.Key == "ag:m1");
        check("Antigravity cache reads are counted in addition to input and use the generation's model name",
            desktop is { Input: 5000, CacheRead: 12000, Output: 60, Model: "gemini-3.8-flash" });

        var missing = new ProviderSnapshot(ProviderId.Antigravity);
        new AntigravityUsage().Read(missing, fake, [Path.Combine(root, "none")], now.AddDays(-29), now);
        check("Missing Antigravity conversation folders are unavailable, not zero", !missing.UsageAvailable);
    }

    // CortexStepMetadata { 1: Timestamp { 1: seconds }, 9: ModelUsageStats { 2 input, 3 output, 5 cache read, 7 message id } }
    private static string?[] Step(DateTimeOffset time, string id, long input, long output, long cacheRead) =>
        [Convert.ToHexString([.. Message(1, Varint(1, (ulong)time.ToUnixTimeSeconds())),
            .. Message(9, [.. Varint(2, (ulong)input), .. Varint(3, (ulong)output), .. Varint(5, (ulong)cacheRead), .. Message(7, System.Text.Encoding.UTF8.GetBytes(id))])])];

    // gen_metadata { 1: ChatModelMetadata { 4: ModelUsageStats { 7 message id }, 19: response_model } }
    private static string?[] Generation(string id, string model) =>
        [Convert.ToHexString(Message(1, [.. Message(4, Message(7, System.Text.Encoding.UTF8.GetBytes(id))), .. Message(19, System.Text.Encoding.UTF8.GetBytes(model))]))];

    private static byte[] Varint(int field, ulong value) => [.. Raw((ulong)(field << 3)), .. Raw(value)];
    private static byte[] Message(int field, byte[] data) => [.. Raw((ulong)(field << 3 | 2)), .. Raw((ulong)data.Length), .. data];
    private static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 0x7F); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 0x80)); } while (value != 0);
        return [.. bytes];
    }

    private sealed class Databases : IDesktopSources
    {
        public Dictionary<string, List<string?[]>> Steps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string?[]>> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ReadCursorToken(string database) => null;
        public Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([]);
        public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql) =>
            (sql.Contains("FROM steps") ? Steps : Models).GetValueOrDefault(database) ?? [];
    }
}
