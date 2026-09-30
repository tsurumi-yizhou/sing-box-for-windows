using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SFW.Controls;

/// <summary>
/// Lays out fixed-size dot tiles row by row, fitting as many columns as the
/// available width allows — the collapsed "dot grid" used by SFA/SFM groups.
/// </summary>
public sealed class DotGridPanel : Panel
{
    private const double DotSize = 11;
    private const double DotSpacing = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnCount(availableSize.Width);
        var rows = Children.Count == 0 ? 0 : (Children.Count + columns - 1) / columns;
        var size = new Size(
            columns * (DotSize + DotSpacing) - DotSpacing,
            Math.Max(0, rows * (DotSize + DotSpacing) - DotSpacing));
        foreach (var child in Children)
        {
            child.Measure(new Size(DotSize, DotSize));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width);
        for (var i = 0; i < Children.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            Children[i].Arrange(new Rect(
                column * (DotSize + DotSpacing),
                row * (DotSize + DotSpacing),
                DotSize,
                DotSize));
        }
        return finalSize;
    }

    private static int ColumnCount(double width) =>
        Math.Max(1, (int)Math.Floor((width + DotSpacing) / (DotSize + DotSpacing)));
}
