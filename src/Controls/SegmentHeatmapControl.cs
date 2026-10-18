using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MirDM.Models;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace MirDM.Controls;

public class SegmentHeatmapControl : FrameworkElement
{
    public static readonly DependencyProperty DownloadItemProperty =
        DependencyProperty.Register(
            nameof(DownloadItem),
            typeof(DownloadItem),
            typeof(SegmentHeatmapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DownloadItem? DownloadItem
    {
        get => (DownloadItem?)GetValue(DownloadItemProperty);
        set => SetValue(DownloadItemProperty, value);
    }

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly Brush PendingBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x26, 0x2D));
    private static readonly Brush CompletedBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x86, 0x36));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly Brush StealBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Pen BorderPen = new Pen(BorderBrush, 1);
    private static readonly Pen SeparatorPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 15, 17, 23)), 1);

    static SegmentHeatmapControl()
    {
        BgBrush.Freeze();
        BorderBrush.Freeze();
        PendingBrush.Freeze();
        CompletedBrush.Freeze();
        ActiveBrush.Freeze();
        StealBrush.Freeze();
        BorderPen.Freeze();
        SeparatorPen.Freeze();
    }

    public SegmentHeatmapControl()
    {
        ClipToBounds = true;
        MinHeight = 28;
    }

    public void RequestRedraw()
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var fullRect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, fullRect, 4, 4);

        var item = DownloadItem;
        if (item == null || item.Chunks.Count == 0 || item.TotalBytes <= 0)
        {
            // Empty state placeholder
            var text = new FormattedText(
                item == null ? "No download selected" : (item.Status == DownloadStatus.Initializing ? "Connecting..." : "Calculating segments..."),
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11,
                new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            return;
        }

        long totalBytes = item.TotalBytes;
        ChunkState[] chunks;
        lock (item.Chunks)
        {
            chunks = item.Chunks.ToArray();
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            long start, end, downloaded, total;
            ChunkStatus status;
            bool isTail;

            lock (chunk)
            {
                start = chunk.StartByte;
                end = chunk.EndByte;
                downloaded = chunk.DownloadedBytes;
                total = chunk.TotalBytes;
                status = chunk.Status;
                isTail = chunk.IsTailSteal;
            }

            double x1 = (double)start / totalBytes * w;
            double x2 = (double)(end + 1) / totalBytes * w;
            double chunkW = Math.Max(1.0, x2 - x1);

            var chunkRect = new Rect(x1, 1, chunkW, h - 2);

            // Chunk pending background
            dc.DrawRectangle(PendingBrush, null, chunkRect);

            // Chunk progress
            if (total > 0 && downloaded > 0)
            {
                double progRatio = Math.Clamp((double)downloaded / total, 0, 1.0);
                double doneW = chunkW * progRatio;

                Brush fillBrush = isTail ? StealBrush :
                                  (status == ChunkStatus.Downloading ? ActiveBrush : CompletedBrush);

                var doneRect = new Rect(x1, 1, doneW, h - 2);
                dc.DrawRectangle(fillBrush, null, doneRect);
            }

            // Separator line between chunks (only if chunk is wide enough)
            if (i > 0 && chunkW >= 4)
            {
                dc.DrawLine(SeparatorPen, new Point(x1, 1), new Point(x1, h - 1));
            }
        }
    }
}
