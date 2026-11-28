using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BrimDeck.Core;

internal static class DownloadRouteTests
{
    private static readonly Uri Direct = new("https://github.test/releases/download/v0.2.0/BrimDeck-0.2.0-Setup.exe");
    private static readonly RouteProbe Probe = new(1000, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(1500));

    public static async Task Run(string root, Action<string, bool> check)
    {
        var file = RandomNumberGenerator.GetBytes(50_000);
        var mirrors = GitHubMirrors.For(Direct);
        check("gh-proxy mirrors put the full GitHub address after their host",
            mirrors.Count == 4 && mirrors[0].AbsoluteUri == "https://v4.gh-proxy.org/" + Direct.AbsoluteUri);

        var server = new RouteServer(file);
        using var http = new HttpClient(server);
        var routes = new DownloadRoutes(http, GitHubMirrors.For, Probe);

        var order = await routes.OrderAsync(Direct, file.Length, CancellationToken.None);
        check("a fast GitHub is used first without contacting any mirror", order[0] == Direct && order.Count == 5 && server.Hosts.All(host => host == "github.test"));

        server.Reset(); server.Delays["github.test"] = 600; server.Delays["v4.gh-proxy.org"] = 400; server.Delays["v6.gh-proxy.org"] = 250;
        server.Delays["axisnow.gh-proxy.org"] = 100;
        order = await routes.OrderAsync(Direct, file.Length, CancellationToken.None);
        check("a slow GitHub lets all mirrors be measured at once and the fastest comes first",
            order.Select(uri => uri.Host).SequenceEqual(["cdn.gh-proxy.org", "axisnow.gh-proxy.org", "v6.gh-proxy.org", "v4.gh-proxy.org", "github.test"]) &&
            server.Hosts.Count(host => host != "github.test") == 4);

        server.Reset(); server.Failing.Add("github.test"); server.Failing.Add("cdn.gh-proxy.org"); server.WrongSize.Add("v4.gh-proxy.org");
        order = await routes.OrderAsync(Direct, file.Length, CancellationToken.None);
        check("failed addresses and a wrong file size go last, GitHub first among them",
            order.Select(uri => uri.Host).Take(2).Order().SequenceEqual(["axisnow.gh-proxy.org", "v6.gh-proxy.org"]) &&
            order.Select(uri => uri.Host).Skip(2).SequenceEqual(["github.test", "v4.gh-proxy.org", "cdn.gh-proxy.org"]));

        server.Reset(); server.Delays["github.test"] = 5000;
        order = await routes.OrderAsync(Direct, file.Length, CancellationToken.None);
        check("a GitHub that does not answer in time follows the mirrors", order[^1] == Direct && order.Count == 5);

        // The backend tries the next address when one sends a damaged file.
        var manifest = new UpdateManifest("0.2.0", "notes", "BrimDeck-0.2.0-Setup.exe", file.Length, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
        server.Reset(); server.Manifest = manifest.ToJson(); server.Delays["github.test"] = 600; server.Corrupt.Add("cdn.gh-proxy.org");
        server.Delays["axisnow.gh-proxy.org"] = 100; server.Delays["v6.gh-proxy.org"] = 250; server.Delays["v4.gh-proxy.org"] = 400;
        var directory = Path.Combine(root, "download-routes");
        var backend = new InstallerUpdateBackend(http, new Uri("https://github.test/latest/update.json"), (_, _) => Direct, "0.1.0", directory,
            UpdateMode.Installer, _ => true, routes: routes);
        var release = await backend.CheckAsync(CancellationToken.None);
        server.Downloads.Clear();
        await backend.DownloadAsync(release!, _ => { }, CancellationToken.None);
        check("a damaged file from one address is discarded and the next address is used",
            server.Downloads.SequenceEqual(["cdn.gh-proxy.org", "axisnow.gh-proxy.org"]) && backend.PendingRelease == release &&
            File.ReadAllBytes(Path.Combine(directory, manifest.File)).SequenceEqual(file));

        server.Reset(); server.Delays["github.test"] = 600;
        foreach (var host in GitHubMirrors.Hosts.Append("github.test")) server.Corrupt.Add(host);
        backend = new InstallerUpdateBackend(http, new Uri("https://github.test/latest/update.json"), (_, _) => Direct, "0.1.0", Path.Combine(root, "download-routes-bad"),
            UpdateMode.Installer, _ => true, routes: routes);
        release = await backend.CheckAsync(CancellationToken.None);
        server.Downloads.Clear(); bool rejected = false;
        try { await backend.DownloadAsync(release!, _ => { }, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        check("when every address fails, the checksum error is reported and nothing is kept",
            rejected && server.Downloads.Count == 5 && backend.PendingRelease is null && !File.Exists(Path.Combine(root, "download-routes-bad", manifest.File + ".partial")));

        server.Reset();
        using var cancel = new CancellationTokenSource();
        server.OnDownload = cancel.Cancel;
        server.Downloads.Clear(); bool cancelled = false;
        try { await backend.DownloadAsync(release!, _ => { }, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("a cancelled download does not move on to the next address", cancelled && server.Downloads.Count == 1);
    }

    private sealed class RouteServer(byte[] file) : HttpMessageHandler
    {
        public Dictionary<string, int> Delays { get; } = [];
        public HashSet<string> Failing { get; } = [];
        public HashSet<string> WrongSize { get; } = [];
        public HashSet<string> Corrupt { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<string> Downloads { get; } = [];
        public string Manifest { get; set; } = "";
        public Action? OnDownload { get; set; }

        public void Reset() { Delays.Clear(); Failing.Clear(); WrongSize.Clear(); Corrupt.Clear(); Downloads.Clear(); OnDownload = null; lock (Hosts) Hosts.Clear(); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/update.json")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest) };
            var host = uri.Host;
            lock (Hosts) Hosts.Add(host);
            if (Delays.TryGetValue(host, out var delay)) await Task.Delay(delay, cancellationToken);
            if (Failing.Contains(host)) throw new HttpRequestException("unreachable");
            if (host != "github.test" && uri.PathAndQuery.TrimStart('/') != Direct.AbsoluteUri) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var body = Corrupt.Contains(host) ? [.. file.Take(file.Length - 1), (byte)(file[^1] ^ 0xFF)] : file;
            var total = WrongSize.Contains(host) ? file.Length + 1 : file.Length;
            if (request.Headers.Range?.Ranges.SingleOrDefault() is { } range)
            {
                var end = (int)Math.Min(range.To ?? total - 1, body.Length - 1);
                var content = new ByteArrayContent(body, 0, end + 1);
                content.Headers.ContentRange = new ContentRangeHeaderValue(0, end, total);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            }
            Downloads.Add(host);
            OnDownload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }
}
