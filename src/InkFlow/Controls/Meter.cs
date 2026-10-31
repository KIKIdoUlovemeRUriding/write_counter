using System.Windows;
using System.Windows.Media;

namespace InkFlow.Controls;

/// <summary>A flat rounded progress bar that renders deterministically (unlike the stock ProgressBar template).</summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumFillProperty = DependencyProperty.Register(
        nameof(MinimumFill), typeof(double), typeof(Meter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double MinimumFill
    {
        get => (double)GetValue(MinimumFillProperty);
        set => SetValue(MinimumFillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
        {
            return;
        }

        var fillWidth = Math.Max(MinimumFill, width * value);
        fillWidth = Math.Min(width, Math.Max(height, fillWidth));
        var fillRadius = Math.Min(radius, fillWidth / 2);
        dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, fillWidth, height), fillRadius, fillRadius);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 6 : availableSize.Height;
        return new Size(width, height);
    }
}
