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

    Task StartAsync(string configContent, CancellationToken cancellationToken = default);
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
}
