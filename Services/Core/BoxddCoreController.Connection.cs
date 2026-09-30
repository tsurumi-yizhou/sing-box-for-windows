using Daemon;
using Desktop;
using Google.Protobuf.WellKnownTypes;

namespace SFW.Services.Core;

public sealed partial class BoxddCoreController
{
    private CancellationTokenSource? _connectionCts;
    private readonly List<Task> _sessionTasks = [];
    private Task _serviceStatusTask = Task.CompletedTask;
    private bool _ownsService;

    private void Track(Task task) => _sessionTasks.Add(task);

    private void SetConnection(DaemonConnectionState connection)
    {
        Connection = connection;
        ConnectionChanged?.Invoke(this, connection);
    }

    private async Task HandshakeAsync(CancellationToken token)
    {
        var bundled = await DaemonServiceManager.QueryBundledVersionAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var client = _desktopClient ?? throw new InvalidOperationException("The daemon connection is not open.");
        var info = await client.GetDaemonInfoAsync(new Empty(), cancellationToken: timeout.Token);
        if (bundled is not null && info.Version != bundled)
        {
            var message = Loc.Get(
                $"The running core is {info.Version}, but this app includes {bundled}. Upgrade the service to continue.",
                $"运行中的内核为 {info.Version}，应用附带的是 {bundled}。请升级服务以继续。");
            SetConnection(new(DaemonConnectionPhase.VersionMismatch, message, info.Version, bundled));
            throw new InvalidOperationException(message);
        }
        if (info.Ownership == DaemonOwnership.Other)
        {
            var message = Loc.Get("The service is owned by another Windows user.", "服务正由其他 Windows 用户占用。");
            SetConnection(new(DaemonConnectionPhase.OwnedByOtherUser, message, info.Version, bundled));
            throw new InvalidOperationException(message);
        }
        if (info.Ownership == DaemonOwnership.Available)
            await client.ClaimServiceAsync(new Empty(), cancellationToken: timeout.Token);
        else if (info.Ownership != DaemonOwnership.Caller)
            throw new InvalidOperationException("The daemon returned an invalid ownership state.");
        _ownsService = true;
        SetConnection(new(DaemonConnectionPhase.Connected, DaemonVersion: info.Version, BundledVersion: bundled));
    }

    private async Task WaitForStartedAsync(CancellationToken token)
    {
        using var status = GetClient().SubscribeServiceStatus(new Empty(), cancellationToken: token);
        while (await status.ResponseStream.MoveNext(token))
        {
            token.ThrowIfCancellationRequested();
            if (status.ResponseStream.Current.Status == ServiceStatus.Types.Type.Started) return;
        }
        throw new IOException("The service status stream ended before the core started.");
    }

    /// <summary>Rebuild the worker and every subscription when the service
    /// session ends. Reconnect only: never replay StartService or seize ownership.</summary>
    private async Task MonitorConnectionAsync(CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await _serviceStatusTask; }
                catch (Exception) when (!token.IsCancellationRequested) { /* reported by status stream */ }
                token.ThrowIfCancellationRequested();

                var connected = false;
                var attempt = 0;
                while (!connected && !token.IsCancellationRequested)
                {
                    await _lifecycleGate.WaitAsync(token);
                    try
                    {
                        if (_connectionCts != lifetime) return;
                        await TeardownConnectionAsync(CancellationToken.None);
                        ResetRuntimeState();
                        SetState(new(false, "Reconnecting"));
                        SetConnection(new(DaemonConnectionPhase.Reconnecting));
                        var probe = DaemonServiceManager.ProbeService();
                        if (probe != DaemonConnectionPhase.Connected)
                        {
                            SetConnection(new(probe));
                        }
                        else
                        {
                            await StartWorkerAsync(token);
                            _channel = NamedPipeChannel.Create(_relayPipeName!);
                            _client = new StartedService.StartedServiceClient(_channel);
                            _managedClient = new ManagedService.ManagedServiceClient(_channel);
                            _desktopClient = new DesktopService.DesktopServiceClient(_channel);
                            await HandshakeAsync(token);

                            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                            var sessionToken = _streamCts.Token;
                            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                            var generation = Interlocked.Increment(ref _startGeneration);
                            _serviceStatusTask = Task.Run(() => StreamServiceStatusAsync(sessionToken, first, started, generation));
                            Track(_serviceStatusTask);
                            await first.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                            Track(Task.Run(() => StreamLogsAsync(sessionToken)));
                            Track(Task.Run(() => StreamStatusAsync(sessionToken)));
                            Track(Task.Run(() => StreamGroupsAsync(sessionToken)));
                            Track(Task.Run(() => StreamConnectionsAsync(sessionToken)));
                            Track(Task.Run(() => StreamOutboundsAsync(sessionToken)));
                            Track(Task.Run(() => StreamClashModeAsync(sessionToken)));
                            connected = true;
                        }
                    }
                    catch (Exception) when (token.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        if (Connection.Phase is not (DaemonConnectionPhase.VersionMismatch or DaemonConnectionPhase.OwnedByOtherUser))
                            SetConnection(new(DaemonConnectionPhase.Unavailable, error.Message));
                        RaiseLog($"daemon reconnect failed: {error.Message}");
                    }
                    finally { _lifecycleGate.Release(); }
                    if (!connected)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(++attempt, 5)), token);
                    else
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { RaiseLog($"daemon session ended: {error.Message}"); }
    }
}
