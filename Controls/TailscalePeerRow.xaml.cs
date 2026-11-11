using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Controls;

/// <summary>
/// One Tailscale peer row. The markup lives in TailscalePeerRow.xaml; the page
/// only hands over the peer, its badges and the brushes, and drives the ping
/// state through the methods below so the row cannot drift from SFM's view.
/// </summary>
public sealed partial class TailscalePeerRow : UserControl
{
    private string _displayName = string.Empty;

    public TailscalePeerRow() => InitializeComponent();

    /// <summary>Raised when the details button is clicked.</summary>
    public event EventHandler? DetailsRequested;

    /// <summary>Raised when the ping/stop button is clicked.</summary>
    public event EventHandler? PingToggled;

    /// <summary>The endpoint this peer belongs to; the page keys its ping stream by it.</summary>
    public string EndpointTag { get; private set; } = string.Empty;

    /// <summary>The address the ping stream targets; empty when the peer cannot be pinged.</summary>
    public string PeerIp { get; private set; } = string.Empty;

    /// <summary>
    /// Applies the per-snapshot peer data. The ping action only appears for peers
    /// that are online and have an address, which is exactly when SFA/SFM show it.
    /// </summary>
    public void Apply(
        CoreTailscalePeer peer,
        string endpointTag,
        IReadOnlyList<Badge> badges,
        string? firstIp,
        Brush dotFill)
    {
        _displayName = peer.DisplayName;
        EndpointTag = endpointTag;
        PeerIp = firstIp ?? string.Empty;

        PeerName.Text = peer.DisplayName;
        Badges.ItemsSource = badges;
        OnlineDot.Fill = dotFill;

        PeerIpText.Text = firstIp ?? string.Empty;
        PeerIpText.Visibility = string.IsNullOrEmpty(firstIp) ? Visibility.Collapsed : Visibility.Visible;

        var detailsName = string.Format(Loc.Get("Tailscale.DetailsFor"), peer.DisplayName);
        AutomationProperties.SetName(DetailsButton, detailsName);
        ToolTipService.SetToolTip(DetailsButton, detailsName);

        var pingable = peer.Online && !string.IsNullOrEmpty(firstIp);
        PingButton.Visibility = pingable ? Visibility.Visible : Visibility.Collapsed;
        if (!pingable) return;

        PingIcon.Glyph = "\uE768"; // Play
        var pingName = string.Format(Loc.Get("Tailscale.PingAlt2"), peer.DisplayName);
        AutomationProperties.SetName(PingButton, pingName);
        ToolTipService.SetToolTip(PingButton, pingName);
    }

    /// <summary>Toggles the ping button into its running state (label + progress).</summary>
    public void SetPingRunning(bool running)
    {
        // One native icon button: play to start, stop while the stream runs.
        PingIcon.Glyph = running ? "\uE71A" : "\uE768";
        AutomationProperties.SetName(PingButton, running
            ? $"{Loc.Get("Tailscale.Stop")} {_displayName}"
            : string.Format(Loc.Get("Tailscale.PingAlt2"), _displayName));
    }

    /// <summary>Clears the last ping error before a new ping starts.</summary>
    public void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Renders one ping result. Empty fields collapse their line, so a stream that
    /// only reports a latency keeps the row as tall as its content.
    /// </summary>
    public void ApplyPing(string latency, string detail, string error)
    {
        LatencyText.Text = latency;
        LatencyText.Visibility = string.IsNullOrEmpty(latency) ? Visibility.Collapsed : Visibility.Visible;
        ConnectionText.Text = detail;
        ConnectionText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        ResultLine.Visibility = string.IsNullOrEmpty(latency) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error;
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e) =>
        DetailsRequested?.Invoke(this, EventArgs.Empty);

    private void PingButton_Click(object sender, RoutedEventArgs e) =>
        PingToggled?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// One badge next to the peer name (shared in, exit node, expired, SSH). The
    /// template binds to it, so the page decides both wording and colour.
    /// </summary>
    public sealed class Badge(string text, Brush brush)
    {
        public string Text { get; } = text;

        public Brush Brush { get; } = brush;
    }
}
