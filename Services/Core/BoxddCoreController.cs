using System.Diagnostics;
using System.Runtime.CompilerServices;
using Daemon;
using Desktop;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace sing_box_for_windows.Services.Core;

/// <summary>
/// Production controller: talks to the upstream SingBox desktop daemon
/// (boxdd, a Windows service) through its worker relay named pipe. The daemon
/// runs privileged and resident, so TUN needs no per-start elevation.
/// </summary>
public sealed class BoxddCoreController : ICoreController
{
    private static readonly TimeSpan WorkerReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Serializes start/stop/orphan-reconcile so a background reconcile can
    /// never tear down a connection that a concurrent StartAsync just built.
    /// </summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private readonly object _gate = new();
    private readonly Dictionary<string, CoreConnection> _connections = new();

    /// <summary>
    /// Closed connections are retained only as recent history. The daemon streams
    /// one closed event per connection, so a busy core retires thousands per
    /// minute; an uncapped dictionary would grow all session and make every
    /// per-second snapshot sort an ever larger set.
    /// </summary>
    private const int MaxClosedConnections = 1000;

    private int _closedConnectionCount;

    private Process? _workerProcess;
    private string? _workerPipeName;
    private string? _relayPipeName;
    private GrpcChannel? _channel;
    private Daemon.StartedService.StartedServiceClient? _client;
    private Daemon.ManagedService.ManagedServiceClient? _managedClient;
    private Desktop.DesktopService.DesktopServiceClient? _desktopClient;
    private CancellationTokenSource? _streamCts;
    private TaskCompletionSource<string?>? _firstServiceStatus;
    private long _startGeneration;

    public event EventHandler<RuntimeState>? StateChanged;
    public event EventHandler<string>? LogReceived;
    public event EventHandler<CoreStatus>? StatusChanged;
    public event EventHandler<IReadOnlyList<CoreProxyGroup>>? GroupsChanged;
    public event EventHandler<IReadOnlyList<CoreConnection>>? ConnectionsChanged;
    public event EventHandler<CoreClashMode>? ClashModeChanged;
    public event EventHandler<IReadOnlyList<CoreProxyGroupItem>>? OutboundsChanged;
    public event EventHandler<CoreSystemProxy>? SystemProxyChanged;

    public RuntimeState State { get; private set; } = RuntimeState.Stopped;
    public CoreStatus Status { get; private set; } = CoreStatus.Empty;
    public CoreClashMode ClashMode { get; private set; } = CoreClashMode.Empty;
    public IReadOnlyList<CoreProxyGroup> Groups { get; private set; } = [];
    public IReadOnlyList<CoreConnection> Connections { get; private set; } = [];
    public IReadOnlyList<CoreProxyGroupItem> Outbounds { get; private set; } = [];
    public CoreSystemProxy SystemProxy { get; private set; } = CoreSystemProxy.Empty;

