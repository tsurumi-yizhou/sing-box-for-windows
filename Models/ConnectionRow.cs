using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SFW.Pages;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Models;

public sealed partial class ConnectionRow : ObservableObject
{
    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush StateBrush { get; set; } = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    [ObservableProperty]
    public partial string UpText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DownText { get; set; } = string.Empty;

    public ConnectionRow(CoreConnection connection)
    {
        Connection = connection;
        var destination = string.IsNullOrWhiteSpace(connection.DisplayDestination)
            ? connection.Destination
            : connection.DisplayDestination;
        Title = $"{connection.Network.ToUpperInvariant()} {destination}";
        InboundText = $"{connection.InboundType}/{connection.Inbound}";
        ChainText = connection.Chain.Count > 0 ? connection.Chain[0] : connection.Outbound;
        Update(connection);
    }

    public CoreConnection Connection { get; private set; }
    public string Title { get; }
    public string InboundText { get; }
    public string ChainText { get; }

    public void Update(CoreConnection c)
    {
        Connection = c;
        var active = c.ClosedAt == 0;
        StateText = active ? Loc.Get("Active", "活动") : Loc.Get("Closed", "已关闭");
        StateBrush = (Brush)Application.Current.Resources[active ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        UpText = active
            ? $"↑ {ConnectionsPage.FormatBytes(c.Uplink)}/s | {ConnectionsPage.FormatBytes(c.UplinkTotal)}"
            : $"↑ {ConnectionsPage.FormatBytes(c.UplinkTotal)}";
        DownText = active
            ? $"↓ {ConnectionsPage.FormatBytes(c.Downlink)}/s | {ConnectionsPage.FormatBytes(c.DownlinkTotal)}"
            : $"↓ {ConnectionsPage.FormatBytes(c.DownlinkTotal)}";
    }
}
