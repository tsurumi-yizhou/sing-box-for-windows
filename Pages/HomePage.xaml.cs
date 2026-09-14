using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using sing_box_for_windows.Models;
using sing_box_for_windows.Services;
using sing_box_for_windows.Services.Core;

namespace sing_box_for_windows.Pages;

public sealed partial class HomePage : Page
{
    private bool _applyingMode;
    private bool _applyingSystemProxy;
    private string _selectedSegmentedMode = string.Empty;

    /// <summary>Relative update times would otherwise freeze while the page stays open.</summary>
    private readonly DispatcherTimer _relativeTimeTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public HomePage()
    {
        InitializeComponent();
        _relativeTimeTimer.Tick += (_, _) => UpdateProfileInfo();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.StateChanged += Core_StateChanged;
        App.State.Core.StatusChanged += Core_StatusChanged;
        App.State.Core.ClashModeChanged += Core_ClashModeChanged;
        App.State.Core.SystemProxyChanged += Core_SystemProxyChanged;

        // SFM TrafficLineChart: line in the primary text color, area fill at 10% opacity.
        if (Application.Current.Resources["TextFillColorPrimaryBrush"] is SolidColorBrush textBrush)
        {
            var color = textBrush.Color;
            foreach (var chart in new[] { UploadChart, DownloadChart })
            {
                chart.StrokeBrush = new SolidColorBrush(color);
                chart.AreaBrush = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B));
            }
        }

        UpdateStatus(App.State.Core.Status);
        UpdateRuntime(App.State.Core.State);
        UpdateClashMode(App.State.Core.ClashMode);
        UpdateSystemProxy(App.State.Core.SystemProxy);
        ApplyCardVisibility();
        RefreshProfiles();
        _relativeTimeTimer.Start();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _relativeTimeTimer.Stop();
        App.State.Core.StateChanged -= Core_StateChanged;
        App.State.Core.StatusChanged -= Core_StatusChanged;
        App.State.Core.ClashModeChanged -= Core_ClashModeChanged;
        App.State.Core.SystemProxyChanged -= Core_SystemProxyChanged;
    }

    private void RefreshProfiles()
    {
        var selected = App.State.ActiveProfile();
        ProfileBox.ItemsSource = App.State.Settings.Profiles;
        ProfileBox.SelectedItem = selected;
        ProfileHint.Severity = InfoBarSeverity.Informational;
        ProfileHint.Title = Loc.Get("Get started", "开始使用");
        ProfileHint.Message = Loc.Get("Add or select a profile, then start the service.", "添加或选择一个配置，然后启动服务。");
        SetProfileHintOpen(selected is null);
        DashboardEditProfileButton.IsEnabled = selected is not null;
        DashboardUpdateProfileButton.IsEnabled = selected?.IsRemote == true;
        DashboardRemoveProfileButton.IsEnabled = selected is not null;
        UpdateProfileInfo();
    }

    /// <summary>
    /// SFA ProfileInfoRow parity: the type badge is always shown, and remote
    /// profiles additionally report when they were last updated.
    /// </summary>
    private void UpdateProfileInfo()
    {
        var profile = App.State.ActiveProfile();
        if (profile is null)
        {
            ProfileInfoRow.Visibility = Visibility.Collapsed;
            return;
        }

        ProfileInfoRow.Visibility = Visibility.Visible;
        ProfileTypeText.Text = profile.IsRemote ? Loc.Get("Remote", "远程") : Loc.Get("Local", "本地");
        var updated = profile.IsRemote ? profile.LastUpdated : null;
        ProfileUpdatedPanel.Visibility = updated is null ? Visibility.Collapsed : Visibility.Visible;
        ProfileUpdatedText.Text = updated is { } time ? RelativeTime.Format(time) : string.Empty;
    }

    // ----- Dashboard items (SFA/SFM card management) -----

    private void DashboardItemsButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var (key, en, zh) in new[]
        {
            ("upload", "Upload", "上传"),
            ("download", "Download", "下载"),
            ("status", "Status", "状态"),
            ("connections", "Connections", "连接"),
            ("proxy", "System Proxy", "系统代理"),
            ("mode", "Clash Mode", "Clash 模式"),
        })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = Loc.Get(en, zh),
                IsChecked = !App.State.Settings.DisabledDashboardCards.Contains(key),
                Tag = key,
            };
            item.Click += DashboardItemToggle_Click;
            flyout.Items.Add(item);
        }

        // Profiles is always visible in SFA/SFM and cannot be hidden.
        flyout.Items.Add(new ToggleMenuFlyoutItem
        {
            Text = Loc.Get("Profiles", "配置"),
            IsChecked = true,
            IsEnabled = false,
        });

        flyout.ShowAt(DashboardItemsButton);
    }

    private void DashboardItemToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string key } item) return;
        var disabled = App.State.Settings.DisabledDashboardCards;
        if (item.IsChecked)
        {
            disabled.Remove(key);
        }
        else if (!disabled.Contains(key))
        {
            disabled.Add(key);
        }
        App.State.Save();
        ApplyCardVisibility();
    }

    private void ApplyCardVisibility()
    {
        var disabled = App.State.Settings.DisabledDashboardCards;
        var running = App.State.Core.State.IsRunning;
        var hasModes = App.State.Core.ClashMode.Modes.Count > 1;
        var proxyAvailable = App.State.Core.SystemProxy.Available;

        // SFA parity: dashboard cards stay visible when stopped (with idle
        // values) so the dashboard always looks like a dashboard; cards the
        // user disabled in "Dashboard items" stay hidden.
        Visibility CardVisibility(string key, bool gate) =>
            !disabled.Contains(key) && gate ? Visibility.Visible : Visibility.Collapsed;

        UploadCard.Visibility = CardVisibility("upload", true);
        DownloadCard.Visibility = CardVisibility("download", true);
        StatusCard.Visibility = CardVisibility("status", true);
        ConnectionsCard.Visibility = CardVisibility("connections", true);
        SystemProxyCard.Visibility = CardVisibility("proxy", !running || proxyAvailable);
        ModeCard.Visibility = CardVisibility("mode", hasModes);

        if (!running) SetIdleDashboardValues();
    }

    private void SetIdleDashboardValues()
    {
        UploadText.Text = "0 B/s";
        DownloadText.Text = "0 B/s";
        UploadTotalText.Text = "0 B";
        DownloadTotalText.Text = "0 B";
        MemoryText.Text = "—";
        GoroutinesText.Text = "—";
        InboundText.Text = "0";
        OutboundText.Text = "0";
    }

    // ----- Profile actions -----

    private async void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not Profile profile) return;
        try
        {
            await App.State.SelectProfileAsync(profile.Id);
            SetProfileHintOpen(false);
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
        }
    }

    /// <summary>
    /// SFA NewProfileScreen parity: one "add profile" form whose fields follow the
    /// selected type. A subscription is simply a remote profile — same entry point
    /// and name field — and remote profiles carry the auto update switch plus the
    /// interval they are scheduled with.
    /// </summary>
    private async void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = Loc.Get("Profile name (optional)", "配置名称（可选）"),
            PlaceholderText = Loc.Get("My server", "我的服务器"),
        };

        var localRadio = new RadioButton
        {
            Content = Loc.Get("Local", "本地"),
            GroupName = "NewProfileType",
            IsChecked = true,
        };
        var remoteRadio = new RadioButton
        {
            Content = Loc.Get("Remote", "远程"),
            GroupName = "NewProfileType",
        };
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        typeRow.Children.Add(localRadio);
        typeRow.Children.Add(remoteRadio);
        var typePanel = new StackPanel { Spacing = 6 };
        typePanel.Children.Add(new TextBlock { Text = Loc.Get("Profile type", "配置类型") });
        typePanel.Children.Add(typeRow);

        var pathBox = new TextBox
        {
            Header = Loc.Get("Configuration file", "配置文件"),
            PlaceholderText = @"C:\Configs\config.json",
            MinWidth = 420,
        };
        var browseButton = new Button
        {
            Content = Loc.Get("Browse", "浏览"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var pathGrid = new Grid { ColumnSpacing = 8 };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.Children.Add(pathBox);
        Grid.SetColumn(browseButton, 1);
        pathGrid.Children.Add(browseButton);

        var urlBox = new TextBox
        {
            Header = Loc.Get("Subscription URL", "订阅 URL"),
            PlaceholderText = "https://example.com/config.json",
            MinWidth = 420,
        };
        var autoUpdateToggle = new ToggleSwitch
        {
            Header = Loc.Get("Auto update", "自动更新"),
            IsOn = true,
            MinWidth = 0,
            OnContent = string.Empty,
            OffContent = string.Empty,
        };
        var intervalBox = new NumberBox
        {
            Header = Loc.Get("Auto update interval (minutes)", "自动更新间隔（分钟）"),
            Minimum = ProfileDialogs.MinAutoUpdateIntervalMinutes,
            Maximum = 7 * 24 * 60,
            Value = ProfileDialogs.DefaultAutoUpdateIntervalMinutes,
            SmallChange = 15,
            LargeChange = 60,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var remotePanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        remotePanel.Children.Add(urlBox);
        remotePanel.Children.Add(autoUpdateToggle);
        remotePanel.Children.Add(intervalBox);

        // Progress and errors live inside the form: the download keeps the dialog
        // open (SFA keeps the form editable, SFM disables it behind a spinner), so
        // the entered URL survives a failure instead of being replaced by a toast.
        var progressRing = new ProgressRing { IsActive = false, Width = 16, Height = 16 };
        var progressText = new TextBlock
        {
            Text = Loc.Get("Downloading the subscription…", "正在下载订阅…"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
        };
        var progressRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = Visibility.Collapsed,
        };
        progressRow.Children.Add(progressRing);
        progressRow.Children.Add(progressText);
        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(typePanel);
        panel.Children.Add(pathGrid);
        panel.Children.Add(remotePanel);
        panel.Children.Add(progressRow);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Add profile", "添加配置"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Add", "添加"),
            CloseButtonText = Loc.Get("Cancel", "取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        void ApplyType()
        {
            var remote = remoteRadio.IsChecked == true;
            pathGrid.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            remotePanel.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        }

        localRadio.Checked += (_, _) => ApplyType();
        remoteRadio.Checked += (_, _) => ApplyType();
        browseButton.Click += async (_, _) =>
        {
            try
            {
                if (await ConfigFilePicker.PickAsync() is { } picked) pathBox.Text = picked;
            }
            catch (Exception exception)
            {
                errorText.Text = exception.Message;
                errorText.Visibility = Visibility.Visible;
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // Keep the form open: it closes itself only once the profile exists.
            args.Cancel = true;

            var remote = remoteRadio.IsChecked == true;
            var source = (remote ? urlBox.Text : pathBox.Text).Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                errorText.Text = remote
                    ? Loc.Get("Enter a valid HTTP or HTTPS subscription URL.", "请输入有效的 HTTP 或 HTTPS 订阅 URL。")
                    : Loc.Get("Choose a SingBox JSON configuration file.", "请选择一个 SingBox JSON 配置文件。");
                errorText.Visibility = Visibility.Visible;
                return;
            }

            errorText.Visibility = Visibility.Collapsed;
            progressRow.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            progressRing.IsActive = remote;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var intervalMinutes = double.IsNaN(intervalBox.Value)
                    ? ProfileDialogs.DefaultAutoUpdateIntervalMinutes
                    : ProfileDialogs.ClampAutoUpdateInterval((int)intervalBox.Value);
                var profile = remote
                    ? await App.State.AddSubscriptionAsync(nameBox.Text.Trim(), source, autoUpdateToggle.IsOn, intervalMinutes)
                    : await AddLocalProfileAsync(nameBox.Text.Trim(), source);
                RefreshProfiles();
                dialog.Hide();
                ShowProfileStatus(InfoBarSeverity.Success, Loc.Get("Add profile", "添加配置"),
                    string.Format(Loc.Get("Added \"{0}\".", "已添加“{0}”。"), profile.Name));
            }
            catch (Exception exception)
            {
                errorText.Text = exception.Message;
                errorText.Visibility = Visibility.Visible;
            }
            finally
            {
                progressRing.IsActive = false;
                progressRow.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
        };

        await dialog.ShowAsync();
    }

    /// <summary>Validates a local configuration file and registers it as a profile.</summary>
    private static async Task<Profile> AddLocalProfileAsync(string name, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(Loc.Get("Configuration file was not found.", "未找到配置文件。"), path);
        AppState.ValidateJsonContent(await File.ReadAllTextAsync(path));
        var profileName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name;
        return App.State.AddProfile(profileName, path);
    }

    private async void DashboardEditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not Profile profile) return;
        try
        {
            if (await ProfileDialogs.ShowEditAsync(XamlRoot, profile))
            {
                RefreshProfiles();
            }
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
        }
    }

    private async void DashboardUpdateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ProfileBox.SelectedItem is not Profile profile || !profile.IsRemote) return;
        await BusyButton.RunAsync(button, async () =>
        {
            try
            {
                await App.State.UpdateSubscriptionAsync(profile.Id);
                RefreshProfiles();
            }
            catch (Exception exception)
            {
                ShowProfileError(exception.Message);
            }
        });
    }

    private async void DashboardRemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not Profile profile) return;

        if (!await ProfileDialogs.ConfirmRemoveAsync(XamlRoot, profile)) return;
        try
        {
            await App.State.RemoveProfileAsync(profile.Id);
        }
        catch (Exception exception)
        {
            ShowProfileError(exception.Message);
        }
        RefreshProfiles();
    }

    private void ManageProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame is null) return;
        try
        {
            Frame.Navigate(typeof(ProfilesPage));
        }
        catch (Exception exception)
        {
            // A page that fails to construct must not look like a dead button.
            StartupDiag.Log($"ManageProfilesButton_Click: {exception}");
            ShowProfileError(exception.Message);
        }
    }

    private void ViewConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.State.Core.State.IsRunning) return;
        MainWindow.Instance?.NavigateTo("connections");
    }

    // ----- Core event handling -----

    private void Core_StateChanged(object? sender, RuntimeState state) =>
        DispatcherQueue.TryEnqueue(() => UpdateRuntime(state));

    private void Core_StatusChanged(object? sender, CoreStatus status) =>
        DispatcherQueue.TryEnqueue(() => UpdateStatus(status));

    private void Core_ClashModeChanged(object? sender, CoreClashMode mode) =>
        DispatcherQueue.TryEnqueue(() => UpdateClashMode(mode));

    private void Core_SystemProxyChanged(object? sender, CoreSystemProxy proxy) =>
        DispatcherQueue.TryEnqueue(() => UpdateSystemProxy(proxy));

    private void UpdateRuntime(RuntimeState state)
    {
        var running = state.IsRunning;
        ViewConnectionsButton.IsEnabled = running;
        if (!running)
        {
            // SFA/SFM: traffic charts and live cards reset when the service stops.
            UploadChart.Clear();
            DownloadChart.Clear();
            SetIdleDashboardValues();
            UpdateSystemProxy(CoreSystemProxy.Empty);
        }
        else
        {
            _ = RefreshSystemProxyAsync();
        }
        ApplyCardVisibility();
    }

    private void UpdateStatus(CoreStatus status)
    {
        if (App.State.Core.State.IsRunning)
        {
            UploadText.Text = $"{FormatBytes(status.Uplink)}/s";
            DownloadText.Text = $"{FormatBytes(status.Downlink)}/s";
            UploadTotalText.Text = FormatBytes(status.UplinkTotal);
            DownloadTotalText.Text = FormatBytes(status.DownlinkTotal);
            UploadChart.AddSample(status.Uplink);
            DownloadChart.AddSample(status.Downlink);

            MemoryText.Text = FormatBytes(status.Memory);
            GoroutinesText.Text = status.Goroutines.ToString();
            InboundText.Text = status.ConnectionsIn.ToString();
            OutboundText.Text = status.ConnectionsOut.ToString();
        }
    }

    // ----- Clash mode (SFM segmented control with menu fallback) -----

    private void UpdateClashMode(CoreClashMode mode)
    {
        _applyingMode = true;
        try
        {
            _selectedSegmentedMode = mode.CurrentMode;
            var modes = mode.Modes;
            ModeCard.Visibility = Visibility.Collapsed; // re-evaluated by ApplyCardVisibility

            if (modes.Count is >= 2 and <= 4)
            {
                BuildModeSegments(modes, mode.CurrentMode);
                ModeSegmented.Visibility = Visibility.Visible;
                ModeBox.Visibility = Visibility.Collapsed;
            }
            else if (modes.Count > 4)
            {
                ModeBox.ItemsSource = modes;
                ModeBox.SelectedItem = string.IsNullOrWhiteSpace(mode.CurrentMode) ? null : mode.CurrentMode;
                ModeBox.IsEnabled = true;
                ModeSegmented.Visibility = Visibility.Collapsed;
                ModeBox.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _applyingMode = false;
        }
        ApplyCardVisibility();
    }

    private void BuildModeSegments(IReadOnlyList<string> modes, string current)
    {
        ModeSegmentedPanel.Children.Clear();
        ModeSegmentedPanel.ColumnDefinitions.Clear();
        var selectedBackground = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
        var selectedBorder = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush;
        var transparent = new SolidColorBrush(Colors.Transparent);

        for (var i = 0; i < modes.Count; i++)
        {
            var mode = modes[i];
            var selected = string.Equals(mode, current, StringComparison.Ordinal);
            ModeSegmentedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = mode,
                    FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = selected
                        ? Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush
                        : Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
                },
                Tag = mode,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(6),
                MinWidth = 0,
                Background = selected ? selectedBackground : transparent,
                BorderBrush = selected ? selectedBorder : transparent,
                BorderThickness = new Thickness(1),
            };
            button.Click += ModeSegment_Click;
            Grid.SetColumn(button, i);
            ModeSegmentedPanel.Children.Add(button);
        }
    }

    private async void ModeSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingMode || sender is not Button { Tag: string mode }) return;
        if (string.Equals(mode, _selectedSegmentedMode, StringComparison.Ordinal)) return;
        _selectedSegmentedMode = mode;
        BuildModeSegments(App.State.Core.ClashMode.Modes, mode);
        await SetModeAsync(mode);
    }

    private async void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingMode || ModeBox.SelectedItem is not string mode || string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        _applyingMode = true;
        try
        {
            await SetModeAsync(mode);
        }
        finally
        {
            _applyingMode = false;
        }
    }

    private async Task SetModeAsync(string mode)
    {
        try
        {
            await App.State.Core.SetClashModeAsync(mode);
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("Unable to change mode", "无法更改模式"),
                Content = exception.Message,
                CloseButtonText = Loc.Get("OK", "确定"),
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    // ----- System proxy -----

    private async void SystemProxyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSystemProxy) return;
        var enabled = SystemProxyToggle.IsOn;
        _applyingSystemProxy = true;
        try
        {
            await App.State.Core.SetSystemProxyEnabledAsync(enabled);
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("Unable to change system proxy", "无法更改系统代理"),
                Content = exception.Message,
                CloseButtonText = Loc.Get("OK", "确定"),
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _applyingSystemProxy = false;
        }
    }

    private void UpdateSystemProxy(CoreSystemProxy proxy)
    {
        _applyingSystemProxy = true;
        try
        {
            SystemProxyToggle.IsOn = proxy.Enabled;
            SystemProxyToggle.IsEnabled = proxy.Available && App.State.Core.State.IsRunning;
        }
        finally
        {
            _applyingSystemProxy = false;
        }
        ApplyCardVisibility();
    }

    private async Task RefreshSystemProxyAsync()
    {
        if (!App.State.Core.State.IsRunning)
        {
            UpdateSystemProxy(CoreSystemProxy.Empty);
            return;
        }

        try
        {
            await App.State.Core.RefreshSystemProxyAsync();
        }
        catch
        {
            // The proxy card simply stays hidden when the status is unavailable.
        }
        UpdateSystemProxy(App.State.Core.SystemProxy);
    }

    private void ShowProfileError(string message) =>
        ShowProfileStatus(InfoBarSeverity.Error, Loc.Get("Profile", "配置"), message);

    private void ShowProfileStatus(InfoBarSeverity severity, string title, string message)
    {
        SetProfileHintOpen(true);
        ProfileHint.Severity = severity;
        ProfileHint.Title = title;
        ProfileHint.Message = message;
    }

    /// <summary>InfoBar keeps its MinHeight when IsOpen=false; collapse it too.</summary>
    private void SetProfileHintOpen(bool open)
    {
        ProfileHint.IsOpen = open;
        ProfileHint.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Same byte ladder as libbox FormatBytes used by SFA/SFM.</summary>
    internal static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{value / 1024d / 1024d:F1} MB",
        _ => $"{value / 1024d / 1024d / 1024d:F2} GB",
    };
}
