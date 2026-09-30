using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace SFW;

/// <summary>
/// Entry point. The app is single-instanced per user session: Windows starts the
/// login instance through the packaged startup task, and every later launch —
/// Start menu, taskbar, tray — is redirected to that instance instead of starting
/// a second one. A second instance would mean a second tray icon and a core
/// claimant the daemon refuses, because ownership of the running service belongs
/// to whoever claimed it first.
/// </summary>
public static class Program
{
    private const string InstanceKey = "sing-box-for-windows";

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!BecomePrimaryInstance()) return;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// Registers this process as the primary instance, or hands the activation to
    /// the primary already running. Returns false when the activation was handed
    /// over, in which case this process must exit without starting XAML.
    /// </summary>
    private static bool BecomePrimaryInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (primary.IsCurrent)
        {
            primary.Activated += OnActivated;
            return true;
        }

        Services.StartupDiag.Log("Program: another instance is running; redirecting this activation to it.");
        RedirectActivationTo(primary, activation);
        return false;
    }

    /// <summary>Another launch was redirected here: surface the window it asked for.</summary>
    private static void OnActivated(object? sender, AppActivationArguments activation) =>
        App.ShowInstanceFromActivation(activation);

    private static void RedirectActivationTo(AppInstance primary, AppActivationArguments activation)
    {
        // RedirectActivationToAsync must not run on this STA thread, so it runs on
        // the thread pool while this one waits — nothing else is running yet, so a
        // plain wait cannot deadlock a message pump.
        using var redirected = new SemaphoreSlim(0, 1);
        _ = Task.Run(async () =>
        {
            try
            {
                await primary.RedirectActivationToAsync(activation);
            }
            catch (Exception exception)
            {
                StartupDiagLog(exception);
            }
            finally
            {
                redirected.Release();
            }
        });
        redirected.Wait();
    }

    private static void StartupDiagLog(Exception exception)
    {
        try
        {
            Services.StartupDiag.Log($"Program: activation redirect failed: {exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
