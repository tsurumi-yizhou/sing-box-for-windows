using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using sing_box_for_windows.Services;

namespace sing_box_for_windows.Pages;

public sealed partial class ProfilesPage : Page
{
    private readonly System.Collections.ObjectModel.ObservableCollection<ProfileRow> _rows = new();

    public ProfilesPage()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _rows;
        Loaded += (_, _) => Refresh();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            if (MainWindow.Instance is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ConfigBox.Text = file.Path;
            }
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Browse", "浏览"), error.Message);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await BusyButton.RunAsync(button, AddProfileFromFormAsync);
    }

    private async Task AddProfileFromFormAsync()
    {
        var source = ConfigBox.Text.Trim();
        ConfigBox.Description = string.Empty;
        if (string.IsNullOrEmpty(source))
        {
            ConfigBox.Description = Loc.Get("Enter a configuration path or an HTTP(S) subscription URL.", "请输入配置路径或 HTTP(S) 订阅 URL。");
            return;
        }

        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await App.State.AddSubscriptionAsync(NameBox.Text.Trim(), source);
            }
            else
            {
                if (!File.Exists(source))
                    throw new FileNotFoundException(Loc.Get("Configuration file was not found.", "未找到配置文件。"), source);
                AppState.ValidateJsonContent(await File.ReadAllTextAsync(source));
                var name = string.IsNullOrWhiteSpace(NameBox.Text)
                    ? Path.GetFileNameWithoutExtension(source)
                    : NameBox.Text.Trim();
                App.State.AddProfile(name, source);
            }
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to add profile", "无法添加配置"), error.Message);
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to save profile", "无法保存配置"), error.Message);
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to switch profile", "无法切换配置"), error.Message);
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
                ShowStatus(InfoBarSeverity.Informational, Loc.Get("Clipboard", "剪贴板"), Loc.Get("The clipboard is empty.", "剪贴板为空。"));
                return;
            }

            if (Uri.TryCreate(content, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await App.State.AddSubscriptionAsync(string.Empty, content);
            }
            else if (File.Exists(content))
            {
                App.State.AddProfile(Path.GetFileNameWithoutExtension(content), content);
            }
            else if (content.StartsWith('{'))
            {
                AppState.ValidateJsonContent(content);
                var directory = Path.Combine(App.State.DataDirectory, "profiles");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(path, content);
                App.State.AddProfile(Loc.Get("Imported profile", "导入的配置"), path);
            }
            else
            {
                ShowStatus(InfoBarSeverity.Warning, Loc.Get("Clipboard", "剪贴板"),
                    Loc.Get("The clipboard does not contain a subscription URL, a configuration path or JSON content.", "剪贴板内容不是订阅 URL、配置路径或 JSON 内容。"));
                return;
            }

            ShowStatus(InfoBarSeverity.Success, Loc.Get("Profile imported", "配置已导入"), content.Length > 80 ? content[..80] + "…" : content);
            Refresh();
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to import", "无法导入"), error.Message);
        }
    }

    /// <summary>SFA profiles overflow: update every remote subscription.</summary>
    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        var remotes = App.State.Settings.Profiles.Where(p => p.IsRemote).ToList();
        if (remotes.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, Loc.Get("Update all", "全部更新"), Loc.Get("There are no remote profiles.", "没有远程配置。"));
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
                ShowStatus(InfoBarSeverity.Success, Loc.Get("Update all", "全部更新"),
                    string.Format(Loc.Get("{0} subscription(s) updated.", "已更新 {0} 个订阅。"), succeeded));
            }
            else
            {
                ShowStatus(InfoBarSeverity.Warning, Loc.Get("Update all", "全部更新"),
                    string.Format(Loc.Get("{0} updated, {1} failed: ", "已更新 {0} 个，{1} 个失败："), succeeded, errors.Count) + string.Join("; ", errors));
            }
        }, Loc.Get("Updating…", "正在更新…"));
    }

    /// <summary>SFA long-press context menu on a profile.</summary>
    private void Profile_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileRow row } element) return;
        var flyout = new MenuFlyout();

        var editItem = new MenuFlyoutItem { Text = Loc.Get("Edit", "编辑"), Icon = new FontIcon { Glyph = "\uE70F" }, Tag = row.Id };
        editItem.Click += EditButton_Click;
        flyout.Items.Add(editItem);

        if (row.IsRemote)
        {
            var updateItem = new MenuFlyoutItem { Text = Loc.Get("Update", "更新"), Icon = new FontIcon { Glyph = "\uE72C" }, Tag = row.Id };
            updateItem.Click += UpdateButton_Click;
            flyout.Items.Add(updateItem);
        }

        var removeItem = new MenuFlyoutItem { Text = Loc.Get("Remove", "删除"), Icon = new FontIcon { Glyph = "\uE74D" }, Tag = row.Id };
        removeItem.Click += RemoveButton_Click;
        flyout.Items.Add(removeItem);

        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long id } button) return;
        await BusyButton.RunAsync(button, async () =>
        {
            try
            {
                var changed = await App.State.UpdateSubscriptionAsync(id);
                Refresh();
                if (!changed)
                {
                    ShowStatus(InfoBarSeverity.Informational, Loc.Get("Update", "更新"),
                        Loc.Get("The subscription content is unchanged.", "订阅内容没有变化。"));
                }
            }
            catch (Exception error)
            {
                Refresh();
                ShowStatus(InfoBarSeverity.Error, Loc.Get("Update failed", "更新失败"), error.Message);
            }
        });
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Auto update", "自动更新"), error.Message);
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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to remove profile", "无法删除配置"), error.Message);
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
        CountText.Text = Loc.Get($"{profiles.Count} profile{(profiles.Count == 1 ? string.Empty : "s")}", $"{profiles.Count} 个配置");
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
        public string DisplaySource => Profile.DisplaySource;
        public string Type => Profile.IsRemote ? Loc.Get("Remote", "远程") : Loc.Get("Local", "本地");
        public bool IsRemote => Profile.IsRemote;
        public bool AutoUpdate => Profile.AutoUpdate;

        public void RefreshDisplay()
        {
            LastUpdated = Profile.LastUpdated is { } updated
                ? FormatRelativeTime(updated)
                : string.Empty;
            // Name/DisplaySource/Type/AutoUpdate are plain getters; raise change
            // notifications so edits are reflected without reopening the page.
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplaySource));
            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(AutoUpdate));
            OnPropertyChanged(nameof(LastUpdateError));
            OnPropertyChanged(nameof(HasLastUpdateError));
        }

        /// <summary>SFA/SFM RelativeDateTimeFormatter parity.</summary>
        private static string FormatRelativeTime(DateTimeOffset time)
        {
            var elapsed = DateTimeOffset.Now - time;
            if (elapsed < TimeSpan.FromMinutes(1)) return Loc.Get("Updated just now", "刚刚更新");
            if (elapsed < TimeSpan.FromHours(1))
                return string.Format(Loc.Get("Updated {0} min ago", "{0} 分钟前更新"), (int)elapsed.TotalMinutes);
            if (elapsed < TimeSpan.FromDays(1))
                return string.Format(Loc.Get("Updated {0} h ago", "{0} 小时前更新"), (int)elapsed.TotalHours);
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        public string? LastUpdateError => Profile.LastUpdateError;
        public bool HasLastUpdateError => !string.IsNullOrEmpty(Profile.LastUpdateError);
    }
}