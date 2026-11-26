using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Active Mihomo controller client for the expanded Proxy control page (DeckPage.Proxy).
// Runs strictly on demand: opens /traffic streaming and polls /proxies + /connections ONLY while the page is open.
// Uses only Mihomo's standard local controller API (Named Pipe or loopback HTTP) and read-only local profile metadata;
// never writes Windows registry or Clash Verge configuration files.
internal sealed class ProxyController : IDisposable
{
    private const int HistoryCapacity = 40;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _dispatcher;
    private readonly ProxyWatcher _watcher;
    private readonly DispatcherTimer _statsTimer;
    private readonly Queue<ProxyTrafficSample> _history = new(HistoryCapacity + 2);
    private readonly HashSet<string> _testingGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _testingNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime WrittenUtc, long Length, ProxySubscriptionInfo? Value)> _profilesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime WrittenUtc, long Length, ProxyLatencyConfig Value)> _vergeControlCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _active;
    private bool _disposed;
    private CancellationTokenSource? _activeCts;
    private CancellationTokenSource? _groupTestCts;
    private int _statsTickCount;
    private int _refreshing;
    private int _repollPending;
    private int _repollIncludeProxies;
    private long _mutationVersion;
    private long _sessionId;
    private long _nextTestOwner;
    private string? _endpointKey;

    public ProxyControlSnapshot Snapshot { get; private set; } = ProxyControlSnapshot.Empty;
    public IReadOnlyList<ProxyTrafficSample> TrafficHistory => [.. _history];
    public bool IsTestingGroup(string groupName) => _testingGroups.Contains(groupName);
    public bool IsTestingNode(string nodeName) => _testingNodes.ContainsKey(nodeName);
    public bool BusyModeSwitch { get; private set; }

    internal void SetPreviewState(ProxyControlSnapshot snapshot, IReadOnlyList<ProxyTrafficSample> traffic)
    {
        Snapshot = snapshot;
        _history.Clear();
        foreach (var sample in traffic) _history.Enqueue(sample);
    }

    // Raised on UI thread ONLY when status, groups, node selection/delays, subscription or busy state actually changed.
    public event Action? StateChanged;
    // Raised on UI thread for live traffic/connections/memory updates (lightweight in-place repaint, zero tree rebuild).
    public event Action? TrafficChanged;

    public ProxyController(Dispatcher dispatcher, ProxyWatcher watcher)
    {
        _dispatcher = dispatcher;
        _watcher = watcher;
        _watcher.Changed += OnWatcherChanged;
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = StatsInterval };
        _statsTimer.Tick += (_, _) =>
        {
            if (!_active || _disposed) return;
            _statsTickCount++;
            bool testing = _testingGroups.Count > 0 || _testingNodes.Count > 0;
            if (_statsTickCount % 4 == 0 && !testing) QueueRefresh(includeProxies: true);
            else QueueRefresh(includeProxies: false);
        };
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (!active)
        {
            _statsTimer.Stop();
            CancelActiveWork();
            return;
        }
        _statsTickCount = 0;
        StartActiveWork();
    }

    public void RefreshNow()
    {
        if (!_active || _disposed) return;
        _watcher.Refresh();
        QueueRefresh(includeProxies: true);
    }

    private void OnWatcherChanged()
    {
        if (_disposed) return;
        var status = _watcher.Snapshot;
        var endpoint = _watcher.Endpoint;
        var newKey = EndpointKey(endpoint);
        bool endpointChanged = !string.Equals(_endpointKey, newKey, StringComparison.Ordinal);
        _endpointKey = newKey;

        if (!status.Running)
        {
            CancelActiveWork();
            _history.Clear();
            bool hadContent = Snapshot.Status.Running || Snapshot.ApiConnected || Snapshot.Groups.Count > 0;
            Snapshot = ProxyControlSnapshot.Empty;
            if (hadContent) StateChanged?.Invoke();
            return;
        }

        bool statusChanged = status != Snapshot.Status;
        bool modeChanged = status.Mode != Snapshot.Status.Mode;
        if (endpointChanged)
        {
            // Switching controller endpoints starts fresh cumulative traffic counters.
            _history.Clear();
            Snapshot = Snapshot with { Status = status, Traffic = default, Connections = default };
            StateChanged?.Invoke();
        }
        else if (statusChanged)
        {
            Snapshot = Snapshot with { Status = status };
            StateChanged?.Invoke();
        }

        if (_active)
        {
            if (endpointChanged || _activeCts is null)
            {
                CancelActiveWork();
                StartActiveWork();
            }
            else if (modeChanged)
            {
                QueueRefresh(includeProxies: true);
            }
        }
    }

    private void StartActiveWork()
    {
        _endpointKey = EndpointKey(_watcher.Endpoint);
        if (_watcher.Snapshot != Snapshot.Status)
            Snapshot = Snapshot with { Status = _watcher.Snapshot };
        if (!_watcher.Snapshot.Running)
        {
            _history.Clear();
            StateChanged?.Invoke();
            return;
        }

        long sessionId = Interlocked.Increment(ref _sessionId);
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;
        _statsTimer.Start();
        QueueRefresh(includeProxies: true);
        _ = Task.Run(() => RunTrafficLoopAsync(sessionId, ct));
    }

    private void CancelActiveWork()
    {
        Interlocked.Increment(ref _sessionId);
        _statsTimer.Stop();
        try { _groupTestCts?.Cancel(); } catch { }
        _groupTestCts?.Dispose();
        _groupTestCts = null;
        try { _activeCts?.Cancel(); } catch { }
        _activeCts?.Dispose();
        _activeCts = null;
    }

    private void QueueRefresh(bool includeProxies)
    {
        if (!_active || _disposed || _activeCts is not { IsCancellationRequested: false } cts) return;
        if (includeProxies) Interlocked.Exchange(ref _repollIncludeProxies, 1);
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            Interlocked.Exchange(ref _repollPending, 1);
            return;
        }
        var token = cts.Token;
        long sessionId = Volatile.Read(ref _sessionId);
        // Do not pass token to Task.Run scheduling so cancellation can never skip releasing the _refreshing latch.
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    if (token.IsCancellationRequested || _disposed || Volatile.Read(ref _sessionId) != sessionId) return;
                    Interlocked.Exchange(ref _repollPending, 0);
                    bool proxies = Interlocked.Exchange(ref _repollIncludeProxies, 0) == 1;
                    var version = Volatile.Read(ref _mutationVersion);
                    var endpoint = ResolveWorkingEndpoint();
                    var status = _watcher.Snapshot;
                    await RefreshCoreAsync(status, endpoint, proxies, version, sessionId, token).ConfigureAwait(false);
                }
                while (!token.IsCancellationRequested && Volatile.Read(ref _sessionId) == sessionId && Interlocked.Exchange(ref _repollPending, 0) == 1);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                if (!token.IsCancellationRequested && !_disposed && Volatile.Read(ref _sessionId) == sessionId && Interlocked.Exchange(ref _repollPending, 0) == 1)
                    QueueRefresh(Interlocked.Exchange(ref _repollIncludeProxies, 0) == 1);
            }
        });
    }

    private ProxyEndpoint? ResolveWorkingEndpoint()
    {
        var ep = _watcher.Endpoint;
        if (ep is not null && (!string.IsNullOrEmpty(ep.PipeName) || ep.HttpBaseUri is not null))
            return ep;
        // Fallback live discovery of Clash Verge Rev named pipes so mode/node commands work even during a watcher poll gap.
        try
        {
            foreach (var prefix in new[] { "verge-mihomo-production-", "verge-mihomo-sidecar-", "verge-mihomo" })
            {
                foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\", prefix + "*"))
                {
                    if (ProxyParsers.NormalizePipeName(path) is { } pipeName)
                        return new ProxyEndpoint(ep?.ClientName ?? "Clash Verge", pipeName, ep?.HttpBaseUri, ep?.Secret, ep?.ConfigDir);
                }
            }
        }
        catch { }
        return ep;
    }

    private async Task RefreshCoreAsync(ProxySnapshot status, ProxyEndpoint? endpoint, bool includeProxies, long expectedVersion, long sessionId, CancellationToken ct)
    {
        if (!status.Running || endpoint is null)
        {
            PublishUpdate(ProxyControlSnapshot.Empty, statsOnly: false, expectedVersion, sessionId);
            return;
        }

        var (subscription, latencyCfg) = ReadLocalVergeMetadata(endpoint.ConfigDir);
        bool hasTransport = !string.IsNullOrEmpty(endpoint.PipeName) || endpoint.HttpBaseUri is not null;
        if (!hasTransport)
        {
            PublishUpdate(Snapshot with
            {
                Status = status,
                ApiConnected = false,
                Groups = [],
                HiddenGroups = [],
                Subscription = subscription,
                LatencyConfig = latencyCfg,
                Error = null
            }, statsOnly: false, expectedVersion, sessionId);
            return;
        }

        var groups = Snapshot.Groups;
        IReadOnlyList<ProxyGroupInfo> hiddenGroups = Snapshot.HiddenGroups ?? [];
        var mode = status.Mode;
        bool apiOk = Snapshot.ApiConnected;
        bool fetchedStructure = false;

        if (includeProxies || groups.Count == 0)
        {
            fetchedStructure = true;
            var cfgText = await SendRequestAsync(endpoint, HttpMethod.Get, "/configs", null, RequestTimeout, ct).ConfigureAwait(false);
            if (cfgText is not null && ProxyParsers.TryParseMihomoConfigs(cfgText, out var liveMode, out var liveTun, out _))
            {
                apiOk = true;
                if (liveMode != ProxyMode.Unknown) mode = liveMode;
                if (liveTun is { } t) status = status with { Mode = mode, Tun = t };
                else status = status with { Mode = mode };
            }

            var proxiesText = await SendRequestAsync(endpoint, HttpMethod.Get, "/proxies", null, RequestTimeout, ct).ConfigureAwait(false);
            if (proxiesText is not null)
            {
                apiOk = true;
                var (visible, hidden) = ProxyControlParsers.ParseMihomoProxiesWithHidden(proxiesText, mode);
                groups = visible;
                hiddenGroups = hidden;
            }
            else if (cfgText is null)
            {
                apiOk = false;
            }
        }

        var connSummary = Snapshot.Connections;
        long memBytes = connSummary.MemoryInUseBytes;
        var connText = await SendRequestAsync(endpoint, HttpMethod.Get, "/connections", null, RequestTimeout, ct).ConfigureAwait(false);
        // Mihomo emits /memory lines at 1 Hz; read it only on full refreshes (every 12s) with enough headroom so 3s polls stay sub-5ms.
        if (fetchedStructure)
        {
            var memLine = await ReadFirstStreamLineAsync(endpoint, "/memory", TimeSpan.FromMilliseconds(1600), ct).ConfigureAwait(false);
            if (ProxyControlParsers.TryParseMemoryBytes(memLine, out var parsedMem) && parsedMem > 0)
                memBytes = parsedMem;
        }
        if (ProxyControlParsers.TryParseConnectionsSummary(connText, out var parsedConn, memBytes))
        {
            apiOk = true;
            connSummary = parsedConn;
        }
        else if (memBytes != connSummary.MemoryInUseBytes)
        {
            connSummary = connSummary with { MemoryInUseBytes = memBytes };
        }

        PublishUpdate(Snapshot with
        {
            Status = status,
            ApiConnected = apiOk,
            Groups = apiOk ? groups : [],
            HiddenGroups = apiOk ? hiddenGroups : [],
            Subscription = subscription,
            Connections = connSummary,
            LatencyConfig = latencyCfg,
            Error = null
        }, statsOnly: !fetchedStructure, expectedVersion, sessionId);
    }

    private async Task RunTrafficLoopAsync(long sessionId, CancellationToken ct)
    {
        int backoffMs = 800;
        while (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _sessionId) == sessionId)
        {
            var endpoint = ResolveWorkingEndpoint();
            if (endpoint is null || (string.IsNullOrEmpty(endpoint.PipeName) && endpoint.HttpBaseUri is null))
            {
                await DelayQuietly(1500, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var http = CreateClient(endpoint, Timeout.InfiniteTimeSpan);
                using var req = CreateRequest(endpoint, HttpMethod.Get, "/traffic", null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    await DelayQuietly(backoffMs, ct).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 4000);
                    continue;
                }

                bool gotSample = false;
                await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!ct.IsCancellationRequested && Volatile.Read(ref _sessionId) == sessionId)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (ProxyControlParsers.TryParseTrafficSample(line, out var sample))
                    {
                        if (!gotSample) { gotSample = true; backoffMs = 800; }
                        PublishTraffic(sample, sessionId);
                    }
                }
                // Back off on unexpected EOF so a closed stream never spin-reconnects.
                if (!ct.IsCancellationRequested && !_disposed && Volatile.Read(ref _sessionId) == sessionId)
                {
                    await DelayQuietly(backoffMs, ct).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 4000);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await DelayQuietly(backoffMs, ct).ConfigureAwait(false);
                backoffMs = Math.Min(backoffMs * 2, 4000);
            }
        }
    }

    public async Task<bool> SetModeAsync(ProxyMode mode)
    {
        if (_disposed || mode == ProxyMode.Unknown) return false;
        var endpoint = ResolveWorkingEndpoint();
        if (endpoint is null || (string.IsNullOrEmpty(endpoint.PipeName) && endpoint.HttpBaseUri is null)) return false;

        long sessionId = Volatile.Read(ref _sessionId);
        Interlocked.Increment(ref _mutationVersion);
        BusyModeSwitch = true;
        StateChanged?.Invoke();
        try
        {
            string modeStr = mode switch
            {
                ProxyMode.Rule => "rule",
                ProxyMode.Global => "global",
                ProxyMode.Direct => "direct",
                _ => "rule"
            };
            var payload = $"{{\"mode\":\"{modeStr}\"}}";
            var ok = await SendStatusAsync(endpoint, HttpMethod.Patch, "/configs", payload, RequestTimeout, CancellationToken.None).ConfigureAwait(false);
            if (!ok) return false;

            var verifiedMode = mode;
            var cfgText = await SendRequestAsync(endpoint, HttpMethod.Get, "/configs", null, RequestTimeout, CancellationToken.None).ConfigureAwait(false);
            if (cfgText is not null && ProxyParsers.TryParseMihomoConfigs(cfgText, out var liveMode, out _, out _) && liveMode != ProxyMode.Unknown)
                verifiedMode = liveMode;

            bool closeConnections = Snapshot.LatencyConfig.AutoCloseConnection;
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_disposed || Volatile.Read(ref _sessionId) != sessionId) return;
                // Re-bump version on commit so any refresh started before PATCH completed is rejected.
                Interlocked.Increment(ref _mutationVersion);
                _watcher.NotifyLiveMode(verifiedMode, endpoint);
                Snapshot = Snapshot with { Status = Snapshot.Status with { Mode = verifiedMode } };
                StateChanged?.Invoke();
                QueueRefresh(includeProxies: true);
            });

            if (closeConnections && Volatile.Read(ref _sessionId) == sessionId)
                _ = Task.Run(() => SendStatusAsync(endpoint, HttpMethod.Delete, "/connections", null, TimeSpan.FromSeconds(2), CancellationToken.None));
            return true;
        }
        finally
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_disposed) return;
                BusyModeSwitch = false;
                StateChanged?.Invoke();
            });
        }
    }

    public async Task<bool> SelectNodeAsync(string groupName, string nodeName)
    {
        if (_disposed || string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(nodeName)) return false;
        var endpoint = ResolveWorkingEndpoint();
        if (endpoint is null || (string.IsNullOrEmpty(endpoint.PipeName) && endpoint.HttpBaseUri is null)) return false;

        long sessionId = Volatile.Read(ref _sessionId);
        Interlocked.Increment(ref _mutationVersion);
        var path = "/proxies/" + Uri.EscapeDataString(groupName);
        var payload = $"{{\"name\":\"{JsonEncodedText.Encode(nodeName)}\"}}";
        var ok = await SendStatusAsync(endpoint, HttpMethod.Put, path, payload, RequestTimeout, CancellationToken.None).ConfigureAwait(false);
        if (!ok) return false;

        bool closeConnections = Snapshot.LatencyConfig.AutoCloseConnection;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_disposed || Volatile.Read(ref _sessionId) != sessionId) return;
            // Re-bump version on commit so any refresh started before PUT completed is rejected.
            Interlocked.Increment(ref _mutationVersion);
            Snapshot = Snapshot with
            {
                Groups = ProxyControlParsers.WithSelectedNode(Snapshot.Groups, groupName, nodeName),
                HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithSelectedNode(h, groupName, nodeName) : Snapshot.HiddenGroups
            };
            StateChanged?.Invoke();
            QueueRefresh(includeProxies: true);
        });

        if (closeConnections)
            _ = Task.Run(() => SendStatusAsync(endpoint, HttpMethod.Delete, "/connections", null, TimeSpan.FromSeconds(2), CancellationToken.None));
        return true;
    }

    // Clears all previous delays for the group immediately, then probes nodes in bounded parallel batches
    // so completed latencies stream into the UI progressively in real time.
    public async Task<bool> TestGroupDelayAsync(string groupName)
    {
        if (_disposed || string.IsNullOrEmpty(groupName)) return false;
        var endpoint = ResolveWorkingEndpoint();
        if (endpoint is null || (string.IsNullOrEmpty(endpoint.PipeName) && endpoint.HttpBaseUri is null) || !_testingGroups.Add(groupName)) return false;

        var group = Snapshot.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.Ordinal));
        if (group is null || group.Nodes.Count == 0)
        {
            _testingGroups.Remove(groupName);
            return false;
        }

        long sessionId = Volatile.Read(ref _sessionId);
        long owner = ++_nextTestOwner;

        // Preserve display order so top visible nodes finish and appear first.
        // Only take ownership of nodes not currently being probed by another operation.
        var targets = new List<string>(group.Nodes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleared = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var node in group.Nodes)
        {
            if (!seen.Add(node.Name)) continue;
            if (IsNonTestableBuiltIn(node.Name))
            {
                cleared[node.Name] = null;
                continue;
            }
            if (_testingNodes.TryAdd(node.Name, owner))
            {
                targets.Add(node.Name);
                cleared[node.Name] = null;
            }
        }

        // Step 1: Immediately wipe all old delays for owned nodes before any probes finish.
        if (cleared.Count > 0)
        {
            Interlocked.Increment(ref _mutationVersion);
            Snapshot = Snapshot with
            {
                Groups = ProxyControlParsers.WithDelays(Snapshot.Groups, cleared),
                HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithDelays(h, cleared) : Snapshot.HiddenGroups
            };
        }
        StateChanged?.Invoke();

        try { _groupTestCts?.Cancel(); } catch { }
        _groupTestCts?.Dispose();
        var testCts = CancellationTokenSource.CreateLinkedTokenSource(_activeCts?.Token ?? CancellationToken.None);
        _groupTestCts = testCts;
        var ct = testCts.Token;

        var cfg = Snapshot.LatencyConfig;
        var singleTimeout = TimeSpan.FromMilliseconds(cfg.TimeoutMs + 2500);
        var pendingLock = new object();
        var pendingDelays = new Dictionary<string, int?>(StringComparer.Ordinal);
        var pendingDone = new List<string>();
        int flushScheduled = 0;
        int anySuccess = 0;

        void ReleaseOwned(IEnumerable<string> names)
        {
            foreach (var n in names)
                if (_testingNodes.TryGetValue(n, out var cur) && cur == owner)
                    _testingNodes.Remove(n);
        }

        void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref flushScheduled, 1) == 1) return;
            _ = Task.Delay(50).ContinueWith(static (_, state) =>
            {
                ((Action)state!)();
            }, (Action)(() =>
            {
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    Interlocked.Exchange(ref flushScheduled, 0);
                    if (_disposed) return;
                    Dictionary<string, int?> batch;
                    List<string> done;
                    lock (pendingLock)
                    {
                        if (pendingDelays.Count == 0 && pendingDone.Count == 0) return;
                        batch = new Dictionary<string, int?>(pendingDelays, StringComparer.Ordinal);
                        done = [.. pendingDone];
                        pendingDelays.Clear();
                        pendingDone.Clear();
                    }
                    ReleaseOwned(done);
                    if (Volatile.Read(ref _sessionId) != sessionId) return;
                    if (batch.Count > 0)
                    {
                        Interlocked.Increment(ref _mutationVersion);
                        Snapshot = Snapshot with
                        {
                            Groups = ProxyControlParsers.WithDelays(Snapshot.Groups, batch),
                            HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithDelays(h, batch) : Snapshot.HiddenGroups
                        };
                    }
                    StateChanged?.Invoke();
                });
            }), TaskScheduler.Default);
        }

        try
        {
            using var gate = new SemaphoreSlim(10);
            var tasks = new List<Task>(targets.Count);
            try
            {
                foreach (var nodeName in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        int? delay = null;
                        try
                        {
                            var path = $"/proxies/{Uri.EscapeDataString(nodeName)}/delay?url={Uri.EscapeDataString(cfg.TestUrl)}&timeout={cfg.TimeoutMs}";
                            var json = await SendRequestAsync(endpoint, HttpMethod.Get, path, null, singleTimeout, ct).ConfigureAwait(false);
                            delay = ProxyControlParsers.ParseSingleDelay(json);
                            if (delay is > 0) Interlocked.Exchange(ref anySuccess, 1);
                        }
                        catch { }
                        finally
                        {
                            try { gate.Release(); } catch (ObjectDisposedException) { }
                            lock (pendingLock)
                            {
                                pendingDelays[nodeName] = delay;
                                pendingDone.Add(nodeName);
                            }
                            ScheduleFlush();
                        }
                    }, CancellationToken.None));
                }
            }
            catch (OperationCanceledException) { }
            // Always wait for already-spawned probe tasks before disposing the semaphore.
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_disposed) return;
                Dictionary<string, int?> leftover;
                lock (pendingLock)
                {
                    leftover = new Dictionary<string, int?>(pendingDelays, StringComparer.Ordinal);
                    pendingDelays.Clear();
                    pendingDone.Clear();
                }
                ReleaseOwned(targets);
                _testingGroups.Remove(groupName);
                if (Volatile.Read(ref _sessionId) != sessionId) return;
                if (leftover.Count > 0)
                {
                    Interlocked.Increment(ref _mutationVersion);
                    Snapshot = Snapshot with
                    {
                        Groups = ProxyControlParsers.WithDelays(Snapshot.Groups, leftover),
                        HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithDelays(h, leftover) : Snapshot.HiddenGroups
                    };
                }
                StateChanged?.Invoke();
            });
        }
        return Volatile.Read(ref anySuccess) == 1;
    }

    private static bool IsNonTestableBuiltIn(string name) =>
        name.Equals("REJECT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PASS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("COMPATIBLE", StringComparison.OrdinalIgnoreCase);

    public async Task<int?> TestNodeDelayAsync(string nodeName)
    {
        if (_disposed || string.IsNullOrEmpty(nodeName)) return null;
        var endpoint = ResolveWorkingEndpoint();
        if (endpoint is null || (string.IsNullOrEmpty(endpoint.PipeName) && endpoint.HttpBaseUri is null)) return null;
        long sessionId = Volatile.Read(ref _sessionId);
        long owner = ++_nextTestOwner;
        if (!_testingNodes.TryAdd(nodeName, owner)) return null;

        // Clear old delay on the tested node immediately before measuring.
        var clearMap = new Dictionary<string, int?>(StringComparer.Ordinal) { [nodeName] = null };
        Interlocked.Increment(ref _mutationVersion);
        Snapshot = Snapshot with
        {
            Groups = ProxyControlParsers.WithDelays(Snapshot.Groups, clearMap),
            HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithDelays(h, clearMap) : Snapshot.HiddenGroups
        };
        StateChanged?.Invoke();
        try
        {
            var cfg = Snapshot.LatencyConfig;
            var path = $"/proxies/{Uri.EscapeDataString(nodeName)}/delay?url={Uri.EscapeDataString(cfg.TestUrl)}&timeout={cfg.TimeoutMs}";
            var timeout = TimeSpan.FromMilliseconds(cfg.TimeoutMs + 3000);
            var json = await SendRequestAsync(endpoint, HttpMethod.Get, path, null, timeout, CancellationToken.None).ConfigureAwait(false);
            var delay = ProxyControlParsers.ParseSingleDelay(json);

            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_disposed) return;
                if (_testingNodes.TryGetValue(nodeName, out var cur) && cur == owner) _testingNodes.Remove(nodeName);
                if (Volatile.Read(ref _sessionId) != sessionId) return;
                Interlocked.Increment(ref _mutationVersion);
                var update = new Dictionary<string, int?>(StringComparer.Ordinal) { [nodeName] = delay };
                Snapshot = Snapshot with
                {
                    Groups = ProxyControlParsers.WithDelays(Snapshot.Groups, update),
                    HiddenGroups = Snapshot.HiddenGroups is { Count: > 0 } h ? ProxyControlParsers.WithDelays(h, update) : Snapshot.HiddenGroups
                };
                StateChanged?.Invoke();
            });
            return delay;
        }
        catch
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_testingNodes.TryGetValue(nodeName, out var cur) && cur == owner) _testingNodes.Remove(nodeName);
                if (!_disposed && Volatile.Read(ref _sessionId) == sessionId) StateChanged?.Invoke();
            });
            return null;
        }
    }

    private void PublishUpdate(ProxyControlSnapshot next, bool statsOnly, long expectedVersion, long sessionId)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_disposed || !_active || Volatile.Read(ref _sessionId) != sessionId) return;
            var prev = Snapshot;
            // Within the same collection session, never roll cumulative totals backwards if /traffic is ahead of /connections.
            var mergedConn = next.Connections with
            {
                UploadTotal = Math.Max(prev.Connections.UploadTotal, next.Connections.UploadTotal),
                DownloadTotal = Math.Max(prev.Connections.DownloadTotal, next.Connections.DownloadTotal)
            };
            if (statsOnly)
            {
                // Stats-only refresh must never overwrite Groups or Status mutated concurrently (e.g. progressive delay probes).
                var statsMerged = prev with
                {
                    ApiConnected = next.ApiConnected || prev.ApiConnected,
                    Subscription = next.Subscription,
                    Connections = mergedConn,
                    LatencyConfig = next.LatencyConfig,
                    Error = next.Error
                };
                bool subChanged = prev.Subscription != statsMerged.Subscription || prev.ApiConnected != statsMerged.ApiConnected;
                bool connChanged = prev.Connections != statsMerged.Connections;
                Snapshot = statsMerged;
                if (subChanged) StateChanged?.Invoke();
                else if (connChanged) TrafficChanged?.Invoke();
                return;
            }
            if (Volatile.Read(ref _mutationVersion) != expectedVersion) return;
            bool testing = _testingGroups.Count > 0 || _testingNodes.Count > 0;
            var merged = next with
            {
                Traffic = prev.Traffic,
                Connections = mergedConn,
                Groups = testing && prev.Groups.Count > 0 ? prev.Groups : next.Groups,
                HiddenGroups = testing && prev.HiddenGroups is { Count: > 0 } ? prev.HiddenGroups : next.HiddenGroups
            };
            bool stateChanged =
                prev.Status != merged.Status
                || prev.ApiConnected != merged.ApiConnected
                || prev.Subscription != merged.Subscription
                || !ProxyControlParsers.GroupsEqual(prev.Groups, merged.Groups)
                || !ProxyControlParsers.GroupsEqual(prev.HiddenGroups ?? [], merged.HiddenGroups ?? []);
            bool connectionsChanged = prev.Connections != merged.Connections;
            Snapshot = merged;
            if (stateChanged) StateChanged?.Invoke();
            else if (connectionsChanged) TrafficChanged?.Invoke();
        });
    }

    private void PublishTraffic(ProxyTrafficSample sample, long sessionId)
    {
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_disposed || !_active || Volatile.Read(ref _sessionId) != sessionId) return;
            _history.Enqueue(sample);
            while (_history.Count > HistoryCapacity) _history.Dequeue();
            var conn = Snapshot.Connections;
            if (sample.UpTotalBytes > 0 || sample.DownTotalBytes > 0)
                conn = conn with { UploadTotal = Math.Max(conn.UploadTotal, sample.UpTotalBytes), DownloadTotal = Math.Max(conn.DownloadTotal, sample.DownTotalBytes) };
            bool becameConnected = !Snapshot.ApiConnected;
            Snapshot = Snapshot with { Traffic = sample, Connections = conn, ApiConnected = true };
            if (becameConnected) StateChanged?.Invoke();
            else TrafficChanged?.Invoke();
        });
    }

    internal (ProxySubscriptionInfo? Subscription, ProxyLatencyConfig LatencyConfig) ReadLocalVergeMetadata(string? configDir)
    {
        if (string.IsNullOrEmpty(configDir))
            return (null, ProxyLatencyConfig.Default);

        var profilesPath = Path.Combine(configDir, "profiles.yaml");
        var vergePath = Path.Combine(configDir, "verge.yaml");
        bool hasCachedProfile = _profilesCache.TryGetValue(profilesPath, out var cachedProfile);
        bool hasCachedLatency = _vergeControlCache.TryGetValue(vergePath, out var cachedLatency);
        ProxySubscriptionInfo? sub = hasCachedProfile ? cachedProfile.Value : null;
        // Preserve the last successfully read verge.yaml config across transient directory/file failures so auto_close_connection: false is never overwritten by Default.
        var latency = hasCachedLatency ? cachedLatency.Value : ProxyLatencyConfig.Default;
        if (!Directory.Exists(configDir))
            return (sub, latency);

        if (TryFileStamp(profilesPath, out var pStamp))
        {
            if (!hasCachedProfile || cachedProfile.WrittenUtc != pStamp.WrittenUtc || cachedProfile.Length != pStamp.Length)
            {
                if (ReadSharedText(profilesPath) is { } text)
                {
                    sub = ProxyControlParsers.ParseVergeProfilesYaml(text);
                    _profilesCache[profilesPath] = (pStamp.WrittenUtc, pStamp.Length, sub);
                }
            }
        }

        if (TryFileStamp(vergePath, out var vStamp))
        {
            if (!hasCachedLatency || cachedLatency.WrittenUtc != vStamp.WrittenUtc || cachedLatency.Length != vStamp.Length)
            {
                if (ReadSharedText(vergePath) is { } text)
                {
                    latency = ProxyControlParsers.ParseVergeControlYaml(text);
                    _vergeControlCache[vergePath] = (vStamp.WrittenUtc, vStamp.Length, latency);
                }
            }
        }
        return (sub, latency);
    }

    private static async Task<string?> SendRequestAsync(ProxyEndpoint endpoint, HttpMethod method, string relativePath, string? jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var http = CreateClient(endpoint, timeout);
            using var req = CreateRequest(endpoint, method, relativePath, jsonBody);
            req.Headers.ConnectionClose = true;
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static async Task<bool> SendStatusAsync(ProxyEndpoint endpoint, HttpMethod method, string relativePath, string? jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var http = CreateClient(endpoint, timeout);
            using var req = CreateRequest(endpoint, method, relativePath, jsonBody);
            req.Headers.ConnectionClose = true;
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<string?> ReadFirstStreamLineAsync(ProxyEndpoint endpoint, string relativePath, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var http = CreateClient(endpoint, timeout);
            using var req = CreateRequest(endpoint, HttpMethod.Get, relativePath, null);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static HttpClient CreateClient(ProxyEndpoint endpoint, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero
        };
        if (!string.IsNullOrEmpty(endpoint.PipeName))
        {
            var pipeName = endpoint.PipeName;
            handler.ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(600, token).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            };
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private static HttpRequestMessage CreateRequest(ProxyEndpoint endpoint, HttpMethod method, string relativePath, string? jsonBody)
    {
        var uri = !string.IsNullOrEmpty(endpoint.PipeName)
            ? new Uri("http://localhost" + relativePath)
            : new Uri(endpoint.HttpBaseUri!, relativePath);
        var req = new HttpRequestMessage(method, uri);
        if (string.IsNullOrEmpty(endpoint.PipeName) && !string.IsNullOrWhiteSpace(endpoint.Secret))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret.Trim());
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    private static string EndpointKey(ProxyEndpoint? e) => e is null ? "" : $"{e.ClientName}|{e.PipeName}|{e.HttpBaseUri}|{e.ConfigDir}";

    private static async Task DelayQuietly(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
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

    private static string? ReadSharedText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4 * 1024 * 1024) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Changed -= OnWatcherChanged;
        CancelActiveWork();
        _history.Clear();
    }
}
