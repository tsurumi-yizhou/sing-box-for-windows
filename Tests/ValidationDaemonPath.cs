namespace SFW.Services.Core;

// The integration fixture is a signed standalone application layout. It never
// queries, starts, stops, or claims the machine's installed Windows service.
internal static class DaemonServiceManager
{
    public static string FindDaemonExecutable() =>
        Path.Combine(AppContext.BaseDirectory, "resources", "daemon", "sing-box-daemon.exe");
}
