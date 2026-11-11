namespace SFW.Services.Core;

public sealed record RuntimeState(bool IsRunning, string Status)
{
    public static RuntimeState Stopped { get; } = new(false, "Stopped");
    public bool IsActive => IsRunning || Status is "Starting" or "Reconnecting";
}

public enum DaemonConnectionPhase
{
    Disconnected, Connecting, Connected, Reconnecting, NotInstalled, NotRunning,
    VersionMismatch, OwnedByOtherUser, Unavailable
}

public sealed record DaemonConnectionState(
    DaemonConnectionPhase Phase, string? Error = null,
    string? DaemonVersion = null, string? BundledVersion = null);

/// <summary>
/// Mirrors libbox StatusMessage. Uplink/downlink are bytes per second;
/// UplinkTotal/downlinkTotal are cumulative bytes since service start.
/// </summary>
public sealed record CoreStatus(
    long Memory,
    int Goroutines,
    int ConnectionsIn,
    int ConnectionsOut,
    bool TrafficAvailable,
    long Uplink,
    long Downlink,
    long UplinkTotal,
    long DownlinkTotal)
{
    public static CoreStatus Empty { get; } = new(0, 0, 0, 0, false, 0, 0, 0, 0);
}

public sealed record CoreClashMode(IReadOnlyList<string> Modes, string CurrentMode)
{
    public static CoreClashMode Empty { get; } = new([], string.Empty);
}

public sealed record CoreSystemProxy(bool Available, bool Enabled)
{
    public static CoreSystemProxy Empty { get; } = new(false, false);
}
public sealed record CoreProxyGroupItem(string Tag, string Type, long UrlTestTime, int UrlTestDelay);

public sealed record CoreProxyGroup(
    string Tag,
    string Type,
    bool Selectable,
    string Selected,
    bool IsExpand,
    IReadOnlyList<CoreProxyGroupItem> Items);

public sealed record CoreProcessInfo(
    long ProcessId,
    int UserId,
    string UserName,
    string ProcessPath,
    IReadOnlyList<string> PackageNames);

public sealed record CoreConnection(
    string Id,
    string Inbound,
    string InboundType,
    int IpVersion,
    string Network,
    string Source,
    string Destination,
    string Domain,
    string DisplayDestination,
    string Protocol,
    string User,
    string FromOutbound,
    long CreatedAt,
    long ClosedAt,
    long Uplink,
    long Downlink,
    long UplinkTotal,
    long DownlinkTotal,
    string Rule,
    string Outbound,
    string OutboundType,
    IReadOnlyList<string> Chain,
    CoreProcessInfo? ProcessInfo);

/// <summary>Options for the libbox network-quality (speed/responsiveness) test.</summary>
public sealed record CoreNetworkQualityTestOptions(
    string? ConfigUrl,
    string? OutboundTag,
    bool Serial,
    int MaxRuntimeSeconds,
    bool Http3);

/// <summary>networkquality.Progress / Result. Phase: 0 idle, 1 download, 2 upload, 3 done.</summary>
public sealed record CoreNetworkQualityProgress(
    int Phase,
    long DownloadCapacity,
    long UploadCapacity,
    int DownloadRpm,
    int UploadRpm,
    int IdleLatencyMs,
    long ElapsedMs,
    bool IsFinal,
    string? Error);

/// <summary>stun.Progress / Result. Phase: 0 binding, 1 NAT mapping, 2 NAT filtering, 3 done.</summary>
public sealed record CoreStunProgress(
    int Phase,
    string ExternalAddr,
    int LatencyMs,
    int NatMapping,
    int NatFiltering,
    bool IsFinal,
    string? Error,
    bool NatTypeSupported);

/// <summary>
/// Mirrors the daemon's TailscalePeer. The daemon reports the same shape for
/// self, peers and the selected exit node, so one record serves all three.
/// </summary>
public sealed record CoreTailscalePeer(
    string HostName,
    string DnsName,
    string Os,
    IReadOnlyList<string> TailscaleIPs,
    bool Online,
    bool ExitNode,
    bool ExitNodeOption,
    bool Active,
    long RxBytes,
    long TxBytes,
    long KeyExpiry,
    string StableId,
    bool Expired,
    IReadOnlyList<string> SshHostKeys,
    bool ShareeNode,
    long LastSeen,
    bool CanReceiveFiles)
{
    /// <summary>
    /// SFM/SFA show the first MagicDNS label, falling back to the host name;
    /// the full DNS name is only used for identity and the details view.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var segment = DnsName.Split('.')[0];
            return string.IsNullOrEmpty(segment) ? HostName : segment;
        }
    }
}

/// <summary>Peers grouped by their Tailscale user, as reported by the daemon.</summary>
public sealed record CoreTailscaleUserGroup(
    long UserId,
    string LoginName,
    string DisplayName,
    string ProfilePicUrl,
    IReadOnlyList<CoreTailscalePeer> Peers);

/// <summary>
/// One configured <c>tailscale</c> endpoint, mirroring the daemon's
/// TailscaleEndpointStatus. BackendState is the raw Tailscale state
/// (Running, NeedsLogin, ...); StateText is the daemon-localized label.
/// </summary>
public sealed record CoreTailscaleEndpoint(
    string EndpointTag,
    string BackendState,
    string StateText,
    string AuthUrl,
    string NetworkName,
    string MagicDnsSuffix,
    CoreTailscalePeer? Self,
    IReadOnlyList<CoreTailscaleUserGroup> UserGroups,
    CoreTailscalePeer? ExitNode,
    bool KeyAuth,
    bool CanShareFiles,
    int WaitingFileCount,
    int ReceivingFileCount,
    int UnreadFileCount)
{
    /// <summary>True when the exit-node picker has anything to offer (SFM parity).</summary>
    public bool HasExitNodeCandidates
    {
        get
        {
            if (ExitNode is not null) return true;
            var selfStableId = Self?.StableId;
            return UserGroups.Any(group =>
                group.Peers.Any(peer => peer.ExitNodeOption && peer.StableId != selfStableId));
        }
    }

    public bool IsRunning => BackendState == "Running";
}

/// <summary>tailscale.ping result. A relay/DERP path has no direct endpoint.</summary>
public sealed record CoreTailscalePingResult(
    double LatencyMs,
    bool IsDirect,
    string Endpoint,
    int DerpRegionId,
    string DerpRegionCode,
    string PeerRelay,
    string? Error);
