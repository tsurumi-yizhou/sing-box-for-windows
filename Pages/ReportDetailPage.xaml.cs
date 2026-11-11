using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SFW.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFM CrashReportDetailView / OOMReportDetailView / PowerReportDetailView (and
/// the SFA detail screens) parity: the files inside one daemon report, inline
/// previews for text files, and the export/delete actions.
/// </summary>
public sealed partial class ReportDetailPage : Page
{
    /// <summary>SFM caps inline previews at 2 MB and shows the tail of the file.</summary>
    private const int MaxPreviewCharacters = 2 * 1024 * 1024;

    private CoreReportReference? _reference;
    private IReadOnlyList<CoreReportFile> _files = [];
    private CancellationTokenSource? _cts;
    private bool _dialogOpen;

    public ReportDetailPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _reference = e.Parameter as CoreReportReference;
        if (_reference is not { } reference)
        {
            TitleText.Text = Loc.Get("ReportDetail.Report");
            SubtitleText.Text = Loc.Get("ReportDetail.NoReportWasSelected");
            SetBusy(false);
            Render([]);
            return;
        }

        TitleText.Text = FormatTimestamp(reference.Timestamp);
        SubtitleText.Text = string.Format(
            Loc.Get("ReportDetail.Local"),
            KindTitle(reference.Kind));

        _ = LoadAsync(reference);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ----- Loading -----

    private async Task LoadAsync(CoreReportReference reference)
    {
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
                    Loc.Get("ReportDetail.ReportUnavailable"),
                    Loc.Get("ReportDetail.TheSingBoxDaemonServiceIsNotRunningSoReportsAreUnava"));
                return;
            }

            var content = await DaemonReportService.ReadAsync(reference.Kind, reference.Name, token);
            if (token.IsCancellationRequested) return;

            Render(content.Files);
            await MarkReadAsync(reference, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            Render([]);
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToOpenTheReport"), error.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested) SetBusy(false);
        }
    }

    /// <summary>
    /// SFA/SFM mark a report read as soon as it is opened. Best effort: the list
    /// page already marked it and a stopped daemon must not break the detail view.
    /// </summary>
    private static async Task MarkReadAsync(CoreReportReference reference, CancellationToken token)
    {
        try
        {
            await DaemonReportService.MarkReadAsync(reference.Kind, reference.Name, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) return;

        FileScroll.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
    }

    // ----- File list -----

    private void Render(IReadOnlyList<CoreReportFile> files)
    {
        _files = files;

        var hasFiles = files.Count > 0;
        FileScroll.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = hasFiles;
        DeleteButton.IsEnabled = _reference is not null;

        FileRows.ItemsSource = files
            .Select(file => new Models.ReportFileRow(
                file,
                file.IsProfile ? Loc.Get("ReportDetail.ProfileFileExportToView") : string.Empty))
            .ToList();
    }

    /// <summary>
    /// The row markup and the chevron live in the page XAML; this handler only
    /// opens text files (binary/profile rows are not navigable).
    /// </summary>
    private async void FileRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Models.ReportFileRow { IsText: true } row) return;
        await ShowFileAsync(row.File);
    }

    private async Task ShowFileAsync(CoreReportFile file)
    {
        if (_dialogOpen) return;

        var text = file.Text ?? string.Empty;
        var truncated = text.Length > MaxPreviewCharacters;
        if (truncated)
        {
            // SFM keeps the tail of an oversized file (log files are the reason).
            text = text[^MaxPreviewCharacters..];
            var lineStart = text.IndexOf('\n');
            if (lineStart >= 0) text = text[(lineStart + 1)..];
            text = "…\n" + text;
        }

        // The preview chrome (read-only text box plus the truncation warning) lives
        // in Controls/TextPreview.xaml.
        var dialog = new Dialogs.DetailDialog(
            file.Name, new TextPreview(text, truncated), "ReportDetail.Close")
        { XamlRoot = XamlRoot };

        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToShowTheFile"), error.Message);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ----- Export -----

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reference is not { } reference || _dialogOpen) return;

        var hasLog = _files.Any(file => IsLogFile(file.Name));
        var hasConfig = _files.Any(file => file.IsProfile || IsConfigFile(file.Name));

        // The sheet's markup and wording live in the dialog; the privacy note only
        // shows when the selection can include private content.
        var dialog = new Dialogs.ExportReportDialog(hasLog, hasLog || hasConfig)
        { XamlRoot = XamlRoot };

        _dialogOpen = true;
        bool confirmed;
        try
        {
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToExportTheReport"), error.Message);
            return;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (!confirmed) return;

        await ExportAsync(reference, dialog.WithConfiguration, dialog.WithLog, dialog.Encrypt);
    }

    private async Task ExportAsync(
        CoreReportReference reference, bool withConfiguration, bool withLog, bool encrypt)
    {
        try
        {
            var archive = await DaemonReportService.ExportAsync(
                reference.Kind, reference.Name, withConfiguration, withLog, encrypt);

            if (archive.Data.Length == 0)
            {
                ShowStatus(
                    InfoBarSeverity.Error,
                    Loc.Get("ReportDetail.UnableToExportTheReport"),
                    Loc.Get("ReportDetail.TheDaemonReturnedAnEmptyArchive"));
                return;
            }

            var extension = archive.FileName.EndsWith(".age", StringComparison.OrdinalIgnoreCase)
                ? ".age"
                : ".zip";

            if (MainWindow.Instance is not { } window) return;

            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(window.AppWindow.Id)
            {
                SuggestedFileName = archive.FileName,
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(Loc.Get("ReportDetail.Archive"), new List<string> { extension });

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await File.WriteAllBytesAsync(file.Path, archive.Data);
            ShowStatus(InfoBarSeverity.Success, Loc.Get("ReportDetail.ReportExported"), file.Path);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToExportTheReport"), error.Message);
        }
    }

    // ----- Delete -----

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reference is not { } reference || _dialogOpen) return;

        var dialog = new Dialogs.ConfirmDialog(
            "ReportDetail.DeleteThisReport",
            "ReportDetail.TheReportAndAllOfItsFilesWillBePermanentlyDeleted",
            "ReportDetail.Delete")
        { XamlRoot = XamlRoot };

        _dialogOpen = true;
        bool confirmed;
        try
        {
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToDeleteTheReport"), error.Message);
            return;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (!confirmed) return;

        try
        {
            await DaemonReportService.DeleteAsync(reference.Kind, reference.Name);
            GoBackToList(reference);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("ReportDetail.UnableToDeleteTheReport"), error.Message);
        }
    }

    private void GoBackToList(CoreReportReference reference)
    {
        if (Frame is null) return;
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
            return;
        }

        Frame.Navigate(reference.Kind switch
        {
            CoreReportKind.Crash => typeof(CrashReportsPage),
            CoreReportKind.OutOfMemory => typeof(OomReportsPage),
            CoreReportKind.Power => typeof(PowerReportsPage),
            _ => typeof(ToolsPage),
        });
    }

    // ----- Helpers -----

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static bool IsLogFile(string name) =>
        name.Equals("go.log", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("native.log", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    private static bool IsConfigFile(string name) =>
        name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string KindTitle(CoreReportKind kind) => kind switch
    {
        CoreReportKind.Crash => Loc.Get("ReportDetail.CrashReport"),
        CoreReportKind.OutOfMemory => Loc.Get("ReportDetail.OOMReport"),
        CoreReportKind.Power => Loc.Get("ReportDetail.PowerReport"),
        _ => Loc.Get("ReportDetail.Report"),
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp == DateTimeOffset.MinValue ? Loc.Get("ReportDetail.Unknown") : timestamp.ToString("g");
}
