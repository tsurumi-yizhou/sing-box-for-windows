using SFW.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SFW.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

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
            item.Icon = selected ? MenuCheckIcon() : null;
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
            StateFilter.All => Loc.Get("Connections.All"),
            StateFilter.Closed => Loc.Get("Connections.Closed"),
            _ => Loc.Get("Connections.Active"),
        };
        Render(App.State.Core.Connections);
    }

    private void SortMenu_Opening(object? sender, object e)
    {
        foreach (var item in SortMenu.Items.OfType<MenuFlyoutItem>())
        {
            var selected = item.Tag?.ToString() == _sort.ToString().ToLowerInvariant();
            item.Icon = selected ? MenuCheckIcon() : null;
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
            ConnectionSort.Traffic => Loc.Get("Connections.Traffic"),
            ConnectionSort.TrafficTotal => Loc.Get("Connections.TrafficTotal"),
            _ => Loc.Get("Connections.Date"),
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
                ShowStatus(InfoBarSeverity.Error, Loc.Get("Connections.UnableToCloseConnections"), error.Message);
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
                Loc.Get("Connections.CoreIsStopped"),
                Loc.Get("Connections.StartAProfileToReceiveConnectionsFromTheCoreService"));
        }
        else if (filtered.Count == 0)
        {
            // Upstream 5bbd923: the empty state names the filter instead of
            // showing one generic sentence. A search query wins over the filter.
            var search = SearchBox?.Text?.Trim();
            var title = !string.IsNullOrEmpty(search)
                ? Loc.Get("Connections.NoMatchingConnections")
                : _stateFilter switch
                {
                    StateFilter.Active => Loc.Get("Connections.NoActiveConnections"),
                    StateFilter.Closed => Loc.Get("Connections.NoClosedConnections"),
                    _ => Loc.Get("Connections.NoConnections"),
                };
            ShowStatus(InfoBarSeverity.Informational, title, string.Empty);
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

    // Row menu items are declared in XAML (ContextFlyout); they only carry the
    // connection id, so the handler looks the live row up again.
    private async void ConnectionCloseItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        await CloseConnectionAsync(id);
    }

    private async void ConnectionDetailsItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        if (FindRow(id)?.Connection is { } connection) await ShowDetailsAsync(connection);
    }

    private ConnectionRow? FindRow(string id) =>
        (ConnectionsList.ItemsSource as System.Collections.IEnumerable)?
            .OfType<ConnectionRow>().FirstOrDefault(row => row.Id == id);

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
            ShowStatus(InfoBarSeverity.Error, Loc.Get("Connections.UnableToCloseConnection"), error.Message);
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task ShowDetailsAsync(CoreConnection c)
    {
        var panel = new SectionStack();

        var basic = new List<(string, string)>
        {
            (Loc.Get("Connections.State"), c.ClosedAt == 0 ? Loc.Get("Connections.Active") : Loc.Get("Connections.Closed")),
            (Loc.Get("Connections.CreatedAt"), FormatTime(c.CreatedAt)),
        };
        if (c.ClosedAt > 0)
        {
            basic.Add((Loc.Get("Connections.ClosedAt"), FormatTime(c.ClosedAt)));
            basic.Add((Loc.Get("Connections.Duration"), FormatDuration(c.ClosedAt - c.CreatedAt)));
        }
        basic.Add((Loc.Get("Connections.Uplink"), FormatBytes(c.UplinkTotal)));
        basic.Add((Loc.Get("Connections.Downlink"), FormatBytes(c.DownlinkTotal)));
        panel.Add(new DetailSection(Loc.Get("Connections.BasicInformation"), basic));

        var metadata = new List<(string, string)>
        {
            (Loc.Get("Connections.Inbound"), c.Inbound),
            (Loc.Get("Connections.InboundType"), c.InboundType),
            (Loc.Get("Connections.IPVersion"), c.IpVersion == 6 ? "IPv6" : "IPv4"),
            (Loc.Get("Connections.Network"), c.Network.ToUpperInvariant()),
            (Loc.Get("Connections.Source"), c.Source),
            (Loc.Get("Connections.Destination"), c.Destination),
        };
        if (!string.IsNullOrWhiteSpace(c.Domain)) metadata.Add((Loc.Get("Connections.Domain"), c.Domain));
        if (!string.IsNullOrWhiteSpace(c.Protocol)) metadata.Add((Loc.Get("Connections.Protocol"), c.Protocol));
        if (!string.IsNullOrWhiteSpace(c.User)) metadata.Add((Loc.Get("Connections.User"), c.User));
        if (!string.IsNullOrWhiteSpace(c.FromOutbound)) metadata.Add((Loc.Get("Connections.FromOutbound"), c.FromOutbound));
        if (!string.IsNullOrWhiteSpace(c.Rule)) metadata.Add((Loc.Get("Connections.MatchRule"), c.Rule));
        metadata.Add((Loc.Get("Connections.Outbound"), c.Outbound));
        metadata.Add((Loc.Get("Connections.OutboundType"), c.OutboundType));
        if (c.Chain.Count > 1) metadata.Add((Loc.Get("Connections.Chain"), string.Join(" → ", c.Chain)));
        panel.Add(new DetailSection(Loc.Get("Connections.Metadata"), metadata));

        if (c.ProcessInfo is { } process)
        {
            var rows = new List<(string, string)>
            {
                (Loc.Get("Connections.ProcessID"), process.ProcessId.ToString()),
                (Loc.Get("Connections.ProcessPath"), process.ProcessPath),
            };
            if (!string.IsNullOrWhiteSpace(process.UserName)) rows.Add((Loc.Get("Connections.UserName"), process.UserName));
            if (process.PackageNames.Count > 0) rows.Add((Loc.Get("Connections.PackageName"), string.Join(", ", process.PackageNames)));
            panel.Add(new DetailSection(Loc.Get("Connections.ProcessInformation"), rows));
        }

        var dialog = new Dialogs.DetailDialog(
            string.IsNullOrWhiteSpace(c.DisplayDestination) ? c.Destination : c.DisplayDestination,
            panel,
            "Connections.Close",
            c.ClosedAt == 0 ? "Connections.CloseConnection" : null)
        { XamlRoot = XamlRoot };
        // Closing a live connection is the destructive action: keep Close as the
        // default button even though a primary button is present.
        if (c.ClosedAt == 0) dialog.DefaultButton = ContentDialogButton.Close;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await CloseConnectionAsync(c.Id);
        }
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
    /// The check mark the filter menu draws on its active item. It comes from a XAML
    /// template because each item needs its own instance: an icon element can only
    /// have one parent, and the menu leaves the slot empty for unmarked items.
    /// </summary>
    private FontIcon? MenuCheckIcon() =>
        ((DataTemplate)Resources["MenuCheckIconTemplate"]).LoadContent() as FontIcon;

}
