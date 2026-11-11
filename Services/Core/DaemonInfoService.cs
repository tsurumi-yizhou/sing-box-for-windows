using Daemon;
using Google.Protobuf.WellKnownTypes;

namespace SFW.Services.Core;

/// <summary>
/// The daemon's per-user working directory (profiles, reports, caches). SFA/SFM
/// show its size as the Core "Data Size"; on Windows it is owned by the daemon
/// service, so it has to be queried over the authenticated relay.
/// </summary>
public sealed record DaemonWorkingDirectoryInfo(string Path, long SizeBytes);

/// <summary>Read-only daemon information used by the Settings pages.</summary>
public static class DaemonInfoService
{
    public static Task<DaemonWorkingDirectoryInfo> GetWorkingDirectoryAsync(
        CancellationToken cancellationToken = default) =>
        DaemonSession.RunAsync(async (desktop, _, token) =>
        {
            var info = await desktop.GetWorkingDirectoryAsync(new Empty(), cancellationToken: token);
            return new DaemonWorkingDirectoryInfo(info.Path, info.Size);
        }, cancellationToken);
}