    public async Task StartAsync(string configContent, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(configContent, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(string configContent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupDiag.Log("Core.StartAsync: enter");
        await StopCoreAsync(cancellationToken);
        StartupDiag.Log("Core.StartAsync: StopAsync done");
        // One-time, elevated; afterwards the service starts with Windows.
        await DaemonServiceManager.EnsureRunningAsync(RaiseLog, cancellationToken);
        StartupDiag.Log("Core.StartAsync: EnsureRunningAsync done");
        try
        {
        // Spawned inside the try so a worker that never became ready is still
        // reaped by the catch below instead of outliving the failed start.
        await StartWorkerAsync(cancellationToken);
        StartupDiag.Log("Core.StartAsync: StartWorkerAsync done (worker READY)");

        _channel = NamedPipeChannel.Create(_relayPipeName!);
        _client = new Daemon.StartedService.StartedServiceClient(_channel);
        _managedClient = new Daemon.ManagedService.ManagedServiceClient(_channel);
        _desktopClient = new Desktop.DesktopService.DesktopServiceClient(_channel);

        var generation = Interlocked.Increment(ref _startGeneration);
        var firstStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupStatus = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstServiceStatus = startupStatus;
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _streamCts.Token;
            using var rpcTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rpcTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            StartupDiag.Log("Core.StartAsync: ClaimServiceAsync...");
            await _desktopClient.ClaimServiceAsync(new Empty(), cancellationToken: rpcTimeout.Token);
            StartupDiag.Log("Core.StartAsync: ClaimServiceAsync done");

            // Establish the stream before starting the core and wait for its
            // immediate upstream snapshot. This prevents us from waiting for a
            // status transition on a relay that was never successfully opened.
            _ = Task.Run(() => StreamServiceStatusAsync(token, firstStatus, startupStatus, generation), CancellationToken.None);
            StartupDiag.Log("Core.StartAsync: waiting for first service status...");
            await firstStatus.Task.WaitAsync(rpcTimeout.Token);
            StartupDiag.Log("Core.StartAsync: first status received");

            SetState(new RuntimeState(false, "Starting"));
            StartupDiag.Log("Core.StartAsync: StartServiceAsync...");
            await _desktopClient.StartServiceAsync(
                new StartServiceRequest { ConfigContent = configContent },
                cancellationToken: rpcTimeout.Token);
            StartupDiag.Log("Core.StartAsync: StartServiceAsync done");

            // Subscribe to buffered daemon logs immediately after the start RPC,
            // before waiting for the terminal status. This exposes config and
            // provider diagnostics while startup is still in progress.
            _ = Task.Run(() => StreamLogsAsync(token), CancellationToken.None);
            _ = Task.Run(() => StreamStatusAsync(token), CancellationToken.None);
            _ = Task.Run(() => StreamGroupsAsync(token), CancellationToken.None);
            _ = Task.Run(() => StreamConnectionsAsync(token), CancellationToken.None);
            _ = Task.Run(() => StreamOutboundsAsync(token), CancellationToken.None);

            // A failed status stream completes this task with its real relay
            // error. Keep the user-facing wait bounded even for a stuck core.
            StartupDiag.Log("Core.StartAsync: waiting for terminal service status...");
            var completed = await Task.WhenAny(startupStatus.Task, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
            if (completed != startupStatus.Task)
                throw new InvalidOperationException("Timed out waiting for the SingBox service to start.");
            if (await startupStatus.Task is { } startError)
                throw new InvalidOperationException(startError);

            StartupDiag.Log("Core.StartAsync: service started, state=Running");
            SetState(new RuntimeState(true, "Running"));
            // Unlike streaming RPCs, GetClashModeStatus requires STARTED and
            // fails immediately while the daemon is still starting.
            _ = Task.Run(() => StreamClashModeAsync(token), CancellationToken.None);
            _ = RefreshSystemProxySafeAsync();
        }
        catch (Exception ex)
        {
            StartupDiag.Log($"Core.StartAsync: FAILED: {ex.GetType().Name}: {ex.Message}");
            await StopCoreAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Spawns the non-privileged worker that relays an authenticated daemon
    /// connection to this app (boxdd three-tier model: daemon service →
    /// worker → app). Exactly one worker is alive at a time: any previous one is
    /// killed first, and a failed readiness handshake kills the one just started
    /// instead of leaving it behind until the app exits.
    /// </summary>
    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        await KillWorkerProcessAsync(CancellationToken.None);
        var daemonExe = DaemonServiceManager.FindWorkerExecutable()
            ?? throw new InvalidOperationException("sing-box-daemon.exe was not found.");
        StartupDiag.Log($"StartWorkerAsync: daemonExe={daemonExe}");

        _workerPipeName = $"sing-box-worker.{Guid.NewGuid():N}";
        _relayPipeName = $"sing-box-worker.{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = daemonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // boxdd's worker watches stdin: it tears down the relay and its gRPC
            // server as soon as stdin reaches EOF. A packaged WinUI app has no
            // usable stdin, so without an explicit pipe the worker sees immediate
            // EOF and exits right after printing READY, leaving the app to hang
            // on a dead relay. Redirecting stdin keeps the pipe open for the
            // worker's lifetime (StopAsync kills the process outright).
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        // boxdd validates this exact argument order before it accepts the
        // worker as a relay peer; keep --parent-pid before --daemon-relay-socket.
        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add("--socket");
        startInfo.ArgumentList.Add($@"\\.\pipe\{_workerPipeName}");
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("--daemon-relay-socket");
        startInfo.ArgumentList.Add($@"\\.\pipe\{_relayPipeName}");
        RaiseLog($"Starting boxdd worker: {daemonExe} worker --socket {startInfo.ArgumentList[2]} --parent-pid {Environment.ProcessId} --daemon-relay-socket {startInfo.ArgumentList[6]}");

        var worker = new Process { StartInfo = startInfo };
        _workerProcess = worker;
        StartupDiag.Log($"StartWorkerAsync: launching worker pid-caller={Environment.ProcessId}");
        if (!worker.Start())
        {
            _workerProcess = null;
            worker.Dispose();
            throw new InvalidOperationException("Unable to start the SingBox worker process.");
        }
        StartupDiag.Log($"StartWorkerAsync: worker started pid={worker.Id}");

        try
        {
            var outputTask = worker.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
            // Read this stream exactly once. It completes when the worker exits,
            // which lets startup errors be reported instead of masquerading as a timeout.
            var errorTask = worker.StandardError.ReadToEndAsync(cancellationToken);
            var exitTask = worker.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(WorkerReadyTimeout, cancellationToken);
            var completed = await Task.WhenAny(outputTask, errorTask, exitTask, timeoutTask);
            StartupDiag.Log($"StartWorkerAsync: WhenAny completed={completed switch { var t when t == outputTask => "output", var t when t == errorTask => "error", var t when t == exitTask => "exit", _ => "timeout" }} workerExited={worker.HasExited}");
            if (completed != outputTask || outputTask.IsFaulted || outputTask.Result != "READY")
            {
                var error = errorTask.IsCompletedSuccessfully ? errorTask.Result : null;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "The SingBox worker did not become ready in time."
                    : $"The SingBox worker failed to start: {error.Trim()}");
            }

            // Forward the worker's remaining stdout and eventual stderr to the log page.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await worker.StandardOutput.ReadLineAsync() is { } line) RaiseLog(line);
                }
                catch { /* worker exited */ }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    var error = await errorTask;
                    if (!string.IsNullOrWhiteSpace(error)) RaiseLog(error.Trim());
                }
                catch { /* worker exited */ }
            });
        }
        catch
        {
            await KillWorkerProcessAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Graceful stop first: the daemon restores the system proxy and flushes
        // state. The daemon service itself stays resident. Bound this RPC so a
        // dead relay (e.g. the worker already exited) can never hang StopAsync,
        // which would otherwise mask the original startup failure and leave the
        // UI stuck on its busy state forever.
        try
        {
            if (_managedClient is not null)
            {
                using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stopTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await _managedClient.StopServiceAsync(new Empty(), cancellationToken: stopTimeout.Token);
            }
        }
        catch
        {
            // Best effort; the worker is killed below regardless.
        }

        await TeardownConnectionAsync(cancellationToken);
        ResetRuntimeState();
    }

