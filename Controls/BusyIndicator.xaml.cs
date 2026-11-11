using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SFW.Controls;

/// <summary>
/// The busy content of a button while a long-running action runs. The markup lives
/// in BusyIndicator.xaml; callers only pass the optional busy label.
/// </summary>
public sealed partial class BusyIndicator : UserControl
{
    public BusyIndicator() => InitializeComponent();

    public BusyIndicator(string? busyText) : this()
    {
        BusyText.Text = busyText ?? string.Empty;
        BusyText.Visibility = string.IsNullOrEmpty(busyText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
