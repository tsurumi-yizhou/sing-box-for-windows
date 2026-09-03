using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using sing_box_for_windows.Pages;
using sing_box_for_windows.Services;
using sing_box_for_windows.Services.Core;

namespace sing_box_for_windows;

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _connectedAt;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ApplyTheme();
        ApplyBackdrop();

        // Wire the tray icon declared in XAML (it must be in the visual tree
        // for H.NotifyIcon.WinUI to create the Shell_NotifyIcon entry).
        // NOTE: the Win32 popup menu mode invokes Command, not Click handlers.
        LoadTrayIcon();
        TrayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        TrayShowItem.Command = new RelayCommand(ShowWindow);
        TrayToggleItem.Command = new AsyncRelayCommand(async () =>
        {
            ShowWindow();
            await ToggleServiceAsync(App.State.Core.State.IsRunning);
            UpdateStartStop(App.State.Core.State);
        });
        TrayQuitItem.Command = new AsyncRelayCommand(QuitAsync);
        UpdateTrayToggle();
        // Closing the window hides to the tray; the core keeps running.
        AppWindow.Closing += AppWindow_Closing;

        NavFrame.Navigate(typeof(HomePage));
        if (FindItem(NavView.MenuItems, "home") is { } home)
        {
            NavView.SelectedItem = home;
        }
        UpdateMenuVisibility();
        UpdateStartStop(App.State.Core.State);
        App.State.Core.StateChanged += Core_StateChanged;
        App.State.Core.GroupsChanged += Core_GroupsChanged;
        _durationTimer.Tick += (_, _) => UpdateDuration();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        var wasRunning = App.State.Core.State.IsRunning;
        await Services.BusyButton.RunAsync(StartStopButton, () => ToggleServiceAsync(wasRunning), wasRunning
            ? Services.Loc.Get("Stopping…", "正在停止…")
            : Services.Loc.Get("Starting…", "正在启动…"));
        UpdateStartStop(App.State.Core.State);
    }

    /// <summary>Shared start/stop used by the title bar button and the tray menu.</summary>
    private async Task ToggleServiceAsync(bool wasRunning)
    {
        StartupDiag.Log($"ToggleServiceAsync: enter wasRunning={wasRunning}");
        try
        {
            if (wasRunning)
            {
                await App.State.StopAsync();
            }
            else
            {
                await App.State.StartAsync();
            }
            StartupDiag.Log("ToggleServiceAsync: completed");
        }
        catch (Exception exception)
        {
            StartupDiag.Log($"ToggleServiceAsync: EXCEPTION {exception.GetType().Name}: {exception.Message}");
            var dialog = new ContentDialog
            {
                Title = wasRunning
                    ? Services.Loc.Get("Unable to stop", "无法停止")
                    : Services.Loc.Get("Unable to start", "无法启动"),
                Content = exception.Message,
                CloseButtonText = Services.Loc.Get("OK", "确定"),
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    // ----- System tray -----

    /// <summary>
    /// Loads the tray icon directly into TrayIcon.Icon, bypassing
    /// H.NotifyIcon.WinUI's IconSource pipeline: it re-encodes the ImageSource
    /// and System.Drawing then rejects the result ("Argument 'picture' must be
    /// a picture that can be used as a Icon"), and the exception — raised from
    /// a fire-and-forget async void continuation — used to crash the process
    /// before the window or tray icon ever appeared (see crash.log). The raw
    /// ICO parses fine through the sized System.Drawing.Icon constructor.
    /// A failure here degrades to "no tray icon" instead of killing the app.
    /// </summary>
    private void LoadTrayIcon()
    {
        try
        {
            var smallIconSize = GetSystemMetrics(SM_CXSMICON);
            TrayIcon.Icon = new System.Drawing.Icon(
                Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                smallIconSize, smallIconSize);
        }
        catch (Exception exception)
        {
            StartupDiag.Log($"LoadTrayIcon: failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private const int SM_CXSMICON = 11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    private void UpdateTrayToggle()
    {
        var running = App.State.Core.State.IsRunning;
        TrayToggleItem.Text = running
            ? Services.Loc.Get("Stop", "停止")
            : Services.Loc.Get("Start", "启动");
        TrayToggleItem.Icon = new FontIcon { Glyph = running ? "\uE71A" : "\uE768" };
    }

    private async Task QuitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        // Destroy the native app window explicitly. Application.Exit only tears
        // down the XAML dispatcher; it does not reliably close a WinUI AppWindow
        // when this command originates from H.NotifyIcon's Win32 popup menu.
        AppWindow.Closing -= AppWindow_Closing;
        TrayIcon.Dispose();
        try
        {
            // Do not let an in-progress core start keep the UI process alive.
            await App.State.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort; the process is exiting anyway.
        }
        AppWindow.Destroy();
        Application.Current.Exit();
    }

    /// <summary>Applies the persisted theme override (SFM: App settings theme picker).</summary>
    public void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = App.State.Settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Window backdrop: Mica or Desktop Acrylic.</summary>
    public void ApplyBackdrop()
    {
        SystemBackdrop = App.State.Settings.Backdrop == "acrylic"
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop();
    }

    private void UpdateStartStop(RuntimeState state)
    {
        var running = state.IsRunning;
        StartStopIcon.Glyph = running ? "\uE71A" : "\uE768"; // Stop / Play
        StartStopText.Text = running
            ? Services.Loc.Get("Stop", "停止")
            : Services.Loc.Get("Start", "启动");
        StartStopButton.Style = running
            ? (Style)Application.Current.Resources["DefaultButtonStyle"]
            : (Style)Application.Current.Resources["AccentButtonStyle"];
        UpdateTrayToggle();

        if (running)
        {
            _connectedAt ??= DateTimeOffset.Now;
            UpdateDuration();
            _durationTimer.Start();
        }
        else
        {
            _connectedAt = null;
            _durationTimer.Stop();
            DurationText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDuration()
    {
        if (_connectedAt is not { } connectedAt)
        {
            DurationText.Visibility = Visibility.Collapsed;
            return;
        }

        // SFA/SFM uptime format: h:mm:ss once an hour has passed, otherwise m:ss.
        var elapsed = DateTimeOffset.Now - connectedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        DurationText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        DurationText.Visibility = Visibility.Visible;
    }


    public void NavigateTo(string tag)
    {
        if (FindItem(NavView.MenuItems, tag) is { } item)
        {
            NavView.SelectedItem = item;
        }
        else
        {
            var page = PageTypeFor(tag);
            if (page is not null && NavFrame.CurrentSourcePageType != page)
            {
                NavFrame.Navigate(page);
            }
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;
    private void TitleBar_BackRequested(TitleBar sender, object args) => NavFrame.GoBack();

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var page = PageTypeFor(item.Tag?.ToString());
        if (page is not null && NavFrame.CurrentSourcePageType != page)
        {
            NavFrame.Navigate(page);
        }
    }

    private void Core_StateChanged(object? sender, RuntimeState state) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateMenuVisibility();
            UpdateStartStop(state);
        });

    private void Core_GroupsChanged(object? sender, IReadOnlyList<CoreProxyGroup> groups) =>
        DispatcherQueue.TryEnqueue(UpdateMenuVisibility);

    private void UpdateMenuVisibility()
    {
        var running = App.State.Core.State.IsRunning;
        var hasGroups = running && App.State.Core.Groups.Count > 0;

        if (GroupsItem is not null) GroupsItem.Visibility = hasGroups ? Visibility.Visible : Visibility.Collapsed;
        if (ConnectionsItem is not null) ConnectionsItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        var current = NavFrame.CurrentSourcePageType;
        if ((!running && (current == typeof(ConnectionsPage) || current == typeof(GroupsPage))) ||
            (!hasGroups && current == typeof(GroupsPage)))
        {
            NavFrame.Navigate(typeof(HomePage));
            if (FindItem(NavView.MenuItems, "home") is { } home)
            {
                NavView.SelectedItem = home;
            }
        }
    }

    private static Type? PageTypeFor(string? tag) => tag switch
    {
        "home" => typeof(HomePage),
        "groups" => typeof(GroupsPage),
        "connections" => typeof(ConnectionsPage),
        "logs" => typeof(LogsPage),
        "tools" => typeof(ToolsPage),
        "settings" => typeof(SettingsPage),
        _ => null
    };

    private static NavigationViewItem? FindItem(IList<object> items, string tag)
    {
        foreach (var raw in items)
        {
            if (raw is NavigationViewItem item && Equals(item.Tag?.ToString(), tag))
            {
                return item;
            }
        }
        return null;
    }
}