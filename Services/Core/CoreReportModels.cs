namespace SFW.Services.Core;

/// <summary>
/// Report stores owned by the boxdd daemon. SFA/SFM show three separate debug
/// tools; the daemon exposes the same three stores over DesktopService.
/// </summary>
public enum CoreReportKind
{
    Crash,
    OutOfMemory,
    Power,
}

/// <summary>One report entry. Timestamps are daemon-recorded (UTC milliseconds).</summary>
public sealed record CoreReportEntry(
    string Name,
    DateTimeOffset Timestamp,
    bool IsRead);

/// <summary>
/// A file inside a report. Crash/OOM/power log files arrive as UTF-8 text;
/// profile entries are name-only and have to be exported to be inspected.
/// </summary>
public sealed record CoreReportFile(
    string Name,
    string? Text,
    bool IsProfile)
{
    /// <summary>Crash/OOM/power log files are UTF-8 text; profile entries are not.</summary>
    public bool IsText => Text is not null;
}

public sealed record CoreReportContent(IReadOnlyList<CoreReportFile> Files);

/// <summary>
/// Identifies one report for the detail page navigation parameter.
/// </summary>
public sealed record CoreReportReference(
    CoreReportKind Kind,
    string Name,
    DateTimeOffset Timestamp);

/// <summary>Export archive produced by the daemon (zip, optionally age-encrypted).</summary>
public sealed record CoreReportArchive(string FileName, byte[] Data);

public sealed record CoreReportUnreadCounts(int Crash, int OutOfMemory, int Power)
{
    public static CoreReportUnreadCounts Empty { get; } = new(0, 0, 0);

    public int For(CoreReportKind kind) => kind switch
    {
        CoreReportKind.Crash => Crash,
        CoreReportKind.OutOfMemory => OutOfMemory,
        CoreReportKind.Power => Power,
        _ => 0,
    };

    public int Total => Crash + OutOfMemory + Power;
}

/// <summary>
/// Service start options. SFA/SFM persist these in shared preferences and pass
/// them with every service start; the daemon merges them into its saved options.
/// </summary>
public sealed record CoreStartOptions(
    bool OomKillerEnabled,
    bool OomKillerKillConnections,
    long OomMemoryLimitBytes,
    bool PowerReportEnabled)
{
    /// <summary>Memory limits offered by SFM's OOM report settings.</summary>
    public static IReadOnlyList<int> MemoryLimitMegabytes { get; } = [50, 100, 200, 300, 500, 750, 1024];
}
