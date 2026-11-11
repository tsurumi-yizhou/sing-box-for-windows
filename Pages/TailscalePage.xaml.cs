using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SFW.Controls;
using SFW.Services;
using SFW.Services.Core;
using Windows.ApplicationModel.DataTransfer;

namespace SFW.Pages;

/// <summary>
/// The Tailscale tool, moved off the tools hub into its own page. Navigated to
/// with an endpoint tag (string) or without a parameter, in which case every
/// endpoint in the running profile is shown.
/// </summary>
public sealed partial class TailscalePage : Page
{
    // The endpoint stream and every per-peer ping stream are cancellable and
    // torn down on Unloaded / core stop; nothing survives leaving the page.
    private CancellationTokenSource? _tsCts;
    private bool _tsStarted;
    private bool _tsDialogOpen;
    private bool _errorShowing;
    private string? _endpointTag;
    private readonly Dictionary<string, TailscalePingState> _tsPings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TailscalePeerRow> _tsPingRows = new(StringComparer.Ordinal);

    public TailscalePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _endpointTag = e.Parameter as string;
        if (string.IsNullOrWhiteSpace(_endpointTag)) _endpointTag = null;
        UpdateTitle();
        UpdateSubtitle();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;
        UpdateTitle();
        UpdateSubtitle();
        UpdateAvailability();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged -= Core_StateChanged;
        StopTailscale();
    }

    private void Core_StateChanged(object? sender, RuntimeState state)
        => DispatcherQueue.TryEnqueue(UpdateAvailability);

    private void UpdateTitle()
    {
        PageTitle.Text = _endpointTag is null
            ? Loc.Get("Tailscale.Tailscale")
            : $"{Loc.Get("Tailscale.Tailscale")}: {_endpointTag}";
    }

    private void UpdateSubtitle()
    {
        PageSubtitle.Text = _endpointTag is null
            ? Loc.Get("Tailscale.EndpointStatusPeersExitNodeAndConnectionTests")
            : Loc.Get("Tailscale.StatusPeersExitNodeAndConnectionTestsForThisEndpoint");
    }

    private void UpdateAvailability()
    {
        var core = App.State.Core;
        var libboxAvailable = core is BoxddCoreController;
        var running = core.State.IsRunning;
        var available = libboxAvailable && running;

        if (available)
        {
            if (!_errorShowing) StatusBar.IsOpen = false;
            TailscaleCard.Visibility = Visibility.Visible;
            EnsureTailscaleSubscription();
            return;
        }

        // Tailscale only exists behind the libbox core; the status stream only
        // makes sense while a service is running.
        _errorShowing = false;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = Loc.Get("Tailscale.Tailscale");
        StatusBar.Message = !libboxAvailable
            ? Loc.Get("Tailscale.TheseToolsRequireTheLibboxCoreTheAppIsCurrentlyUsing")
            : Loc.Get("Tailscale.StartTheServiceToInspectTailscaleEndpoints");
        StatusBar.IsOpen = true;
        StopTailscale();
    }

    // ----- Tailscale -----
    // Mirrors SFM/SFA TailscaleEndpointView + TailscalePeerView + TailscalePingViewModel:
    // one block per endpoint (state, network, this device, exit node, auth URL),
    // then peers grouped by tailnet user with an inline, cancellable ping.

    private void EnsureTailscaleSubscription()
    {
        if (_tsStarted) return;
        _tsStarted = true;

        _tsCts = new CancellationTokenSource();
        TailscaleProgress.Visibility = Visibility.Visible;
        TailscaleProgress.IsActive = true;

        _ = RunTailscaleStatusLoopAsync(_tsCts.Token);
    }

    private void StopTailscale()
    {
        _tsStarted = false;
        _tsCts?.Cancel();
        _tsCts = null;

        foreach (var key in _tsPings.Keys.ToList()) StopTailscalePing(key);

        _tsPingRows.Clear();
        TailscaleEndpoints.Children.Clear();
        TailscaleEmptyText.Visibility = Visibility.Collapsed;
        TailscaleProgress.IsActive = false;
        TailscaleProgress.Visibility = Visibility.Collapsed;
        TailscaleCard.Visibility = Visibility.Collapsed;
    }

    private async Task RunTailscaleStatusLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var endpoints in App.State.Core.SubscribeTailscaleStatusAsync(token))
            {
                if (token.IsCancellationRequested) break;
                var snapshot = endpoints;
                if (!DispatcherQueue.TryEnqueue(() => RenderTailscale(snapshot))) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // Surface once and keep the page usable. The subscription is only
            // retried on the next stopped -> running transition (never in a loop).
            var message = error.Message;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                TailscaleProgress.IsActive = false;
                TailscaleProgress.Visibility = Visibility.Collapsed;
                ShowError(message);
            });
        }
    }

    private void RenderTailscale(IReadOnlyList<CoreTailscaleEndpoint> endpoints)
    {
        // Snapshots that were already queued when the page stopped subscribing
        // must not repopulate a torn-down card.
        if (!_tsStarted) return;

        TailscaleProgress.IsActive = false;
        TailscaleProgress.Visibility = Visibility.Collapsed;

        // The hub navigates with the endpoint it wants; no parameter means all.
        IReadOnlyList<CoreTailscaleEndpoint> visible = _endpointTag is null
            ? endpoints
            : endpoints.Where(endpoint => string.Equals(
                endpoint.EndpointTag, _endpointTag, StringComparison.Ordinal)).ToList();

        _tsPingRows.Clear();
        TailscaleEndpoints.Children.Clear();

        var livePingKeys = new HashSet<string>(StringComparer.Ordinal);
        var multiple = visible.Count > 1;
        foreach (var endpoint in visible) BuildTailscaleEndpoint(endpoint, multiple, livePingKeys);

        if (visible.Count == 0)
        {
            TailscaleEmptyText.Text = _endpointTag is null
                ? Loc.Get("Tailscale.NoTailscaleEndpointsInTheRunningProfile")
                : string.Format(Loc.Get("Tailscale.TheEndpointIsNotInTheRunningProfile"), _endpointTag);
        }
        TailscaleEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // A peer (or a whole endpoint) that disappeared from the snapshot must not
        // keep a ping stream alive.
        foreach (var key in _tsPings.Keys.Where(pingKey => !livePingKeys.Contains(pingKey)).ToList())
            StopTailscalePing(key);
    }

    /// <summary>
    /// Fills one endpoint card. The card markup lives in
    /// Controls/TailscaleEndpointCard.xaml; this method only decides which of the
    /// optional rows exist in the current snapshot, in the order SFM uses.
    /// </summary>
    private void BuildTailscaleEndpoint(
        CoreTailscaleEndpoint endpoint, bool multiple, HashSet<string> livePingKeys)
    {
        var card = new TailscaleEndpointCard();
        card.SetFramed(multiple);
        card.ApplyHeader(endpoint, multiple, StateBrush(endpoint.BackendState));

        if (!string.IsNullOrEmpty(endpoint.NetworkName)) card.ShowNetwork(endpoint.NetworkName);

        if (endpoint.IsRunning && endpoint.Self is { } self)
        {
            var firstIp = self.TailscaleIPs.FirstOrDefault();
            var copyTarget = !string.IsNullOrEmpty(firstIp)
                ? firstIp
                : !string.IsNullOrEmpty(self.DnsName) ? self.DnsName : self.HostName;

            card.CopyAddressRequested += (_, _) => CopyText(copyTarget);
            // A key-authenticated node has no interactive session to end, so the
            // card keeps the log-out button hidden for it.
            card.LogOutRequested += async (_, _) => await ConfirmTailscaleLogoutAsync(endpoint.EndpointTag);
            card.ShowThisDevice(self.DisplayName, firstIp, !endpoint.KeyAuth);
        }

        if (endpoint.IsRunning && endpoint.HasExitNodeCandidates)
        {
            var current = endpoint.ExitNode?.DisplayName;
            if (string.IsNullOrEmpty(current)) current = Loc.Get("Tailscale.Disabled");
            card.ChangeExitNodeRequested += async (_, _) => await ShowTailscaleExitNodeDialogAsync(endpoint);
            card.ShowExitNode(current);
        }

        if (!string.IsNullOrEmpty(endpoint.AuthUrl))
        {
            var authUrl = endpoint.AuthUrl;
            card.OpenAuthUrlRequested += (_, _) => OpenUrl(authUrl);
            card.CopyAuthUrlRequested += (_, _) => CopyText(authUrl);
            card.ShowAuthUrl(authUrl);
        }

        // Peers are grouped by tailnet user; a user without a display name still
        // gets its peers, just no caption row.
        foreach (var group in endpoint.UserGroups)
        {
            var groupName = string.IsNullOrEmpty(group.DisplayName) ? group.LoginName : group.DisplayName;
            var block = new TailscalePeerGroup();
            block.SetTitle(groupName);
            foreach (var peer in group.Peers)
                block.AddPeer(BuildTailscalePeer(endpoint, peer, livePingKeys));
            card.AddBlock(block);
        }

        TailscaleEndpoints.Children.Add(card);
    }

    /// <summary>
    /// One peer row: the row markup lives in Controls/TailscalePeerRow.xaml, the
    /// ping bookkeeping stays here because this page owns the ping streams.
    /// </summary>
    private UIElement BuildTailscalePeer(
        CoreTailscaleEndpoint endpoint, CoreTailscalePeer peer, HashSet<string> livePingKeys)
    {
        var firstIp = peer.TailscaleIPs.FirstOrDefault();

        var row = new TailscalePeerRow();
        row.Apply(
            peer,
            endpoint.EndpointTag,
            PeerBadges(peer).ToList(),
            firstIp,
            peer.Online ? StateBrush("Running") : StateBrush(string.Empty));
        row.DetailsRequested += (_, _) => _ = ShowTailscalePeerDetailsAsync(peer);

        // Details are always available; ping only when it can reach the
        // peer, so the ping button is visible in the row.
        if (!peer.Online || string.IsNullOrEmpty(firstIp)) return row;

        var pingKey = endpoint.EndpointTag + "\u001F" + firstIp;
        livePingKeys.Add(pingKey);
        _tsPingRows[pingKey] = row;
        row.PingToggled += (_, _) => ToggleTailscalePing(pingKey);

        // The status stream may refresh while a ping is running: restore the ping
        // UI into the freshly built row instead of cancelling it.
        if (_tsPings.TryGetValue(pingKey, out var ping))
        {
            row.SetPingRunning(true);
            row.ApplyPing(ping.LatencyText, ping.DetailText, ping.ErrorText);
        }

        return row;
    }

    private void ToggleTailscalePing(string key)
    {
        if (_tsPings.ContainsKey(key))
        {
            StopTailscalePing(key);
            return;
        }

        if (!_tsPingRows.TryGetValue(key, out var row)) return;

        var ping = new TailscalePingState();
        _tsPings[key] = ping;

        row.SetPingRunning(true);
        row.HideError();

        _ = RunTailscalePingAsync(row.EndpointTag, row.PeerIp, key, ping);
    }

    private async Task RunTailscalePingAsync(
        string endpointTag, string peerIp, string key, TailscalePingState ping)
    {
        var token = ping.Cts.Token;
        try
        {
            await foreach (var result in App.State.Core.StartTailscalePingAsync(endpointTag, peerIp, token))
            {
                if (token.IsCancellationRequested) break;
                var captured = result;
                DispatcherQueue.TryEnqueue(() => ApplyTailscalePingResult(key, ping, captured));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            var message = error.Message;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                ShowError(message);
            });
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() => FinishTailscalePing(key, ping));
        }
    }

    private void ApplyTailscalePingResult(string key, TailscalePingState ping, CoreTailscalePingResult result)
    {
        if (!_tsPings.TryGetValue(key, out var current) || !ReferenceEquals(current, ping)) return;

        if (result.Error is { Length: > 0 } error)
        {
            ping.ErrorText = error;
        }
        else
        {
            ping.LatencyText = string.Format("{0:F0} ms", result.LatencyMs);
            ping.DetailText = PingConnectionText(result);
            ping.ErrorText = string.Empty;
        }

        if (_tsPingRows.TryGetValue(key, out var row))
            row.ApplyPing(ping.LatencyText, ping.DetailText, ping.ErrorText);
    }

    private void StopTailscalePing(string key)
    {
        if (!_tsPings.Remove(key, out var ping)) return;
        try { ping.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
        ResetTailscalePingButton(key);
    }

    private void FinishTailscalePing(string key, TailscalePingState ping)
    {
        if (_tsPings.TryGetValue(key, out var current))
        {
            if (!ReferenceEquals(current, ping)) return; // a newer ping owns this row
            if (!ping.Cts.IsCancellationRequested) _tsPings.Remove(key); // stream ended on its own
        }

        ResetTailscalePingButton(key);
    }

    private void ResetTailscalePingButton(string key)
    {
        if (!_tsPingRows.TryGetValue(key, out var row)) return;
        row.SetPingRunning(false);
    }

    private static string PingConnectionText(CoreTailscalePingResult result)
    {
        if (result.IsDirect) return Loc.Get("Tailscale.Direct");
        if (!string.IsNullOrEmpty(result.PeerRelay))
            return Loc.Get("Tailscale.PeerRelay") + " · " + result.PeerRelay;
        var derp = Loc.Get("Tailscale.DERP");
        if (!string.IsNullOrEmpty(result.DerpRegionCode)) derp += " · " + result.DerpRegionCode;
        return derp;
    }

    private async Task ConfirmTailscaleLogoutAsync(string endpointTag)
    {
        if (_tsDialogOpen) return;

        var dialog = new Dialogs.ConfirmDialog(
            "Tailscale.LogOutOfTailscale",
            "Tailscale.LogOutTheEndpointThisUnregistersTheNodeFromYourTailn",
            "Tailscale.LogOut",
            endpointTag)
        { XamlRoot = XamlRoot };

        _tsDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await App.State.Core.TailscaleLogoutAsync(endpointTag, CancellationToken.None);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _tsDialogOpen = false;
        }
    }

    private async Task ShowTailscaleExitNodeDialogAsync(CoreTailscaleEndpoint endpoint)
    {
        if (_tsDialogOpen) return;

        // The picker owns its rows; the page keeps the network call so the dialog
        // closes exactly like it did before the extraction.
        var dialog = new Dialogs.TailscaleExitNodeDialog(
            endpoint,
            StateBrush("Running"),
            StateBrush(string.Empty),
            stableId => SetTailscaleExitNodeAsync(endpoint.EndpointTag, stableId))
        { XamlRoot = XamlRoot };

        _tsDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _tsDialogOpen = false;
        }
    }

    /// <summary>
    /// SFM TailscalePeerView parity: read-only details for one peer. Pure data
    /// from the latest snapshot, so the dialog holds no streams and nothing can
    /// outlive the page.
    /// </summary>
    private async Task ShowTailscalePeerDetailsAsync(CoreTailscalePeer peer)
    {
        if (_tsDialogOpen) return;

        var rows = new List<(string Label, string Value)>();

        void AddRow(string label, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            rows.Add((label, value));
        }

        AddRow(Loc.Get("Tailscale.MagicDNS"), peer.DnsName);
        AddRow(Loc.Get("Tailscale.Hostname"), peer.HostName);

        foreach (var ip in peer.TailscaleIPs)
        {
            if (string.IsNullOrEmpty(ip)) continue;
            AddRow(ip.Contains(':') ? Loc.Get("Tailscale.IPv6") : Loc.Get("Tailscale.IPv4"), ip);
        }

        AddRow(Loc.Get("Tailscale.OS"), peer.Os);

        if (peer.Expired)
            AddRow(Loc.Get("Tailscale.KeyExpiry"), Loc.Get("Tailscale.Expired"));
        else if (peer.KeyExpiry > 0)
            AddRow(Loc.Get("Tailscale.KeyExpiry"), FormatUnixSeconds(peer.KeyExpiry));
        else
            AddRow(Loc.Get("Tailscale.KeyExpiry"), Loc.Get("Tailscale.Disabled"));

        if (!peer.Online && peer.LastSeen > 0)
            AddRow(Loc.Get("Tailscale.LastSeen"), FormatRelativeUnixSeconds(peer.LastSeen));

        if (peer.ExitNode) AddRow(Loc.Get("Tailscale.ExitNode"), Loc.Get("Tailscale.Active"));
        else if (peer.ExitNodeOption) AddRow(Loc.Get("Tailscale.ExitNode"), Loc.Get("Tailscale.Available"));

        if (peer.ShareeNode) AddRow(Loc.Get("Tailscale.SharedIn"), Loc.Get("Tailscale.Yes"));

        // The line layout is the shared Controls/CardLines control; the dialog only
        // scrolls it, exactly like the endpoint card rows.
        var dialog = new Dialogs.DetailDialog(
            peer.DisplayName, new CardLines(rows), "Tailscale.Close", maxHeight: 420)
        { XamlRoot = XamlRoot };

        _tsDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            _tsDialogOpen = false;
        }
    }

    private static string FormatUnixSeconds(long unixSeconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("g");
        }
        catch (ArgumentOutOfRangeException)
        {
            return unixSeconds.ToString();
        }
    }

    private static string FormatRelativeUnixSeconds(long unixSeconds)
    {
        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return unixSeconds.ToString();
        }

        var delta = DateTimeOffset.Now - timestamp;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 60) return Loc.Get("Tailscale.JustNow");
        if (delta.TotalMinutes < 60)
            return string.Format(Loc.Get("Tailscale.MinAgo"), (int)delta.TotalMinutes);
        if (delta.TotalHours < 24)
            return string.Format(Loc.Get("Tailscale.HoursAgo"), (int)delta.TotalHours);
        if (delta.TotalDays < 30)
            return string.Format(Loc.Get("Tailscale.DaysAgo"), (int)delta.TotalDays);

        return timestamp.LocalDateTime.ToString("g");
    }

    private async Task SetTailscaleExitNodeAsync(string endpointTag, string stableId)
    {
        try
        {
            await App.State.Core.SetTailscaleExitNodeAsync(endpointTag, stableId, CancellationToken.None);
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private bool IsDarkTheme => ActualTheme == ElementTheme.Dark;

    private SolidColorBrush StateBrush(string backendState) => backendState switch
    {
        "Running" => ThemeBrush(0x3D, 0x81, 0x68, 0x7C, 0xB8, 0x9E),
        "NeedsLogin" or "NeedsMachineAuth" => ThemeBrush(0xC2, 0x74, 0x0C, 0xE0, 0xA4, 0x58),
        "Starting" => ThemeBrush(0x9A, 0x7B, 0x00, 0xE8, 0xC5, 0x47),
        _ => ThemeBrush(0x8A, 0x8A, 0x8A, 0xA0, 0xA0, 0xA0),
    };

    private SolidColorBrush BadgeGreen() => ThemeBrush(0x3D, 0x81, 0x68, 0x7C, 0xB8, 0x9E);
    private SolidColorBrush BadgeBlue() => ThemeBrush(0x2F, 0x6F, 0xBE, 0x4E, 0x8C, 0xD8);
    private SolidColorBrush BadgeRed() => ThemeBrush(0xB4, 0x43, 0x2F, 0xD1, 0x6B, 0x52);

    private SolidColorBrush ThemeBrush(
        byte lightR, byte lightG, byte lightB, byte darkR, byte darkG, byte darkB) =>
        new(Windows.UI.Color.FromArgb(
            0xFF,
            IsDarkTheme ? darkR : lightR,
            IsDarkTheme ? darkG : lightG,
            IsDarkTheme ? darkB : lightB));

    /// <summary>
    /// SFM peer badges in a stable order: shared in, exit node, expired, SSH. The
    /// row template draws them; this method only supplies wording and colour.
    /// </summary>
    private IEnumerable<TailscalePeerRow.Badge> PeerBadges(CoreTailscalePeer peer)
    {
        if (peer.ShareeNode) yield return new(Loc.Get("Tailscale.SharedIn"), BadgeRed());
        if (peer.ExitNodeOption) yield return new(Loc.Get("Tailscale.ExitNodeAlt2"), BadgeBlue());
        if (peer.Expired) yield return new(Loc.Get("Tailscale.Expired"), BadgeRed());
        if (peer.SshHostKeys.Count > 0) yield return new(Loc.Get("Tailscale.SSH"), BadgeGreen());
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the system browser is best effort; the Copy button still works.
        }
    }

    private void ShowError(string message)
    {
        _errorShowing = true;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = Loc.Get("Tailscale.ToolError");
        StatusBar.Message = message;
    }

    private sealed class TailscalePingState
    {
        public CancellationTokenSource Cts { get; } = new();
        public string LatencyText { get; set; } = string.Empty;
        public string DetailText { get; set; } = string.Empty;
        public string ErrorText { get; set; } = string.Empty;
    }
}
