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

public sealed class TsCheckTimelineEventSelectedEventArgs(TsCheckEvent item) : EventArgs
{
    public TsCheckEvent Item { get; } = item;
}

/// <summary>
/// 自绘 TS 码率曲线和异常轨道。控件只绘制折线与像素聚合后的异常标记，
/// 避免为每个采样点创建 Avalonia 可视元素，长时间录制也能保持低内存和流畅悬停。
/// </summary>
public sealed class TsCheckTimeline : Control
{
    private const double LeftPadding = 66;
    private const double RightPadding = 14;
    private const double TopPadding = 14;
    private const double BottomPadding = 28;
    private const double AnomalyLaneHeight = 22;

    public static readonly StyledProperty<IReadOnlyList<TsCheckTimelineBucket>?> BucketsProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IReadOnlyList<TsCheckTimelineBucket>?>(nameof(Buckets));

    public static readonly StyledProperty<IReadOnlyList<TsCheckEvent>?> EventsProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IReadOnlyList<TsCheckEvent>?>(nameof(Events));

    public static readonly StyledProperty<int> SelectedPidProperty =
        AvaloniaProperty.Register<TsCheckTimeline, int>(nameof(SelectedPid), -1);

    public static readonly StyledProperty<string> SeriesNameProperty =
        AvaloniaProperty.Register<TsCheckTimeline, string>(nameof(SeriesName), string.Empty);

    public static readonly StyledProperty<TsCheckEvent?> SelectedEventProperty =
        AvaloniaProperty.Register<TsCheckTimeline, TsCheckEvent?>(
            nameof(SelectedEvent), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> PrimaryBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(PrimaryBrush));

    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(SecondaryBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> ErrorBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(ErrorBrush));

