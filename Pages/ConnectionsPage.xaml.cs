using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sing_box_for_windows.Services;
using sing_box_for_windows.Services.Core;

namespace sing_box_for_windows.Pages;

public sealed partial class ConnectionsPage : Page
{
    private enum StateFilter { All, Active, Closed }
    private enum ConnectionSort { Date, Traffic, TrafficTotal }

    private StateFilter _stateFilter = StateFilter.Active;
    private ConnectionSort _sort = ConnectionSort.Date;
    private bool _applying;

    public ConnectionsPage()
    {
        InitializeComponent();
        ConnectionsList.ItemsSource = _rows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.ConnectionsChanged += OnConnectionsChanged;
        App.State.Core.StateChanged += OnStateChanged;
        Render(App.State.Core.Connections);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.ConnectionsChanged -= OnConnectionsChanged;
        App.State.Core.StateChanged -= OnStateChanged;
    }

    private void OnConnectionsChanged(object? sender, IReadOnlyList<CoreConnection> connections) =>
        DispatcherQueue.TryEnqueue(() => Render(connections));

    private void OnStateChanged(object? sender, RuntimeState state) =>
        DispatcherQueue.TryEnqueue(() => Render(App.State.Core.Connections));

    // ----- Toolbar -----

    private void StateFilterMenu_Opening(object? sender, object e)
    {
        foreach (var item in StateFilterMenu.Items.OfType<MenuFlyoutItem>())
        {
            var selected = item.Tag?.ToString() == _stateFilter.ToString().ToLowerInvariant();
            item.Icon = selected ? new FontIcon { Glyph = "\uE73E" } : null;
        }
    }

