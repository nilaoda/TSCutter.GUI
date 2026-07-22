using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Controls;

/// <summary>
/// 自绘修复区间时间轴。只为每个异常区间绘制矩形，不创建逐包或逐采样控件，
/// 因此缩放仅改变坐标换算，长文件也不会额外占用大量内存。
/// </summary>
public sealed class TsRepairMapTimeline : Control
{
    public const double HeaderHeight = 32;
    public const double RowHeight = 38;
    public const double FooterHeight = 26;

    public static readonly StyledProperty<IReadOnlyList<TsRepairMapTrackView>?> TracksProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IReadOnlyList<TsRepairMapTrackView>?>(nameof(Tracks));

    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, double>(nameof(DurationSeconds), 1);

    public static readonly StyledProperty<TsRepairMapRegionView?> SelectedRegionProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, TsRepairMapRegionView?>(
            nameof(SelectedRegion), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> SuccessBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(SuccessBrush));

    public static readonly StyledProperty<IBrush?> WarningBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(WarningBrush));

    public static readonly StyledProperty<IBrush?> ErrorBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(ErrorBrush));

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(MutedBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<IBrush?> TooltipBackgroundBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(TooltipBackgroundBrush));

    public static readonly StyledProperty<IBrush?> TooltipTextBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(TooltipTextBrush));

    public static readonly StyledProperty<IBrush?> TooltipBorderBrushProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, IBrush?>(nameof(TooltipBorderBrush));

    public static readonly StyledProperty<string> TimeLabelProperty =
        AvaloniaProperty.Register<TsRepairMapTimeline, string>(nameof(TimeLabel), string.Empty);

    private readonly List<(Rect Bounds, TsRepairMapRegionView Region)> _hitRegions = [];
    private Point? _pointer;
    private TsRepairMapRegionView? _hoveredRegion;

    static TsRepairMapTimeline()
    {
        AffectsRender<TsRepairMapTimeline>(
            TracksProperty, DurationSecondsProperty, SelectedRegionProperty,
            BackgroundBrushProperty, GridBrushProperty, TextBrushProperty,
            SuccessBrushProperty, WarningBrushProperty, ErrorBrushProperty, MutedBrushProperty,
            SelectionBrushProperty, TooltipBackgroundBrushProperty, TooltipTextBrushProperty,
            TooltipBorderBrushProperty, TimeLabelProperty);
    }

    public TsRepairMapTimeline()
    {
        ClipToBounds = true;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
    }

    public IReadOnlyList<TsRepairMapTrackView>? Tracks
    {
        get => GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    public double DurationSeconds
    {
        get => GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    public TsRepairMapRegionView? SelectedRegion
    {
        get => GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    public IBrush? BackgroundBrush { get => GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }
    public IBrush? WarningBrush { get => GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public IBrush? ErrorBrush { get => GetValue(ErrorBrushProperty); set => SetValue(ErrorBrushProperty, value); }
    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? TooltipBackgroundBrush { get => GetValue(TooltipBackgroundBrushProperty); set => SetValue(TooltipBackgroundBrushProperty, value); }
    public IBrush? TooltipTextBrush { get => GetValue(TooltipTextBrushProperty); set => SetValue(TooltipTextBrushProperty, value); }
    public IBrush? TooltipBorderBrush { get => GetValue(TooltipBorderBrushProperty); set => SetValue(TooltipBorderBrushProperty, value); }
    public string TimeLabel { get => GetValue(TimeLabelProperty); set => SetValue(TimeLabelProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Tracks?.Count ?? 0;
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 820,
            HeaderHeight + rows * RowHeight + FooterHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = BackgroundBrush ?? Brushes.Transparent;
        var grid = GridBrush ?? Brushes.Gray;
        var text = TextBrush ?? Brushes.Black;
        context.FillRectangle(background, new Rect(Bounds.Size));
        _hitRegions.Clear();

        var tracks = Tracks;
        if (tracks is null || tracks.Count == 0 || Bounds.Width < 40)
            return;

        DrawAxis(context, grid, text, tracks.Count);
        for (var row = 0; row < tracks.Count; row++)
        {
            var top = HeaderHeight + row * RowHeight;
            using (context.PushOpacity(0.28))
                context.DrawLine(new Pen(grid, 1), new Point(0, top), new Point(Bounds.Width, top));
            DrawTrack(context, tracks[row], top);
        }
        using (context.PushOpacity(0.28))
            context.DrawLine(new Pen(grid, 1),
                new Point(0, HeaderHeight + tracks.Count * RowHeight),
                new Point(Bounds.Width, HeaderHeight + tracks.Count * RowHeight));
        DrawTooltip(context, text, grid);
    }

    private void DrawAxis(DrawingContext context, IBrush grid, IBrush text, int rowCount)
    {
        var height = HeaderHeight + rowCount * RowHeight;
        var duration = Math.Max(1, DurationSeconds);
        for (var index = 0; index <= 5; index++)
        {
            var ratio = index / 5.0;
            var x = ratio * Math.Max(1, Bounds.Width - 1);
            using (context.PushOpacity(0.3))
                context.DrawLine(new Pen(grid, 1), new Point(x, HeaderHeight - 3), new Point(x, height));
            var label = CreateText(TsCheckEvent.FormatTime(duration * ratio), 11, text);
            var labelX = Math.Clamp(x - label.Width / 2, 3, Math.Max(3, Bounds.Width - label.Width - 3));
            context.DrawText(label, new Point(labelX, 5));
        }
        if (!string.IsNullOrEmpty(TimeLabel))
        {
            var label = CreateText(TimeLabel, 11, text);
            context.DrawText(label, new Point(4, height + 5));
        }
    }

    private void DrawTrack(DrawingContext context, TsRepairMapTrackView track, double rowTop)
    {
        foreach (var region in track.Regions)
        {
            var startX = TimeToX(region.StartSeconds);
            var endX = TimeToX(region.EndSeconds);
            var width = Math.Max(5, endX - startX);
            var rect = new Rect(startX, rowTop + 9, Math.Min(width, Bounds.Width - startX), 20);
            var brush = region.Status switch
            {
                TsRepairMapRegionStatus.Success => SuccessBrush ?? Brushes.Green,
                TsRepairMapRegionStatus.Warning => WarningBrush ?? Brushes.Goldenrod,
                TsRepairMapRegionStatus.Error => ErrorBrush ?? Brushes.Red,
                _ => MutedBrush ?? Brushes.Gray
            };
            using (context.PushOpacity(region.Status == TsRepairMapRegionStatus.Muted ? 0.45 : 0.88))
                context.FillRectangle(brush, rect);
            if (ReferenceEquals(region, SelectedRegion))
                context.DrawRectangle(new Pen(SelectionBrush ?? Brushes.DodgerBlue, 2), rect.Inflate(2));
            _hitRegions.Add((rect.Inflate(3), region));
        }
    }

    private void DrawTooltip(DrawingContext context, IBrush textBrush, IBrush gridBrush)
    {
        if (_pointer is not { } pointer || _hoveredRegion is null)
            return;
        var region = _hoveredRegion;
        var content = string.IsNullOrEmpty(region.BroadcastTimeText)
            ? $"{region.TimeText}\n{region.IssueText}\n{region.StatusText}"
            : $"{region.TimeText}\n{region.BroadcastTimeText}\n{region.IssueText}\n{region.StatusText}";
        var formatted = CreateText(content, 12, TooltipTextBrush ?? textBrush);
        const double paddingX = 10;
        const double paddingY = 6;
        var width = Math.Max(formatted.Width, formatted.WidthIncludingTrailingWhitespace) + paddingX * 2;
        var height = formatted.Height + paddingY * 2;
        var x = pointer.X + 10;
        if (x + width > Bounds.Width - 4)
            x = pointer.X - width - 10;
        x = Math.Clamp(x, 4, Math.Max(4, Bounds.Width - width - 4));
        var y = pointer.Y - height - 8;
        if (y < 4)
            y = pointer.Y + 8;
        y = Math.Clamp(y, 4, Math.Max(4, Bounds.Height - height - 4));
        var rect = new Rect(x, y, width, height);
        context.FillRectangle(TooltipBackgroundBrush ?? BackgroundBrush ?? Brushes.White, rect);
        context.DrawRectangle(new Pen(TooltipBorderBrush ?? gridBrush, 1.5), rect);
        context.DrawText(formatted, rect.Position + new Vector(paddingX, paddingY));
    }

    private double TimeToX(double value) =>
        Math.Clamp(value / Math.Max(1, DurationSeconds), 0, 1) * Math.Max(1, Bounds.Width - 1);

    private void OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        _pointer = eventArgs.GetPosition(this);
        _hoveredRegion = FindRegion(_pointer.Value);
        InvalidateVisual();
    }

    private void OnPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _pointer = null;
        _hoveredRegion = null;
        InvalidateVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var region = FindRegion(eventArgs.GetPosition(this));
        if (region is null)
            return;
        SetCurrentValue(SelectedRegionProperty, region);
        eventArgs.Handled = true;
    }

    private TsRepairMapRegionView? FindRegion(Point point)
    {
        for (var index = _hitRegions.Count - 1; index >= 0; index--)
        {
            if (_hitRegions[index].Bounds.Contains(point))
                return _hitRegions[index].Region;
        }
        return null;
    }

    private static FormattedText CreateText(string value, double size, IBrush brush) => new(
        value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Typeface.Default, size, brush);
}
