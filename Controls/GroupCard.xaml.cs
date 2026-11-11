using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SFW.Services;
using SFW.Services.Core;

namespace SFW.Controls;

/// <summary>
/// One selector-group card. The card markup lives in GroupCard.xaml; the page adds
/// the chip grid or dot grid because it owns the group state and the core calls
/// behind both.
/// </summary>
public sealed partial class GroupCard : UserControl
{
    public GroupCard() => InitializeComponent();

    /// <summary>Raised by the header's URL-test action.</summary>
    public event EventHandler? UrlTestRequested;

    /// <summary>Raised by the chevron action.</summary>
    public event EventHandler? ExpandToggleRequested;

    /// <summary>Raised when the header itself is tapped, like SFA/SFM.</summary>
    public event EventHandler? HeaderTapped;

    public string GroupTag { get; private set; } = string.Empty;

    /// <summary>
    /// Applies the header data. While a group URL test runs the bolt glyph is
    /// replaced by a progress ring and the action is disabled, which is how the
    /// group card showed pending work before the extraction.
    /// </summary>
    public void Apply(CoreProxyGroup group, bool expanded, bool testing)
    {
        GroupTag = group.Tag;
        TagText.Text = group.Tag;
        TypeText.Text = group.Type;
        CountText.Text = group.Items.Count.ToString();

        TestGlyph.Visibility = testing ? Visibility.Collapsed : Visibility.Visible;
        TestRing.IsActive = testing;
        TestRing.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        TestButton.IsEnabled = !testing;
        ToolTipService.SetToolTip(TestButton, Loc.Get("Groups.URLTestThisGroup"));

        ChevronGlyph.Glyph = expanded ? "\uE70E" : "\uE70D";
        ToolTipService.SetToolTip(ChevronButton, Loc.Get(expanded ? "Groups.Collapse" : "Groups.Expand"));
    }

    /// <summary>Sets the card body: the expanded chip grid or the collapsed dot grid.</summary>
    public void SetContent(UIElement content)
    {
        BodyHost.Children.Clear();
        BodyHost.Children.Add(content);
    }

    private void TestButton_Click(object sender, RoutedEventArgs e) =>
        UrlTestRequested?.Invoke(this, EventArgs.Empty);

    private void ChevronButton_Click(object sender, RoutedEventArgs e) =>
        ExpandToggleRequested?.Invoke(this, EventArgs.Empty);

    private void Header_Tapped(object sender, TappedRoutedEventArgs e) =>
        HeaderTapped?.Invoke(this, EventArgs.Empty);
}
