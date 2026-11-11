using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SFW.Controls;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Pages;

public sealed partial class GroupsPage : Page
{
    private readonly Dictionary<string, bool> _expandOverrides = new();
    private readonly HashSet<string> _testingGroups = new();
    private readonly HashSet<string> _testingItems = new();
    private bool _applying;
    private bool _allExpanded = true;

    public GroupsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.Core.GroupsChanged += OnGroupsChanged;
        Render(App.State.Core.Groups);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.State.Core.GroupsChanged -= OnGroupsChanged;

    private void OnGroupsChanged(object? sender, IReadOnlyList<CoreProxyGroup> groups) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            // Fresh results arrived: any pending URL test is done.
            _testingGroups.Clear();
            _testingItems.Clear();
            Render(groups);
        });

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        Render(App.State.Core.Groups);

    private async void ExpandAllButton_Click(object sender, RoutedEventArgs e)
    {
        _allExpanded = !_allExpanded;
        ExpandAllIcon.Glyph = _allExpanded ? "\uE70E" : "\uE70D";
        var groups = App.State.Core.Groups;
        foreach (var group in groups)
        {
            _expandOverrides[group.Tag] = _allExpanded;
        }
        Render(groups);
        foreach (var group in groups)
        {
            try
            {
                await App.State.Core.SetGroupExpandAsync(group.Tag, _allExpanded);
            }
            catch
            {
                // Expansion state is kept locally when the core cannot persist it.
            }
        }
    }

    // ----- Rendering -----

    private void Render(IReadOnlyList<CoreProxyGroup> groups)
    {
        GroupsHost.Children.Clear();
        foreach (var group in groups)
        {
            GroupsHost.Children.Add(BuildGroupCard(group));
        }

        StatusBar.IsOpen = true;
        if (!App.State.Core.State.IsRunning)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = Loc.Get("Groups.CoreIsStopped");
            StatusBar.Message = Loc.Get("Groups.StartAProfileToReceiveGroupsFromTheCoreService");
        }
        else if (groups.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = Loc.Get("Groups.NoSelectableGroups");
            StatusBar.Message = Loc.Get("Groups.TheActiveConfigurationDidNotReportAnySelectorGroups");
        }
        else
        {
            StatusBar.IsOpen = false;
        }
    }

    private bool IsExpanded(CoreProxyGroup group) =>
        _expandOverrides.TryGetValue(group.Tag, out var expanded) ? expanded : group.IsExpand;

    /// <summary>
    /// One group card. The card markup lives in Controls/GroupCard.xaml; this
    /// method only wires the three interactions and picks the body variant.
    /// </summary>
    private UIElement BuildGroupCard(CoreProxyGroup group)
    {
        var expanded = IsExpanded(group);

        var card = new GroupCard();
        card.Apply(group, expanded, _testingGroups.Contains(group.Tag));
        card.UrlTestRequested += async (_, _) => await GroupUrlTestAsync(group.Tag);
        card.ExpandToggleRequested += async (_, _) => await ToggleExpandAsync(group.Tag);
        card.HeaderTapped += async (_, _) => await ToggleExpandAsync(group.Tag);
        card.SetContent(expanded ? BuildChipGrid(group) : BuildDotGrid(group));
        return card;
    }

    private UIElement BuildChipGrid(CoreProxyGroup group)
    {
        var elements = new List<FrameworkElement>();
        foreach (var item in group.Items)
        {
            elements.Add(BuildChip(group, item));
        }

        // Approximates SwiftUI's LazyVGrid(adaptive minimum: 170, spacing: 10).
        // The grid is only a layout panel; the chips are GroupChip controls.
        var grid = new AdaptiveGrid(elements, 170, 10)
        {
            Margin = new Thickness(16, 2, 16, 14),
        };
        return grid;
    }

    private GroupChip BuildChip(CoreProxyGroup group, CoreProxyGroupItem item)
    {
        var selected = string.Equals(item.Tag, group.Selected, StringComparison.Ordinal);

        var chip = new GroupChip();
        chip.Apply(
            group.Tag,
            item.Tag,
            item.Type,
            selected,
            group.Selectable,
            _testingItems.Contains(item.Tag),
            item.UrlTestDelay,
            DelayBrush(item.UrlTestDelay));
        chip.Invoked += async (_, _) => await SelectOutboundAsync(chip.GroupTag, chip.ItemTag);
        chip.TestRequested += async (_, _) => await UrlTestItemAsync(chip.ItemTag);
        return chip;
    }

    private UIElement BuildDotGrid(CoreProxyGroup group)
    {
        var dots = new DotGridPanel
        {
            Margin = new Thickness(16, 6, 16, 16),
            Background = new SolidColorBrush(Colors.Transparent), // needed for hit-testing
        };
        foreach (var item in group.Items)
        {
            var dot = new GroupDot();
            dot.Apply(
                DelayBrush(item.UrlTestDelay),
                string.Equals(item.Tag, group.Selected, StringComparison.Ordinal));
            dots.Children.Add(dot);
        }

        // Tapping the dot grid expands the group, like SFM.
        dots.Tapped += (_, _) => _ = ToggleExpandAsync(group.Tag);
        return dots;
    }

    private static Brush DelayBrush(int delay)
    {
        var key = delay <= 0 ? "ControlFillColorTertiaryBrush"
            : delay < 800 ? "UrlTestGoodBrush"
            : delay < 1500 ? "UrlTestMediumBrush"
            : "UrlTestBadBrush";
        return (Brush)Application.Current.Resources[key];
    }

    // ----- Interactions -----

    private async Task ToggleExpandAsync(string tag)
    {
        var group = App.State.Core.Groups.FirstOrDefault(g => g.Tag == tag);
        if (group is null) return;
        var expanded = !IsExpanded(group);
        _expandOverrides[tag] = expanded;
        Render(App.State.Core.Groups);
        try
        {
            await App.State.Core.SetGroupExpandAsync(tag, expanded);
        }
        catch
        {
            // Expansion state is kept locally when the core cannot persist it.
        }
    }

    /// <summary>
    /// Commits the tapped outbound. Non-selectable groups never reach here: the
    /// chip only raises the event when the group allows selection.
    /// </summary>
    private async Task SelectOutboundAsync(string groupTag, string itemTag)
    {
        if (_applying) return;

        _applying = true;
        try
        {
            await App.State.Core.SelectOutboundAsync(groupTag, itemTag);
        }
        catch (Exception error)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = Loc.Get("Groups.UnableToChangeSelection");
            StatusBar.Message = error.Message;
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task GroupUrlTestAsync(string tag)
    {
        if (_applying) return;
        _applying = true;
        _testingGroups.Add(tag);
        Render(App.State.Core.Groups);
        try
        {
            await App.State.Core.UrlTestAsync(tag);
        }
        catch (Exception error)
        {
            _testingGroups.Remove(tag);
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = Loc.Get("Groups.UnableToStartURLTest");
            StatusBar.Message = error.Message;
            Render(App.State.Core.Groups);
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task UrlTestItemAsync(string itemTag)
    {
        _testingItems.Add(itemTag);
        Render(App.State.Core.Groups);
        try
        {
            await App.State.Core.UrlTestAsync(itemTag);
        }
        catch (Exception error)
        {
            _testingItems.Remove(itemTag);
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = Loc.Get("Groups.UnableToStartURLTest");
            StatusBar.Message = error.Message;
            Render(App.State.Core.Groups);
        }
    }
}

/// <summary>
/// Approximates SwiftUI's LazyVGrid(adaptive minimum): recomputes star-sized
/// columns from the available width so every child gets an equal share.
/// </summary>
internal sealed class AdaptiveGrid : Grid
{
    private readonly List<FrameworkElement> _elements;
    private readonly double _minItemWidth;
    private readonly double _spacing;
    private int _columns;

    public AdaptiveGrid(List<FrameworkElement> elements, double minItemWidth, double spacing)
    {
        _elements = elements;
        _minItemWidth = minItemWidth;
        _spacing = spacing;
        ColumnSpacing = spacing;
        RowSpacing = spacing;
        SizeChanged += (_, _) => Relayout();
        Relayout();
    }

    private void Relayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : 800;
        var columns = Math.Max(1, (int)Math.Floor((width + _spacing) / (_minItemWidth + _spacing)));
        if (columns == _columns && Children.Count == _elements.Count) return;
        _columns = columns;

        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = (_elements.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < _elements.Count; i++)
        {
            SetRow(_elements[i], i / columns);
            SetColumn(_elements[i], i % columns);
            Children.Add(_elements[i]);
        }
    }
}
