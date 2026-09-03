using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace sing_box_for_windows.Controls;

/// <summary>
/// Rolling traffic line chart in the spirit of the SFA/SFM dashboard charts:
/// a smoothed line over a soft area fill, fed one bytes-per-second sample at a time.
/// </summary>
public sealed partial class TrafficChart : UserControl
{
    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(TrafficChart),
            new PropertyMetadata(null, (d, e) => ((TrafficChart)d).LinePath.Stroke = e.NewValue as Brush));

    public static readonly DependencyProperty AreaBrushProperty =
        DependencyProperty.Register(nameof(AreaBrush), typeof(Brush), typeof(TrafficChart),
            new PropertyMetadata(null, (d, e) => ((TrafficChart)d).AreaPath.Fill = e.NewValue as Brush));

    private const int Capacity = 60;
    private readonly List<double> _samples = new(Capacity);

    public TrafficChart()
    {
        InitializeComponent();
        // SFA/SFM grid: secondary text color, dashed 5/5.
        if (Application.Current.Resources["TextFillColorSecondaryBrush"] is Brush gridBrush)
        {
            GridPath.Stroke = gridBrush;
        }
        GridPath.StrokeDashArray = new DoubleCollection { 5, 5 };
        SizeChanged += (_, _) => Redraw();
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush AreaBrush
    {
        get => (Brush)GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    public void AddSample(double value)
    {
        _samples.Add(Math.Max(0, value));
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
        Redraw();
    }

    public void Clear()
    {
        _samples.Clear();
        Redraw();
    }

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2)
        {
            LinePath.Data = null;
            AreaPath.Data = null;
            GridPath.Data = null;
            return;
        }

        // 4 dashed horizontal grid lines (including the edges), like SFA/SFM.
        var grid = new PathGeometry();
        for (var i = 0; i <= 3; i++)
        {
            var y = height * i / 3;
            var figure = new PathFigure { StartPoint = new Point(0, y) };
            figure.Segments.Add(new LineSegment { Point = new Point(width, y) });
            grid.Figures.Add(figure);
        }
        GridPath.Data = grid;

        if (_samples.Count < 2)
        {
            LinePath.Data = null;
            AreaPath.Data = null;
            return;
        }

        var max = 0d;
        foreach (var sample in _samples) max = Math.Max(max, sample);
        if (max <= 0) max = 1;
        max *= 1.15; // headroom so peaks do not touch the top edge

        var step = width / (Capacity - 1);
        var points = new List<Point>(_samples.Count);
        for (var i = 0; i < _samples.Count; i++)
        {
            var x = width - (_samples.Count - 1 - i) * step;
            var y = height - _samples[i] / max * height;
            points.Add(new Point(x, Math.Clamp(y, 0, height)));
        }

        var line = SmoothedGeometry(points, closeToBaseline: false, height);
        var area = SmoothedGeometry(points, closeToBaseline: true, height);
        LinePath.Data = line;
        AreaPath.Data = area;
    }

    /// <summary>
    /// Builds a smoothed path through the points using quadratic segments
    /// between midpoints, optionally closed along the bottom edge for fills.
    /// </summary>
    private static PathGeometry SmoothedGeometry(IReadOnlyList<Point> points, bool closeToBaseline, double height)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = points[0], IsFilled = closeToBaseline };

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var mid = new Point((previous.X + current.X) / 2, (previous.Y + current.Y) / 2);
            figure.Segments.Add(new QuadraticBezierSegment { Point1 = previous, Point2 = mid });
        }
        figure.Segments.Add(new LineSegment { Point = points[^1] });

        if (closeToBaseline)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, height) });
            figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, height) });
            figure.IsClosed = true;
        }

        geometry.Figures.Add(figure);
        return geometry;
    }
}
