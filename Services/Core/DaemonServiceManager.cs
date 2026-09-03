using System.Diagnostics;
using Windows.ApplicationModel;
using sing_box_for_windows.Services;

namespace sing_box_for_windows.Services.Core;

/// <summary>
/// Ensures the boxdd Windows service ("sing-box-daemon") is installed and running.
/// Installation/start requires elevation once; the service then starts
/// automatically at boot and needs no further UAC prompts.
/// </summary>
public static class DaemonServiceManager
{
    // Must match serviceName in boxdd's main.go — boxdd installs the service
    // under its own name, not "sing-box".
    public const string ServiceName = "sing-box-daemon";
    public const string DaemonPipePath = @"\\.\pipe\ProtectedPrefix\Administrators\sing-box";

    public static string? FindDaemonExecutable()
    {
        var candidates = new List<string>();
        try
        {
            candidates.Add(Path.Combine(Package.Current.InstalledLocation.Path,
                "resources", "daemon", "sing-box-daemon.exe"));
        }
        catch (InvalidOperationException)
        {
            // Unpackaged processes have no deployable daemon location.
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Resolves the daemon executable that must be spawned as the boxdd worker.
    /// boxdd peer authentication requires the worker process to be the very same
    /// file the installed Windows service was started from (same final path and
    /// file identity). When the app is launched from a directory other than the
    /// installed layout (for example a raw bin\ build output), the nearby copy is
    /// a different file and the daemon rejects it with "named pipe client is not
    /// the installed sing-box worker". Prefer the service's recorded ImagePath so
    /// the worker always matches the running daemon.
    /// </summary>
    public static string? FindWorkerExecutable()
    {
        var serviceImagePath = QueryServiceImagePath();
        if (!string.IsNullOrWhiteSpace(serviceImagePath) && File.Exists(serviceImagePath))
        {
            return serviceImagePath;
        }
        return FindDaemonExecutable();
    }

    /// <summary>
    /// Returns the executable path recorded in the installed Windows service's
    /// ImagePath, or null when the service is not installed. This is the exact
    /// file the running daemon service was started from, so it is authoritative
    /// for boxdd peer authentication (the worker must be that same file).
    /// </summary>
    public static string? QueryServiceImagePath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            var imagePath = key?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            return SplitCommandLine(imagePath).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// True when Windows reports the daemon service as running. Do not probe the
    /// protected daemon pipe directly: boxdd deliberately accepts only an
    /// authenticated worker there, so a raw probe produces misleading rejection
    /// events and is not a valid health check.
    /// </summary>
    public static Task<bool> IsDaemonReachableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(QueryServiceStatus() == ServiceStatus.Running);
    }

    /// <summary>
    /// Installs and/or starts the daemon service. Service control requires
    /// elevation, so this runs the daemon CLI with the "runas" verb (one UAC
    /// prompt on first use).
    /// </summary>
    public static async Task EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken = default)
    {
        StartupDiag.Log("EnsureRunningAsync: enter");
        if (await IsDaemonReachableAsync(cancellationToken))
        {
            StartupDiag.Log("EnsureRunningAsync: already reachable");
            return;
        }

        // Prefer the file the installed service actually runs; `service start`
        // and `service install` then operate on the same binary the worker will
        // later be spawned from, keeping the daemon and worker peer-identical.
        var daemonExe = FindWorkerExecutable()
            ?? throw new InvalidOperationException("sing-box-daemon.exe was not found next to the app (resources\\daemon).");

        var status = QueryServiceStatus();
        var installedPath = QueryServiceImagePath();
        if (status is null || !PathsEqual(installedPath, daemonExe))
        {
            log(status is null
                ? "Installing the SingBox daemon service (requires administrator)…"
                : "Updating the SingBox daemon service path (requires administrator)…");
            await RunElevatedAsync(daemonExe, "service install", cancellationToken);
        }
        else if (status != ServiceStatus.Running)
        {
            log("Starting the SingBox daemon service (requires administrator)…");
            await RunElevatedAsync(daemonExe, "service start", cancellationToken);
        }

        // The pipe can take a moment after the service reports running.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await IsDaemonReachableAsync(timeout.Token)) return;
            await Task.Delay(200, cancellationToken);
        }
        throw new InvalidOperationException("The SingBox daemon did not come up in time.");
    }

    private static bool PathsEqual(string? first, string? second) =>
        first is not null && second is not null &&
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private enum ServiceStatus { Stopped, Running, Other }

    private static ServiceStatus? QueryServiceStatus()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null; // service does not exist
            if (output.Contains("RUNNING")) return ServiceStatus.Running;
            if (output.Contains("STOPPED")) return ServiceStatus.Stopped;
            return ServiceStatus.Other;
        }
        catch
        {
            return null;
        }
    }

    private static async Task RunElevatedAsync(string executable, string arguments, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Failed to launch the daemon installer.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(
                "Installing/starting the SingBox daemon requires administrator approval; please allow the UAC prompt. / 安装或启动 SingBox 守护进程需要管理员授权，请在 UAC 提示中允许。",
                ex);
        }
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sing-box-daemon {arguments} failed with exit code {process.ExitCode}.");
        }
    }
}