    public static readonly StyledProperty<IBrush?> WarningBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(WarningBrush));

    public static readonly StyledProperty<IBrush?> TooltipBackgroundBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(TooltipBackgroundBrush));

    public static readonly StyledProperty<IBrush?> TooltipTextBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(TooltipTextBrush));

    public static readonly StyledProperty<IBrush?> TooltipBorderBrushProperty =
        AvaloniaProperty.Register<TsCheckTimeline, IBrush?>(nameof(TooltipBorderBrush));

    public static readonly StyledProperty<string> TotalTextProperty =
        AvaloniaProperty.Register<TsCheckTimeline, string>(nameof(TotalText), string.Empty);

    public static readonly StyledProperty<string> NoDataTextProperty =
        AvaloniaProperty.Register<TsCheckTimeline, string>(nameof(NoDataText), string.Empty);

    public static readonly StyledProperty<string> ErrorTextProperty =
        AvaloniaProperty.Register<TsCheckTimeline, string>(nameof(ErrorText), string.Empty);

    public static readonly StyledProperty<string> WarningTextProperty =
        AvaloniaProperty.Register<TsCheckTimeline, string>(nameof(WarningText), string.Empty);

    private Point? _pointerPosition;
    private Rect _plotBounds;
    private Rect _anomalyBounds;
    private double _maxTime;
    private TsCheckEvent?[] _markerEvents = [];
    private TsCheckSeverity[] _markerSeverities = [];

    static TsCheckTimeline()
    {
        AffectsRender<TsCheckTimeline>(
            BucketsProperty, EventsProperty, SelectedPidProperty, SeriesNameProperty, SelectedEventProperty,
            BackgroundBrushProperty, PrimaryBrushProperty, SecondaryBrushProperty, GridBrushProperty,
            TextBrushProperty, ErrorBrushProperty, WarningBrushProperty,
            TooltipBackgroundBrushProperty, TooltipTextBrushProperty, TooltipBorderBrushProperty,
            TotalTextProperty, NoDataTextProperty, ErrorTextProperty, WarningTextProperty);
    }

    public TsCheckTimeline()
    {
        ClipToBounds = true;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
    }

    public event EventHandler<TsCheckTimelineEventSelectedEventArgs>? EventSelected;

    public IReadOnlyList<TsCheckTimelineBucket>? Buckets
    {
        get => GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    public IReadOnlyList<TsCheckEvent>? Events
    {
        get => GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public int SelectedPid
    {
        get => GetValue(SelectedPidProperty);
        set => SetValue(SelectedPidProperty, value);
    }

    public string SeriesName
    {
        get => GetValue(SeriesNameProperty);
        set => SetValue(SeriesNameProperty, value);
    }

    public TsCheckEvent? SelectedEvent
    {
        get => GetValue(SelectedEventProperty);
        set => SetValue(SelectedEventProperty, value);
    }

    public IBrush? BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush? PrimaryBrush
    {
        get => GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public IBrush? SecondaryBrush
    {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? ErrorBrush
    {
        get => GetValue(ErrorBrushProperty);
        set => SetValue(ErrorBrushProperty, value);
    }

    public IBrush? WarningBrush
    {
        get => GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public IBrush? TooltipBackgroundBrush
    {
        get => GetValue(TooltipBackgroundBrushProperty);
        set => SetValue(TooltipBackgroundBrushProperty, value);
    }

    public IBrush? TooltipTextBrush
    {
        get => GetValue(TooltipTextBrushProperty);
        set => SetValue(TooltipTextBrushProperty, value);
    }

    public IBrush? TooltipBorderBrush
    {
        get => GetValue(TooltipBorderBrushProperty);
        set => SetValue(TooltipBorderBrushProperty, value);
    }

    public string TotalText
    {
        get => GetValue(TotalTextProperty);
        set => SetValue(TotalTextProperty, value);
    }

    public string NoDataText
    {
        get => GetValue(NoDataTextProperty);
        set => SetValue(NoDataTextProperty, value);
    }

    public string ErrorText
    {
        get => GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public string WarningText
    {
        get => GetValue(WarningTextProperty);
        set => SetValue(WarningTextProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = BackgroundBrush ?? Brushes.Transparent;
        var textBrush = TextBrush ?? Brushes.Gray;
        var gridBrush = GridBrush ?? textBrush;
        context.FillRectangle(background, new Rect(Bounds.Size));

        _plotBounds = new Rect(
            LeftPadding, TopPadding,
            Math.Max(0, Bounds.Width - LeftPadding - RightPadding),
            Math.Max(0, Bounds.Height - TopPadding - BottomPadding - AnomalyLaneHeight));
        _anomalyBounds = new Rect(
            _plotBounds.X, _plotBounds.Bottom,
            _plotBounds.Width, AnomalyLaneHeight);

        var buckets = Buckets;
        if (buckets is null || buckets.Count == 0 || _plotBounds.Width < 20 || _plotBounds.Height < 20)
        {
            DrawCenteredText(context, NoDataText, textBrush);
            return;
        }

        _maxTime = GetMaxTime(buckets, Events);
        var maxBitrate = GetMaxBitrate(buckets, SelectedPid);
        if (_maxTime <= 0 || maxBitrate <= 0)
        {
            DrawCenteredText(context, NoDataText, textBrush);
            return;
        }
        maxBitrate = NiceCeiling(maxBitrate);

        DrawGrid(context, gridBrush, textBrush, maxBitrate);
        if (SelectedPid >= 0)
            DrawSeries(context, buckets, -1, maxBitrate, SecondaryBrush ?? gridBrush, 1, 0.55);
        DrawSeries(context, buckets, SelectedPid, maxBitrate, PrimaryBrush ?? Brushes.DodgerBlue, 2, 1);
        DrawAnomalies(context, Events, gridBrush);
        DrawPointerOverlay(context, buckets, maxBitrate, textBrush, background, gridBrush);
    }

    private void DrawGrid(DrawingContext context, IBrush gridBrush, IBrush textBrush, double maxBitrate)
    {
        var gridPen = new Pen(gridBrush, 0.6);
        for (var row = 0; row <= 4; row++)
        {
            var ratio = row / 4.0;
            var y = _plotBounds.Bottom - _plotBounds.Height * ratio;
            using (context.PushOpacity(0.28))
                context.DrawLine(gridPen, new Point(_plotBounds.Left, y), new Point(_plotBounds.Right, y));
            var label = CreateText(FormatBitrate(maxBitrate * ratio), 11, textBrush);
            context.DrawText(label, new Point(Math.Max(2, _plotBounds.Left - label.Width - 7), y - label.Height / 2));
        }

        for (var column = 0; column <= 4; column++)
        {
            var ratio = column / 4.0;
            var x = _plotBounds.Left + _plotBounds.Width * ratio;
            using (context.PushOpacity(0.28))
                context.DrawLine(gridPen, new Point(x, _plotBounds.Top), new Point(x, _anomalyBounds.Bottom));
            var label = CreateText(FormatTime(_maxTime * ratio), 11, textBrush);
            var labelX = Math.Clamp(x - label.Width / 2, _plotBounds.Left, _plotBounds.Right - label.Width);
            context.DrawText(label, new Point(labelX, _anomalyBounds.Bottom + 4));
        }
        using (context.PushOpacity(0.55))
            context.DrawLine(new Pen(gridBrush, 1),
                new Point(_anomalyBounds.Left, _anomalyBounds.Top),
                new Point(_anomalyBounds.Right, _anomalyBounds.Top));
    }

    private void DrawSeries(
        DrawingContext context, IReadOnlyList<TsCheckTimelineBucket> buckets,
        int pid, double maxBitrate, IBrush brush, double thickness, double opacity)
    {
        var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using var opacityScope = context.PushOpacity(opacity);
        Point? previous = null;
        var previousSegment = -1;
        foreach (var bucket in buckets)
        {
            var bitrate = pid < 0 ? bucket.TotalBitrate : bucket.GetPidBitrate(pid);
            var x = TimeToX(bucket.StartSeconds + bucket.DurationSeconds / 2);
            var y = _plotBounds.Bottom - Math.Clamp(bitrate / maxBitrate, 0, 1) * _plotBounds.Height;
            var current = new Point(x, y);
            if (previous is { } point && previousSegment == bucket.Segment)
                context.DrawLine(pen, point, current);
            previous = current;
            previousSegment = bucket.Segment;
        }
    }

    private void DrawAnomalies(DrawingContext context, IReadOnlyList<TsCheckEvent>? events, IBrush fallbackBrush)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(_plotBounds.Width));
        EnsureMarkerBuffers(pixelWidth);
        Array.Clear(_markerEvents);
        Array.Clear(_markerSeverities);

        if (events is not null)
        {
            foreach (var item in events)
            {
                if (item.TimeSeconds is not { } time || time < 0 || time > _maxTime)
                    continue;
                var pixel = Math.Clamp((int)(time / _maxTime * (pixelWidth - 1)), 0, pixelWidth - 1);
                if (_markerEvents[pixel] is null || item.Severity > _markerSeverities[pixel])
                {
                    _markerEvents[pixel] = item;
                    _markerSeverities[pixel] = item.Severity;
                }
            }
        }

        for (var pixel = 0; pixel < pixelWidth; pixel++)
        {
            var item = _markerEvents[pixel];
            if (item is null)
                continue;
            var brush = item.Severity == TsCheckSeverity.Error
                ? ErrorBrush ?? fallbackBrush
                : WarningBrush ?? fallbackBrush;
            var x = _anomalyBounds.Left + pixel;
            var width = ReferenceEquals(item, SelectedEvent) ? 4 : 2;
            context.FillRectangle(brush, new Rect(x - width / 2.0, _anomalyBounds.Top + 4, width, 14));
        }
    }

    private void DrawPointerOverlay(
        DrawingContext context, IReadOnlyList<TsCheckTimelineBucket> buckets, double maxBitrate,
        IBrush textBrush, IBrush background, IBrush borderBrush)
    {
        if (_pointerPosition is not { } pointer || !_plotBounds.Contains(pointer))
            return;

        var time = Math.Clamp((pointer.X - _plotBounds.Left) / _plotBounds.Width, 0, 1) * _maxTime;
        var bucket = FindBucket(buckets, time);
        if (bucket is null)
            return;

        context.DrawLine(new Pen(borderBrush, 1),
            new Point(pointer.X, _plotBounds.Top), new Point(pointer.X, _anomalyBounds.Bottom));

        var totalBitrate = bucket.TotalBitrate;
        var content = $"{FormatTime(time)}\n{TotalText}  {FormatBitrate(totalBitrate)}";
        if (SelectedPid >= 0)
            content += $"\n{SeriesName}  {FormatBitrate(bucket.GetPidBitrate(SelectedPid))}";

        var markerPixel = Math.Clamp((int)(time / _maxTime * (_markerEvents.Length - 1)), 0, _markerEvents.Length - 1);
        if (_markerEvents[markerPixel] is { } marker)
            content += $"\n{(marker.Severity == TsCheckSeverity.Error ? ErrorText : WarningText)}";

        var tooltipTextBrush = TooltipTextBrush ?? textBrush;
        var tooltipBackground = TooltipBackgroundBrush ?? background;
        var tooltipBorder = TooltipBorderBrush ?? borderBrush;
        var text = CreateText(content, 12, tooltipTextBrush);
        const double horizontalPadding = 12;
        const double verticalPadding = 7;
        // Classic 字体可能存在左右 overhang，仅使用 Width 会让末尾字形压到边框上。
        var textWidth = Math.Max(text.Width, text.WidthIncludingTrailingWhitespace) +
                        Math.Abs(text.OverhangLeading) + Math.Abs(text.OverhangTrailing);
        var tooltipSize = new Size(
            textWidth + horizontalPadding * 2,
            text.Height + verticalPadding * 2);
        var tooltipX = pointer.X + 10;
        if (tooltipX + tooltipSize.Width > _plotBounds.Right - 4)
            tooltipX = pointer.X - tooltipSize.Width - 10;
        tooltipX = Math.Clamp(
            tooltipX,
            _plotBounds.Left + 4,
            Math.Max(_plotBounds.Left + 4, _plotBounds.Right - tooltipSize.Width - 4));
        var tooltipY = pointer.Y - tooltipSize.Height - 8;
        if (tooltipY < _plotBounds.Top + 4)
            tooltipY = pointer.Y + 8;
        tooltipY = Math.Clamp(
            tooltipY,
            _plotBounds.Top + 4,
            Math.Max(_plotBounds.Top + 4, _plotBounds.Bottom - tooltipSize.Height - 4));
        var tooltipRect = new Rect(new Point(tooltipX, tooltipY), tooltipSize);
        context.FillRectangle(tooltipBackground, tooltipRect);
        context.DrawRectangle(new Pen(tooltipBorder, 1.5), tooltipRect);
        context.DrawText(text, tooltipRect.Position + new Vector(
            horizontalPadding + Math.Abs(text.OverhangLeading), verticalPadding));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        _pointerPosition = eventArgs.GetPosition(this);
        InvalidateVisual();
    }

    private void OnPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        _pointerPosition = null;
        InvalidateVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_markerEvents.Length == 0 || _maxTime <= 0)
            return;
        var point = eventArgs.GetPosition(this);
        if (!_anomalyBounds.Contains(point))
            return;
        var pixel = Math.Clamp((int)(point.X - _anomalyBounds.Left), 0, _markerEvents.Length - 1);
        for (var distance = 0; distance <= 4; distance++)
        {
            var left = pixel - distance;
            if (left >= 0 && _markerEvents[left] is { } leftEvent)
            {
                SelectEvent(leftEvent);
                eventArgs.Handled = true;
                return;
            }
            var right = pixel + distance;
            if (right < _markerEvents.Length && _markerEvents[right] is { } rightEvent)
            {
                SelectEvent(rightEvent);
                eventArgs.Handled = true;
                return;
            }
        }
    }

    private void SelectEvent(TsCheckEvent item)
    {
        SetCurrentValue(SelectedEventProperty, item);
        EventSelected?.Invoke(this, new TsCheckTimelineEventSelectedEventArgs(item));
    }

    private void EnsureMarkerBuffers(int length)
    {
        if (_markerEvents.Length == length)
            return;
        _markerEvents = new TsCheckEvent?[length];
        _markerSeverities = new TsCheckSeverity[length];
    }

    private static TsCheckTimelineBucket? FindBucket(
        IReadOnlyList<TsCheckTimelineBucket> buckets, double time)
    {
        var low = 0;
        var high = buckets.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var bucket = buckets[middle];
            if (time < bucket.StartSeconds)
                high = middle - 1;
            else if (time > bucket.EndSeconds)
                low = middle + 1;
            else
                return bucket;
        }
        return null;
    }

    private double TimeToX(double seconds) =>
        _plotBounds.Left + Math.Clamp(seconds / _maxTime, 0, 1) * _plotBounds.Width;

    private static double GetMaxTime(
        IReadOnlyList<TsCheckTimelineBucket> buckets, IReadOnlyList<TsCheckEvent>? events)
    {
        var result = buckets[^1].EndSeconds;
        if (events is null)
            return result;
        foreach (var item in events)
        {
            if (item.TimeSeconds is { } time)
                result = Math.Max(result, time);
        }
        return result;
    }

    private static double GetMaxBitrate(IReadOnlyList<TsCheckTimelineBucket> buckets, int pid)
    {
        var result = 0.0;
        foreach (var bucket in buckets)
        {
            result = Math.Max(result, bucket.TotalBitrate);
            if (pid >= 0)
                result = Math.Max(result, bucket.GetPidBitrate(pid));
        }
        return result;
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 0)
            return 1;
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / exponent;
        var ceiling = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return ceiling * exponent;
    }

    private static string FormatBitrate(double bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? $"{bitsPerSecond / 1_000_000:0.##} Mb/s"
        : $"{bitsPerSecond / 1_000:0.##} kb/s";

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static FormattedText CreateText(string text, double size, IBrush brush) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Typeface.Default, size, brush);

    private void DrawCenteredText(DrawingContext context, string text, IBrush brush)
    {
        var formatted = CreateText(text, 13, brush);
        context.DrawText(formatted, new Point(
            Math.Max(0, (Bounds.Width - formatted.Width) / 2),
            Math.Max(0, (Bounds.Height - formatted.Height) / 2)));
    }
}
