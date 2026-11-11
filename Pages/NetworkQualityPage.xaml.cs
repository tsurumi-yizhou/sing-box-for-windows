using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFA/SFM network quality tool (download/upload capacity, RPM, idle latency).
/// Moved verbatim off the tools hub; the test stream is cancelled on Unloaded.
/// </summary>
public sealed partial class NetworkQualityPage : Page
{
    private CancellationTokenSource? _nqCts;
    private bool _errorShowing;

    public NetworkQualityPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;
        UpdateAvailability();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        _nqCts?.Cancel();
        _nqCts = null;
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(UpdateAvailability);

    private void UpdateAvailability()
    {
        var core = App.State.Core;
        var libboxAvailable = core is BoxddCoreController;
        var running = core.State.IsRunning;

        NqStartButton.IsEnabled = libboxAvailable && running && _nqCts is null;

        if (libboxAvailable && running)
        {
            if (!_errorShowing) StatusBar.IsOpen = false;
            return;
        }

        _errorShowing = false;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = Loc.Get("NetworkQuality.NetworkQuality");
        StatusBar.Message = !libboxAvailable
            ? Loc.Get("NetworkQuality.TheseToolsRequireTheLibboxCoreTheAppIsCurrentlyUsing")
            : Loc.Get("NetworkQuality.StartTheServiceToRunTheNetworkQualityTest");
        StatusBar.IsOpen = true;
    }

    private async void NqStartButton_Click(object sender, RoutedEventArgs e)
    {
        _nqCts?.Cancel();
        _nqCts = new CancellationTokenSource();
        var token = _nqCts.Token;

        _errorShowing = false;
        NqStartButton.IsEnabled = false;
        NqProgress.Visibility = Visibility.Visible;
        NqProgress.IsActive = true;
        NqResults.Visibility = Visibility.Collapsed;
        NqPhaseText.Text = string.Empty;

        try
        {
            var options = new CoreNetworkQualityTestOptions(
                string.IsNullOrWhiteSpace(NqConfigUrlBox.Text) ? null : NqConfigUrlBox.Text.Trim(),
                null, Serial: false, MaxRuntimeSeconds: 0, NqHttp3Box.IsChecked == true);

            CoreNetworkQualityProgress? last = null;
            await foreach (var progress in App.State.Core.StartNetworkQualityTestAsync(options, token))
            {
                last = progress;
                NqPhaseText.Text = NqPhaseName(progress.Phase, progress.ElapsedMs);
                UpdateNqResults(progress);
            }
            if (last is { Error: { } error })
            {
                ShowError(error);
            }
            else
            {
                NqPhaseText.Text = Loc.Get("NetworkQuality.Done");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            NqProgress.IsActive = false;
            NqProgress.Visibility = Visibility.Collapsed;
            _nqCts = null;
            NqStartButton.IsEnabled =
                App.State.Core is BoxddCoreController && App.State.Core.State.IsRunning;
        }
    }

    private void UpdateNqResults(CoreNetworkQualityProgress p)
    {
        if (p.DownloadCapacity <= 0 && p.UploadCapacity <= 0 && p.IdleLatencyMs <= 0) return;
        NqResults.Visibility = Visibility.Visible;
        NqDownloadText.Text = p.DownloadCapacity > 0 ? $"{p.DownloadCapacity * 8.0 / 1_000_000:F1} Mbps" : "…";
        NqUploadText.Text = p.UploadCapacity > 0 ? $"{p.UploadCapacity * 8.0 / 1_000_000:F1} Mbps" : "…";
        var rpm = new List<string>();
        if (p.DownloadRpm > 0) rpm.Add($"↓ {p.DownloadRpm}");
        if (p.UploadRpm > 0) rpm.Add($"↑ {p.UploadRpm}");
        NqRpmText.Text = rpm.Count > 0 ? string.Join("  ", rpm) : "…";
        NqIdleLatencyText.Text = p.IdleLatencyMs > 0 ? $"{p.IdleLatencyMs} ms" : "…";
    }

    private static string NqPhaseName(int phase, long elapsedMs) => phase switch
    {
        0 => Loc.Get("NetworkQuality.MeasuringIdleLatency"),
        1 => string.Format(Loc.Get("NetworkQuality.DownloadTest0F0S"), elapsedMs / 1000.0),
        2 => string.Format(Loc.Get("NetworkQuality.UploadTest0F0S"), elapsedMs / 1000.0),
        _ => Loc.Get("NetworkQuality.Done"),
    };

    private void ShowError(string message)
    {
        _errorShowing = true;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = Loc.Get("NetworkQuality.ToolError");
        StatusBar.Message = message;
    }
}
