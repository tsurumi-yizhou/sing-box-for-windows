using Microsoft.UI.Xaml;
using sing_box_for_windows.Services;

namespace sing_box_for_windows;

public partial class App : Application
{
    private static Window? _window;
    private static int _coreStopAttempted;
    public static AppState State { get; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            WriteCrashLog("App.UnhandledException", e.Exception);
            TryStopCoreBeforeExit("App.UnhandledException");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TryStopCoreBeforeExit("AppDomain.UnhandledException");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryStopCoreBeforeExit("ProcessExit");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// A later launch of the app was redirected to this instance: surface the window
    /// the launch asked for, because the primary may be sitting in the tray (the
    /// login instance starts hidden). A redirected startup-task activation is not a
    /// user gesture, so it never shows anything.
    /// </summary>
    internal static void ShowInstanceFromActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments activation)
    {
        if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask) return;
        StartupDiag.Log($"OnActivated: {activation.Kind} redirected to the running instance");

        if (_window is MainWindow window)
        {
            window.ShowFromTray();
        }
        else
        {
            // Redirected before the window existed: the launch path shows it.
            _windowRequested = true;
        }
    }

    private static bool _windowRequested;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // "Start at login" launches the app through its packaged startup task; that
        // run belongs in the tray only — no window, and the core is started with the
        // selected profile so the proxy is up without anyone clicking anything.
        var loginLaunch = IsLoginLaunch();
        StartupDiag.Log($"OnLaunched: loginLaunch={loginLaunch}");

        var window = new MainWindow(startHidden: loginLaunch);
        _window = window;
        window.Closed += (_, _) =>
        {
            TryStopCoreBeforeExit("Window.Closed");
            _ = State.DisposeAsync();
        };

        if (loginLaunch && !_windowRequested)
        {
            // The daemon's own WasRunning restore is the desired behaviour here, so
            // skip the orphan reconcile and just start the core.
            _ = StartAtLoginAsync();
            return;
        }

        if (_windowRequested)
        {
            // A launch was redirected here before the window existed.
            _windowRequested = false;
            window.ShowFromTray();
        }
        else
        {
            window.Activate();
        }

        // Lifecycle binding, other direction: a core must not outlive the app.
        // The daemon service is resident and restores a previously running core
        // at boot (boxdd WasRunning), and a crashed/killed previous instance
        // leaves its core behind as well. Stop such orphans on startup so a
        // running core always implies a live tray icon.
        _ = Task.Run(async () =>
        {
            try
            {
                await State.Core.StopOrphanedServiceAsync();
            }
            catch (Exception exception)
            {
                StartupDiag.Log($"OnLaunched: orphan reconcile failed: {exception.GetType().Name}: {exception.Message}");
            }
        });
    }

    private static async Task StartAtLoginAsync()
    {
        try
        {
            await State.TryStartAtLoginAsync();
        }
        catch (Exception exception)
        {
            StartupDiag.Log($"OnLaunched: login start failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// True when Windows started this process from the app's startup task rather
    /// than from a user gesture opening the app.
    /// </summary>
    private static bool IsLoginLaunch()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            return activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch (Exception exception)
        {
            StartupDiag.Log($"OnLaunched: activation kind unavailable: {exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort, bounded synchronous core stop for the exit paths where no
    /// async teardown is possible (crash, process exit, window destruction).
    /// Runs the stop on the thread pool: StopAsync's gRPC continuations must
    /// not capture the UI dispatcher — blocking the UI thread here would
    /// deadlock them until the timeout. A hard kill runs no managed code; the
    /// worker's --parent-pid watch drops the relay then, and the orphan check
    /// in OnLaunched stops the core on the next launch.
    /// </summary>
    private static void TryStopCoreBeforeExit(string source)
    {
        if (Interlocked.Exchange(ref _coreStopAttempted, 1) == 1) return;
        try
        {
            StartupDiag.Log($"{source}: stopping the core before exit");
            using var task = Task.Run(() => State.StopAsync());
            if (!task.Wait(TimeSpan.FromSeconds(2)))
            {
                StartupDiag.Log($"{source}: core stop timed out; exiting anyway");
            }
        }
        catch (Exception exception)
        {
            StartupDiag.Log($"{source}: core stop failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var path = Path.Combine(State.DataDirectory, "crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best effort only.
        }
    }
}
