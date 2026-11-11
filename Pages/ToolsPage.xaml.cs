using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

/// <summary>
/// SFA/SFM tools hub: endpoint rows (Tailscale), network tools and the debug
/// report tools. Every tool lives on its own page; this page only decides which
/// sections exist and navigates to them. The sections are native
/// <see cref="SettingsExpander"/> groups of <see cref="SettingsCard"/> items, so
/// the visuals, keyboard handling and accessibility come from the Windows
/// settings controls instead of a hand-drawn list.
/// </summary>
public sealed partial class ToolsPage : Page
{
    private CancellationTokenSource? _endpointCts;
    private CancellationTokenSource? _reportsCts;
    private bool _endpointStreamStarted;
    private readonly Dictionary<CoreReportKind, InfoBadge> _reportBadges = new();

    public ToolsPage()
    {
        InitializeComponent();
        _reportBadges[CoreReportKind.Crash] = CrashReportBadge;
        _reportBadges[CoreReportKind.OutOfMemory] = OomReportBadge;
        _reportBadges[CoreReportKind.Power] = PowerReportBadge;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;
        UpdateAvailability();
        _ = RefreshReportBadgesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        StopEndpointSubscription();
        _reportsCts?.Cancel();
        _reportsCts = null;
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(UpdateAvailability);

    private void UpdateAvailability()
    {
        var core = App.State.Core;
        var libboxAvailable = core is BoxddCoreController;
        var running = core.State.IsRunning;

        StatusBar.IsOpen = !(libboxAvailable && running);
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = !libboxAvailable
            ? Loc.Get("Tools.TheseToolsRequireTheLibboxCoreTheAppIsCurrentlyUsing")
            : Loc.Get("Tools.StartTheServiceToUseTheNetworkQualitySTUNAndTailscal");

        // The endpoint section only exists while a libbox service is running and
        // the Tailscale status stream reports endpoints.
        if (libboxAvailable && running) EnsureEndpointSubscription();
        else StopEndpointSubscription();
    }

    // ----- Navigation -----

    private void NetworkQualityCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(NetworkQualityPage));

    private void StunTestCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(StunTestPage));

    private void CrashReportCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(CrashReportsPage));

    private void OomReportCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(OomReportsPage));

    private void PowerReportCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(PowerReportsPage));

    private void Navigate(Type pageType, object? parameter = null)
    {
        if (Frame is null) return;
        if (parameter is null) Frame.Navigate(pageType);
        else Frame.Navigate(pageType, parameter);
    }

    // ----- Endpoint section (Tailscale status stream) -----

    private void EnsureEndpointSubscription()
    {
        if (_endpointStreamStarted) return;
        _endpointStreamStarted = true;
        _endpointCts = new CancellationTokenSource();
        _ = RunEndpointStatusLoopAsync(_endpointCts.Token);
    }

    private void StopEndpointSubscription()
    {
        _endpointStreamStarted = false;
        _endpointCts?.Cancel();
        _endpointCts = null;
        EndpointsExpander.Items = new List<object>();
        EndpointsExpander.Visibility = Visibility.Collapsed;
    }

    private async Task RunEndpointStatusLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var endpoints in App.State.Core.SubscribeTailscaleStatusAsync(token))
            {
                if (token.IsCancellationRequested) break;
                var snapshot = endpoints;
                if (!DispatcherQueue.TryEnqueue(() => RenderEndpoints(snapshot))) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // The hub only uses the stream for the endpoint rows; surface the
            // failure once here and retry on the next stopped -> running change.
            var message = error.Message;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                RenderEndpoints([]);
                StatusBar.IsOpen = true;
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Title = Loc.Get("Tools.ToolError");
                StatusBar.Message = message;
            });
        }
    }

    private void RenderEndpoints(IReadOnlyList<CoreTailscaleEndpoint> endpoints)
    {
        // Snapshots queued before the page stopped subscribing must not
        // repopulate a torn-down section.
        if (!_endpointStreamStarted) return;

        var multiple = endpoints.Count > 1;
        var items = new List<object>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var tag = endpoint.EndpointTag;
            var title = multiple && !string.IsNullOrEmpty(tag)
                ? $"{Loc.Get("Tools.Tailscale")}: {tag}"
                : Loc.Get("Tools.Tailscale");
            items.Add(CreateEndpointCard(title, tag));
        }

        // Replacing the list updates the expander's ItemsRepeater, which is not
        // watching the collection itself.
        EndpointsExpander.Items = items;
        EndpointsExpander.Visibility = endpoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SettingsCard CreateEndpointCard(string title, string? endpointTag)
    {
        var card = new SettingsCard
        {
            Header = title,
            IsClickEnabled = true,
            // The expander's default item style indents rows to the header text;
            // keep the row flush with the expander header instead.
            Padding = new Thickness(16, 8, 16, 8),
        };
        card.Click += (_, _) => Navigate(typeof(TailscalePage), endpointTag);
        return card;
    }

    // ----- Debug badges -----

    private async Task RefreshReportBadgesAsync()
    {
        _reportsCts?.Cancel();
        _reportsCts = new CancellationTokenSource();
        var token = _reportsCts.Token;
        try
        {
            var counts = await DaemonReportService.GetUnreadCountsAsync(token);
            if (token.IsCancellationRequested) return;
            SetBadge(CoreReportKind.Crash, counts.Crash);
            SetBadge(CoreReportKind.OutOfMemory, counts.OutOfMemory);
            SetBadge(CoreReportKind.Power, counts.Power);
            DebugCaption.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // The report stores are daemon-owned; without the daemon the tools
            // stay reachable but carry no counts. A caption is enough here.
            if (token.IsCancellationRequested) return;
            SetBadge(CoreReportKind.Crash, 0);
            SetBadge(CoreReportKind.OutOfMemory, 0);
            SetBadge(CoreReportKind.Power, 0);
            DebugCaption.Visibility = Visibility.Visible;
        }
    }

    private void SetBadge(CoreReportKind kind, int count)
    {
        if (!_reportBadges.TryGetValue(kind, out var badge)) return;
        if (count <= 0)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }
        badge.Value = count;
        badge.Visibility = Visibility.Visible;
    }
}
