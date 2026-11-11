using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Dialogs;

/// <summary>
/// The Tailscale exit-node picker. The option rows live in the XAML template; the
/// dialog only projects the latest snapshot into them and hands the chosen stable
/// id back, so the page keeps owning the network call.
/// </summary>
public sealed partial class TailscaleExitNodeDialog : ContentDialog
{
    private readonly Func<string, Task> _choose;

    public TailscaleExitNodeDialog(
        CoreTailscaleEndpoint endpoint,
        Brush onlineDot,
        Brush offlineDot,
        Func<string, Task> choose)
    {
        InitializeComponent();
        _choose = choose;
        Options.ItemsSource = BuildOptions(endpoint, onlineDot, offlineDot);
    }

    /// <summary>
    /// "Disabled" first, then every peer that offers itself as an exit node. The
    /// endpoint's own node is skipped, like SFM; the current choice is ticked.
    /// </summary>
    private static List<Option> BuildOptions(
        CoreTailscaleEndpoint endpoint, Brush onlineDot, Brush offlineDot)
    {
        var options = new List<Option>
        {
            new(Loc.Get("Tailscale.Disabled"), null, string.Empty, endpoint.ExitNode is null, null),
        };

        var selfStableId = endpoint.Self?.StableId;
        foreach (var group in endpoint.UserGroups)
        {
            foreach (var peer in group.Peers)
            {
                if (!peer.ExitNodeOption) continue;
                if (peer.StableId == selfStableId) continue;
                options.Add(new Option(
                    peer.DisplayName,
                    peer.TailscaleIPs.FirstOrDefault(),
                    peer.StableId,
                    string.Equals(endpoint.ExitNode?.StableId, peer.StableId, StringComparison.Ordinal),
                    peer.Online ? onlineDot : offlineDot));
            }
        }

        return options;
    }

    private async void Option_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Option option) return;

        // Hide first so the page's network call never runs behind an open dialog,
        // which is the order SFM uses when it commits the selection.
        Hide();
        await _choose(option.StableId);
    }

    /// <summary>
    /// One picker row. An empty stable id means "disabled"; a null dot brush means
    /// the row has no peer behind it and therefore no online marker. Public because
    /// the row template binds to it by reflection.
    /// </summary>
    public sealed class Option(
        string title, string? ip, string stableId, bool selected, Brush? dotBrush)
    {
        public string Title { get; } = title;

        public string Ip { get; } = ip ?? string.Empty;

        public string StableId { get; } = stableId;

        public Brush? DotBrush { get; } = dotBrush;

        public Visibility CheckVisibility => selected ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DotVisibility => DotBrush is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility IpVisibility => string.IsNullOrEmpty(Ip) ? Visibility.Collapsed : Visibility.Visible;
    }
}
