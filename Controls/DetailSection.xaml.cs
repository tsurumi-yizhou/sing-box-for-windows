using Microsoft.UI.Xaml.Controls;
using SFW.Models;

namespace SFW.Controls;

/// <summary>
/// A titled card of label/value rows. The section markup lives in
/// DetailSection.xaml; callers only hand over the title and the rows.
/// </summary>
public sealed partial class DetailSection : UserControl
{
    public DetailSection() => InitializeComponent();

    public DetailSection(string title, IEnumerable<(string Label, string Value)> rows) : this()
    {
        SectionTitle.Text = title;
        Rows.ItemsSource = rows.Select(row => new DetailRow(row.Label, row.Value)).ToList();
    }
}
