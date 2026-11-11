using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM PowerReportListView / SFA PowerReportListScreen parity: the power reports
/// the daemon saved for previous service runs, plus the switch that enables
/// saving one per run (SFM keeps that switch above the list).
/// </summary>
public sealed partial class PowerReportsPage : Page
{
    private const CoreReportKind Kind = CoreReportKind.Power;

    private CancellationTokenSource? _cts;
    private bool _dialogOpen;
    private bool _initializing;
    private bool _applyingSettings;

    public PowerReportsPage()
    {
        InitializeComponent();
        ReportList.ReportInvoked += async (_, row) => await OpenAsync(row.Report);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ----- Settings -----

    private void LoadSettings()
    {
        // The assignment raises Toggled; suppress it so merely opening the page
        // never persists (and therefore never restarts the service).
        _initializing = true;
        try
        {
            PowerReportToggle.IsOn = App.State.Settings.PowerReportEnabled;
        }
        finally
        {
            _initializing = false;
        }
    }

    private async void PowerReportToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var enabled = PowerReportToggle.IsOn;
        if (_applyingSettings) return;
        _applyingSettings = true;
        PowerReportToggle.IsEnabled = false;
        try
        {
            await App.State.ApplyDebugReportSettingsAsync(
                settings => settings.PowerReportEnabled = enabled);
        }
        catch (Exception error)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                Loc.Get("PowerReports.UnableToUpdateThePowerReportSetting"),
                error.Message);
        }
        finally
        {
            _applyingSettings = false;
            PowerReportToggle.IsEnabled = true;
        }
    }

    // ----- Reports -----

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // Cancel any in-flight load so a slow older response cannot overwrite a
        // newer list (delete-all refreshes while a load may still be running).
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetBusy(true);
        try
        {
            if (!await DaemonReportService.IsAvailableAsync(token))
            {
                if (token.IsCancellationRequested) return;
                Render([]);
                ShowStatus(
                    InfoBarSeverity.Warning,
                    Loc.Get("PowerReports.ReportsUnavailable"),
                    Loc.Get("PowerReports.TheSingBoxDaemonServiceIsNotRunningSoReportsAreUnava"));
                return;
            }

            var reports = await DaemonReportService.ListAsync(Kind, token);
            if (token.IsCancellationRequested) return;

            StatusBar.IsOpen = false;
            Render(reports);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            Render([]);
            ShowStatus(InfoBarSeverity.Error, Loc.Get("PowerReports.UnableToListReports"), error.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested) SetBusy(false);
        }
    }

    private void Render(IReadOnlyList<CoreReportEntry> reports)
    {
        var hasReports = reports.Count > 0;
        ReportScroll.Visibility = hasReports ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasReports ? Visibility.Collapsed : Visibility.Visible;
        DeleteAllButton.Visibility = hasReports ? Visibility.Visible : Visibility.Collapsed;

        // SFM/SFA list reports newest first; keep that order even if the daemon
        // ever returns an unsorted store. The row markup is Controls/ReportList.xaml.
        ReportList.SetRows(reports
            .OrderByDescending(report => report.Timestamp)
            .Select(report => new Models.ReportListRow(
                report, FormatTimestamp(report.Timestamp), Loc.Get("PowerReports.Local"))));
    }

    private async Task OpenAsync(CoreReportEntry report)
    {
        try
        {
            await DaemonReportService.MarkReadAsync(Kind, report.Name);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }

        Frame.Navigate(
            typeof(ReportDetailPage),
            new CoreReportReference(Kind, report.Name, report.Timestamp));
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;

        var dialog = new Dialogs.ConfirmDialog(
            "PowerReports.DeleteAllReports",
            "PowerReports.AllSavedPowerReportsWillBePermanentlyDeleted",
            "PowerReports.DeleteAll")
        { XamlRoot = XamlRoot };

        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await DaemonReportService.DeleteAllAsync(Kind);
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("PowerReports.UnableToDeleteReports"), error.Message);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ----- Helpers -----

    private void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) return;

        ReportScroll.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        DeleteAllButton.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp == DateTimeOffset.MinValue ? Loc.Get("PowerReports.Unknown") : timestamp.ToString("g");

    private static Style ResStyle(string key) => (Style)Application.Current.Resources[key];
}
