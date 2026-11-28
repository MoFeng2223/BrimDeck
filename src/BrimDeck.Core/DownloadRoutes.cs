using System.Diagnostics;
using System.Net.Http.Headers;

namespace BrimDeck.Core;

// gh-proxy (gh-proxy.org) fetches a public GitHub URL on the caller's behalf: the mirror address is its host followed by
// the full GitHub URL. A mirror could serve any bytes, so only files whose size and SHA-256 were read from GitHub directly
// may be downloaded through one.
public static class GitHubMirrors
{
    public static readonly string[] Hosts = ["v4.gh-proxy.org", "cdn.gh-proxy.org", "v6.gh-proxy.org", "axisnow.gh-proxy.org"];

    public static IReadOnlyList<Uri> For(Uri direct) => [.. Hosts.Select(host => new Uri("https://" + host + "/" + direct.AbsoluteUri))];
}

// How the addresses of a file are measured: each reads the first Bytes of the file and is timed until they arrive.
// GitHub is measured first and alone; when it delivers them within DirectEnough, it is used and no mirror is contacted.
public sealed record RouteProbe(int Bytes, TimeSpan DirectEnough, TimeSpan DirectTimeout, TimeSpan MirrorTimeout)
{
    public static readonly RouteProbe Default = new(512 * 1024, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
}

// Orders the addresses a file is tried from: the direct address and its mirrors. Addresses that answered are ordered by the
// time they took; those that failed or sent a file of the wrong size follow, the direct address first.
public sealed class DownloadRoutes(HttpClient http, Func<Uri, IReadOnlyList<Uri>> mirrors, RouteProbe? probe = null)
{
    private readonly RouteProbe _probe = probe ?? RouteProbe.Default;

    public async Task<IReadOnlyList<Uri>> OrderAsync(Uri direct, long size, CancellationToken cancellation)
    {
        var others = mirrors(direct);
        if (others.Count == 0) return [direct];
        var directTime = await ProbeAsync(direct, size, _probe.DirectTimeout, cancellation).ConfigureAwait(false);
        // A direct address that is fast enough is used without contacting a mirror; the mirrors stay behind it in case the
        // download itself fails.
        if (directTime <= _probe.DirectEnough) return [direct, .. others];
        var times = await Task.WhenAll(others.Select(uri => ProbeAsync(uri, size, _probe.MirrorTimeout, cancellation))).ConfigureAwait(false);
        var measured = others.Select((uri, index) => (uri, time: times[index])).Prepend((uri: direct, time: directTime)).ToList();
        return [.. measured.Where(route => route.time is not null).OrderBy(route => route.time).Select(route => route.uri),
            .. measured.Where(route => route.time is null).Select(route => route.uri)];
    }

    // The time until the first bytes of the file arrived, or null when the address failed, was too slow or announced a file of
    // another size.
    private async Task<TimeSpan?> ProbeAsync(Uri uri, long size, TimeSpan timeout, CancellationToken cancellation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, _probe.Bytes - 1);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            // A partial answer names the whole length in Content-Range; a server that ignores the range sends the whole file.
            var announced = response.Content.Headers.ContentRange?.Length ?? (response.StatusCode == System.Net.HttpStatusCode.PartialContent ? null : response.Content.Headers.ContentLength);
            if (announced is { } length && length != size) return null;
            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var buffer = new byte[16384]; long total = 0; int read;
            var wanted = Math.Min(_probe.Bytes, size);
            while (total < wanted && (read = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0) total += read;
            return total >= wanted ? watch.Elapsed : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }
}
