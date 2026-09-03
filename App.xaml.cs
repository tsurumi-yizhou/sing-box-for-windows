using Microsoft.UI.Xaml;
using sing_box_for_windows.Services;

namespace sing_box_for_windows;

public partial class App : Application
{
    private Window? _window;
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            TryStopCoreBeforeExit("Window.Closed");
            _ = State.DisposeAsync();
        };
        _window.Activate();

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
