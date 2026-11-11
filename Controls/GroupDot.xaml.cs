using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SFW.Controls;

/// <summary>
/// One dot of a collapsed selector group. The shape markup lives in GroupDot.xaml;
/// the page only supplies the colour for the last URL-test delay and whether this
/// outbound is the selected one.
/// </summary>
public sealed partial class GroupDot : UserControl
{
    public GroupDot() => InitializeComponent();

    public void Apply(Brush fill, bool selected)
    {
        DotRect.Fill = fill;
        SelectedDot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
