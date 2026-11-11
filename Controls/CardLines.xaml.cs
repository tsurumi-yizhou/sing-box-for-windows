using Microsoft.UI.Xaml.Controls;
using SFW.Models;

namespace SFW.Controls;

/// <summary>
/// Plain label/value lines, shared by the Tailscale peer details dialog. The row
/// markup lives in CardLines.xaml; callers only hand over the data.
/// </summary>
public sealed partial class CardLines : UserControl
{
    public CardLines() => InitializeComponent();

    public CardLines(IEnumerable<(string Label, string Value)> rows) : this() =>
        Rows.ItemsSource = rows.Select(row => new DetailRow(row.Label, row.Value)).ToList();
}
