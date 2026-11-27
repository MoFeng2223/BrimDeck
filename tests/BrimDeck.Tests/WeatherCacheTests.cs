using BrimDeck.Core;

// 天气缓存：TTL 命中与过期、手动刷新的绕过层、同键并发只发一次、失败退回过期副本，以及历史用的 Cached 作用域。
static class WeatherCacheTests
{
    public static async Task Run(Action<string, bool> check)
    {
        // 独立临时根目录：测试既不写正式缓存，也不依赖用户目录是否存在。
        string originalRoot = WeatherFiles.Root;
        string root = Path.Combine(Path.GetTempPath(), "brimdeck-weather-tests-" + Guid.NewGuid().ToString("N"));
        WeatherFiles.UseRoot(root);
        string ns = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            // TTL 内第二次读取直接命中，不再调用 fetch。
            int calls = 0;
            Task<string?> Fetch(CancellationToken _) { Interlocked.Increment(ref calls); return Task.FromResult<string?>("v1"); }
            var first = await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
            var second = await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
            check("Weather cache: a hit inside the TTL is served without fetching again", first == "v1" && second == "v1" && calls == 1);

            // Fresh 只绕过「未过期直用」，退出后恢复缓存行为。
            calls = 0;
            using (WeatherCache.Fresh())
            {
                var bypassed = await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
                check("Weather cache: a manual refresh bypasses the TTL", bypassed == "v1" && calls == 1);
            }
            await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
            check("Weather cache: the bypass ends with its scope", calls == 1);

            // Cached 在 Fresh 内部临时归零：历史请求仍按 TTL 走缓存，退出后 Fresh 依旧有效。
            calls = 0;
            using (WeatherCache.Fresh())
            {
                using (WeatherCache.Cached())
                    await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
                check("Weather cache: Cached serves history from the TTL cache during a manual refresh", calls == 0);
                await WeatherCache.GetAsync(ns, "ttl", TimeSpan.FromMinutes(10), Fetch, CancellationToken.None);
                check("Weather cache: Cached restores the manual refresh when it ends", calls == 1);
            }

            // 同一键 8 个并发请求，闸门内只放一个 fetch 出去，其余复用它的结果。
            calls = 0;
            Task<string?> Slow(CancellationToken token) => Slowly();
            async Task<string?> Slowly()
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(60);
                return "shared";
            }
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                WeatherCache.GetAsync(ns, "single-flight", TimeSpan.FromMinutes(10), Slow, CancellationToken.None)));
            check("Weather cache: concurrent callers of one key make a single request",
                concurrent.All(value => value == "shared") && calls == 1);

            // 过期后 fetch 失败：按 allowStale 返回上一份数据，而不是 null。
            calls = 0;
            await WeatherCache.GetAsync(ns, "stale", TimeSpan.FromMinutes(10), _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<string?>("old");
            }, CancellationToken.None);
            var stale = await WeatherCache.GetAsync(ns, "stale", TimeSpan.Zero, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<string?>(null);
            }, CancellationToken.None);
            check("Weather cache: a failed refresh falls back to the expired copy", stale == "old" && calls == 2);
            var strict = await WeatherCache.GetAsync(ns, "stale", TimeSpan.Zero,
                _ => Task.FromResult<string?>(null), CancellationToken.None, allowStale: false);
            check("Weather cache: with stale copies disallowed a failed refresh returns nothing", strict is null && calls == 2);
            foreach (bool allowStale in new[] { true, false })
            {
                var fallback = await WeatherCache.GetAsync<string>(ns, "stale", TimeSpan.Zero,
                    _ => throw new System.Text.Json.JsonException(), CancellationToken.None, allowStale);
                check($"Invalid JSON respects stale policy {allowStale}", fallback == (allowStale ? "old" : null));
            }
            using (WeatherCache.Fresh())
            {
                var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                int requests = 0;
                Task<string?> ManualFetch(CancellationToken _) { requests++; return release.Task; }
                var leading = WeatherCache.GetAsync(ns, "parallel-refresh", TimeSpan.Zero, ManualFetch, CancellationToken.None);
                var waiting = WeatherCache.GetAsync(ns, "parallel-refresh", TimeSpan.Zero, ManualFetch, CancellationToken.None);
                release.SetResult("updated");
                var results = await Task.WhenAll(leading, waiting);
                check("Overlapping manual refreshes share a successful fetch", requests == 1 && results.All(value => value == "updated"));
            }
            check("Nonadjacent city hierarchy levels survive", City.Compose("New York", "County", "New York") == "New York · County · New York");
        }
        finally
        {
            WeatherFiles.UseRoot(originalRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch { }
        }
    }
}
