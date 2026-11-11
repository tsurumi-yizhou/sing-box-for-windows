using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM CoreView / SFA CoreSettingsScreen parity: core version, the daemon's data
/// size, its working directory and the app data folder.
///
/// The version comes from the daemon binary and needs neither elevation nor a
/// running service. The data size and the daemon-reported working directory are
/// computed by the daemon, so they only exist while the service runs; the
/// directory recorded on the installed service is shown as a fallback.
/// </summary>
public sealed partial class CoreSettingsPage : Page
{
    private CancellationTokenSource? _loadCts;
    private string? _workingDirectory;

    public CoreSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Starting or stopping the service from the title bar changes the data
        // size and the current working directory, so refresh with it.
        App.State.Core.StateChanged += Core_StateChanged;
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        // Cancel only: an in-flight daemon call may still register on the token,
        // so the source is left to be collected with the page.
        _loadCts?.Cancel();
        _loadCts = null;
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        var unavailable = Loc.Get("CoreSettings.Unavailable");

        // The app data folder is local and always known.
        DataFolderCard.Description = App.State.DataDirectory;

        // The registered service directory is readable from the registry even
        // while the daemon is stopped, so this card is never blank.
        _workingDirectory = DaemonServiceManager.QueryServiceWorkingDirectoryPath();
        RenderWorkingDirectory();

        DataSizeText.Text = string.Empty;
        DataSizeProgress.IsActive = true;
        DataSizeProgress.Visibility = Visibility.Visible;
        DataSizeCaption.Visibility = Visibility.Collapsed;

        // Core version: no elevation, no service required.
        try
        {
            var version = await DaemonServiceManager.QueryVersionAsync(token);
            if (token.IsCancellationRequested) return;
            var bundled = await DaemonServiceManager.QueryBundledVersionAsync(token);
            if (token.IsCancellationRequested) return;
            CoreVersionCard.Description = version is null
                ? unavailable
                : !string.IsNullOrEmpty(bundled) && !string.Equals(version, bundled, StringComparison.OrdinalIgnoreCase)
                    ? string.Format(Loc.Get("CoreSettings.AppShips"), version, bundled)
                    : version;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) CoreVersionCard.Description = unavailable;
        }

        // Data size and the current working directory come over the
        // authenticated relay, which needs the service running.
        try
        {
            var info = await DaemonInfoService.GetWorkingDirectoryAsync(token);
            if (token.IsCancellationRequested) return;
            DataSizeText.Text = FormatSize(info.SizeBytes);
            if (!string.IsNullOrWhiteSpace(info.Path))
            {
                _workingDirectory = info.Path;
                RenderWorkingDirectory();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (token.IsCancellationRequested) return;
            DataSizeText.Text = unavailable;
            DataSizeCaption.Text = Loc.Get("CoreSettings.TheDaemonReportsThisSizeWhileItRunsStartTheServiceTo");
            DataSizeCaption.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                DataSizeProgress.IsActive = false;
                DataSizeProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>B/KB/MB/GB with one decimal, the SFA/SFM data-size format.</summary>
    private static string FormatSize(long bytes)
    {
        const double kilobyte = 1024d;
        const double megabyte = kilobyte * 1024d;
        const double gigabyte = megabyte * 1024d;
        if (bytes < kilobyte) return $"{bytes} B";
        if (bytes < megabyte) return $"{bytes / kilobyte:0.0} KB";
        if (bytes < gigabyte) return $"{bytes / megabyte:0.0} MB";
        return $"{bytes / gigabyte:0.0} GB";
    }

    private void RenderWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(_workingDirectory))
        {
            WorkingDirectoryCard.Description = Loc.Get("CoreSettings.Unavailable");
            WorkingDirectoryCard.IsClickEnabled = false;
            return;
        }

        var exists = Directory.Exists(_workingDirectory);
        WorkingDirectoryCard.Description = exists
            ? _workingDirectory
            : $"{_workingDirectory} — {Loc.Get("CoreSettings.NotCreatedYet")}";
        WorkingDirectoryCard.IsClickEnabled = exists;
    }

    // ----- Folder shortcuts -----

    private void WorkingDirectoryCard_Click(object sender, RoutedEventArgs e) => OpenFolder(_workingDirectory);

    private void DataFolderCard_Click(object sender, RoutedEventArgs e) => OpenFolder(App.State.DataDirectory);

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            var reason = Loc.Get("CoreSettings.UnableToOpenTheFolder");
            App.State.AppendLog($"[{DateTime.Now:HH:mm:ss}] {reason}: {error.Message}");
        }
    }
}
