namespace SFW.Services.Core;

/// <summary>
/// Command/control surface modelled on the libbox command client used by
/// sing-box-for-android and sing-box-for-apple. It is not a Clash API client
/// and it does not spawn an external SingBox binary.
/// </summary>
public interface ICoreController : IAsyncDisposable
{
    event EventHandler<RuntimeState>? StateChanged;
    event EventHandler<DaemonConnectionState>? ConnectionChanged;
    event EventHandler<string>? LogReceived;
    event EventHandler? LogsReset;
    event EventHandler<CoreStatus>? StatusChanged;
    event EventHandler<IReadOnlyList<CoreProxyGroup>>? GroupsChanged;
    event EventHandler<IReadOnlyList<CoreConnection>>? ConnectionsChanged;
    event EventHandler<CoreClashMode>? ClashModeChanged;
    event EventHandler<IReadOnlyList<CoreProxyGroupItem>>? OutboundsChanged;
    event EventHandler<CoreSystemProxy>? SystemProxyChanged;

    RuntimeState State { get; }
    DaemonConnectionState Connection { get; }
    CoreStatus Status { get; }
    CoreClashMode ClashMode { get; }
    IReadOnlyList<CoreProxyGroup> Groups { get; }
    IReadOnlyList<CoreConnection> Connections { get; }
    IReadOnlyList<CoreProxyGroupItem> Outbounds { get; }
    CoreSystemProxy SystemProxy { get; }

    Task StartAsync(string configContent, CoreStartOptions? options = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifecycle binding: stops a core that is running in the daemon without a
    /// live app — left behind by a crashed/killed instance or auto-restored by
    /// the daemon at boot. No-op when nothing is running; never elevates.
    /// </summary>
    Task StopOrphanedServiceAsync(CancellationToken cancellationToken = default);
    Task SelectOutboundAsync(string groupTag, string itemTag, CancellationToken cancellationToken = default);
    Task UrlTestAsync(string outboundTag, CancellationToken cancellationToken = default);
    Task SetGroupExpandAsync(string groupTag, bool expand, CancellationToken cancellationToken = default);
    Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    Task CloseConnectionsAsync(CancellationToken cancellationToken = default);
    Task SetClashModeAsync(string mode, CancellationToken cancellationToken = default);
    Task ClearLogsAsync(CancellationToken cancellationToken = default);
    Task RefreshSystemProxyAsync(CancellationToken cancellationToken = default);
    Task SetSystemProxyEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>libbox StartNetworkQualityTest stream. Requires a running service.</summary>
    IAsyncEnumerable<CoreNetworkQualityProgress> StartNetworkQualityTestAsync(
        CoreNetworkQualityTestOptions options, CancellationToken cancellationToken = default);

    /// <summary>libbox StartSTUNTest stream. Requires a running service.</summary>
    IAsyncEnumerable<CoreStunProgress> StartStunTestAsync(
        string server, string? outboundTag, CancellationToken cancellationToken = default);

    // ----- Tailscale endpoints (SFM/SFA Tools parity) -----
    // The daemon owns the Tailscale engine; the app only renders and controls
    // it through the StartedService RPCs below, exactly like libbox does.

    /// <summary>
    /// SubscribeTailscaleStatus stream. Emits the full endpoint list whenever
    /// any Tailscale endpoint changes state, plus an initial snapshot. An empty
    /// list means the running profile has no <c>tailscale</c> endpoint.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<CoreTailscaleEndpoint>> SubscribeTailscaleStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>StartTailscalePing stream: continuous peer pings until cancelled.</summary>
    IAsyncEnumerable<CoreTailscalePingResult> StartTailscalePingAsync(
        string endpointTag, string peerIp, CancellationToken cancellationToken = default);

    /// <summary>SetTailscaleExitNode. An empty <paramref name="stableId"/> clears it.</summary>
    Task SetTailscaleExitNodeAsync(
        string endpointTag, string stableId, CancellationToken cancellationToken = default);

    /// <summary>TailscaleLogout. Unregisters the endpoint's node from the tailnet.</summary>
    Task TailscaleLogoutAsync(string endpointTag, CancellationToken cancellationToken = default);
}
