using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SFW.Models;

namespace SFW.Controls;

/// <summary>
/// The report list shared by the crash, OOM and power report pages. The row
/// markup lives in ReportList.xaml; pages only hand over the rows.
/// </summary>
public sealed partial class ReportList : UserControl
{
    public ReportList() => InitializeComponent();

    /// <summary>Raised when a report row is activated.</summary>
    public event EventHandler<ReportListRow>? ReportInvoked;

    public void SetRows(IEnumerable<ReportListRow> rows) => Items.ItemsSource = rows.ToList();

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ReportListRow row) return;
        ReportInvoked?.Invoke(this, row);
    }
}
