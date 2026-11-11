using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SFW.Controls;

/// <summary>
/// One outbound chip of a selector group. The chip markup lives in GroupChip.xaml;
/// the page only supplies the strings, the selected/delay visuals and the two
/// interactions (select, URL test), so the chip cannot drift from the group card.
/// </summary>
public sealed partial class GroupChip : UserControl
{
    public GroupChip() => InitializeComponent();

    /// <summary>Raised when a selectable chip is tapped.</summary>
    public event EventHandler? Invoked;

    /// <summary>Raised by the context menu's URL-test item.</summary>
    public event EventHandler? TestRequested;

    public string GroupTag { get; private set; } = string.Empty;

    public string ItemTag { get; private set; } = string.Empty;

    public bool Selectable { get; private set; }

    /// <summary>
    /// Applies the per-render state: the selected chip is tinted with the accent,
    /// while a running URL test shows the pending marker instead of its delay.
    /// </summary>
    public void Apply(
        string groupTag, string itemTag, string type, bool selected, bool selectable,
        bool testing, int delay, Brush delayBrush)
    {
        GroupTag = groupTag;
        ItemTag = itemTag;
        Selectable = selectable;

        TagText.Text = itemTag;
        TypeText.Text = type;

        var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var accentColor = accent.Color;
        ChipBorder.Background = selected
            ? new SolidColorBrush(Color.FromArgb(31, accentColor.R, accentColor.G, accentColor.B))
            : (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        ChipBorder.BorderBrush = selected
            ? accent
            : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

        PendingText.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;

        var showDelay = !testing && delay > 0;
        DelayText.Text = $"{delay}ms";
        DelayText.Foreground = delayBrush;
        DelayText.Visibility = showDelay ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Chip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // A non-selectable group is informational only; the context menu still works.
        if (!Selectable) return;
        Invoked?.Invoke(this, EventArgs.Empty);
    }

    private void TestItem_Click(object sender, RoutedEventArgs e) =>
        TestRequested?.Invoke(this, EventArgs.Empty);
}
