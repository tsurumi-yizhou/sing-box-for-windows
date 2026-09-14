using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using sing_box_for_windows.Services;
using sing_box_for_windows.Services.Core;

namespace sing_box_for_windows.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _applyingTheme;
    private bool _applyingStartup;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        await LoadCoreVersionAsync();
        await LoadStartupStateAsync();
    }

    /// <summary>
    /// SFA CoreSettingsScreen / SFM CoreView parity: the core version is read from
    /// the bundled daemon itself, so it is shown even before the service ever runs.
    /// </summary>
    private async Task LoadCoreVersionAsync()
    {
        var version = await DaemonServiceManager.QueryVersionAsync();
        CoreVersionCard.Description = version ?? Loc.Get("Unavailable", "不可用");
    }

    private void LoadSettings()
    {
        var settings = App.State.Settings;
        DataFolderCard.Description = App.State.DataDirectory;
        VersionCard.Description = GetVersionText();

        _applyingTheme = true;
        try
        {
            foreach (var item in ThemeBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), settings.Theme, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeBox.SelectedItem = item;
                    break;
                }
            }
            ThemeBox.SelectedItem ??= ThemeBox.Items.FirstOrDefault();

            foreach (var item in BackdropBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), settings.Backdrop, StringComparison.OrdinalIgnoreCase))
                {
                    BackdropBox.SelectedItem = item;
                    break;
                }
            }
            BackdropBox.SelectedItem ??= BackdropBox.Items.FirstOrDefault();
        }
        finally
        {
            _applyingTheme = false;
        }
    }

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingTheme || BackdropBox.SelectedItem is not ComboBoxItem item) return;
        App.State.Settings.Backdrop = item.Tag?.ToString() ?? "mica";
        App.State.Save();
        MainWindow.Instance?.ApplyBackdrop();
    }

    private static string GetVersionText()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            var assembly = typeof(App).Assembly.GetName().Version;
            return assembly is null ? string.Empty : $"Version {assembly.Major}.{assembly.Minor}.{assembly.Build}";
        }
    }

    // ----- Theme -----

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingTheme || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        App.State.Settings.Theme = item.Tag?.ToString() ?? "default";
        App.State.Save();
        MainWindow.Instance?.ApplyTheme();
    }

    // ----- Start at login (StartupTask; packaged apps only) -----

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync("SingBoxStartup");
            _applyingStartup = true;
            LaunchAtLoginToggle.IsOn = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            _applyingStartup = false;
        }
        catch
        {
            // Unpackaged development runs have no StartupTask identity.
            LaunchAtLoginToggle.IsEnabled = false;
            LaunchCard.Description = Loc.Get(
                "Available when the app is installed as a package.",
                "应用以包形式安装后可用。");
        }
    }

    private async void LaunchAtLoginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingStartup) return;
        try
        {
            var task = await StartupTask.GetAsync("SingBoxStartup");
            if (LaunchAtLoginToggle.IsOn)
            {
                var state = await task.RequestEnableAsync();
                _applyingStartup = true;
                LaunchAtLoginToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                _applyingStartup = false;
            }
            else
            {
                task.Disable();
            }
        }
        catch
        {
            _applyingStartup = true;
            LaunchAtLoginToggle.IsOn = false;
            _applyingStartup = false;
        }
    }

    // ----- Data folder / profiles / save -----

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = App.State.DataDirectory,
            UseShellExecute = true,
        });
    }

    private void OpenProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(ProfilesPage));
    }

    private async void SourceCard_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/SagerNet/sing-box"));
    }
}
