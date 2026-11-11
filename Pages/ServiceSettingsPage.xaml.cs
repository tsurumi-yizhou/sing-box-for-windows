using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM SettingView "Packet Tunnel" / SFA "Service" parity for Windows: the boxdd
/// daemon service, the core it runs and the paths it runs from.
///
/// The one Windows-specific failure mode this page exists for is a stale service:
/// the registered service keeps running an older daemon than the one the app
/// ships, so the service "works" but silently lags the app. The page shows both
/// versions and offers one explicit Repair action.
///
/// Nothing here runs the service or elevates on its own: installing and starting
/// require one UAC approval and only ever happen from a click.
/// </summary>
public sealed partial class ServiceSettingsPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _actionCts;
    private bool _actionRunning;
    private bool _unloaded;
    private DaemonConnectionPhase _phase = DaemonConnectionPhase.Disconnected;
    private bool _versionMismatch;
    private string? _executablePath;
    private string? _workingDirectory;

    public ServiceSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unloaded = false;
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;
        // Cancel, but never dispose: an in-flight daemon call may still register
        // on the token, and the CTS is collected with the page.
        _refreshCts?.Cancel();
        _refreshCts = null;
        _actionCts?.Cancel();
        _actionCts = null;
    }

    // ----- Refresh -----

    private async Task RefreshAsync()
    {
        if (_actionRunning) return;

        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        // Service state and paths come straight from the service manager and the
        // registry: no elevation, no daemon call, safe while the service is down.
        var phase = DaemonServiceManager.ProbeService();
        _executablePath = DaemonServiceManager.QueryServiceImagePath();
        _workingDirectory = DaemonServiceManager.QueryServiceWorkingDirectoryPath();
        if (_unloaded) return;

        // A previous refresh may have flagged a mismatch; the version cards
        // re-decide that below, so clear the stale verdict first.
        _versionMismatch = false;
        RepairBar.IsOpen = false;

        RenderStatus(phase);
        RenderPaths();
        CoresCard.Description = Loc.Get("ServiceSettings.Checking");

        string? bundledVersion = null;
        string? serviceVersion = null;
        try
        {
            bundledVersion = await DaemonServiceManager.QueryBundledVersionAsync(token);
            if (token.IsCancellationRequested) return;
            // Without an installed service the "service version" is just the
            // bundled daemon the app would install, so do not report it as one.
            if (phase != DaemonConnectionPhase.NotInstalled)
            {
                serviceVersion = await DaemonServiceManager.QueryVersionAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Versions are informational; the status and path cards are already
            // populated and stay usable.
        }

        if (token.IsCancellationRequested) return;
        RenderVersions(phase, serviceVersion, bundledVersion);
    }

    private void RenderStatus(DaemonConnectionPhase phase)
    {
        _phase = phase;
        StatusText.Text = phase switch
        {
            DaemonConnectionPhase.Connected => Loc.Get("ServiceSettings.Running"),
            DaemonConnectionPhase.NotInstalled => Loc.Get("ServiceSettings.NotInstalled"),
            _ => Loc.Get("ServiceSettings.Stopped"),
        };
        StatusText.Foreground = ResBrush(phase == DaemonConnectionPhase.Connected
            ? "SystemFillColorSuccessBrush"
            : "SystemFillColorCautionBrush");
        ServiceStatusCard.Description = phase switch
        {
            DaemonConnectionPhase.Connected => Loc.Get("ServiceSettings.TheDaemonServiceIsInstalledAndRunning"),
            DaemonConnectionPhase.NotInstalled => Loc.Get("ServiceSettings.TheDaemonServiceIsNotInstalledInstallItSoProfilesCan"),
            _ => Loc.Get("ServiceSettings.TheDaemonServiceIsInstalledButStopped"),
        };
        UpdateActions();
    }

    private void RenderPaths()
    {
        var executableFolder = FolderOf(_executablePath);
        ExecutableCard.Description = string.IsNullOrWhiteSpace(_executablePath)
            ? Loc.Get("ServiceSettings.NotInstalled")
            : _executablePath;
        ExecutableCard.IsClickEnabled = executableFolder is not null;

        if (string.IsNullOrWhiteSpace(_workingDirectory))
        {
            ServiceWorkingDirectoryCard.Description = Loc.Get("ServiceSettings.Unavailable");
            ServiceWorkingDirectoryCard.IsClickEnabled = false;
            return;
        }

        var exists = Directory.Exists(_workingDirectory);
        ServiceWorkingDirectoryCard.Description = exists
            ? _workingDirectory
            : $"{_workingDirectory} — {Loc.Get("ServiceSettings.NotCreatedYet")}";
        ServiceWorkingDirectoryCard.IsClickEnabled = exists;
    }

    private void RenderVersions(DaemonConnectionPhase phase, string? serviceVersion, string? bundledVersion)
    {
        var unavailable = Loc.Get("ServiceSettings.Unavailable");
        var serviceText = phase == DaemonConnectionPhase.NotInstalled
            ? Loc.Get("ServiceSettings.NotInstalled")
            : serviceVersion ?? unavailable;
        var appText = bundledVersion ?? unavailable;

        CoresCard.Description = string.Format(
            Loc.Get("ServiceSettings.ServiceApp"),
            serviceText, appText);

        _versionMismatch = phase != DaemonConnectionPhase.NotInstalled
            && !string.IsNullOrEmpty(serviceVersion)
            && !string.IsNullOrEmpty(bundledVersion)
            && !string.Equals(serviceVersion, bundledVersion, StringComparison.OrdinalIgnoreCase);

        RepairBar.IsOpen = _versionMismatch;
        if (_versionMismatch)
        {
            RepairBar.Message = string.Format(
                Loc.Get("ServiceSettings.TheInstalledServiceRunsCoreButThisAppShipsRepairingI"),
                serviceVersion, bundledVersion);
        }

        UpdateActions();
    }

    private void UpdateActions()
    {
        var canStart = _phase == DaemonConnectionPhase.NotRunning;
        var canInstall = _phase == DaemonConnectionPhase.NotInstalled || _versionMismatch;

        StartServiceButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        RepairServiceButton.Visibility = canInstall ? Visibility.Visible : Visibility.Collapsed;
        StartServiceButton.IsEnabled = canStart && !_actionRunning;
        RepairServiceButton.IsEnabled = canInstall && !_actionRunning;
        RepairActionButton.IsEnabled = !_actionRunning;
        RefreshButton.IsEnabled = !_actionRunning;
    }

    // ----- Actions -----

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void StartServiceButton_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync(install: false);

    private async void RepairServiceButton_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync(install: true);

    private async void RepairActionButton_Click(object sender, RoutedEventArgs e) =>
        await RunServiceActionAsync(install: true);

    /// <summary>
    /// Starts or installs the service. Both paths raise one UAC prompt; a declined
    /// prompt surfaces as an InfoBar instead of an unhandled exception.
    /// </summary>
    private async Task RunServiceActionAsync(bool install)
    {
        if (_actionRunning) return;

        _actionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _actionCts = cts;
        var token = cts.Token;

        StatusBar.IsOpen = false;
        SetActionBusy(true);
        try
        {
            if (install)
            {
                await DaemonServiceManager.InstallBundledServiceAsync(AppendLog, token);
            }
            else
            {
                await DaemonServiceManager.EnsureRunningAsync(AppendLog, token);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_unloaded)
            {
                ShowError(Loc.Get("ServiceSettings.TheServiceActionWasCancelledSoNothingWasChanged"));
            }
        }
        catch (Exception error)
        {
            if (!_unloaded) ShowError(error.Message);
        }
        finally
        {
            SetActionBusy(false);
            if (ReferenceEquals(_actionCts, cts)) _actionCts = null;
            if (!_unloaded) await RefreshAsync();
        }
    }

    private void SetActionBusy(bool busy)
    {
        _actionRunning = busy;
        ActionProgress.IsActive = busy;
        ActionProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private void ShowError(string message)
    {
        StatusBar.Title = Loc.Get("ServiceSettings.ServiceActionFailed");
        StatusBar.Message = message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    /// <summary>Daemon progress goes to the app log, timestamped like the rest.</summary>
    private static void AppendLog(string line) =>
        App.State.AppendLog($"[{DateTime.Now:HH:mm:ss}] {line}");

    // ----- Folder shortcuts -----

    private void ExecutableCard_Click(object sender, RoutedEventArgs e) => OpenFolder(FolderOf(_executablePath));

    private void ServiceWorkingDirectoryCard_Click(object sender, RoutedEventArgs e) => OpenFolder(_workingDirectory);

    private static string? FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var folder = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
    }

    private static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            var reason = Loc.Get("ServiceSettings.UnableToOpenTheFolder");
            App.State.AppendLog($"[{DateTime.Now:HH:mm:ss}] {reason}: {error.Message}");
        }
    }

    private static Brush ResBrush(string key) => (Brush)Application.Current.Resources[key];
}
