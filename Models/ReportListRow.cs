using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml.Media;
using SFW.Services.Core;

namespace SFW.Models;

/// <summary>
/// One row of a crash / OOM / power report list. The row markup lives in
/// Controls/ReportList.xaml; this type only holds what the template binds to.
/// </summary>
public sealed class ReportListRow(CoreReportEntry report, string title, string kindCaption)
{
    private static readonly Brush TransparentDot = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public CoreReportEntry Report { get; } = report;

    public string Title { get; } = title;

    public string KindCaption { get; } = kindCaption;

    /// <summary>SFM ReportLabel: unread reports are bold, read ones are not.</summary>
    public FontWeight TitleWeight => Report.IsRead ? FontWeights.Normal : FontWeights.SemiBold;

    /// <summary>Unread marker; a transparent brush keeps the column width stable.</summary>
    public Brush DotBrush => Report.IsRead
        ? TransparentDot
        : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
}
