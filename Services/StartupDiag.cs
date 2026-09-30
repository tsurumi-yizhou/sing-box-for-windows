namespace SFW.Services;

/// <summary>
/// Append-only startup trace used to diagnose hangs in the connect path.
/// Written to the same data directory AppState uses, so it survives app
/// relaunches and can be inspected without a debugger attached.
/// </summary>
public static class StartupDiag
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sing-Box", "startup-diag.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic only; never let tracing break startup.
        }
    }
}
