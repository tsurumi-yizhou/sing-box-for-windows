using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.ApplicationModel;
using SFW.Services;

namespace SFW.Services.Core;

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
    public static string? QueryServiceImagePath() =>
        QueryServiceCommandLine() is { } commandLine
            ? SplitCommandLine(commandLine).FirstOrDefault()
            : null;

    /// <summary>
    /// The daemon's working directory as recorded by the installed service, so
    /// re-installing keeps the existing data location instead of moving it.
    /// </summary>
    private static string? QueryServiceWorkingDirectory()
    {
        if (QueryServiceCommandLine() is not { } commandLine) return null;
        var arguments = SplitCommandLine(commandLine).ToList();
        var index = arguments.FindIndex(argument =>
            string.Equals(argument, "--working-directory", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static string? QueryServiceCommandLine()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            var imagePath = key?.GetValue("ImagePath") as string;
            return string.IsNullOrWhiteSpace(imagePath) ? null : imagePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>boxdd's default daemon working directory.</summary>
    private static string DefaultServiceWorkingDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "sing-box-daemon");

    private static readonly SemaphoreSlim VersionQueryLock = new(1, 1);
    private static string? _cachedVersion;
    private static string? _bundledVersion;

    public static async Task<string?> QueryBundledVersionAsync(CancellationToken token = default)
    {
        if (_bundledVersion is not null) return _bundledVersion;
        var executable = FindDaemonExecutable();
        if (executable is null) return null;
        var output = await Task.Run(() => RunCaptured(executable, ["version"]), token);
        const string prefix = "sing-box-daemon version ";
        _bundledVersion = output.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return _bundledVersion;
    }

    public static DaemonConnectionPhase ProbeService() => QueryServiceStatus() switch
    {
        null => DaemonConnectionPhase.NotInstalled,
        ServiceStatus.Running => DaemonConnectionPhase.Connected,
        _ => DaemonConnectionPhase.NotRunning,
    };

    public static async Task InstallBundledServiceAsync(Action<string> log, CancellationToken token = default)
    {
        var executable = FindDaemonExecutable()
            ?? throw new InvalidOperationException("The bundled daemon was not found.");
        // The daemon installer handles stopping/replacing its service registration.
        // Preserve the existing working directory, including on path/version repair.
        await RunElevatedAsync(executable, InstallArguments(executable, log), token);
        _cachedVersion = null;
        await EnsureRunningAsync(log, token);
    }

    /// <summary>
    /// The daemon's own version report, e.g. "1.14.0" (`sing-box-daemon version`).
    /// Needs neither elevation nor a running service, so the UI can show the core
    /// version before anything is started — the upstream desktop client reads it
    /// the same way. Cached for the process lifetime: the answer only changes when
    /// the app is updated, while every call spawns a full daemon binary.
    /// </summary>
    public static async Task<string?> QueryVersionAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cachedVersion) is { } cached) return cached;
        await VersionQueryLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _cachedVersion) is { } raced) return raced;

            var executable = FindWorkerExecutable();
            if (executable is null) return null;
            var output = await Task.Run(() => RunCaptured(executable, ["version"]), cancellationToken);
            const string prefix = "sing-box-daemon version ";
            foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var version = line[prefix.Length..].Trim();
                if (version.Length > 0)
                {
                    Volatile.Write(ref _cachedVersion, version);
                    return version;
                }
            }
            return null;
        }
        finally
        {
            VersionQueryLock.Release();
        }
    }

    /// <summary>
    /// Arguments for boxdd's `service install`. The working directory is
    /// mandatory — boxdd has no default and fails with "missing daemon working
    /// directory" without it.
    ///
    /// boxdd also refuses to register a service whose executable lives where a
    /// non-administrator can write, because that user could then swap the binary
    /// that runs as SYSTEM. Only a layout under Program Files satisfies that by
    /// itself; a development layout (dotnet run, loose layout, raw bin output)
    /// lives in the user profile and has to opt in explicitly.
    /// </summary>
    private static IReadOnlyList<string> InstallArguments(string daemonExecutable, Action<string> log)
    {
        var arguments = new List<string>
        {
            "service",
            "install",
            "--working-directory",
            QueryServiceWorkingDirectory() ?? DefaultServiceWorkingDirectory,
        };
        if (IsAdministratorOnlyLocation(daemonExecutable)) return arguments;

        log(Loc.Get(
            "The daemon installation directory is not administrator-only; boxdd will be told to allow it.",
            "守护进程安装目录并非仅管理员可写；将按 boxdd 要求显式允许。"));
        arguments.Add("--allow-unsafe-installation-directory-permissions");
        return arguments;
    }

    /// <summary>
    /// True when the executable sits where only administrators can write, i.e.
    /// under Program Files — which includes the WindowsApps package root of a
    /// packaged app. Do not use the package install location as the criterion: a
    /// `dotnet run`/loose layout is itself registered as the package's install
    /// location, so it would look "installed" while living in the user profile.
    /// </summary>
    private static bool IsAdministratorOnlyLocation(string executable)
    {
        var path = Path.GetFullPath(executable);
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
            await RunElevatedAsync(daemonExe, InstallArguments(daemonExe, log), cancellationToken);
        }
        else if (status != ServiceStatus.Running)
        {
            log("Starting the SingBox daemon service (requires administrator)…");
            await RunElevatedAsync(daemonExe, ["service", "start"], cancellationToken);
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

    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;
    private const int ServiceStoppedState = 0x00000001;
    private const int ServiceRunningState = 0x00000004;
    private const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, int desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        ref ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>
    /// Queries the service manager directly instead of running `sc.exe`: this is
    /// called on every health check and EnsureRunningAsync polls it while the
    /// service comes up, so spawning a process per poll is pure churn. Returns
    /// null when the service is not installed (or the manager cannot be opened),
    /// which is what the caller treats as "not installed".
    /// </summary>
    private static ServiceStatus? QueryServiceStatus()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null; // not installed, or not queryable
            try
            {
                var status = default(ServiceStatusProcess);
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status,
                        Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    return null;
                }
                return status.CurrentState switch
                {
                    ServiceRunningState => ServiceStatus.Running,
                    ServiceStoppedState => ServiceStatus.Stopped,
                    _ => ServiceStatus.Other,
                };
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static async Task RunElevatedAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                // ShellExecuteEx (needed for `runas`) takes a parameter string, not
                // an argument list.
                Arguments = string.Join(' ', arguments.Select(QuoteArgument)),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch the daemon installer.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(
                "Installing/starting the SingBox daemon requires administrator approval; please allow the UAC prompt. / 安装或启动 SingBox 守护进程需要管理员授权，请在 UAC 提示中允许。",
                ex);
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(DescribeFailure(executable, arguments, process.ExitCode));
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Builds the failure message. The elevated process cannot hand over its
    /// output (`runas` gives no handle to it), and an exit code alone never says
    /// what to fix. boxdd validates the installation layout and the working
    /// directory before it touches the service manager, so re-running the same
    /// command unelevated reproduces those diagnostics verbatim.
    /// </summary>
    private static string DescribeFailure(string executable, IReadOnlyList<string> arguments, int exitCode)
    {
        var commandLine = $"sing-box-daemon {string.Join(' ', arguments)}";
        // Only when this process is unelevated: a re-run from an elevated app
        // would really perform the command again, not just report on it.
        var output = IsProcessElevated() ? string.Empty : RunCaptured(executable, arguments);
        return string.IsNullOrWhiteSpace(output)
            ? $"{commandLine} failed with exit code {exitCode}."
            : $"{commandLine} failed: {output}";
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true; // Unknown: skip the diagnostic re-run.
        }
    }

    /// <summary>Quotes one argument for the ShellExecuteEx parameter string.</summary>
    private static string QuoteArgument(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>Runs the command with captured output, for diagnostics only.</summary>
    private static string RunCaptured(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null) return string.Empty;
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return "The daemon command timed out.";
            }
            var error = errorTask.GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();
            return (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
