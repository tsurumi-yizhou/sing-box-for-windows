using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SFW.Controls;

/// <summary>
/// A vertical stack of detail sections, used as a dialog body. The layout lives in
/// SectionStack.xaml; callers only add the sections they have data for.
/// </summary>
public sealed partial class SectionStack : UserControl
{
    public SectionStack() => InitializeComponent();

    /// <summary>Appends one section to the dialog body.</summary>
    public void Add(UIElement section) => Sections.Children.Add(section);
}