    /// <summary>
    /// Drops the worker, channel, clients and streams without touching the
    /// service inside the daemon. Callers must hold <see cref="_lifecycleGate"/>.
    /// </summary>
    private async Task TeardownConnectionAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _streamCts?.Cancel();
            _streamCts = null;
            _client = null;
            _managedClient = null;
            _desktopClient = null;
        }
        _channel?.Dispose();
        _channel = null;

        // The worker is our non-elevated child; it also exits on its own when
        // this app exits (its --parent-pid watch).
        await KillWorkerProcessAsync(cancellationToken);
        _firstServiceStatus = null;
    }

    /// <summary>
    /// Kills and disposes the tracked worker, leaving no process handle behind.
    /// Bounded, so a worker that refuses to die can never stall the lifecycle gate.
    /// </summary>
    private async Task KillWorkerProcessAsync(CancellationToken cancellationToken)
    {
        var worker = _workerProcess;
        if (worker is null) return;
        _workerProcess = null;
        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await worker.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            RaiseLog($"Error stopping the SingBox worker: {ex.Message}");
        }
        finally
        {
            worker.Dispose();
        }
    }

    private void ResetRuntimeState()
    {
        _connections.Clear();
        _closedConnectionCount = 0;
        Status = CoreStatus.Empty;
        ClashMode = CoreClashMode.Empty;
        Groups = [];
        Connections = [];
        Outbounds = [];
        SystemProxy = CoreSystemProxy.Empty;
        StatusChanged?.Invoke(this, Status);
        ClashModeChanged?.Invoke(this, ClashMode);
        GroupsChanged?.Invoke(this, Groups);
        ConnectionsChanged?.Invoke(this, Connections);
        OutboundsChanged?.Invoke(this, Outbounds);
        SystemProxyChanged?.Invoke(this, SystemProxy);
        SetState(RuntimeState.Stopped);
    }

    /// <summary>
    /// Lifecycle binding: a running core must never outlive the app. When the
    /// daemon holds a core this process did not start — the previous instance
    /// crashed or was killed, or the daemon auto-restored it at boot
    /// (boxdd's WasRunning restore) — stop it. Never elevates: a stopped or
    /// uninstalled daemon service means there is no core to clean up.
    /// Best effort; skipped when a start/stop is already in flight.
    /// </summary>
    public async Task StopOrphanedServiceAsync(CancellationToken cancellationToken = default)
    {
        if (State.IsRunning) return; // This process owns a running core.
        if (!await DaemonServiceManager.IsDaemonReachableAsync(cancellationToken)) return;
        // A start/stop in progress owns the lifecycle; reconciling on top of
        // it could tear down a brand-new connection, so just skip this pass.
        if (!await _lifecycleGate.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)) return;
        try
        {
            StartupDiag.Log("StopOrphanedServiceAsync: daemon running, checking for an orphaned core");
            await StartWorkerAsync(cancellationToken);
            _channel = NamedPipeChannel.Create(_relayPipeName!);
            _client = new Daemon.StartedService.StartedServiceClient(_channel);
            _managedClient = new Daemon.ManagedService.ManagedServiceClient(_channel);
            _desktopClient = new Desktop.DesktopService.DesktopServiceClient(_channel);

            using var rpcTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rpcTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _desktopClient.ClaimServiceAsync(new Empty(), cancellationToken: rpcTimeout.Token);

            if (await IsServiceStartedAsync(rpcTimeout.Token))
            {
                RaiseLog(Loc.Get(
                    "Stopping the SingBox core left running without the app.",
                    "正在停止上次遗留运行的 SingBox 核心。"));
                await _managedClient.StopServiceAsync(new Empty(), cancellationToken: rpcTimeout.Token);
            }
        }
        catch (Exception ex)
        {
            // Best effort only: a foreign owner or a dead relay must not
            // break app startup.
            StartupDiag.Log($"StopOrphanedServiceAsync: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                await TeardownConnectionAsync(CancellationToken.None);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
    }

    /// <summary>Reads the daemon's immediate service-status snapshot.</summary>
    private async Task<bool> IsServiceStartedAsync(CancellationToken cancellationToken)
    {
        using var call = GetClient().SubscribeServiceStatus(new Empty(), cancellationToken: cancellationToken);
        if (!await call.ResponseStream.MoveNext(cancellationToken)) return false;
        return call.ResponseStream.Current.Status
            is ServiceStatus.Types.Type.Starting or ServiceStatus.Types.Type.Started;
    }

    public async Task SelectOutboundAsync(string groupTag, string itemTag, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.SelectOutboundAsync(
            new SelectOutboundRequest { GroupTag = groupTag, OutboundTag = itemTag },
            cancellationToken: cancellationToken);
    }

    public async Task UrlTestAsync(string outboundTag, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.URLTestAsync(
            new URLTestRequest { OutboundTag = outboundTag },
            cancellationToken: cancellationToken);
    }

    public async Task SetGroupExpandAsync(string groupTag, bool expand, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.SetGroupExpandAsync(
            new SetGroupExpandRequest { GroupTag = groupTag, IsExpand = expand },
            cancellationToken: cancellationToken);
    }

    public async Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.CloseConnectionAsync(
            new CloseConnectionRequest { Id = connectionId },
            cancellationToken: cancellationToken);
    }

    public async Task CloseConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.CloseAllConnectionsAsync(new Empty(), cancellationToken: cancellationToken);
    }

    public async Task SetClashModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.SetClashModeAsync(new Daemon.ClashMode { Mode = mode }, cancellationToken: cancellationToken);
    }

    public async Task ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.ClearLogsAsync(new Empty(), cancellationToken: cancellationToken);
    }

    public async Task RefreshSystemProxyAsync(CancellationToken cancellationToken = default)
    {
        var client = GetManagedClient();
        var status = await client.GetSystemProxyStatusAsync(new Empty(), cancellationToken: cancellationToken);
        SystemProxy = new CoreSystemProxy(status.Available, status.Enabled);
        SystemProxyChanged?.Invoke(this, SystemProxy);
    }

    public async Task SetSystemProxyEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var client = GetManagedClient();
        await client.SetSystemProxyEnabledAsync(
            new SetSystemProxyEnabledRequest { Enabled = enabled },
            cancellationToken: cancellationToken);
        SystemProxy = SystemProxy with { Enabled = enabled };
        SystemProxyChanged?.Invoke(this, SystemProxy);
    }

    public async IAsyncEnumerable<CoreNetworkQualityProgress> StartNetworkQualityTestAsync(
        CoreNetworkQualityTestOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        using var call = client.StartNetworkQualityTest(new NetworkQualityTestRequest
        {
            ConfigURL = options.ConfigUrl ?? string.Empty,
            OutboundTag = options.OutboundTag ?? string.Empty,
            Serial = options.Serial,
            MaxRuntimeSeconds = options.MaxRuntimeSeconds,
            Http3 = options.Http3,
        });
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var p = call.ResponseStream.Current;
            yield return new CoreNetworkQualityProgress(
                p.Phase, p.DownloadCapacity, p.UploadCapacity, p.DownloadRPM, p.UploadRPM,
                p.IdleLatencyMs, p.ElapsedMs, p.IsFinal, string.IsNullOrEmpty(p.Error) ? null : p.Error);
        }
    }

    public async IAsyncEnumerable<CoreStunProgress> StartStunTestAsync(
        string server,
        string? outboundTag,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        using var call = client.StartSTUNTest(new STUNTestRequest
        {
            Server = server,
            OutboundTag = outboundTag ?? string.Empty,
        });
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var p = call.ResponseStream.Current;
            yield return new CoreStunProgress(
                p.Phase, p.ExternalAddr, p.LatencyMs, p.NatMapping, p.NatFiltering,
                p.IsFinal, string.IsNullOrEmpty(p.Error) ? null : p.Error, p.NatTypeSupported);
        }
    }

    private async Task RefreshSystemProxySafeAsync()
    {
        try
        {
            await RefreshSystemProxyAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            RaiseLog($"system-proxy status refresh failed: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private Daemon.StartedService.StartedServiceClient GetClient()
    {
        var client = _client ?? throw new InvalidOperationException("The daemon connection is not open.");
        return client;
    }

    private Daemon.ManagedService.ManagedServiceClient GetManagedClient()
    {
        var client = _managedClient ?? throw new InvalidOperationException("The daemon connection is not open.");
        return client;
    }

    private async Task StreamServiceStatusAsync(
        CancellationToken token,
        TaskCompletionSource firstStatus,
        TaskCompletionSource<string?> startupStatus,
        long generation)
    {
        try
        {
            using var call = GetClient().SubscribeServiceStatus(new Empty(), cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                if (generation != Volatile.Read(ref _startGeneration)) return;
                var status = call.ResponseStream.Current;
                firstStatus.TrySetResult();
                switch (status.Status)
                {
                    case ServiceStatus.Types.Type.Starting:
                        SetState(new RuntimeState(false, "Starting"));
                        break;
                    case ServiceStatus.Types.Type.Started:
                        SetState(new RuntimeState(true, "Running"));
                        startupStatus.TrySetResult(null);
                        _ = RefreshSystemProxySafeAsync();
                        break;
                    case ServiceStatus.Types.Type.Fatal:
                        SetState(new RuntimeState(false, "Stopped"));
                        var error = string.IsNullOrWhiteSpace(status.ErrorMessage)
                            ? "The service failed to start."
                            : status.ErrorMessage;
                        startupStatus.TrySetResult(error);
                        RaiseLog($"service failed: {error}");
                        break;
                    case ServiceStatus.Types.Type.Stopping:
                    case ServiceStatus.Types.Type.Idle:
                        SetState(new RuntimeState(false, "Stopped"));
                        break;
                }
            }
            if (!token.IsCancellationRequested && generation == Volatile.Read(ref _startGeneration))
            {
                const string message = "The SingBox service-status stream ended unexpectedly.";
                RaiseLog(message);
                firstStatus.TrySetException(new IOException(message));
                startupStatus.TrySetResult(message);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation != Volatile.Read(ref _startGeneration)) return;
            var message = $"service-status stream error: {ex.Message}";
            RaiseLog(message);
            firstStatus.TrySetException(ex);
            startupStatus.TrySetResult(message);
        }
    }

    private async Task StreamStatusAsync(CancellationToken token)
    {
        try
        {
            using var call = GetClient().SubscribeStatus(
                new SubscribeStatusRequest { Interval = 1_000_000_000 },
                cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                var s = call.ResponseStream.Current;
                Status = new CoreStatus(
                    (long)s.Memory,
                    s.Goroutines,
                    s.ConnectionsIn,
                    s.ConnectionsOut,
                    s.TrafficAvailable,
                    s.Uplink,
                    s.Downlink,
                    s.UplinkTotal,
                    s.DownlinkTotal);
                StatusChanged?.Invoke(this, Status);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"status stream error: {ex.Message}");
        }
    }

    private async Task StreamGroupsAsync(CancellationToken token)
    {
        try
        {
            using var call = GetClient().SubscribeGroups(new Empty(), cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                var groups = call.ResponseStream.Current.Group
                    .Select(ToCoreProxyGroup)
                    .ToList();
                Groups = groups;
                GroupsChanged?.Invoke(this, Groups);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"groups stream error: {ex.Message}");
        }
    }

    private async Task StreamLogsAsync(CancellationToken token)
    {
        try
        {
            using var call = GetClient().SubscribeLog(new Empty(), cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                var log = call.ResponseStream.Current;
                foreach (var entry in log.Messages)
                {
                    RaiseLog(entry.Message_);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"log stream error: {ex.Message}");
        }
    }

    private async Task StreamConnectionsAsync(CancellationToken token)
    {
        try
        {
            using var call = GetClient().SubscribeConnections(
                new SubscribeConnectionsRequest { Interval = 1_000_000_000 },
                cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                var events = call.ResponseStream.Current;
                // The daemon ticks once per interval even when nothing happened.
                if (!events.Reset && events.Events.Count == 0) continue;
                ApplyConnectionEvents(events);
                Connections = _connections.Values
                    .OrderByDescending(x => x.CreatedAt)
                    .ToList();
                ConnectionsChanged?.Invoke(this, Connections);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"connections stream error: {ex.Message}");
        }
    }

    private async Task StreamClashModeAsync(CancellationToken token)
    {
        try
        {
            var status = await GetClient().GetClashModeStatusAsync(new Empty(), cancellationToken: token);
            ClashMode = new CoreClashMode(status.ModeList.ToList(), status.CurrentMode);
            ClashModeChanged?.Invoke(this, ClashMode);

            using var call = GetClient().SubscribeClashMode(new Empty(), cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                ClashMode = new CoreClashMode(ClashMode.Modes, call.ResponseStream.Current.Mode);
                ClashModeChanged?.Invoke(this, ClashMode);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"clash-mode stream error: {ex.Message}");
        }
    }

    private async Task StreamOutboundsAsync(CancellationToken token)
    {
        try
        {
            using var call = GetClient().SubscribeOutbounds(new Empty(), cancellationToken: token);
            while (await call.ResponseStream.MoveNext(token))
            {
                Outbounds = call.ResponseStream.Current.Outbounds
                    .Select(ToCoreProxyGroupItem)
                    .ToList();
                OutboundsChanged?.Invoke(this, Outbounds);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"outbounds stream error: {ex.Message}");
        }
    }

    private void ApplyConnectionEvents(Daemon.ConnectionEvents events)
    {
        if (events.Reset)
        {
            _connections.Clear();
            _closedConnectionCount = 0;
        }

        foreach (var e in events.Events)
        {
            switch (e.Type)
            {
                case ConnectionEventType.ConnectionEventNew:
                    if (e.Connection is not null)
                        StoreConnection(e.Id, ToCoreConnection(e.Connection));
                    break;

                case ConnectionEventType.ConnectionEventUpdate:
                    if (_connections.TryGetValue(e.Id, out var existing))
                    {
                        _connections[e.Id] = existing with
                        {
                            Uplink = e.UplinkDelta,
                            Downlink = e.DownlinkDelta,
                            UplinkTotal = existing.UplinkTotal + e.UplinkDelta,
                            DownlinkTotal = existing.DownlinkTotal + e.DownlinkDelta,
                        };
                    }
                    break;

                case ConnectionEventType.ConnectionEventClosed:
                    if (e.Connection is not null)
                    {
                        StoreConnection(e.Id, ToCoreConnection(e.Connection) with
                        {
                            ClosedAt = e.ClosedAt,
                            Uplink = 0,
                            Downlink = 0,
                        });
                    }
                    else
                    {
                        MarkConnectionClosed(e.Id, e.ClosedAt);
                    }
                    break;
            }
        }

        TrimClosedConnections();
    }

    private void StoreConnection(string id, CoreConnection connection)
    {
        if (_connections.TryGetValue(id, out var previous) && previous.ClosedAt > 0)
        {
            _closedConnectionCount--;
        }
        _connections[id] = connection;
        if (connection.ClosedAt > 0) _closedConnectionCount++;
    }

    private void MarkConnectionClosed(string id, long closedAt)
    {
        if (!_connections.TryGetValue(id, out var existing)) return;
        var wasRunning = existing.ClosedAt == 0;
        _connections[id] = existing with
        {
            ClosedAt = closedAt,
            Uplink = 0,
            Downlink = 0,
        };
        if (wasRunning) _closedConnectionCount++;
    }

    /// <summary>Drops the oldest closed connections once the history cap is exceeded.</summary>
    private void TrimClosedConnections()
    {
        var excess = _closedConnectionCount - MaxClosedConnections;
        if (excess <= 0) return;
        var stale = _connections
            .Where(entry => entry.Value.ClosedAt > 0)
            .OrderBy(entry => entry.Value.ClosedAt)
            .Take(excess)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var id in stale) _connections.Remove(id);
        _closedConnectionCount -= stale.Count;
    }

    private static CoreProxyGroup ToCoreProxyGroup(Daemon.Group group) =>
        new(
            group.Tag,
            group.Type,
            group.Selectable,
            group.Selected,
            group.IsExpand,
            group.Items.Select(ToCoreProxyGroupItem).ToList());

    private static CoreProxyGroupItem ToCoreProxyGroupItem(Daemon.GroupItem item) =>
        new(item.Tag, item.Type, item.UrlTestTime, item.UrlTestDelay);

    private static CoreConnection ToCoreConnection(Daemon.Connection c) =>
        new(
            c.Id,
            c.Inbound,
            c.InboundType,
            c.IpVersion,
            c.Network,
            c.Source,
            c.Destination,
            c.Domain,
            string.IsNullOrWhiteSpace(c.Domain) ? c.Destination : c.Domain,
            c.Protocol,
            c.User,
            c.FromOutbound,
            c.CreatedAt,
            c.ClosedAt,
            c.Uplink,
            c.Downlink,
            c.UplinkTotal,
            c.DownlinkTotal,
            c.Rule,
            c.Outbound,
            c.OutboundType,
            c.ChainList.ToList(),
            c.ProcessInfo is null
                ? null
                : new CoreProcessInfo(
                    c.ProcessInfo.ProcessId,
                    c.ProcessInfo.UserId,
                    c.ProcessInfo.UserName,
                    c.ProcessInfo.ProcessPath,
                    c.ProcessInfo.PackageNames.ToList()));

    private void SetState(RuntimeState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void RaiseLog(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            LogReceived?.Invoke(this, line);
    }
}
