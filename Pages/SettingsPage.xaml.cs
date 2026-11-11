using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Services;

namespace SFW.Pages;

/// <summary>
/// SFA SettingsScreen / SFM SettingView parity: a two-section settings hub.
/// The first expander groups the pages that exist on Windows (App,
/// Core, Service); the "About" expander holds the external links.
/// Apple/Android-only rows (profile override, remote control, privileged) have
/// no Windows equivalent and are omitted here.
///
/// The hub itself never touches the daemon: every daemon query lives on the Core
/// and Service pages, so a stopped service cannot break navigation here.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    // ----- Navigation -----

    private void AppCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(AppSettingsPage));

    private void CoreCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(CoreSettingsPage));

    private void ServiceCard_Click(object sender, RoutedEventArgs e) => Navigate(typeof(ServiceSettingsPage));

    private void DocumentationCard_Click(object sender, RoutedEventArgs e) => OpenLink("https://sing-box.sagernet.org/");

    private void SourceCodeCard_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/SagerNet/sing-box");

    private void SponsorCard_Click(object sender, RoutedEventArgs e) => OpenLink("https://sekai.icu/sponsors/");

    private void Navigate(Type pageType)
    {
        Frame?.Navigate(pageType);
    }

    // ----- Links -----

    private async void OpenLink(string uri)
    {
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(new Uri(uri))) return;
            ShowError(Loc.Get("Settings.TheLinkCouldNotBeOpened"));
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void ShowError(string message)
    {
        StatusBar.Message = message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }
}
