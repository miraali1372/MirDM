using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;

namespace MirDM.Controls;

public class SpeedGraphControl : FrameworkElement
{
    private const int MaxSamples = 60;
    private readonly List<double> _samples = new(MaxSamples);
    private double _peakSpeedMB;

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly Pen BorderPen = new Pen(BorderBrush, 1);
    private static readonly Pen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1)
    {
        DashStyle = DashStyles.Dash
    };
    private static readonly Pen LinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF)), 2);
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly Brush SpeedReadoutBrush = new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF));

    static SpeedGraphControl()
    {
        BgBrush.Freeze();
        BorderBrush.Freeze();
        BorderPen.Freeze();
        GridPen.Freeze();
        LinePen.Freeze();
        TextBrush.Freeze();
        SpeedReadoutBrush.Freeze();
    }

    public SpeedGraphControl()
    {
        ClipToBounds = true;
        MinHeight = 60;
        for (int i = 0; i < MaxSamples; i++)
        {
            _samples.Add(0);
        }
    }

    public void AddSample(double speedBytesPerSec)
    {
        double speedMB = Math.Max(0, speedBytesPerSec / (1024.0 * 1024.0));
        _samples.Add(speedMB);
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        if (speedMB > _peakSpeedMB)
        {
            _peakSpeedMB = speedMB;
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _samples.Clear();
        for (int i = 0; i < MaxSamples; i++)
        {
            _samples.Add(0);
        }
        _peakSpeedMB = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        var fullRect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, fullRect, 4, 4);

        double maxSpeed = Math.Max(5.0, _samples.Max() * 1.2);
        double currentSpeed = _samples.LastOrDefault();

        // Draw horizontal grid lines (3 levels)
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (int g = 1; g <= 3; g++)
        {
            double ratio = (double)g / 3.0;
            double y = h - (ratio * (h - 20)) - 10;
            dc.DrawLine(GridPen, new Point(10, y), new Point(w - 10, y));

            double labelVal = ratio * maxSpeed;
            var labelText = new FormattedText(
                $"{labelVal:0.0} MB/s",
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                9,
                TextBrush,
                dpi);

            dc.DrawText(labelText, new Point(12, y - 11));
        }

        // Build curve geometry
        var streamGeometry = new StreamGeometry();
        var fillGeometry = new StreamGeometry();

        double stepX = (w - 20) / (MaxSamples - 1);
        var points = new Point[_samples.Count];

        for (int i = 0; i < _samples.Count; i++)
        {
            double x = 10 + (i * stepX);
            double normalizedY = Math.Clamp(_samples[i] / maxSpeed, 0, 1.0);
            double y = (h - 10) - (normalizedY * (h - 25));
            points[i] = new Point(x, y);
        }

        if (points.Length > 1)
        {
            // Line
            using (var ctx = streamGeometry.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points.Skip(1).ToArray(), true, false);
            }
            streamGeometry.Freeze();

            // Fill
            using (var ctx = fillGeometry.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h - 8), true, true);
                ctx.LineTo(points[0], true, false);
                ctx.PolyLineTo(points.Skip(1).ToArray(), true, false);
                ctx.LineTo(new Point(points[^1].X, h - 8), true, false);
            }
            fillGeometry.Freeze();

            var gradient = new LinearGradientBrush(
                Color.FromArgb(90, 0x2F, 0x81, 0xF7),
                Color.FromArgb(5, 0x2F, 0x81, 0xF7),
                new Point(0, 0),
                new Point(0, 1));
            gradient.Freeze();

            dc.DrawGeometry(gradient, null, fillGeometry);
            dc.DrawGeometry(null, LinePen, streamGeometry);
        }

        // Live speed readout in top right
        var readoutText = new FormattedText(
            $"⚡ {currentSpeed:0.0} MB/s  (Peak: {_peakSpeedMB:0.0} MB/s)",
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11,
            SpeedReadoutBrush,
            dpi);

        dc.DrawText(readoutText, new Point(w - readoutText.Width - 14, 8));
    }
}
