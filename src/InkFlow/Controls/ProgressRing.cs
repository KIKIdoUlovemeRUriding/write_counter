using System.Windows;
using System.Windows.Media;

namespace InkFlow.Controls;

/// <summary>A lightweight circular progress indicator drawn with a rounded arc.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 4 || height <= 4)
        {
            return;
        }

        var thickness = Math.Max(1, Thickness);
        var radius = Math.Min(width, height) / 2 - thickness / 2 - 1;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        var trackPen = new Pen(TrackBrush, thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0.0001)
        {
            return;
        }

        var progressPen = new Pen(ProgressBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        if (value >= 0.9999)
        {
            dc.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        var startAngle = -Math.PI / 2;
        var endAngle = startAngle + value * Math.PI * 2;
        var start = new Point(
            center.X + radius * Math.Cos(startAngle),
            center.Y + radius * Math.Sin(startAngle));
        var end = new Point(
            center.X + radius * Math.Cos(endAngle),
            center.Y + radius * Math.Sin(endAngle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, progressPen, geometry);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height;
        return new Size(width, height);
    }
}
