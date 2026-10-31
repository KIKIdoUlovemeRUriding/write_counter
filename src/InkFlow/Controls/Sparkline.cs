using System.Windows;
using System.Windows.Media;

namespace InkFlow.Controls;

/// <summary>A small smooth sparkline used for the live rhythm graph.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 8 || height <= 8)
        {
            return;
        }

        var source = Values?.ToArray();
        if (source is null || source.Length < 2)
        {
            return;
        }

        var max = Math.Max(1, source.Max());
        var points = new Point[source.Length];
        var padding = StrokeThickness / 2 + 1;
        var usableWidth = width - padding * 2;
        var usableHeight = height - padding * 2;

        for (var i = 0; i < source.Length; i++)
        {
            var x = padding + usableWidth * i / (source.Length - 1.0);
            var normalized = Math.Clamp(source[i] / max, 0, 1);
            var y = height - padding - normalized * usableHeight;
            points[i] = new Point(x, y);
        }

        var line = BuildCurve(points, false);
        if (Fill is not null)
        {
            var area = BuildCurve(points, true, width, height);
            dc.DrawGeometry(Fill, null, area);
        }

        var pen = new Pen(Stroke, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, pen, line);

        var last = points[^1];
        var dotRadius = Math.Max(2, StrokeThickness + 0.5);
        dc.DrawEllipse(Stroke, null, last, dotRadius, dotRadius);
    }

    private StreamGeometry BuildCurve(IReadOnlyList<Point> points, bool area, double width = 0, double height = 0)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (area)
            {
                context.BeginFigure(new Point(points[0].X, height), true, true);
                context.LineTo(points[0], true, false);
            }
            else
            {
                context.BeginFigure(points[0], false, false);
            }

            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = points[Math.Max(i - 1, 0)];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = points[Math.Min(i + 2, points.Count - 1)];

                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                context.BezierTo(c1, c2, p2, true, false);
            }

            if (area)
            {
                context.LineTo(new Point(points[^1].X, height), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 56 : availableSize.Height;
        return new Size(width, height);
    }
}
