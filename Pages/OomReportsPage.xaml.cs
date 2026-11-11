using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM OOMReportListView / SFA OOMReportListScreen parity: the daemon's OOM
/// reports plus the soft memory limit settings that SFM keeps next to them.
/// Changing a setting persists it and restarts a running service, so every
/// handler goes through App.State.ApplyDebugReportSettingsAsync.
/// </summary>
public sealed partial class OomReportsPage : Page
{
    private const CoreReportKind Kind = CoreReportKind.OutOfMemory;

    private readonly IReadOnlyList<int> _memoryLimitOptions = CoreStartOptions.MemoryLimitMegabytes;

    private CancellationTokenSource? _cts;
    private bool _dialogOpen;
    private bool _fetching;
    private bool _initializing;
    private bool _applyingSettings;

    public OomReportsPage()
    {
        InitializeComponent();
        ReportList.ReportInvoked += async (_, row) => await OpenAsync(row.Report);
        BuildMemoryLimitOptions();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;

        LoadSettings();
        UpdateAvailability();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        _cts?.Cancel();
        _cts = null;
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(UpdateAvailability);

    // ----- Settings -----

    private void BuildMemoryLimitOptions()
    {
        foreach (var megabytes in _memoryLimitOptions)
        {
            OomMemoryLimitBox.Items.Add(new ComboBoxItem
            {
                Content = FormatMemoryLimit(megabytes),
                Tag = megabytes,
            });
        }
    }

    private void LoadSettings()
    {
        // Assigning IsOn / SelectedIndex raises the change events: suppress them
        // so loading the page never restarts the service by itself.
        _initializing = true;
        try
        {
            var settings = App.State.Settings;
            OomKillerToggle.IsOn = settings.OomKillerEnabled;
            OomKillConnectionsToggle.IsOn = settings.OomKillerKillConnections;

            var index = _memoryLimitOptions.ToList().IndexOf(settings.OomMemoryLimitMegabytes);
            var defaultIndex = _memoryLimitOptions.ToList().IndexOf(50);
            OomMemoryLimitBox.SelectedIndex = index >= 0 ? index : defaultIndex;

            UpdateSettingsVisibility();
        }
        finally
        {
            _initializing = false;
        }
    }

    private void UpdateSettingsVisibility()
    {
        OomOptionsPanel.Visibility = OomKillerToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OomKillerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var enabled = OomKillerToggle.IsOn;
        UpdateSettingsVisibility();
        await ApplySettingsAsync(
            settings => settings.OomKillerEnabled = enabled,
            Loc.Get("OomReports.UnableToUpdateTheMemoryLimitSetting"));
    }

    private async void OomMemoryLimitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (OomMemoryLimitBox.SelectedItem is not ComboBoxItem { Tag: int megabytes }) return;

        await ApplySettingsAsync(
            settings => settings.OomMemoryLimitMegabytes = megabytes,
            Loc.Get("OomReports.UnableToUpdateTheMemoryLimit"));
    }

    private async void OomKillConnectionsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        var killConnections = OomKillConnectionsToggle.IsOn;
        await ApplySettingsAsync(
            settings => settings.OomKillerKillConnections = killConnections,
            Loc.Get("OomReports.UnableToUpdateTheKillConnectionsSetting"));
    }

    private async Task ApplySettingsAsync(Action<SFW.Models.AppSettings> mutate, string errorTitle)
    {
        // Persisting a debug setting restarts a running service; overlapping
        // restarts would fight each other, so changes are serialized.
        if (_applyingSettings) return;
        _applyingSettings = true;
        SetSettingsEnabled(false);
        try
        {
            await App.State.ApplyDebugReportSettingsAsync(mutate);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, errorTitle, error.Message);
        }
        finally
        {
            _applyingSettings = false;
            SetSettingsEnabled(true);
        }
    }

    private void SetSettingsEnabled(bool enabled)
    {
        OomKillerToggle.IsEnabled = enabled;
        OomMemoryLimitBox.IsEnabled = enabled;
        OomKillConnectionsToggle.IsEnabled = enabled;
        UpdateAvailability();
    }

    // ----- Fetch memory report (SFM "Fetch Memory Report") -----

    private void UpdateAvailability()
    {
        var running = App.State.Core.State.IsRunning;
        FetchReportButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        FetchReportButton.IsEnabled = running && !_fetching && !_applyingSettings;
    }

    private async void FetchReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fetching) return;
        if (!App.State.Core.State.IsRunning)
        {
            ShowStatus(
                InfoBarSeverity.Informational,
                Loc.Get("OomReports.ServiceNotStarted"),
                Loc.Get("OomReports.StartTheServiceToCollectAMemoryReport"));
            return;
        }

        _fetching = true;
        UpdateAvailability();
        try
        {
            await DaemonReportService.TriggerOomReportAsync();
            // The daemon writes the report asynchronously; SFM/SFA wait one second
            // before refreshing the list.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("OomReports.UnableToFetchTheMemoryReport"), error.Message);
        }
        finally
        {
            _fetching = false;
            UpdateAvailability();
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
                    Loc.Get("OomReports.ReportsUnavailable"),
                    Loc.Get("OomReports.TheSingBoxDaemonServiceIsNotRunningSoReportsAreUnava"));
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("OomReports.UnableToListReports"), error.Message);
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
                report, FormatTimestamp(report.Timestamp), Loc.Get("OomReports.Local"))));
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
            "OomReports.DeleteAllReports",
            "OomReports.AllSavedMemoryReportsWillBePermanentlyDeleted",
            "OomReports.DeleteAll")
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("OomReports.UnableToDeleteReports"), error.Message);
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
        timestamp == DateTimeOffset.MinValue ? Loc.Get("OomReports.Unknown") : timestamp.ToString("g");

    /// <summary>SFM shows the memory limit in the units the daemon reports, so 1024 MB is "1 GB".</summary>
    private static string FormatMemoryLimit(int megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024.0:0.##} GB" : $"{megabytes} MB";

    private static Style ResStyle(string key) => (Style)Application.Current.Resources[key];
}
