using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Pages;
using SFW.Services;
using SFW.Services.Core;

namespace SFW;

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly DispatcherTimer _durationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _connectedAt;
    private bool _exiting;

    /// <param name="startHidden">
    /// Login launch: keep the window out of the way and live in the tray only. The
    /// tray entry is created explicitly, because H.NotifyIcon normally creates it
    /// when the window is shown.
    /// </param>
    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        Instance = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Frame.CanGoBack raises no change notification, so the x:Bind on the
        // title bar never updates on its own; tools subpages need the back button.
        NavFrame.Navigated += (_, _) => AppTitleBar.IsBackButtonVisible = NavFrame.CanGoBack;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ApplyTheme();
        ApplyBackdrop();

        // Wire the tray icon declared in XAML (it must be in the visual tree
        // for H.NotifyIcon.WinUI to create the Shell_NotifyIcon entry).
        // NOTE: the Win32 popup menu mode invokes Command, not Click handlers.
        LoadTrayIcon();
        if (startHidden)
        {
            TrayIcon.ForceCreate();
        }
        TrayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        TrayShowItem.Command = new RelayCommand(ShowWindow);
        TrayToggleItem.Command = new AsyncRelayCommand(async () =>
        {
            ShowWindow();
            await ToggleServiceAsync(App.State.Core.State.IsActive);
            UpdateStartStop(App.State.Core.State);
        });
        TrayQuitItem.Command = new AsyncRelayCommand(QuitAsync);
        UpdateTrayToggle();
        // Closing the window hides to the tray; the core keeps running.
        AppWindow.Closing += AppWindow_Closing;

        // Every informational TextBlock becomes copyable through a right-click
        // menu. Handling it at the shell root also covers text created later by
        // lists and cards, and leaves left-click/selection behavior untouched.
        if (Content is FrameworkElement shell)
        {
            shell.RightTapped += Shell_RightTapped;
        }

        // Static page text becomes selectable; text added later still gets the
        // root right-click copy menu.
        NavFrame.Navigated += (_, args) =>
        {
            if (args.Content is FrameworkElement page)
                page.Loaded += (_, _) => Services.TextCopy.AttachTree(page);
        };

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

    private void Shell_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (Services.TextCopy.TryShowCopyMenu(e.OriginalSource as DependencyObject, Content.XamlRoot, out var target))
        {
            e.Handled = true;
            Services.TextCopy.ShowCopyMenu(target!, e.GetPosition(target!));
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        var wasRunning = App.State.Core.State.IsActive;
        await Services.BusyButton.RunAsync(StartStopButton, () => ToggleServiceAsync(wasRunning), wasRunning
            ? Services.Loc.Get("Tray.Stopping")
            : Services.Loc.Get("Tray.Starting"));
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
            var dialog = new Dialogs.MessageDialog(
                wasRunning ? "Tray.UnableToStop" : "Tray.UnableToStart",
                exception.Message)
            { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }
    }

    // ----- System tray -----

    /// <summary>
    /// Records the tray icon variants. The icons themselves are created per
    /// switch: H.NotifyIcon takes ownership of an assigned Icon and disposes the
    /// previous one, so caching System.Drawing.Icon instances and re-assigning
    /// them later throws ObjectDisposedException (a stowed-exception fail-fast)
    /// the first time the state toggles. Building from the ICO each time also
    /// bypasses H.NotifyIcon.WinUI's IconSource pipeline, which rejects the
    /// re-encoded image.
    /// </summary>
    private void LoadTrayIcon()
    {
        _trayActiveIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var stoppedPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayStopped.ico");
        _trayStoppedIconPath = File.Exists(stoppedPath) ? stoppedPath : null;
        _trayIconShowsRunning = null;
    }

    private string? _trayActiveIconPath;
    private string? _trayStoppedIconPath;
    private bool? _trayIconShowsRunning;

    /// <summary>
    /// Switches the tray glyph between the running and stopped variants. A new
    /// Icon owns the handle each time; the control disposes it when replaced.
    /// </summary>
    private void UpdateTrayIcon()
    {
        var running = App.State.Core.State.IsActive;
        if (_trayIconShowsRunning == running) return;
        var path = running ? _trayActiveIconPath : (_trayStoppedIconPath ?? _trayActiveIconPath);
        if (path is null) return;
        try
        {
            var size = GetSystemMetrics(SM_CXSMICON);
            TrayIcon.Icon = new System.Drawing.Icon(path, size, size);
            _trayIconShowsRunning = running;
        }
        catch (Exception exception)
        {
            // A tray glyph is decoration; never let it take the window down.
            StartupDiag.Log($"UpdateTrayIcon: failed: {exception.GetType().Name}: {exception.Message}");
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

    /// <summary>
    /// Brings the window up from the tray (or from a redirected launch) on the UI
    /// thread, so it can be called from the tray icon or an activation callback.
    /// </summary>
    public void ShowFromTray() =>
        DispatcherQueue.TryEnqueue(ShowWindow);

    private void UpdateTrayToggle()
    {
        var running = App.State.Core.State.IsActive;
        TrayToggleItem.Text = running
            ? Services.Loc.Get("Tray.Stop")
            : Services.Loc.Get("Tray.Start");
        TrayToggleIcon.Glyph = running ? "\uE71A" : "\uE768";
    }

    private async Task QuitAsync()
    {
        if (_exiting) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            // H.NotifyIcon's popup menu calls back on its own Win32 thread.
            DispatcherQueue.Post(() => _ = QuitAsync(), nameof(QuitAsync));
            return;
        }
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
        var running = state.IsActive;
        StartStopIcon.Glyph = running ? "\uE71A" : "\uE768"; // Stop / Play
        StartStopText.Text = running
            ? Services.Loc.Get("Tray.Stop")
            : Services.Loc.Get("Tray.Start");
        StartStopButton.Style = running
            ? (Style)Application.Current.Resources["DefaultButtonStyle"]
            : (Style)Application.Current.Resources["AccentButtonStyle"];
        UpdateTrayToggle();
        UpdateTrayIcon();

        if (state.IsRunning)
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
        DispatcherQueue.Post(() =>
        {
            UpdateMenuVisibility();
            UpdateStartStop(state);
        }, "MainWindow.Core_StateChanged");

    private void Core_GroupsChanged(object? sender, IReadOnlyList<CoreProxyGroup> groups) =>
        DispatcherQueue.Post(UpdateMenuVisibility, nameof(UpdateMenuVisibility));

    private void UpdateMenuVisibility()
    {
        var running = App.State.Core.State.IsRunning;
        var hasGroups = running && App.State.Core.Groups.Count > 0;

        if (GroupsItem is not null) GroupsItem.Visibility = hasGroups ? Visibility.Visible : Visibility.Collapsed;
        if (ConnectionsItem is not null) ConnectionsItem.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        var current = NavFrame.CurrentSourcePageType;
        // Like SFA, the tools hub stays reachable while the service is stopped;
        // ToolsPage itself hides the sections that need a running core.
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
