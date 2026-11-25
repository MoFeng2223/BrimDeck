using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Whether the local proxy status is wanted. Watch: at least one panel shows the proxy icon on its collapsed surface.
// None: every watcher and timer stops so reading costs nothing while off, hidden or with the display off.
internal enum ProxyDemand { None, Watch }

// Read-only monitor for Clash Verge (and compatible Clash / Mihomo desktop clients) on Windows.
// Reads local IPC (named pipe / loopback HTTP /configs), local config files with shared read-only access,
// Windows system proxy registry state and active TUN network interfaces — never writes files or modifies the proxy client.
internal sealed class ProxyWatcher : IDisposable
{
    private sealed record ClientProfile(string Name, string[] Processes, string? PipeName, string[] PipePrefixes, Func<string[]> ConfigDirs, string[] ConfigFiles, string? VergeFile);

    private static readonly ClientProfile[] Clients =
    [
        new("Clash Verge",
            ["clash-verge.exe", "verge-mihomo.exe", "verge-mihomo-alpha.exe"],
            "verge-mihomo",
            ["verge-mihomo-production-", "verge-mihomo-sidecar-"],
            () =>
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrEmpty(appData) ? [] :
                [
                    Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"),
                    Path.Combine(appData, "clash-verge")
                ];
            },
            ["clash-verge.yaml", "config.yaml"],
            "verge.yaml"),
        new("Clash Nyanpasu",
            ["clash-nyanpasu.exe", "nyanpasu-mihomo.exe"],
            null,
            [],
            () =>
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrEmpty(appData) ? [] : [Path.Combine(appData, "clash-nyanpasu")];
            },
            ["clash-nyanpasu.yaml", "config.yaml"],
            "verge.yaml"),
        new("Clash Party",
            ["Clash Party.exe", "Mihomo Party.exe"],
            null,
            [],
            () =>
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrEmpty(appData) ? [] :
                [
                    Path.Combine(appData, "mihomo-party", "work"),
                    Path.Combine(appData, "clash-party", "work")
                ];
            },
            ["config.yaml"],
            null),
        new("Clash for Windows",
            ["Clash for Windows.exe"],
            null,
            [],
            () =>
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home) ? [] : [Path.Combine(home, ".config", "clash")];
            },
            ["config.yaml"],
            null),
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PipeAclBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(450);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _linger;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _debounce;
    private readonly List<FileSystemWatcher> _fileWatchers = [];
    private ProxyDemand _demand = ProxyDemand.None, _running = ProxyDemand.None;
    private CancellationTokenSource? _runCts;
    private int _polling;
    private int _repollRequested;
    private readonly Dictionary<string, long> _blockedPipes = new(StringComparer.OrdinalIgnoreCase);
    private string? _activePipe;
    private string? _lastClient;
    private readonly Dictionary<string, (DateTime WrittenUtc, long Length, ClashConfigValues Value)> _configCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime WrittenUtc, long Length, VergeConfigValues Value)> _vergeCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ProxySnapshot Snapshot { get; private set; } = ProxySnapshot.Stopped;
    public ProxyDemand Demand => _running;
    // Raised on the UI thread whenever Snapshot changes.
    public event Action? Changed;

    public ProxyWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _linger = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = MemoryPlan.For(MemoryMode.Balanced).ReadingsLinger };
        _linger.Tick += (_, _) => { _linger.Stop(); Run(_demand); };
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollInterval };
        _poll.Tick += (_, _) => QueuePoll();
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); QueuePoll(); };
    }

    public void UsePlan(MemoryPlan plan) => _linger.Interval = plan.ReadingsLinger;

    public void SetDemand(ProxyDemand demand)
    {
        if (_disposed) return;
        _demand = demand;
        if (demand < _running)
        {
            if (!_linger.IsEnabled) _linger.Start();
            return;
        }
        _linger.Stop();
        Run(demand);
    }

    public void Refresh()
    {
        if (_running == ProxyDemand.Watch) QueuePoll();
    }

    private void Run(ProxyDemand demand)
    {
        if (demand == _running) return;
        _running = demand;
        if (demand == ProxyDemand.None)
        {
            StopWatchers();
            _poll.Stop();
            _debounce.Stop();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
            return;
        }
        _runCts = new CancellationTokenSource();
        StartWatchers();
        _poll.Start();
        QueuePoll();
    }

    private void StartWatchers()
    {
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        foreach (var client in Clients)
        {
            foreach (var dir in client.ConfigDirs())
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(dir, "*.yaml")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += ConfigFileChanged;
                    watcher.Created += ConfigFileChanged;
                    watcher.Renamed += ConfigFileChanged;
                    _fileWatchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
    }

    private void StopWatchers()
    {
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        foreach (var watcher in _fileWatchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= ConfigFileChanged;
                watcher.Created -= ConfigFileChanged;
                watcher.Renamed -= ConfigFileChanged;
                watcher.Dispose();
            }
            catch { }
        }
        _fileWatchers.Clear();
    }

    private void NetworkAddressChanged(object? sender, EventArgs e) => ScheduleDebouncedPoll();
    private void ConfigFileChanged(object sender, FileSystemEventArgs e) => ScheduleDebouncedPoll();

    private void ScheduleDebouncedPoll()
    {
        if (_disposed || _running != ProxyDemand.Watch) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed || _running != ProxyDemand.Watch) return;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private void QueuePoll()
    {
        if (_disposed || _running != ProxyDemand.Watch || _runCts is not { IsCancellationRequested: false } cts) return;
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            Interlocked.Exchange(ref _repollRequested, 1);
            return;
        }
        var token = cts.Token;
        _ = Task.Run(() => PollAsync(token), token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _repollRequested, 0);
                var next = await ReadSnapshotAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _disposed) return;
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (_disposed || _running != ProxyDemand.Watch) return;
                    if (next == Snapshot) return;
                    Snapshot = next;
                    Changed?.Invoke();
                });
            }
            while (!ct.IsCancellationRequested && Interlocked.Exchange(ref _repollRequested, 0) == 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            if (!ct.IsCancellationRequested && !_disposed && Interlocked.Exchange(ref _repollRequested, 0) == 1)
                QueuePoll();
        }
    }

    private async Task<ProxySnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var client = DetectRunningClient();
        if (client is null)
        {
            _lastClient = null;
            _activePipe = null;
            _blockedPipes.Clear();
            return ProxySnapshot.Stopped;
        }
        if (!string.Equals(_lastClient, client.Name, StringComparison.Ordinal))
        {
            _lastClient = client.Name;
            _activePipe = null;
            _blockedPipes.Clear();
        }

        ProxyMode? apiMode = null;
        bool? apiTun = null;
        string? apiTunDevice = null;
        var triedPipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Named Pipe probe first (Clash Verge Rev's default IPC channel; zero disk I/O when reachable).
        // Prefer cached active pipe, then live enumerated v2.4+ production/sidecar pipes, then legacy pipe name.
        var initialPipes = new List<string>(4);
        if (_activePipe is { Length: > 0 } cachedPipe) initialPipes.Add(cachedPipe);
        var pipeResult = await ProbeCandidatePipesAsync(initialPipes, triedPipes, ct).ConfigureAwait(false);
        if (!pipeResult.Ok)
        {
            _activePipe = null;
            var livePipes = EnumerateLocalPipes(client.PipePrefixes);
            if (client.PipeName is { Length: > 0 } legacyPipe) livePipes.Add(legacyPipe);
            pipeResult = await ProbeCandidatePipesAsync(livePipes, triedPipes, ct).ConfigureAwait(false);
        }
        if (pipeResult.Ok)
        {
            apiMode = pipeResult.Mode;
            apiTun = pipeResult.Tun;
            apiTunDevice = pipeResult.TunDevice;
        }

        // Only inspect local YAML files when the named pipe did not already return both mode and TUN state.
        ClashConfigValues config = default;
        VergeConfigValues verge = default;
        if (apiMode is null || apiTun is null)
            (config, verge) = ReadClientFiles(client);

        // 1b. If config YAML declares a custom external-controller-pipe not yet tried, probe it now.
        if (apiMode is null && ProxyParsers.NormalizePipeName(config.ExternalControllerPipe) is { } configuredPipe)
        {
            var cfgPipeResult = await ProbeCandidatePipesAsync([configuredPipe], triedPipes, ct).ConfigureAwait(false);
            if (cfgPipeResult.Ok)
            {
                apiMode = cfgPipeResult.Mode;
                apiTun = cfgPipeResult.Tun;
                apiTunDevice = cfgPipeResult.TunDevice;
            }
        }

        // 2. Loopback HTTP controller probe (fallback when external-controller is bound locally).
        if (apiMode is null && ProxyParsers.NormalizeLoopbackController(config.ExternalController) is { } baseUri)
        {
            var httpResult = await ProbeLoopbackHttpAsync(baseUri, config.Secret, ct).ConfigureAwait(false);
            if (httpResult.Ok)
            {
                apiMode = httpResult.Mode;
                apiTun = httpResult.Tun;
                apiTunDevice = httpResult.TunDevice;
            }
        }

        // 3. Authoritative Windows OS signals: HKCU system proxy and active Clash/Mihomo TUN adapter.
        bool? osSysProxy = ReadWindowsSystemProxy();
        bool? osTun = apiTun is null ? ReadWindowsTunInterface(apiTunDevice ?? config.TunDevice) : null;

        return ProxyParsers.Resolve(true, client.Name, apiMode, apiTun, config, verge, osSysProxy, osTun);
    }

    private async Task<ProbeResult> ProbeCandidatePipesAsync(IEnumerable<string> candidates, HashSet<string> tried, CancellationToken ct)
    {
        long now = Environment.TickCount64;
        foreach (var raw in candidates)
        {
            if (string.IsNullOrEmpty(raw) || !tried.Add(raw)) continue;
            if (_blockedPipes.TryGetValue(raw, out var until) && now < until) continue;
            var result = await ProbeNamedPipeAsync(raw, ct).ConfigureAwait(false);
            if (result.AccessDenied)
            {
                _blockedPipes[raw] = Environment.TickCount64 + (long)PipeAclBackoff.TotalMilliseconds;
                if (string.Equals(_activePipe, raw, StringComparison.OrdinalIgnoreCase)) _activePipe = null;
                continue;
            }
            if (result.Ok)
            {
                _blockedPipes.Remove(raw);
                _activePipe = raw;
                return result;
            }
        }
        return default;
    }

    private static List<string> EnumerateLocalPipes(string[] prefixes)
    {
        var found = new List<string>();
        if (prefixes.Length == 0) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in prefixes)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\", prefix + "*"))
                {
                    if (ProxyParsers.NormalizePipeName(path) is { } name && seen.Add(name))
                        found.Add(name);
                }
            }
            catch { }
        }
        return found;
    }

    private static ClientProfile? DetectRunningClient()
    {
        foreach (var client in Clients)
        {
            foreach (var process in client.Processes)
            {
                if (ProcessSnapshot.AnyRecent(process)) return client;
            }
        }
        return null;
    }

    private (ClashConfigValues Config, VergeConfigValues Verge) ReadClientFiles(ClientProfile client)
    {
        string? activeDir = null;
        foreach (var dir in client.ConfigDirs())
        {
            if (Directory.Exists(dir)) { activeDir = dir; break; }
        }
        if (activeDir is null) return (default, default);

        VergeConfigValues verge = default;
        if (client.VergeFile is { Length: > 0 } vergeName)
            verge = ReadCachedVergeYaml(Path.Combine(activeDir, vergeName));

        // Read every candidate config YAML in newest-first order so live mode edits (in config.yaml or clash-verge.yaml) win,
        // while missing fields (external-controller, secret, tun.device) still fill in from the companion file.
        var files = client.ConfigFiles
            .Select(name => Path.Combine(activeDir, name))
            .Where(File.Exists)
            .OrderByDescending(SafeLastWriteUtc)
            .ToList();

        var mode = ProxyMode.Unknown;
        bool? tunEnabled = null;
        string? tunDevice = null, controller = null, secret = null, controllerPipe = null;
        foreach (var path in files)
        {
            var parsed = ReadCachedClashYaml(path);
            if (mode == ProxyMode.Unknown && parsed.Mode != ProxyMode.Unknown) mode = parsed.Mode;
            tunEnabled ??= parsed.TunEnabled;
            tunDevice ??= parsed.TunDevice;
            controller ??= parsed.ExternalController;
            secret ??= parsed.Secret;
            controllerPipe ??= parsed.ExternalControllerPipe;
        }
        return (new ClashConfigValues(mode, tunEnabled, tunDevice, controller, secret, controllerPipe), verge);
    }

    private VergeConfigValues ReadCachedVergeYaml(string path)
    {
        if (!TryFileStamp(path, out var stamp)) return default;
        if (_vergeCache.TryGetValue(path, out var cached) && cached.WrittenUtc == stamp.WrittenUtc && cached.Length == stamp.Length)
            return cached.Value;
        var text = ReadSharedText(path);
        if (text is null) return default;
        var parsed = ProxyParsers.ParseVergeYaml(text);
        _vergeCache[path] = (stamp.WrittenUtc, stamp.Length, parsed);
        return parsed;
    }

    private ClashConfigValues ReadCachedClashYaml(string path)
    {
        if (!TryFileStamp(path, out var stamp)) return default;
        if (_configCache.TryGetValue(path, out var cached) && cached.WrittenUtc == stamp.WrittenUtc && cached.Length == stamp.Length)
            return cached.Value;
        var text = ReadSharedText(path);
        if (text is null) return default;
        var parsed = ProxyParsers.ParseClashConfigYaml(text);
        _configCache[path] = (stamp.WrittenUtc, stamp.Length, parsed);
        return parsed;
    }

    private static bool TryFileStamp(string path, out (DateTime WrittenUtc, long Length) stamp)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { stamp = default; return false; }
            stamp = (info.LastWriteTimeUtc, info.Length);
            return true;
        }
        catch { stamp = default; return false; }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static string? ReadSharedText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Guard against unexpectedly huge files while easily covering large merged Clash profiles.
            if (stream.Length > 8 * 1024 * 1024) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private readonly record struct ProbeResult(bool Ok, ProxyMode Mode, bool? Tun, string? TunDevice, bool AccessDenied = false);

    private static async Task<ProbeResult> ProbeNamedPipeAsync(string pipeName, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async (_, token) =>
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(300, token).ConfigureAwait(false);
                        return pipe;
                    }
                    catch
                    {
                        pipe.Dispose();
                        throw;
                    }
                }
            };
            using var http = new HttpClient(handler) { Timeout = ProbeTimeout };
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/configs");
            req.Headers.ConnectionClose = true;
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return default;
            var body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ProxyParsers.TryParseMihomoConfigs(body, out var mode, out var tun, out var dev)
                ? new ProbeResult(true, mode, tun, dev)
                : default;
        }
        catch (UnauthorizedAccessException) { return new ProbeResult(false, ProxyMode.Unknown, null, null, AccessDenied: true); }
        catch (HttpRequestException ex) when (ex.InnerException is UnauthorizedAccessException) { return new ProbeResult(false, ProxyMode.Unknown, null, null, AccessDenied: true); }
        catch { return default; }
    }

    private static async Task<ProbeResult> ProbeLoopbackHttpAsync(Uri baseUri, string? secret, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.Zero
            };
            using var http = new HttpClient(handler) { Timeout = ProbeTimeout };
            var endpoint = new Uri(baseUri, "/configs");
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.ConnectionClose = true;
            if (!string.IsNullOrWhiteSpace(secret))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return default;
            var body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ProxyParsers.TryParseMihomoConfigs(body, out var mode, out var tun, out var dev)
                ? new ProbeResult(true, mode, tun, dev)
                : default;
        }
        catch { return default; }
    }

    private static bool? ReadWindowsSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
            if (key is null) return null;
            int enabled = key.GetValue("ProxyEnable") is int v ? v : 0;
            if (enabled == 0) return false;
            var server = key.GetValue("ProxyServer") as string;
            return !string.IsNullOrWhiteSpace(server);
        }
        catch { return null; }
    }

    private static bool? ReadWindowsTunInterface(string? configuredDevice)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (ProxyParsers.IsClashTunInterface(nic.Name, nic.Description, configuredDevice))
                    return true;
            }
            return false;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatchers();
        _linger.Stop();
        _poll.Stop();
        _debounce.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }
}
