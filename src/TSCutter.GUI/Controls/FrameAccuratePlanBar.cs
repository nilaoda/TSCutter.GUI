using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TSCutter.GUI.Models.Editing;

namespace TSCutter.GUI.Controls;

/// <summary>
/// 帧精确导出计划条：按输出时长比例画出各片段的重新编码与直接复制部分。
/// 已处理部分用实色，未处理部分用淡色，并在当前位置画一条标记线；多个片段之间留出间隙。
/// </summary>
public sealed class FrameAccuratePlanBar : Control
{
    private const double SegmentGap = 3;
    private const double PendingOpacity = 0.32;

    public static readonly StyledProperty<IReadOnlyList<FrameAccuratePlanPart>?> PartsProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, IReadOnlyList<FrameAccuratePlanPart>?>(nameof(Parts));

    public static readonly StyledProperty<double> TotalProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, double>(nameof(Total));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, double>(nameof(Position));

    public static readonly StyledProperty<IBrush?> EncodeBrushProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, IBrush?>(nameof(EncodeBrush));

    public static readonly StyledProperty<IBrush?> CopyBrushProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, IBrush?>(nameof(CopyBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<FrameAccuratePlanBar, IBrush?>(nameof(MarkerBrush));

    static FrameAccuratePlanBar()
    {
        AffectsRender<FrameAccuratePlanBar>(PartsProperty, TotalProperty, PositionProperty, EncodeBrushProperty,
            CopyBrushProperty, TrackBrushProperty, MarkerBrushProperty);
    }

    public IReadOnlyList<FrameAccuratePlanPart>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    /// <summary>输出总时长（秒）。</summary>
    public double Total
    {
        get => GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    /// <summary>已处理到的输出位置（秒）。</summary>
    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IBrush? EncodeBrush
    {
        get => GetValue(EncodeBrushProperty);
        set => SetValue(EncodeBrushProperty, value);
    }

    public IBrush? CopyBrush
    {
        get => GetValue(CopyBrushProperty);
        set => SetValue(CopyBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;
        if (TrackBrush is { } track)
            context.FillRectangle(track, new Rect(0, 0, width, height));
        var parts = Parts;
        var total = Total;
        if (parts is not { Count: > 0 } || total <= 0)
            return;
        // 片段之间的间隙从可用宽度中扣除，各部分仍按时长比例分配。
        var segments = 1;
        for (var i = 1; i < parts.Count; i++)
            if (parts[i].Segment != parts[i - 1].Segment)
                segments++;
        var usable = Math.Max(1, width - SegmentGap * (segments - 1));
        var position = Math.Clamp(Position, 0, total);
        double markerX = 0;
        var gaps = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (i > 0 && part.Segment != parts[i - 1].Segment)
                gaps++;
            var x = part.Start / total * usable + gaps * SegmentGap;
            var w = Math.Max(1, part.Duration / total * usable);
            var brush = part.Kind == FrameAccuratePartKind.Encode ? EncodeBrush : CopyBrush;
            if (brush is null)
                continue;
            // 已处理部分实色，其余淡色。
            var done = Math.Clamp(position - part.Start, 0, part.Duration) / Math.Max(part.Duration, 1e-9) * w;
            using (context.PushOpacity(PendingOpacity))
                context.FillRectangle(brush, new Rect(x + done, 0, w - done, height));
            if (done > 0)
                context.FillRectangle(brush, new Rect(x, 0, done, height));
            if (position >= part.Start && position <= part.Start + part.Duration)
                markerX = x + done;
            else if (position > part.Start + part.Duration)
                markerX = x + w;
        }
        if (MarkerBrush is { } marker && position > 0 && position < total)
            context.FillRectangle(marker, new Rect(Math.Clamp(markerX - 1, 0, width - 2), 0, 2, height));
    }
}
