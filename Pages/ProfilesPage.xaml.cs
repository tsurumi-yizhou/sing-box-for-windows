using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SFW.Services;

namespace SFW.Pages;

public sealed partial class ProfilesPage : Page
{
    private readonly System.Collections.ObjectModel.ObservableCollection<ProfileRow> _rows = new();

    /// <summary>Relative update times would otherwise freeze while the page stays open.</summary>
    private readonly DispatcherTimer _relativeTimeTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public ProfilesPage()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _rows;
        _relativeTimeTimer.Tick += (_, _) => RefreshRelativeTimes();
        Loaded += (_, _) =>
        {
            ApplyProfileType();
            Refresh();
            _relativeTimeTimer.Start();
        };
        Unloaded += (_, _) => _relativeTimeTimer.Stop();
    }

    /// <summary>SFA NewProfileScreen: one form whose fields follow the selected type.</summary>
    private void NewProfileType_Checked(object sender, RoutedEventArgs e) => ApplyProfileType();

    private void ApplyProfileType()
    {
        // Also raised during InitializeComponent, before the later fields exist.
        if (ConfigBox is null || BrowseButton is null || RemoteOptionsPanel is null) return;
        var remote = TypeRemoteRadio.IsChecked == true;
        ConfigBox.Header = remote ? Loc.Get("Profiles.SubscriptionURL") : Loc.Get("Profiles.ConfigurationFile");
        ConfigBox.PlaceholderText = remote ? "https://example.com/config.json" : @"C:\Configs\config.json";
        BrowseButton.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        ConfigBox.Padding = remote ? new Thickness(12, 8, 12, 8) : new Thickness(12, 8, 44, 8);
        RemoteOptionsPanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        AutoUpdateToggle.Header = Loc.Get("Profiles.AutoUpdate");
        AutoUpdateIntervalBox.Header = Loc.Get("Profiles.AutoUpdateIntervalMinutes");
    }

    private void RefreshRelativeTimes()
    {
        foreach (var row in _rows) row.RefreshDisplay();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfigFilePicker.PickAsync() is { } path)
            {
                ConfigBox.Text = path;
            }
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.Browse"), error.Message);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await BusyButton.RunAsync(button, AddProfileFromFormAsync);
    }

    private async Task AddProfileFromFormAsync()
    {
        var remote = TypeRemoteRadio.IsChecked == true;
        var source = ConfigBox.Text.Trim();
        ConfigBox.Description = string.Empty;
        if (string.IsNullOrEmpty(source))
        {
            ConfigBox.Description = remote
                ? Loc.Get("Profiles.EnterAValidHTTPOrHTTPSSubscriptionURL")
                : Loc.Get("Profiles.EnterASingBoxConfigurationFilePath");
            return;
        }

        try
        {
            if (remote)
            {
                var intervalMinutes = double.IsNaN(AutoUpdateIntervalBox.Value)
                    ? ProfileDialogs.DefaultAutoUpdateIntervalMinutes
                    : ProfileDialogs.ClampAutoUpdateInterval((int)AutoUpdateIntervalBox.Value);
                await App.State.AddSubscriptionAsync(NameBox.Text.Trim(), source, AutoUpdateToggle.IsOn, intervalMinutes);
            }
            else
            {
                // Validated and copied into the app data directory, so the
                // profile does not depend on the picked file staying put.
                await App.State.AddLocalProfileAsync(NameBox.Text.Trim(), source);
            }
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UnableToAddProfile"), error.Message);
            return;
        }

        NameBox.Text = string.Empty;
        ConfigBox.Text = string.Empty;
        ConfigBox.Description = string.Empty;
        Refresh();
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long id) return;
        var profile = App.State.Settings.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null) return;

        try
        {
            if (await ProfileDialogs.ShowEditAsync(XamlRoot, profile))
            {
                Refresh();
            }
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UnableToSaveProfile"), error.Message);
        }
    }

    private async void ProfilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // SFA behavior: tapping a profile selects it as the active profile.
        if (e.ClickedItem is not ProfileRow row) return;
        try
        {
            await App.State.SelectProfileAsync(row.Id);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UnableToSwitchProfile"), error.Message);
        }
        Refresh();
    }

    /// <summary>SFA "Import from clipboard": URL, file path or raw JSON content.</summary>
    private async void ImportClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await BusyButton.RunAsync(button, ImportFromClipboardAsync);
    }

    private async Task ImportFromClipboardAsync()
    {
        try
        {
            var content = await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync();
            content = content.Trim();
            if (string.IsNullOrEmpty(content))
            {
                ShowStatus(InfoBarSeverity.Informational, Loc.Get("Profiles.Clipboard"), Loc.Get("Profiles.TheClipboardIsEmpty"));
                return;
            }

            if (Uri.TryCreate(content, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await App.State.AddSubscriptionAsync(string.Empty, content);
            }
            else if (File.Exists(content))
            {
                await App.State.AddLocalProfileAsync(string.Empty, content);
            }
            else if (content.StartsWith('{'))
            {
                await App.State.AddLocalProfileContentAsync(Loc.Get("Profiles.ImportedProfile"), content);
            }
            else
            {
                ShowStatus(InfoBarSeverity.Warning, Loc.Get("Profiles.Clipboard"),
                    Loc.Get("Profiles.TheClipboardDoesNotContainASubscriptionURLAConfigura"));
                return;
            }

            ShowStatus(InfoBarSeverity.Success, Loc.Get("Profiles.ProfileImported"), content.Length > 80 ? content[..80] + "…" : content);
            Refresh();
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UnableToImport"), error.Message);
        }
    }

    /// <summary>SFA profiles overflow: update every remote subscription.</summary>
    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        var remotes = App.State.Settings.Profiles.Where(p => p.IsRemote).ToList();
        if (remotes.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, Loc.Get("Profiles.UpdateAll"), Loc.Get("Profiles.ThereAreNoRemoteProfiles"));
            return;
        }

        await BusyButton.RunAsync(UpdateAllButton, async () =>
        {
            var succeeded = 0;
            var errors = new List<string>();
            foreach (var profile in remotes)
            {
                try
                {
                    await App.State.UpdateSubscriptionAsync(profile.Id);
                    succeeded++;
                }
                catch (Exception error)
                {
                    errors.Add($"{profile.Name}: {error.Message}");
                }
            }
            Refresh();

            if (errors.Count == 0)
            {
                ShowStatus(InfoBarSeverity.Success, Loc.Get("Profiles.UpdateAll"),
                    string.Format(Loc.Get("Profiles.SubscriptionSUpdated"), succeeded));
            }
            else
            {
                ShowStatus(InfoBarSeverity.Warning, Loc.Get("Profiles.UpdateAll"),
                    string.Format(Loc.Get("Profiles.UpdatedFailed"), succeeded, errors.Count) + string.Join("; ", errors));
            }
        }, Loc.Get("Profiles.Updating"));
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        // The InfoBar's built-in Message is a plain TextBlock; put the text in a
        // selectable block instead so an error can be copied.
        StatusBar.Message = string.Empty;
        StatusBar.Content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long id }) return;
        if (sender is Button button)
        {
            await BusyButton.RunAsync(button, () => UpdateProfileAsync(id));
            return;
        }
        await UpdateProfileAsync(id);
    }

    private async Task UpdateProfileAsync(long id)
    {
        try
        {
            var changed = await App.State.UpdateSubscriptionAsync(id);
            Refresh();
            if (!changed)
            {
                ShowStatus(InfoBarSeverity.Informational, Loc.Get("Profiles.Update"),
                    Loc.Get("Profiles.TheSubscriptionContentIsUnchanged"));
            }
        }
        catch (Exception error)
        {
            Refresh();
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UpdateFailed"), error.Message);
        }
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: long id } toggle) return;
        try
        {
            App.State.SetProfileAutoUpdate(id, toggle.IsOn);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.AutoUpdate"), error.Message);
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not long id) return;
        var profile = App.State.Settings.Profiles.FirstOrDefault(x => x.Id == id);
        if (profile is null) return;

        if (!await ProfileDialogs.ConfirmRemoveAsync(XamlRoot, profile)) return;
        try
        {
            await App.State.RemoveProfileAsync(id);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Profiles.UnableToRemoveProfile"), error.Message);
        }
        Refresh();
    }

    private void Refresh()
    {
        var selectedId = App.State.Settings.SelectedProfileId;
        var profiles = App.State.Settings.Profiles
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Id)
            .ToList();

        // Reconcile rows in place instead of rebinding the whole list.
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (profiles.All(p => p.Id != _rows[i].Id))
            {
                _rows.RemoveAt(i);
            }
        }
        foreach (var profile in profiles)
        {
            var row = _rows.FirstOrDefault(r => r.Id == profile.Id);
            if (row is null)
            {
                row = new ProfileRow(profile);
                _rows.Add(row);
            }
            row.IsSelected = profile.Id == selectedId;
            row.RefreshDisplay();
        }
        for (var i = 0; i < profiles.Count; i++)
        {
            var row = _rows.First(r => r.Id == profiles[i].Id);
            var index = _rows.IndexOf(row);
            if (index != i)
            {
                _rows.Move(index, i);
            }
        }
        CountText.Text = string.Format(
            Loc.Get("Profiles.ProfileCount"), profiles.Count, profiles.Count == 1 ? string.Empty : "s");
    }

    private sealed partial class ProfileRow : ObservableObject
    {
        public ProfileRow(Models.Profile profile)
        {
            Profile = profile;
            RefreshDisplay();
        }

        public Models.Profile Profile { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        [ObservableProperty]
        public partial string LastUpdated { get; set; } = string.Empty;

        public long Id => Profile.Id;
        public string Name => Profile.Name;
        public string DisplaySource => Profile.IsRemote ? Profile.DisplaySource : string.Empty;
        public bool HasDisplaySource => !string.IsNullOrEmpty(DisplaySource);
        public string Type => Profile.IsRemote ? Loc.Get("Profiles.Remote") : Loc.Get("Profiles.Local");
        public bool IsRemote => Profile.IsRemote;
        public bool AutoUpdate => Profile.AutoUpdate;

        public void RefreshDisplay()
        {
            LastUpdated = Profile.LastUpdated is { } updated
                ? RelativeTime.Format(updated)
                : string.Empty;
            // Name/DisplaySource/Type/AutoUpdate are plain getters; raise change
            // notifications so edits are reflected without reopening the page.
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplaySource));
            OnPropertyChanged(nameof(HasDisplaySource));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(AutoUpdate));
            OnPropertyChanged(nameof(LastUpdateError));
            OnPropertyChanged(nameof(HasLastUpdateError));
        }

        public string? LastUpdateError => Profile.LastUpdateError;
        public bool HasLastUpdateError => !string.IsNullOrEmpty(Profile.LastUpdateError);
    }
}
