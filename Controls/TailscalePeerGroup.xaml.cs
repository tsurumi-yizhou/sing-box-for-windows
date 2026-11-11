using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SFW.Controls;

/// <summary>
/// The peers of one tailnet user inside a Tailscale endpoint card. The group
/// caption is XAML; the page adds one row per peer, so the ping bookkeeping stays
/// with the page that owns the streams.
/// </summary>
public sealed partial class TailscalePeerGroup : UserControl
{
    public TailscalePeerGroup() => InitializeComponent();

    /// <summary>
    /// Sets the user caption. The daemon reports either a display name or a login
    /// name; no name at all means no caption row, like SFA/SFM.
    /// </summary>
    public void SetTitle(string? title)
    {
        GroupTitle.Text = title ?? string.Empty;
        GroupTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Adds one peer row to this user's block.</summary>
    public void AddPeer(UIElement peer) => Peers.Children.Add(peer);
}
