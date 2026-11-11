using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using SFW.Models;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

public sealed partial class HomePage : Page
{
    private bool _applyingMode;
    private bool _modeChangePending;
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
        App.State.Core.ConnectionChanged += Core_ConnectionChanged;
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
        UpdateConnection(App.State.Core.Connection);
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
        App.State.Core.ConnectionChanged -= Core_ConnectionChanged;
        App.State.Core.StatusChanged -= Core_StatusChanged;
        App.State.Core.ClashModeChanged -= Core_ClashModeChanged;
        App.State.Core.SystemProxyChanged -= Core_SystemProxyChanged;
    }

    private void RefreshProfiles()
    {
        var selected = App.State.ActiveProfile();
        // The combo holds the list instance it was first given, so adding a
        // profile to the settings list alone does not rebuild its items; a fresh
        // snapshot makes the dashboard pick the new entry up.
        ProfileBox.ItemsSource = App.State.Settings.Profiles.ToList();
        ProfileBox.SelectedItem = selected;
        ProfileHint.Severity = InfoBarSeverity.Informational;
        ProfileHint.Title = Loc.Get("Home.GetStarted");
        ProfileHint.Message = Loc.Get("Home.AddOrSelectAProfileThenStartTheService");
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
        ProfileTypeText.Text = profile.IsRemote ? Loc.Get("Home.Remote") : Loc.Get("Home.Local");
        var updated = profile.IsRemote ? profile.LastUpdated : null;
        ProfileUpdatedPanel.Visibility = updated is null ? Visibility.Collapsed : Visibility.Visible;
        ProfileUpdatedText.Text = updated is { } time ? RelativeTime.Format(time) : string.Empty;
    }

    // ----- Dashboard items (SFA/SFM card management) -----

    private void DashboardItemsFlyout_Opening(object? sender, object e)
    {
        var disabled = App.State.Settings.DisabledDashboardCards;
        foreach (var item in DashboardItemsFlyout.Items.OfType<ToggleMenuFlyoutItem>())
        {
            if (item.Tag is string key) item.IsChecked = !disabled.Contains(key);
        }
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
        var dialog = new Dialogs.AddProfileDialog { XamlRoot = XamlRoot };
        dialog.SubmitAsync = async form =>
        {
            try
            {
                var profile = form.IsRemote
                    ? await App.State.AddSubscriptionAsync(
                        form.Name, form.Source, form.AutoUpdate, form.IntervalMinutes)
                    : await App.State.AddLocalProfileAsync(form.Name, form.Source);
                RefreshProfiles();
                ShowProfileStatus(InfoBarSeverity.Success, Loc.Get("Home.AddProfile"),
                    string.Format(Loc.Get("Home.Added"), profile.Name));
                return null;
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        };
        await dialog.ShowAsync();
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
        MainWindow.Instance?.NavigateTo("connections");
    }

    // ----- Core event handling -----

    private void Core_ConnectionChanged(object? sender, DaemonConnectionState state) =>
        DispatcherQueue.Post(() => UpdateConnection(state), nameof(UpdateConnection));

    private void UpdateConnection(DaemonConnectionState state)
    {
        ServiceSetupButton.Visibility = state.Phase is DaemonConnectionPhase.NotInstalled or
            DaemonConnectionPhase.NotRunning or DaemonConnectionPhase.VersionMismatch ? Visibility.Visible : Visibility.Collapsed;
        ServiceSetupButton.Content = state.Phase switch
        {
            DaemonConnectionPhase.NotInstalled => Loc.Get("Home.Install"),
            DaemonConnectionPhase.VersionMismatch => Loc.Get("Home.Upgrade"),
            _ => Loc.Get("Home.Start"),
        };
        ConnectionInfo.IsOpen = state.Phase is not (DaemonConnectionPhase.Connected or DaemonConnectionPhase.Disconnected);
        ConnectionInfo.Message = state.Error ?? state.Phase switch
        {
            DaemonConnectionPhase.Connecting => Loc.Get("Home.ConnectingToTheService"),
            DaemonConnectionPhase.Reconnecting => Loc.Get("Home.ConnectionLostReconnecting"),
            DaemonConnectionPhase.NotInstalled => Loc.Get("Home.ServiceIsNotInstalled"),
            DaemonConnectionPhase.NotRunning => Loc.Get("Home.ServiceIsStopped"),
            _ => Loc.Get("Home.TheServiceIsUnavailable"),
        };
    }

    private async void ServiceSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var phase = App.State.Core.Connection.Phase;
        await BusyButton.RunAsync(ServiceSetupButton, async () =>
        {
            try
            {
                if (phase == DaemonConnectionPhase.NotRunning)
                    await DaemonServiceManager.EnsureRunningAsync(App.State.AppendLog);
                else
                    await DaemonServiceManager.InstallBundledServiceAsync(App.State.AppendLog);
                // A reconnecting session will discover the service automatically.
                if (!App.State.Core.State.IsActive)
                {
                    ConnectionInfo.IsOpen = false;
                    ServiceSetupButton.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception error) { ConnectionInfo.Message = error.Message; }
        });
    }

    private void Core_StateChanged(object? sender, RuntimeState state) =>
        DispatcherQueue.Post(() => UpdateRuntime(state), nameof(UpdateRuntime));

    private void Core_StatusChanged(object? sender, CoreStatus status) =>
        DispatcherQueue.Post(() => UpdateStatus(status), nameof(UpdateStatus));

    private void Core_ClashModeChanged(object? sender, CoreClashMode mode) =>
        DispatcherQueue.Post(() => UpdateClashMode(mode), nameof(UpdateClashMode));

    private void Core_SystemProxyChanged(object? sender, CoreSystemProxy proxy) =>
        DispatcherQueue.Post(() => UpdateSystemProxy(proxy), nameof(UpdateSystemProxy));

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
                ModeBox.IsEnabled = !_modeChangePending;
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

    /// <summary>
    /// Rebuilds the native segmented mode selector from the modes the core
    /// reports; the control itself owns the look and the selection visuals.
    /// </summary>
    private void BuildModeSegments(IReadOnlyList<string> modes, string current)
    {
        ModeSegmented.ItemsSource = modes;
        ModeSegmented.SelectedItem = string.IsNullOrWhiteSpace(current) ? null : current;
        ModeSegmented.IsEnabled = !_modeChangePending;
    }

    private async void ModeSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingMode || _modeChangePending) return;
        if (ModeSegmented.SelectedItem is not string mode) return;
        if (string.Equals(mode, _selectedSegmentedMode, StringComparison.Ordinal)) return;
        _selectedSegmentedMode = mode;
        await SetModeAsync(mode);
    }

    private async void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingMode || _modeChangePending || ModeBox.SelectedItem is not string mode || string.IsNullOrWhiteSpace(mode))
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
        _modeChangePending = true;
        ModeSegmented.IsEnabled = false;
        ModeBox.IsEnabled = false;
        try
        {
            await App.State.Core.SetClashModeAsync(mode);
        }
        catch (Exception exception)
        {
            // Revert the optimistic selection to the last daemon-confirmed mode.
            UpdateClashMode(App.State.Core.ClashMode);
            var dialog = new Dialogs.MessageDialog("Home.UnableToChangeMode", exception.Message)
            { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
        finally
        {
            _modeChangePending = false;
            ModeSegmented.IsEnabled = true;
            ModeBox.IsEnabled = true;
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
            var dialog = new Dialogs.MessageDialog("Home.UnableToChangeSystemProxy", exception.Message)
            { XamlRoot = XamlRoot };
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
        ShowProfileStatus(InfoBarSeverity.Error, Loc.Get("Home.Profile"), message);

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