    private void StateFilterItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;
        _stateFilter = tag switch
        {
            "all" => StateFilter.All,
            "closed" => StateFilter.Closed,
            _ => StateFilter.Active,
        };
        StateFilterText.Text = _stateFilter switch
        {
            StateFilter.All => Loc.Get("All", "全部"),
            StateFilter.Closed => Loc.Get("Closed", "已关闭"),
            _ => Loc.Get("Active", "活动"),
        };
        Render(App.State.Core.Connections);
    }

    private void SortMenu_Opening(object? sender, object e)
    {
        foreach (var item in SortMenu.Items.OfType<MenuFlyoutItem>())
        {
            var selected = item.Tag?.ToString() == _sort.ToString().ToLowerInvariant();
            item.Icon = selected ? new FontIcon { Glyph = "\uE73E" } : null;
        }
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag }) return;
        _sort = tag switch
        {
            "traffic" => ConnectionSort.Traffic,
            "trafficTotal" => ConnectionSort.TrafficTotal,
            _ => ConnectionSort.Date,
        };
        SortText.Text = _sort switch
        {
            ConnectionSort.Traffic => Loc.Get("Traffic", "流量"),
            ConnectionSort.TrafficTotal => Loc.Get("Traffic Total", "总流量"),
            _ => Loc.Get("Date", "日期"),
        };
        Render(App.State.Core.Connections);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Render(App.State.Core.Connections);

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || sender is not Button button) return;
        _applying = true;
        await BusyButton.RunAsync(button, async () =>
        {
            try
            {
                await App.State.Core.CloseConnectionsAsync();
            }
            catch (Exception error)
            {
                ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to close connections", "无法关闭连接"), error.Message);
            }
            finally
            {
                _applying = false;
            }
        });
    }

    // ----- Rendering -----

    private readonly System.Collections.ObjectModel.ObservableCollection<ConnectionRow> _rows = new();

    private void Render(IReadOnlyList<CoreConnection> connections)
    {
        var filtered = FilterAndSort(connections).ToList();

        // The connection stream refreshes once per second. Reconcile the
        // existing rows in place (update rates, add/remove, minimal moves)
        // instead of rebinding — full rebinds re-create every container and
        // make scrolling stutter.
        var existing = new Dictionary<string, ConnectionRow>(_rows.Count);
        foreach (var row in _rows) existing[row.Connection.Id] = row;

        var desired = new List<ConnectionRow>(filtered.Count);
        foreach (var connection in filtered)
        {
            if (existing.TryGetValue(connection.Id, out var row))
            {
                row.Update(connection);
                desired.Add(row);
                existing.Remove(connection.Id);
            }
            else
            {
                desired.Add(new ConnectionRow(connection));
            }
        }
        foreach (var stale in existing.Values)
        {
            _rows.Remove(stale);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            var currentIndex = _rows.IndexOf(row);
            if (currentIndex < 0)
            {
                _rows.Insert(i, row);
            }
            else if (currentIndex != i)
            {
                _rows.Move(currentIndex, i);
            }
        }

        if (!App.State.Core.State.IsRunning)
        {
            ShowStatus(InfoBarSeverity.Informational,
                Loc.Get("Core is stopped", "核心已停止"),
                Loc.Get("Start a profile to receive connections from the core service.", "启动配置以接收来自核心服务的连接。"));
        }
        else if (filtered.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational,
                Loc.Get("No connections", "没有连接"),
                Loc.Get("No connections match the current filter.", "没有符合当前过滤条件的连接。"));
        }
        else
        {
            StatusBar.IsOpen = false;
        }
    }

    private IEnumerable<CoreConnection> FilterAndSort(IReadOnlyList<CoreConnection> connections)
    {
        // SFM: DNS bookkeeping connections are not shown.
        IEnumerable<CoreConnection> query = connections
            .Where(c => !string.Equals(c.OutboundType, "dns", StringComparison.OrdinalIgnoreCase));

        query = _stateFilter switch
        {
            StateFilter.All => query,
            StateFilter.Closed => query.Where(c => c.ClosedAt > 0),
            _ => query.Where(c => c.ClosedAt == 0),
        };

        var search = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(c => Matches(c, search));
        }

        return _sort switch
        {
            ConnectionSort.Traffic => query.OrderByDescending(c => c.Uplink + c.Downlink),
            ConnectionSort.TrafficTotal => query.OrderByDescending(c => c.UplinkTotal + c.DownlinkTotal),
            _ => query.OrderByDescending(c => c.CreatedAt),
        };
    }

    /// <summary>
    /// SFA search syntax: space separated terms; `key:value` matches a specific
    /// field, bare words match destination/domain/outbound/rule.
    /// </summary>
    private static bool Matches(CoreConnection c, string search)
    {
        foreach (var term in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = term.IndexOf(':');
            if (separator > 0)
            {
                var key = term[..separator];
                var value = term[(separator + 1)..];
                var field = key switch
                {
                    "network" => c.Network,
                    "inbound" => c.Inbound,
                    "inbound.type" => c.InboundType,
                    "source" => c.Source,
                    "destination" => c.Destination,
                    "outbound" => c.Outbound,
                    "outbound.type" => c.OutboundType,
                    "rule" => c.Rule,
                    "protocol" => c.Protocol,
                    "user" => c.User,
                    "chain" => string.Join("/", c.Chain),
                    _ => null,
                };
                if (field is null || !field.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                var hit = Contains(c.Destination, term) || Contains(c.Domain, term) ||
                          Contains(c.Outbound, term) || Contains(c.Rule, term) ||
                          Contains(c.DisplayDestination, term);
                if (!hit) return false;
            }
        }
        return true;

        static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Item interactions -----

    private async void Connection_Click(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ConnectionRow row) return;
        await ShowDetailsAsync(row.Connection);
    }

    private void Connection_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConnectionRow row } element) return;
        var flyout = new MenuFlyout();
        if (row.Connection.ClosedAt == 0)
        {
            var closeItem = new MenuFlyoutItem
            {
                Text = Loc.Get("Close", "关闭"),
                Icon = new FontIcon { Glyph = "\uE74D" },
            };
            closeItem.Click += async (_, _) => await CloseConnectionAsync(row.Connection.Id);
            flyout.Items.Add(closeItem);
        }
        var detailsItem = new MenuFlyoutItem
        {
            Text = Loc.Get("Details", "详细信息"),
            Icon = new FontIcon { Glyph = "\uE946" },
        };
        detailsItem.Click += async (_, _) => await ShowDetailsAsync(row.Connection);
        flyout.Items.Add(detailsItem);
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private async Task CloseConnectionAsync(string id)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            await App.State.Core.CloseConnectionAsync(id);
        }
        catch (Exception error)
        {
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Unable to close connection", "无法关闭连接"), error.Message);
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ShowDetailsAsync(CoreConnection c)
    {
        var panel = new StackPanel { Spacing = 16, MinWidth = 420 };

        var basic = new List<(string, string)>
        {
            (Loc.Get("State", "状态"), c.ClosedAt == 0 ? Loc.Get("Active", "活动") : Loc.Get("Closed", "已关闭")),
            (Loc.Get("Created At", "创建时间"), FormatTime(c.CreatedAt)),
        };
        if (c.ClosedAt > 0)
        {
            basic.Add((Loc.Get("Closed At", "关闭时间"), FormatTime(c.ClosedAt)));
            basic.Add((Loc.Get("Duration", "时长"), FormatDuration(c.ClosedAt - c.CreatedAt)));
        }
        basic.Add((Loc.Get("Uplink", "上行"), FormatBytes(c.UplinkTotal)));
        basic.Add((Loc.Get("Downlink", "下行"), FormatBytes(c.DownlinkTotal)));
        panel.Children.Add(BuildSection(Loc.Get("Basic Information", "基本信息"), basic));

        var metadata = new List<(string, string)>
        {
            (Loc.Get("Inbound", "入站"), c.Inbound),
            (Loc.Get("Inbound Type", "入站类型"), c.InboundType),
            (Loc.Get("IP Version", "IP 版本"), c.IpVersion == 6 ? "IPv6" : "IPv4"),
            (Loc.Get("Network", "网络"), c.Network.ToUpperInvariant()),
            (Loc.Get("Source", "来源"), c.Source),
            (Loc.Get("Destination", "目标"), c.Destination),
        };
        if (!string.IsNullOrWhiteSpace(c.Domain)) metadata.Add((Loc.Get("Domain", "域名"), c.Domain));
        if (!string.IsNullOrWhiteSpace(c.Protocol)) metadata.Add((Loc.Get("Protocol", "协议"), c.Protocol));
        if (!string.IsNullOrWhiteSpace(c.User)) metadata.Add((Loc.Get("User", "用户"), c.User));
        if (!string.IsNullOrWhiteSpace(c.FromOutbound)) metadata.Add((Loc.Get("From Outbound", "来源出站"), c.FromOutbound));
        if (!string.IsNullOrWhiteSpace(c.Rule)) metadata.Add((Loc.Get("Match Rule", "匹配规则"), c.Rule));
        metadata.Add((Loc.Get("Outbound", "出站"), c.Outbound));
        metadata.Add((Loc.Get("Outbound Type", "出站类型"), c.OutboundType));
        if (c.Chain.Count > 1) metadata.Add((Loc.Get("Chain", "链"), string.Join(" → ", c.Chain)));
        panel.Children.Add(BuildSection(Loc.Get("Metadata", "元数据"), metadata));

        if (c.ProcessInfo is { } process)
        {
            var rows = new List<(string, string)>
            {
                (Loc.Get("Process ID", "进程 ID"), process.ProcessId.ToString()),
                (Loc.Get("Process Path", "进程路径"), process.ProcessPath),
            };
            if (!string.IsNullOrWhiteSpace(process.UserName)) rows.Add((Loc.Get("User Name", "用户名"), process.UserName));
            if (process.PackageNames.Count > 0) rows.Add((Loc.Get("Package Name", "包名"), string.Join(", ", process.PackageNames)));
            panel.Children.Add(BuildSection(Loc.Get("Process Information", "进程信息"), rows));
        }

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(c.DisplayDestination) ? c.Destination : c.DisplayDestination,
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            CloseButtonText = Loc.Get("Close", "关闭"),
            XamlRoot = XamlRoot,
        };
        if (c.ClosedAt == 0)
        {
            dialog.PrimaryButtonText = Loc.Get("Close connection", "关闭连接");
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await CloseConnectionAsync(c.Id);
        }
    }

    private static UIElement BuildSection(string title, List<(string Label, string Value)> rows)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Padding = new Thickness(16, 12, 16, 12),
        };
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = rows[i].Label,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 13,
            };
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var value = new TextBlock
            {
                Text = rows[i].Value,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalTextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        card.Child = grid;
        section.Children.Add(card);
        return section;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
    }

    private static string FormatTime(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0) return "-";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString("HH:mm:ss");
        }
        catch
        {
            return "-";
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds < 0) return "-";
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    internal static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024d:F1} KB",
        < 1024 * 1024 * 1024 => $"{value / 1024d / 1024d:F1} MB",
        _ => $"{value / 1024d / 1024d / 1024d:F2} GB",
    };

    /// <summary>
    /// Mutable row: per-second stream updates only refresh the rate/state
    /// fields in place so existing item containers are reused.
    /// </summary>
    /// <summary>
    /// Mutable row: per-second stream updates only refresh the rate/state
    /// fields in place so existing item containers are reused.
    /// </summary>
    private sealed partial class ConnectionRow : ObservableObject
    {
        [ObservableProperty]
        public partial string StateText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial Brush StateBrush { get; set; } = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        [ObservableProperty]
        public partial string UpText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DownText { get; set; } = string.Empty;

        public ConnectionRow(CoreConnection connection)
        {
            Connection = connection;
            var destination = string.IsNullOrWhiteSpace(connection.DisplayDestination)
                ? connection.Destination
                : connection.DisplayDestination;
            Title = $"{connection.Network.ToUpperInvariant()} {destination}";
            InboundText = $"{connection.InboundType}/{connection.Inbound}";
            ChainText = connection.Chain.Count > 0 ? connection.Chain[0] : connection.Outbound;
            Update(connection);
        }

        public CoreConnection Connection { get; private set; }
        public string Title { get; }
        public string InboundText { get; }
        public string ChainText { get; }

        public void Update(CoreConnection c)
        {
            Connection = c;
            var active = c.ClosedAt == 0;
            StateText = active ? Loc.Get("Active", "活动") : Loc.Get("Closed", "已关闭");
            StateBrush = (Brush)Application.Current.Resources[active ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
            UpText = active
                ? $"↑ {FormatBytes(c.Uplink)}/s | {FormatBytes(c.UplinkTotal)}"
                : $"↑ {FormatBytes(c.UplinkTotal)}";
            DownText = active
                ? $"↓ {FormatBytes(c.Downlink)}/s | {FormatBytes(c.DownlinkTotal)}"
                : $"↓ {FormatBytes(c.DownlinkTotal)}";
        }
    }
}
