namespace sing_box_for_windows.Services.Core;

public sealed record RuntimeState(bool IsRunning, string Status)
{
    public static RuntimeState Stopped { get; } = new(false, "Stopped");
}

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

