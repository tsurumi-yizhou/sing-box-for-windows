using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
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
            StatusBar.Title = Loc.Get("Core is stopped", "核心已停止");
            StatusBar.Message = Loc.Get("Start a profile to receive groups from the core service.", "启动配置以接收来自核心服务的分组。");
        }
        else if (groups.Count == 0)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = Loc.Get("No selectable groups", "无可用分组");
            StatusBar.Message = Loc.Get("The active configuration did not report any selector groups.", "当前配置未报告任何选择器分组。");
        }
        else
        {
            StatusBar.IsOpen = false;
        }
    }

    private bool IsExpanded(CoreProxyGroup group) =>
        _expandOverrides.TryGetValue(group.Tag, out var expanded) ? expanded : group.IsExpand;

    private UIElement BuildGroupCard(CoreProxyGroup group)
    {
        var expanded = IsExpanded(group);

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Padding = new Thickness(0),
        };
        var layout = new StackPanel();
        card.Child = layout;

        layout.Children.Add(BuildGroupHeader(group, expanded));
        layout.Children.Add(expanded ? BuildChipGrid(group) : BuildDotGrid(group));
        return card;
    }

    private UIElement BuildGroupHeader(CoreProxyGroup group, bool expanded)
    {
        var header = new Grid
        {
            Padding = new Thickness(16, 12, 8, 10),
            ColumnSpacing = 8,
            Background = new SolidColorBrush(Colors.Transparent), // needed for hit-testing
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titlePanel.Children.Add(new TextBlock
        {
            Text = group.Tag,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = group.Type,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(titlePanel);

        // Count capsule (SFM: caption monospaced on a neutral capsule).
        var countCapsule = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = group.Items.Count.ToString(),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            },
        };
        Grid.SetColumn(countCapsule, 1);
        header.Children.Add(countCapsule);

        // Group URL test (SFA/SFM: bolt/speed icon, progress while testing).
        FrameworkElement testContent;
        var testing = _testingGroups.Contains(group.Tag);
        if (testing)
        {
            testContent = new ProgressRing { Width = 16, Height = 16, IsIndeterminate = true, IsActive = true };
        }
        else
        {
            testContent = new FontIcon { Glyph = "\uE945", FontSize = 16 };
        }
        var testButton = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
            Content = testContent,
            IsEnabled = !testing,
            Tag = group.Tag,
            Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(testButton, Loc.Get("URL test this group", "测试该分组延迟"));
        testButton.Click += GroupUrlTest_Click;
        Grid.SetColumn(testButton, 2);
        header.Children.Add(testButton);

        // Expand chevron.
        var chevronButton = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButtonStyle"],
            Content = new FontIcon { Glyph = expanded ? "\uE70E" : "\uE70D", FontSize = 14 },
            Tag = group.Tag,
            Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(chevronButton, expanded ? Loc.Get("Collapse", "收起") : Loc.Get("Expand", "展开"));
        chevronButton.Click += ToggleExpand_Click;
        Grid.SetColumn(chevronButton, 3);
        header.Children.Add(chevronButton);

        // Tapping the header toggles expansion, like SFA/SFM.
        header.Tapped += Header_Tapped;
        header.Tag = group.Tag;
        return header;
    }

    private UIElement BuildChipGrid(CoreProxyGroup group)
    {
        var elements = new List<FrameworkElement>();
        foreach (var item in group.Items)
        {
            elements.Add(BuildChip(group, item));
        }

        // Approximates SwiftUI's LazyVGrid(adaptive minimum: 170, spacing: 10).
        var grid = new AdaptiveGrid(elements, 170, 10)
        {
            Margin = new Thickness(16, 2, 16, 14),
        };
        return grid;
    }

    private Border BuildChip(CoreProxyGroup group, CoreProxyGroupItem item)
    {
        var selected = string.Equals(item.Tag, group.Selected, StringComparison.Ordinal);
        var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var accentColor = accent.Color;

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            Background = selected
                ? new SolidColorBrush(Color.FromArgb(31, accentColor.R, accentColor.G, accentColor.B))
                : (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = selected
                ? accent
                : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Tag = (group.Tag, item.Tag, group.Selectable),
        };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = item.Tag,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var bottomRow = new Grid();
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomRow.Children.Add(new TextBlock
        {
            Text = item.Type,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (_testingItems.Contains(item.Tag))
        {
            // Immediate feedback while a single-item URL test is running.
            var pendingText = new TextBlock
            {
                Text = "…",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pendingText, 1);
            bottomRow.Children.Add(pendingText);
        }
        else if (item.UrlTestDelay > 0)
        {
            var delayText = new TextBlock
            {
                Text = $"{item.UrlTestDelay}ms",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = DelayBrush(item.UrlTestDelay),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(delayText, 1);
            bottomRow.Children.Add(delayText);
        }
        content.Children.Add(bottomRow);
        chip.Child = content;

        if (group.Selectable)
        {
            chip.Tapped += Chip_Tapped;
        }
        chip.RightTapped += Chip_RightTapped;
        return chip;
    }

    private UIElement BuildDotGrid(CoreProxyGroup group)
    {
        var dots = new DotGridPanel
        {
            Margin = new Thickness(16, 6, 16, 16),
            Background = new SolidColorBrush(Colors.Transparent), // needed for hit-testing
        };
        var selectedColor = Colors.White;
        foreach (var item in group.Items)
        {
            var cell = new Grid { Width = 11, Height = 11 };
            cell.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                Fill = DelayBrush(item.UrlTestDelay),
            });
            if (string.Equals(item.Tag, group.Selected, StringComparison.Ordinal))
            {
                cell.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = new SolidColorBrush(selectedColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            dots.Children.Add(cell);
        }

        // Tapping the dot grid expands the group, like SFM.
        dots.Tag = group.Tag;
        dots.Tapped += Header_Tapped;
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

    private async void Header_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        await ToggleExpandAsync(tag);
    }

    private async void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        await ToggleExpandAsync(tag);
    }

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

    private async void Chip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_applying || sender is not FrameworkElement chip ||
            chip.Tag is not (string groupTag, string itemTag, bool selectable)) return;
        if (!selectable) return;

        _applying = true;
        try
        {
            await App.State.Core.SelectOutboundAsync(groupTag, itemTag);
        }
        catch (Exception error)
        {
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = Loc.Get("Unable to change selection", "无法更改选择");
            StatusBar.Message = error.Message;
        }
        finally
        {
            _applying = false;
        }
    }

    private void Chip_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement chip) return;
        var flyout = new MenuFlyout();
        var testItem = new MenuFlyoutItem
        {
            Text = Loc.Get("Test", "测试"),
            Icon = new FontIcon { Glyph = "\uE945" },
        };
        testItem.Click += async (_, _) =>
        {
            var (groupTag, itemTag, _) = ((string, string, bool))chip.Tag;
            await UrlTestItemAsync(itemTag);
        };
        flyout.Items.Add(testItem);
        flyout.ShowAt(chip, new FlyoutShowOptions { Position = e.GetPosition(chip) });
        e.Handled = true;
    }

    private async void GroupUrlTest_Click(object sender, RoutedEventArgs e)
    {
        if (_applying || sender is not FrameworkElement { Tag: string tag }) return;
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
            StatusBar.Title = Loc.Get("Unable to start URLTest", "无法开始 URLTest");
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
            StatusBar.Title = Loc.Get("Unable to start URLTest", "无法开始 URLTest");
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
