using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM CrashReportListView / SFA CrashReportListScreen parity: the crash reports
/// the daemon saved for this Windows user, newest first. Opening a report marks
/// it read and pushes the detail page onto the navigation stack.
/// </summary>
public sealed partial class CrashReportsPage : Page
{
    private const CoreReportKind Kind = CoreReportKind.Crash;

    private CancellationTokenSource? _cts;
    private bool _dialogOpen;

    public CrashReportsPage()
    {
        InitializeComponent();
        ReportList.ReportInvoked += async (_, row) => await OpenAsync(row.Report);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Every visit re-lists the store: reports are written by the daemon and
        // can appear while the user is looking at another page.
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
    }

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
            // The daemon service owns the report stores. Without it there are no
            // reports to show, but the page must still explain that instead of
            // failing with a raw exception.
            if (!await DaemonReportService.IsAvailableAsync(token))
            {
                if (token.IsCancellationRequested) return;
                Render([]);
                ShowStatus(
                    InfoBarSeverity.Warning,
                    Loc.Get("CrashReports.ReportsUnavailable"),
                    Loc.Get("CrashReports.TheSingBoxDaemonServiceIsNotRunningSoReportsAreUnava"));
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("CrashReports.UnableToListReports"), error.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested) SetBusy(false);
        }
    }

    // ----- Rendering -----

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
                report, FormatTimestamp(report.Timestamp), Loc.Get("CrashReports.Local"))));
    }

    private async Task OpenAsync(CoreReportEntry report)
    {
        // SFA/SFM mark a report read when it is opened. A daemon that stopped in
        // the meantime must not block navigation, so this is best effort.
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

    // ----- Delete all -----

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;

        var dialog = new Dialogs.ConfirmDialog(
            "CrashReports.DeleteAllReports",
            "CrashReports.AllSavedCrashReportsWillBePermanentlyDeleted",
            "CrashReports.DeleteAll")
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("CrashReports.UnableToDeleteReports"), error.Message);
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

        // Never leave a stale list visible while the store is being re-read.
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
        timestamp == DateTimeOffset.MinValue ? Loc.Get("CrashReports.Unknown") : timestamp.ToString("g");

    private static Style ResStyle(string key) => (Style)Application.Current.Resources[key];
}
