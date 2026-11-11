using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using SFW.Services;

namespace SFW.Pages;

/// <summary>
/// SFM SettingView "App" / SFA AppSettingsScreen parity: theme, backdrop
/// material, start at login, profile management and the app version. This is the
/// behavior that used to live on the single Settings page, moved here unchanged.
/// </summary>
public sealed partial class AppSettingsPage : Page
{
    private bool _applyingTheme;
    private bool _applyingStartup;
    private bool _applyingLanguage;

    public AppSettingsPage()
    {
        InitializeComponent();
        Loaded += AppSettingsPage_Loaded;
    }

    private async void AppSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        await LoadStartupStateAsync();
    }

    private void LoadSettings()
    {
        var settings = App.State.Settings;
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

        _applyingLanguage = true;
        try
        {
            // Older builds stored the generic tag; 繁體中文 now means Hong Kong.
            var languageTag = settings.LanguageTag == "zh-Hant" ? "zh-HK" : settings.LanguageTag ?? string.Empty;
            LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), languageTag, StringComparison.Ordinal))
                ?? LanguageBox.Items.FirstOrDefault();
        }
        finally
        {
            _applyingLanguage = false;
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

    // ----- Language -----

    /// <summary>
    /// The resource system resolves x:Uid text at load time, so a language change
    /// is stored and applied on the next start instead of live-switching half the UI.
    /// </summary>
    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingLanguage || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? string.Empty;
        if (string.Equals(App.State.Settings.LanguageTag, tag, StringComparison.Ordinal)) return;
        App.State.Settings.LanguageTag = tag;
        App.State.Save();
        LanguageRestartHint.Visibility = Visibility.Visible;
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
            LaunchCard.Description = Loc.Get("AppSettings.AvailableWhenTheAppIsInstalledAsAPackage");
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

    // ----- Profiles -----

    private void OpenProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(ProfilesPage));
    }
}
