using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFA/SFM STUN tool: external address plus NAT mapping/filtering behavior.
/// Moved verbatim off the tools hub; the probe stream is cancelled on Unloaded.
/// </summary>
public sealed partial class StunTestPage : Page
{
    private CancellationTokenSource? _stunCts;
    private bool _errorShowing;

    public StunTestPage()
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
        _stunCts?.Cancel();
        _stunCts = null;
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(UpdateAvailability);

    private void UpdateAvailability()
    {
        var core = App.State.Core;
        var libboxAvailable = core is BoxddCoreController;
        var running = core.State.IsRunning;

        StunStartButton.IsEnabled = libboxAvailable && running && _stunCts is null;

        if (libboxAvailable && running)
        {
            if (!_errorShowing) StatusBar.IsOpen = false;
            return;
        }

        _errorShowing = false;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = Loc.Get("StunTest.STUNTest");
        StatusBar.Message = !libboxAvailable
            ? Loc.Get("StunTest.TheseToolsRequireTheLibboxCoreTheAppIsCurrentlyUsing")
            : Loc.Get("StunTest.StartTheServiceToRunTheSTUNTest");
        StatusBar.IsOpen = true;
    }

    private async void StunStartButton_Click(object sender, RoutedEventArgs e)
    {
        _stunCts?.Cancel();
        _stunCts = new CancellationTokenSource();
        var token = _stunCts.Token;

        _errorShowing = false;
        StunStartButton.IsEnabled = false;
        StunProgress.Visibility = Visibility.Visible;
        StunProgress.IsActive = true;
        StunResults.Visibility = Visibility.Collapsed;
        StunPhaseText.Text = string.Empty;

        try
        {
            var server = string.IsNullOrWhiteSpace(StunServerBox.Text) ? string.Empty : StunServerBox.Text.Trim();
            CoreStunProgress? last = null;
            await foreach (var progress in App.State.Core.StartStunTestAsync(server, null, token))
            {
                last = progress;
                StunPhaseText.Text = StunPhaseName(progress.Phase);
                UpdateStunResults(progress);
            }
            if (last is { Error: { } error })
            {
                ShowError(error);
            }
            else
            {
                StunPhaseText.Text = Loc.Get("StunTest.Done");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            StunProgress.IsActive = false;
            StunProgress.Visibility = Visibility.Collapsed;
            _stunCts = null;
            StunStartButton.IsEnabled =
                App.State.Core is BoxddCoreController && App.State.Core.State.IsRunning;
        }
    }

    private void UpdateStunResults(CoreStunProgress p)
    {
        if (string.IsNullOrEmpty(p.ExternalAddr) && p.LatencyMs <= 0 && p.NatMapping <= 0 && p.NatFiltering <= 0) return;
        StunResults.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(p.ExternalAddr)) StunExternalText.Text = p.ExternalAddr;
        if (p.LatencyMs > 0) StunLatencyText.Text = $"{p.LatencyMs} ms";
        if (p.NatMapping > 0) StunMappingText.Text = NatBehaviorName(p.NatMapping);
        if (p.NatFiltering > 0) StunFilteringText.Text = NatBehaviorName(p.NatFiltering);
    }

    private static string StunPhaseName(int phase) => phase switch
    {
        0 => Loc.Get("StunTest.Binding"),
        1 => Loc.Get("StunTest.ProbingNATMapping"),
        2 => Loc.Get("StunTest.ProbingNATFiltering"),
        _ => Loc.Get("StunTest.Done"),
    };

    private static string NatBehaviorName(int value) => value switch
    {
        1 => Loc.Get("StunTest.EndpointIndependent"),
        2 => Loc.Get("StunTest.AddressDependent"),
        3 => Loc.Get("StunTest.AddressAndPortDependent"),
        _ => Loc.Get("StunTest.Unknown"),
    };

    private void ShowError(string message)
    {
        _errorShowing = true;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = Loc.Get("StunTest.ToolError");
        StatusBar.Message = message;
    }
}
