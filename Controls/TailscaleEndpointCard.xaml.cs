using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Controls;

/// <summary>
/// One Tailscale endpoint. The card markup lives in TailscaleEndpointCard.xaml;
/// the page fills the optional rows and adds the peer groups, because it owns the
/// ping streams and the dialogs that the buttons open.
/// </summary>
public sealed partial class TailscaleEndpointCard : UserControl
{
    public TailscaleEndpointCard()
    {
        InitializeComponent();

        var copyAddress = Loc.Get("Tailscale.CopyAddress");
        AutomationProperties.SetName(CopyAddressButton, copyAddress);
        ToolTipService.SetToolTip(CopyAddressButton, copyAddress);
        AutomationProperties.SetName(LogOutButton, Loc.Get("Tailscale.LogOutOfTailscale"));
        AutomationProperties.SetName(ExitNodeCard, Loc.Get("Tailscale.ChooseExitNode"));
        AutomationProperties.SetName(OpenAuthUrlButton, Loc.Get("Tailscale.OpenAuthURL"));
        AutomationProperties.SetName(CopyAuthUrlButton, Loc.Get("Tailscale.CopyAuthURL"));
    }

    /// <summary>Raised when the current device address should be copied.</summary>
    public event EventHandler? CopyAddressRequested;

    /// <summary>Raised when the endpoint should be logged out.</summary>
    public event EventHandler? LogOutRequested;

    /// <summary>Raised when the exit node picker should open.</summary>
    public event EventHandler? ChangeExitNodeRequested;

    /// <summary>Raised when the authentication URL should be opened in a browser.</summary>
    public event EventHandler? OpenAuthUrlRequested;

    /// <summary>Raised when the authentication URL should be copied.</summary>
    public event EventHandler? CopyAuthUrlRequested;

    /// <summary>
    /// Several endpoints in one profile are separated by a rounded outline; a
    /// single endpoint sits directly in the page, like SFM.
    /// </summary>
    public void SetFramed(bool framed)
    {
        // Each endpoint is its own SettingsExpander card, so no manual outline.
    }

    /// <summary>Header: backend-state dot, title (tag-qualified when several) and state label.</summary>
    public void ApplyHeader(CoreTailscaleEndpoint endpoint, bool multiple, Brush dotFill)
    {
        StateDot.Fill = dotFill;
        CardTitleText.Text = multiple
            ? $"{Loc.Get("Tailscale.Tailscale")}: {endpoint.EndpointTag}"
            : Loc.Get("Tailscale.Tailscale");
        StateText.Text = StateTextFor(endpoint);
    }

    /// <summary>Shows the network row (the daemon omits the name for the default network).</summary>
    public void ShowNetwork(string value)
    {
        NetworkValue.Text = value;
        NetworkCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Shows the current device. <paramref name="canLogOut"/> follows the endpoint's
    /// KeyAuth flag: a key-authenticated node has no interactive session to end.
    /// </summary>
    public void ShowThisDevice(string name, string? caption, bool canLogOut)
    {
        DeviceName.Text = name;
        DeviceCaption.Text = caption ?? string.Empty;
        DeviceCaption.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        LogOutButton.Visibility = canLogOut ? Visibility.Visible : Visibility.Collapsed;
        DeviceCard.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the selected exit node (or the localized "Disabled" label).</summary>
    public void ShowExitNode(string value)
    {
        ExitNodeValue.Text = value;
        ExitNodeCard.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the authentication URL with its open/copy actions.</summary>
    public void ShowAuthUrl(string url)
    {
        AuthUrlValue.Text = url;
        AuthUrlCard.Visibility = Visibility.Visible;
    }

    /// <summary>Adds one user's peer block below the endpoint rows.</summary>
    public void AddBlock(UIElement block) => Blocks.Children.Add(block);

    /// <summary>Mirrors the page's state text mapping so the card owns its own header.</summary>
    private static string StateTextFor(CoreTailscaleEndpoint endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.StateText)) return endpoint.StateText;
        return endpoint.BackendState switch
        {
            "Running" => Loc.Get("Tailscale.Running"),
            "Starting" => Loc.Get("Tailscale.Starting"),
            "Stopped" => Loc.Get("Tailscale.Stopped"),
            "NeedsLogin" => Loc.Get("Tailscale.NeedsLogin"),
            "NeedsMachineAuth" => Loc.Get("Tailscale.NeedsMachineAuthorization"),
            "NoState" or "" => Loc.Get("Tailscale.NoState"),
            _ => endpoint.BackendState,
        };
    }

    private void CopyAddressButton_Click(object sender, RoutedEventArgs e) =>
        CopyAddressRequested?.Invoke(this, EventArgs.Empty);

    private void LogOutButton_Click(object sender, RoutedEventArgs e) =>
        LogOutRequested?.Invoke(this, EventArgs.Empty);

    private void ChangeExitNodeButton_Click(object sender, RoutedEventArgs e) =>
        ChangeExitNodeRequested?.Invoke(this, EventArgs.Empty);

    private void OpenAuthUrlButton_Click(object sender, RoutedEventArgs e) =>
        OpenAuthUrlRequested?.Invoke(this, EventArgs.Empty);

    private void CopyAuthUrlButton_Click(object sender, RoutedEventArgs e) =>
        CopyAuthUrlRequested?.Invoke(this, EventArgs.Empty);
}
