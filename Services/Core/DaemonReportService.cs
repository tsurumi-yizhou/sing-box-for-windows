using System.Text;
using Daemon;
using Desktop;
using Google.Protobuf.WellKnownTypes;

namespace SFW.Services.Core;

/// <summary>
/// Access to the daemon's crash/OOM/power report stores, mirroring the SFA/SFM
/// report managers. Reports live in the daemon's per-user working directory and
/// are only reachable through an authenticated worker relay, so every operation
/// opens a short-lived worker session instead of requiring a running core.
/// </summary>
public static class DaemonReportService
{
    /// <summary>True when the daemon service is running and can answer report RPCs.</summary>
    public static Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        DaemonServiceManager.IsDaemonReachableAsync(cancellationToken);

    /// <summary>Unread counts for the three debug tools (Tools page badges).</summary>
    public static Task<CoreReportUnreadCounts> GetUnreadCountsAsync(CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) =>
        {
            var crash = await desktop.ListCrashReportsAsync(new Empty(), cancellationToken: token);
            var oom = await desktop.ListOOMReportsAsync(new Empty(), cancellationToken: token);
            var power = await desktop.ListPowerReportsAsync(new Empty(), cancellationToken: token);
            return new CoreReportUnreadCounts(
                crash.Reports.Count(report => !report.IsRead),
                oom.Reports.Count(report => !report.IsRead),
                power.Reports.Count(report => !report.IsRead));
        }, cancellationToken);

    public static Task<IReadOnlyList<CoreReportEntry>> ListAsync(
        CoreReportKind kind, CancellationToken cancellationToken = default) =>
        WithDesktopAsync<IReadOnlyList<CoreReportEntry>>(async (desktop, _, token) => kind switch
        {
            CoreReportKind.Crash => (await desktop.ListCrashReportsAsync(new Empty(), cancellationToken: token))
                .Reports
                .Select(report => new CoreReportEntry(report.Name, FromUnixMilliseconds(report.CrashedAt), report.IsRead))
                .ToList(),
            CoreReportKind.OutOfMemory => (await desktop.ListOOMReportsAsync(new Empty(), cancellationToken: token))
                .Reports
                .Select(report => new CoreReportEntry(report.Name, FromUnixMilliseconds(report.RecordedAt), report.IsRead))
                .ToList(),
            CoreReportKind.Power => (await desktop.ListPowerReportsAsync(new Empty(), cancellationToken: token))
                .Reports
                .Select(report => new CoreReportEntry(report.Name, FromUnixMilliseconds(report.RecordedAt), report.IsRead))
                .ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, cancellationToken);

    public static Task<CoreReportContent> ReadAsync(
        CoreReportKind kind, string name, CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) => kind switch
        {
            CoreReportKind.Crash => new CoreReportContent(
                (await desktop.ReadCrashReportAsync(
                        new CrashReportRequest { Name = name }, cancellationToken: token))
                    .Files
                    .Select(file => new CoreReportFile(file.Name, file.Content, IsProfile: false))
                    .ToList()),
            CoreReportKind.OutOfMemory => new CoreReportContent(ToFiles(
                (await desktop.ReadOOMReportAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token)).Files)),
            CoreReportKind.Power => new CoreReportContent(ToFiles(
                (await desktop.ReadPowerReportAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token)).Files)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, cancellationToken);

    public static Task MarkReadAsync(
        CoreReportKind kind, string name, CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) =>
        {
            switch (kind)
            {
                case CoreReportKind.Crash:
                    await desktop.MarkCrashReportReadAsync(
                        new CrashReportRequest { Name = name }, cancellationToken: token);
                    break;
                case CoreReportKind.OutOfMemory:
                    await desktop.MarkOOMReportReadAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token);
                    break;
                case CoreReportKind.Power:
                    await desktop.MarkPowerReportReadAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return true;
        }, cancellationToken);

    public static Task DeleteAsync(
        CoreReportKind kind, string name, CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) =>
        {
            switch (kind)
            {
                case CoreReportKind.Crash:
                    await desktop.DeleteCrashReportAsync(
                        new CrashReportRequest { Name = name }, cancellationToken: token);
                    break;
                case CoreReportKind.OutOfMemory:
                    await desktop.DeleteOOMReportAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token);
                    break;
                case CoreReportKind.Power:
                    await desktop.DeletePowerReportAsync(
                        new OOMReportRequest { Name = name }, cancellationToken: token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return true;
        }, cancellationToken);

    public static Task DeleteAllAsync(
        CoreReportKind kind, CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) =>
        {
            switch (kind)
            {
                case CoreReportKind.Crash:
                    await desktop.DeleteAllCrashReportsAsync(new Empty(), cancellationToken: token);
                    break;
                case CoreReportKind.OutOfMemory:
                    await desktop.DeleteAllOOMReportsAsync(new Empty(), cancellationToken: token);
                    break;
                case CoreReportKind.Power:
                    await desktop.DeleteAllPowerReportsAsync(new Empty(), cancellationToken: token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
            return true;
        }, cancellationToken);

    public static Task<CoreReportArchive> ExportAsync(
        CoreReportKind kind,
        string name,
        bool withConfiguration,
        bool withLog,
        bool encrypt,
        CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (desktop, _, token) =>
        {
            CrashReportArchive archive = kind switch
            {
                CoreReportKind.Crash => await desktop.ExportCrashReportAsync(
                    new CrashReportExportRequest
                    {
                        Name = name,
                        WithConfiguration = withConfiguration,
                        WithLog = withLog,
                        Encrypt = encrypt,
                    }, cancellationToken: token),
                CoreReportKind.OutOfMemory => await desktop.ExportOOMReportAsync(
                    new OOMReportExportRequest
                    {
                        Name = name,
                        WithConfiguration = withConfiguration,
                        WithLog = withLog,
                        Encrypt = encrypt,
                    }, cancellationToken: token),
                CoreReportKind.Power => await desktop.ExportPowerReportAsync(
                    new OOMReportExportRequest
                    {
                        Name = name,
                        WithConfiguration = withConfiguration,
                        WithLog = withLog,
                        Encrypt = encrypt,
                    }, cancellationToken: token),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            return new CoreReportArchive(archive.FileName, archive.Data.ToByteArray());
        }, cancellationToken);

    /// <summary>
    /// ManagedService.TriggerOOMReport: SFA's "Fetch Memory Report". Requires a
    /// running core; the caller checks the runtime state first.
    /// </summary>
    public static Task TriggerOomReportAsync(CancellationToken cancellationToken = default) =>
        WithDesktopAsync(async (_, managed, token) =>
        {
            await managed.TriggerOOMReportAsync(new Empty(), cancellationToken: token);
            return true;
        }, cancellationToken);

    private static IReadOnlyList<CoreReportFile> ToFiles(IEnumerable<OOMReportFile> files) =>
        files.Select(file => new CoreReportFile(
                file.Name,
                file.IsProfile ? null : Encoding.UTF8.GetString(file.Content.Span),
                file.IsProfile))
            .ToList();

    private static DateTimeOffset FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Report RPCs share the daemon relay session with the other daemon-level
    /// tools; see <see cref="DaemonSession"/> for the worker handshake.
    /// </summary>
    private static Task<T> WithDesktopAsync<T>(
        Func<DesktopService.DesktopServiceClient, ManagedService.ManagedServiceClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        DaemonSession.RunAsync(action, cancellationToken);
}
